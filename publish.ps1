# ============================================================================
#  Mosytool packaging script (x64 framework-dependent single-file publish -> dist\)
#
#  Mosytool.exe is a strictly single-file app (PublishSingleFile=true in
#  Mosytool.csproj): every managed dependency AND every external binary that has to
#  exist on disk (adb, fastboot, the two native *_ffi.dll) is inside it. The latter
#  are EmbeddedResource items that ToolPaths extracts to %LOCALAPPDATA%\Mosytool\tools
#  on first use, so the published result is literally one file: dist\Mosytool.exe.
#  scrcpy (~25 MB) is NOT bundled - the app downloads it on demand into that same
#  tools folder, unless you pass -WithScrcpy to ship it next to the exe.
#  The target PC must have the .NET 10 Desktop Runtime (x64) installed, otherwise
#  Mosytool.exe will not start (Windows will show the "install .NET" dialog).
#
#  ASCII only on purpose: cmd.exe and PowerShell 5.1 read .bat/.cmd/.ps1 using
#  the active OEM/ANSI code page (936 on zh-CN), so non-ASCII text here can
#  break parsing and swallow line endings. Keep it ASCII.
#
#  Steps:
#    1) kill every process that holds files in this folder
#       (Mosytool.exe, adb, scrcpy, or anything started from the project tree)
#    2) shutdown dotnet build servers
#    3) wipe dist\ and bin\Release\ - if the delete fails, find the process that
#       still locks the folder, kill it, and retry
#    4) dotnet publish -c Release
#    5) rebuild dist\ = Mosytool.exe + everything found in dist-extra\ (add -WithScrcpy
#       to drop scrcpy next to it as dist\tools\scrcpy for offline distribution)
#    6) zip dist\ into Mosytool-portable.zip (the binaries deflate to ~half)
#
#  Usage:
#    publish.bat                       double click / plain run
#    powershell -File publish.ps1 -NoPause -SkipNative
#
#  Switches:
#    -NoPause     do not wait for a key press at the end
#    -SkipNative  skip building penumbra_ffi.dll / qualcomm_ffi.dll
#    -KeepDist    do not wipe dist\ before publishing
#    -WithScrcpy  also ship tools\scrcpy next to the exe (~25 MB). Off by default
#                 because the app downloads scrcpy on demand (ScrcpyInstaller).
# ============================================================================

[CmdletBinding()]
param(
    [switch]$NoPause,
    [switch]$SkipNative,
    [switch]$KeepDist,
    [switch]$WithScrcpy
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# paths
# ----------------------------------------------------------------------------
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$root = (Resolve-Path -LiteralPath $root).Path

$project    = Join-Path $root 'Mosytool.csproj'
$dist       = Join-Path $root 'dist'
$releaseIn  = Join-Path $root 'bin\Release'
$objRelease = Join-Path $root 'obj\Release'
# Where the native layer dlls are produced (defaults of PenumbraFfiOutDir /
# QualcommFfiOutDir in Mosytool.csproj). They get embedded into the exe at compile
# time, so the check below is about these source files existing at all.
$nativeDlls = [ordered]@{
    'penumbra_ffi.dll' = Join-Path $env:LOCALAPPDATA 'Mosytool\penumbra-ffi-target\release\penumbra_ffi.dll'
    'qualcomm_ffi.dll' = Join-Path $env:LOCALAPPDATA 'Mosytool\qualcomm-ffi-target\release\qualcomm_ffi.dll'
}

Set-Location -LiteralPath $root

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Note([string]$Text) {
    Write-Host "      $Text" -ForegroundColor DarkGray
}

function Write-Mark([string]$Text) {
    Write-Host "      $Text" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------------
# process handling
# ----------------------------------------------------------------------------

# Processes that were started from $Folder, plus processes that merely have a
# module loaded from there (a copy of Mosytool.exe running elsewhere, a shell
# that loaded one of our DLLs, ...).
function Get-FolderProcess {
    param([string]$Folder)

    $prefix = $Folder.TrimEnd('\') + '\'
    $list = New-Object System.Collections.ArrayList

    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        $exePath = $null
        try { $exePath = $p.Path } catch { $exePath = $null }

        if ($exePath -and $exePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$list.Add($p)
            continue
        }

        # Not started here - check loaded modules. Throws for protected /
        # cross-bitness processes, which is fine: just skip those.
        try {
            foreach ($m in $p.Modules) {
                if ($m.FileName -and $m.FileName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    [void]$list.Add($p)
                    break
                }
            }
        } catch { }
    }

    return $list
}

# Force-kill anything that could lock our output. Returns the list of what was
# killed so the caller can print it.
function Stop-LockingProcess([string]$Folder) {
    $killed = New-Object System.Collections.ArrayList
    $done   = New-Object System.Collections.Generic.HashSet[int]

    # 1) the app itself, no matter where it was launched from
    foreach ($p in @(Get-Process -Name 'Mosytool' -ErrorAction SilentlyContinue)) {
        if ($done.Add($p.Id)) {
            [void]$killed.Add("Mosytool.exe (pid $($p.Id))")
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # 2) everything living inside the project folder (adb.exe, scrcpy.exe, ...)
    foreach ($p in @(Get-FolderProcess -Folder $Folder)) {
        if ($done.Contains($p.Id)) { continue }
        $done.Add($p.Id) | Out-Null

        $label = $p.ProcessName
        try { if ($p.Path) { $label = $p.Path } } catch { }

        [void]$killed.Add("$label (pid $($p.Id))")
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }

    # 3) helpers the app extracted to %LOCALAPPDATA%\Mosytool\tools and spawned (adb /
    #    fastboot / scrcpy). A child process inherits the parent's working directory, so
    #    an app launched from dist\ leaves that adb.exe holding dist\ open - which makes
    #    the cleanup in step 3 fail with "cannot delete 'dist'". They live outside the
    #    project folder, so pass 2 never sees them; match on the extraction root instead.
    #    Only the app's own extracted copies are touched, never a system-wide adb.
    $toolPrefix = (Join-Path $env:LOCALAPPDATA 'Mosytool').TrimEnd('\') + '\'
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        if ($done.Contains($p.Id)) { continue }

        $exePath = $null
        try { $exePath = $p.Path } catch { $exePath = $null }
        if (-not $exePath -or -not $exePath.StartsWith($toolPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $done.Add($p.Id) | Out-Null
        [void]$killed.Add("$exePath (pid $($p.Id))")
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }

    return $killed
}

# ----------------------------------------------------------------------------
# delete / copy with retry-after-kill
# ----------------------------------------------------------------------------
function Remove-FolderWithRetry {
    param([string]$Path, [int]$Attempts = 4)

    if (-not (Test-Path -LiteralPath $Path)) { return $true }

    for ($i = 1; $i -le $Attempts; $i++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop } catch { }
        if (-not (Test-Path -LiteralPath $Path)) { return $true }

        Write-Mark "locked: $Path (try $i/$Attempts) - terminating the processes holding it"
        foreach ($item in @(Stop-LockingProcess -Folder $root)) { Write-Mark "killed: $item" }
        Start-Sleep -Milliseconds 600
    }

    return (-not (Test-Path -LiteralPath $Path))
}

function Copy-FileWithRetry {
    param([string]$Source, [string]$Destination, [int]$Attempts = 4)

    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return $true
        } catch {
            Write-Mark "copy failed (try $i/$Attempts): $($_.Exception.Message)"
            foreach ($item in @(Stop-LockingProcess -Folder $root)) { Write-Mark "killed: $item" }
            Start-Sleep -Milliseconds 600
        }
    }

    return $false
}

# ----------------------------------------------------------------------------
# main
# ----------------------------------------------------------------------------
try {
    Write-Host '=========================================='
    Write-Host '  Mosytool packaging (x64 single-file, framework-dependent)'
    Write-Host '=========================================='
    Write-Note "project: $root"

    Write-Step '[1/5] Terminating processes that lock this folder...'
    $killed = @(Stop-LockingProcess -Folder $root)
    if ($killed.Count -eq 0) { Write-Note 'nothing is running.' }
    else { foreach ($item in $killed) { Write-Mark "killed: $item" } }

    Write-Step '[2/5] Shutting down dotnet build servers...'
    & dotnet build-server shutdown 2>&1 | Out-Null

    if ($KeepDist) {
        Write-Step '[3/5] Cleaning skipped (-KeepDist)'
    } else {
        Write-Step '[3/5] Cleaning dist\, bin\Release\ and obj\Release\ ...'
        foreach ($target in @($dist, $releaseIn)) {
            if (Remove-FolderWithRetry -Path $target) { Write-Note "removed: $target" }
            else {
                throw "cannot delete '$target' - something outside this folder still holds it. Close it and run again, or delete it by hand."
            }
        }

        # obj\Release is intermediate state. It gets wiped too because a stale
        # markup-compile list there is the classic cause of
        # "error BG1002: cannot find ...\<RID>\Views\XxxPage.baml" right after other XAML
        # was built or another configuration was compiled: bin\Release is unrelated to
        # those baml files, so deleting it alone leaves the stale state behind. Wiping
        # costs one extra markup pass and removes that whole class of failure.
        # Unlike dist\ this is best effort - a lingering MSBuild node can hold the folder
        # and the publish still works - so only warn instead of aborting.
        if (Remove-FolderWithRetry -Path $objRelease) { Write-Note "removed: $objRelease" }
        else { Write-Mark "kept: $objRelease (still locked) - continuing" }
    }

    Write-Step '[4/5] dotnet publish -c Release (framework-dependent)...'
    $dotnetArgs = @('publish', $project, '-c', 'Release', '--nologo')
    if ($SkipNative) {
        $dotnetArgs += '/p:SkipPenumbraFfi=true'
        $dotnetArgs += '/p:SkipQualcommFfi=true'
        Write-Mark 'native FFI disabled (-SkipNative)'
    }

    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $exe = @(Get-ChildItem -LiteralPath $releaseIn -Recurse -File -Filter 'Mosytool.exe' -ErrorAction SilentlyContinue |
             Where-Object { $_.DirectoryName -like '*\publish*' } |
             Sort-Object LastWriteTime -Descending)
    if ($exe.Count -eq 0) {
        $exe = @(Get-ChildItem -LiteralPath $releaseIn -Recurse -File -Filter 'Mosytool.exe' -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending)
    }
    if ($exe.Count -eq 0) { throw "Mosytool.exe was not produced under $releaseIn" }

    $exe        = $exe[0]
    $publishDir = $exe.DirectoryName
    Write-Note "published: $($exe.FullName)"

    Write-Step '[5/5] Building dist\ (single exe, tools are embedded)...'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    $exeDst = Join-Path $dist 'Mosytool.exe'
    if (-not (Copy-FileWithRetry -Source $exe.FullName -Destination $exeDst)) {
        throw "cannot copy '$exeDst' - the file is still in use."
    }

    # Single-file publish (PublishSingleFile=true): Mosytool.dll, *.json, WPF-UI and
    # the other managed dependencies are all inside Mosytool.exe, so there is normally
    # nothing else in the publish folder to copy. This loop stays as a safety net in
    # case a sidecar file reappears (e.g. someone turns PublishSingleFile off again).
    # tools\ is skipped here on purpose: it is merged further down, and Copy-Item into
    # an already existing directory would nest the source as tools\tools.
    $copied = 0
    foreach ($item in @(Get-ChildItem -LiteralPath $publishDir -Force)) {
        if ($item.Name -ieq 'Mosytool.exe') { continue }
        if ($item.PSIsContainer -and ($item.Name -ieq 'tools')) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $dist -Recurse -Force
        $copied++
    }
    if ($copied -gt 0) {
        Write-Note "copied: Mosytool.exe + $copied sidecar files"
    } else {
        Write-Note 'copied: Mosytool.exe (single file, managed deps are bundled)'
    }

    # Everything else lives inside the exe now: adb, fastboot and the two native
    # *_ffi.dll are EmbeddedResource items that ToolPaths extracts to
    # %LOCALAPPDATA%\Mosytool\tools on first use (see Mosytool.csproj). So dist is
    # normally exactly one file. The only optional sidecar is scrcpy - it is ~25 MB
    # and ScrcpyService treats a missing scrcpy.exe as "not installed", offering to
    # download it on demand; -WithScrcpy ships it offline. ToolPaths prefers a real
    # tools\ folder next to the exe over the extracted one, so this just works.
    if ($WithScrcpy) {
        $toolsDst = Join-Path $dist 'tools'
        New-Item -ItemType Directory -Path $toolsDst -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'tools\scrcpy') -Destination (Join-Path $toolsDst 'scrcpy') -Recurse -Force
        Write-Note 'copied: tools\scrcpy (offline bundle, ~25 MB)'
    } else {
        Write-Note 'no tools\ folder: adb/fastboot/native layer are inside the exe, scrcpy is on-demand'
    }

    # Anything dropped into dist-extra\ is copied next to the exe, so it also lands in
    # the zip. Currently that is 打不开请安装这个.txt, the .NET Desktop Runtime download
    # link for users whose PC refuses to start the exe. The folder name itself is ASCII
    # and whole items are copied by object, so file names can be non-ASCII even though
    # this script must stay ASCII-only (PowerShell 5.1 reads .ps1 with the ANSI code page).
    $extras = Join-Path $root 'dist-extra'
    if (Test-Path -LiteralPath $extras) {
        $extraItems = @(Get-ChildItem -LiteralPath $extras -Force)
        foreach ($item in $extraItems) {
            Copy-Item -LiteralPath $item.FullName -Destination $dist -Recurse -Force
        }
        if ($extraItems.Count -gt 0) {
            Write-Note "copied: $($extraItems.Count) extra item(s) from dist-extra\"
        } else {
            Write-Note 'dist-extra\ is empty: nothing extra to ship'
        }
    }

    # Sanity check: the native layer is embedded at compile time, so if the Rust dlls
    # were never built the exe silently ships without MTK/EDL support. Warn loudly
    # instead of handing out a package whose native features are dead.
    if (-not $SkipNative) {
        $missing = @($nativeDlls.Keys | Where-Object { -not (Test-Path -LiteralPath $nativeDlls[$_]) })
        if ($missing.Count -gt 0) {
            Write-Mark "WARNING: not built, so NOT embedded in the exe: $($missing -join ', ')"
            Write-Mark 'WARNING: this package has no native layer - MTK (penumbra) / EDL (qualcomm) will not work.'
            Write-Mark 'WARNING: make sure Rust + mingw-w64 are installed, then publish again.'
        }
    }

    # only dist is kept; bin\Release can be several hundred MB
    Remove-FolderWithRetry -Path $releaseIn | Out-Null

    $files  = @(Get-ChildItem -LiteralPath $dist -Recurse -File)
    $bytes  = ($files | Measure-Object -Property Length -Sum).Sum
    $sizeMB = [math]::Round($bytes / 1MB, 1)
    $exeMB  = [math]::Round((Get-Item -LiteralPath $exeDst).Length / 1MB, 1)

    # One file instead of a folder is what actually gets shared, and exe/dll payloads
    # deflate to roughly half. Optimal is the highest level exposed by .NET Framework
    # (CompressionLevel.SmallestSize does not exist in PowerShell 5.1).
    $zipPath = Join-Path $root 'Mosytool-portable.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $dist, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipMB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)

    Write-Host ''
    Write-Host '==========================================' -ForegroundColor Green
    Write-Host '  Done!' -ForegroundColor Green
    Write-Host "  dist\        : $dist"
    Write-Host "  Mosytool.exe : $exeMB MB (single file, framework-dependent)"
    Write-Host "  total        : $($files.Count) files, $sizeMB MB"
    Write-Host "  share this   : $zipPath ($zipMB MB)" -ForegroundColor Green
    Write-Host ''
    Write-Host '  Target PC needs the .NET 10 Desktop Runtime (x64):' -ForegroundColor Yellow
    Write-Host '  https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Yellow
    Write-Host '==========================================' -ForegroundColor Green

    if (-not $NoPause) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit 0
}
catch {
    Write-Host ''
    Write-Host '==========================================' -ForegroundColor Red
    Write-Host '  Publish FAILED!' -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '==========================================' -ForegroundColor Red

    if (-not $NoPause) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
