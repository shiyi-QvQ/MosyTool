@echo off
rem ============================================================================
rem Native FFI build wrapper. Shared by every Rust crate in native\:
rem penumbra-ffi (MTK), qualcomm-ffi (EDL) and any future one.
rem   %1 = cargo.exe full path
rem   %2 = rust toolchain (e.g. nightly-x86_64-pc-windows-gnu)
rem   %3 = manifest path (native\<crate>\Cargo.toml)
rem   %4 = CARGO_TARGET_DIR
rem   %5 = (optional) mingw bin directory
rem
rem The crate name is deliberately NOT hardcoded here: %3 decides what gets
rem built, and the success check below just looks for "a .dll appeared".
rem
rem NOTE: keep this file pure ASCII. cmd.exe reads .cmd/.bat using the active
rem OEM code page (936/GBK on zh-CN), and UTF-8 Chinese comments can swallow the
rem CRLF at end of line, merging lines and executing garbage.
rem
rem Why this wrapper exists:
rem   1) The rustup GNU target ships dlltool.exe / ld.exe / a prefixed gcc only
rem      inside lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained, which is
rem      not on PATH. Some crate build scripts spawn those directly
rem      (libusb1-sys via cc, getrandom via dlltool).
rem   2) self-contained has no real gcc.exe (only x86_64-w64-mingw32-gcc.exe) and
rem      lacks as.exe / cc1, so it cannot compile C. Prefer a full mingw-w64.
rem
rem Toolchain lookup order:
rem   %5  ->  PENUMBRA_MINGW_BIN  ->  common install dirs  ->  gcc already on PATH
rem ============================================================================

setlocal enableextensions

set "SC=%USERPROFILE%\.rustup\toolchains\%~2\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained"

set "MINGW="

rem --- 1) explicit 5th argument ---
if not "%~5"=="" if exist "%~5\gcc.exe" set "MINGW=%~5"

rem --- 2) environment variable ---
if not defined MINGW if defined PENUMBRA_MINGW_BIN if exist "%PENUMBRA_MINGW_BIN%\gcc.exe" set "MINGW=%PENUMBRA_MINGW_BIN%"

rem --- 3) WinLibs installed via winget (dir name has a random suffix) ---
if not defined MINGW for /d %%G in ("%LOCALAPPDATA%\Microsoft\WinGet\Packages\BrechtSanders.WinLibs.*") do (
    if exist "%%~fG\mingw64\bin\gcc.exe" set "MINGW=%%~fG\mingw64\bin"
)

rem --- 3b) repo-local drop: same %LOCALAPPDATA%\Mosytool tree as the cargo target
rem         dirs. Handy when winget's portable install is broken/incomplete
rem         ("Access is denied" while copying, then gcc.exe is simply missing):
rem         unpack winlibs-x86_64-posix-seh-gcc-*.zip so that bin\gcc.exe ends up here.
if not defined MINGW if exist "%LOCALAPPDATA%\Mosytool\mingw64\bin\gcc.exe" set "MINGW=%LOCALAPPDATA%\Mosytool\mingw64\bin"

rem --- 4) other common locations ---
if not defined MINGW if exist "C:\msys64\mingw64\bin\gcc.exe" set "MINGW=C:\msys64\mingw64\bin"
if not defined MINGW if exist "C:\mingw64\bin\gcc.exe" set "MINGW=C:\mingw64\bin"
if not defined MINGW if exist "C:\ProgramData\mingw64\mingw64\bin\gcc.exe" set "MINGW=C:\ProgramData\mingw64\mingw64\bin"
if not defined MINGW if exist "C:\tools\mingw64\bin\gcc.exe" set "MINGW=C:\tools\mingw64\bin"

rem --- 5) gcc already on PATH ---
if not defined MINGW (
    for /f "delims=" %%P in ('where gcc 2^>nul') do if not defined MINGW set "MINGW=%%~dpP"
    rem Strip the trailing backslash that the drive+path expansion leaves behind.
    rem Written as a for-loop on purpose: cmd.exe rejects a quoted backslash
    rem comparison inside a parenthesized block as a syntax error, and that
    rem failure kills the whole script before it prints anything.
    if defined MINGW for %%Q in ("%MINGW%.") do set "MINGW=%%~fQ"
)

if defined MINGW (
    echo [native-ffi] mingw bin = %MINGW%
) else (
    echo [native-ffi] WARNING: full mingw-w64 with gcc.exe not found.
    echo [native-ffi]          if the build fails, install WinLibs:
    echo [native-ffi]          winget install BrechtSanders.WinLibs.POSIX.MSVCRT
)

rem Order matters: rustc resolves the linker as x86_64-w64-mingw32-gcc.exe.
rem The self-contained dir also has a stub by that name but WITHOUT crt/lib files,
rem so the full mingw bin must end up in front of it.
if exist "%SC%" set "PATH=%SC%;%PATH%"
if defined MINGW set "PATH=%MINGW%;%PATH%"

set "CARGO_TARGET_DIR=%~4"

"%~1" +%~2 build --release --manifest-path "%~3"
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo [native-ffi] cargo build failed, exit code %RC%
    exit /b %RC%
)

rem Crate-agnostic check: whatever was built, a cdylib .dll must have appeared.
set "FOUND="
for %%D in ("%~4\release\*.dll") do set "FOUND=%%~fD"

if not defined FOUND (
    echo [native-ffi] cargo succeeded but no .dll was produced in %~4\release
    exit /b 1
)

echo [native-ffi] OK: %FOUND%
exit /b 0
