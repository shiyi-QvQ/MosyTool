using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// KernelSU 系（KernelSU / SukiSU / ReSukiSU）的离线内核修补。
///
/// 和 Magisk 的 ramdisk 注入是两条完全不同的路：这里一个字节都不改 ramdisk，而是把补丁镜像
/// kpimg 嵌进 boot.img 里的 kernel，靠 kptools 的 unpack → -p 打补丁 → repack 三步完成。
/// kpimg 从用户选的那个 APK 里现取（assets/kpimg），所以打到内核里的是这个包自己的版本，
/// 后续用同一个管理器就能识别。
///
/// 前提：镜像是「带 kernel 的 boot.img」且内核开了 CONFIG_KALLSYMS。init_boot.img 只有
/// ramdisk，走不了这条路（那个方向属于 LKM 模式，要另外塞 .ko）。
/// </summary>
public static class KernelSuPatchService
{
    /// <summary>kptools.exe 的完整路径；没随程序发布时为 null。</summary>
    public static string? ExePath { get; private set; }

    public static bool IsAvailable => ExePath is not null;

    public static void Initialize() => ExePath = Resolve();

    /// <summary>kptools 在 tools\kpatch\ 下，且必须和两个 msys dll 同目录。</summary>
    private static string? Resolve()
    {
        foreach (var dir in ToolPaths.Dirs)
        {
            try
            {
                var path = Path.Combine(dir, "kpatch", "kptools.exe");
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 目录名非法就当这个候选不存在
            }
        }

        return null;
    }

    /// <summary>
    /// 把 boot.img 的 kernel 打上 APK 里的 kpimg，输出新镜像。
    /// </summary>
    public static async Task<BootPatchService.PatchResult> PatchAsync(string bootImage,
                                                                     string apkPath,
                                                                     string outputImage,
                                                                     Action<string>? log = null,
                                                                     CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        BootPatchService.PatchResult Fail(string message)
        {
            L(message);
            return new BootPatchService.PatchResult(false, string.Empty, message);
        }

        if (ExePath is null && (ExePath = Resolve()) is null)
            return Fail("未找到 kptools.exe（tools\\kpatch\\），KernelSU 系脱机修补不可用。");
        if (!File.Exists(bootImage)) return Fail($"boot 镜像不存在：{bootImage}");
        if (!File.Exists(apkPath))   return Fail($"安装包不存在：{apkPath}");

        // kptools 是 msys 程序，对非 ASCII 路径不保证可靠，工作目录固定放 %TEMP%，
        // 结果最后再复制到用户选的位置。
        var work = Path.Combine(Path.GetTempPath(), "Mosytool-ksu", Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(work);

            // 1) 取补丁镜像
            var kpimg = ExtractKpimg(apkPath, work);

            var kpimgInfo = await RunAsync("-l -k kpimg", work, L, ct);
            var version = Regex1(kpimgInfo.Output, @"version=(\S+)");
            var arch    = Regex1(kpimgInfo.Output, @"arch=(\S+)");
            L($"使用安装包内的 kpimg（版本 {version ?? "未知"}，架构 {arch ?? "未知"}）。");

            // 2) 解包：kpimg 要嵌进 kernel，镜像里必须真的有 kernel
            L("解包 boot 镜像…");
            File.Copy(bootImage, Path.Combine(work, "boot.img"), true);

            var unpack = await RunAsync("unpack \"boot.img\"", work, L, ct);
            var kernel = Path.Combine(work, "kernel");
            if (unpack.ExitCode != 0 || !File.Exists(kernel))
                return Fail("解包失败，或镜像里没有可修补的内核。\n"
                          + "KernelSU 系改的是 boot.img 里的 kernel，init_boot.img 只有 ramdisk，走不了这条路。");

            // 3) 内核得带符号表，否则 kptools 定位不到需要的函数
            var flags = await RunAsync("-i \"kernel\" -f", work, L, ct);
            if (flags.ExitCode != 0)
                return Fail("读取内核配置失败，该内核可能不受支持。");
            if (!flags.Output.Contains("CONFIG_KALLSYMS=y", StringComparison.OrdinalIgnoreCase))
                return Fail("当前内核没有开启 CONFIG_KALLSYMS=y，KernelSU 系无法修补。");
            if (!flags.Output.Contains("CONFIG_KALLSYMS_ALL=y", StringComparison.OrdinalIgnoreCase))
                L("内核未开启 CONFIG_KALLSYMS_ALL=y，按兼容模式继续。");

            // 4) 已经打过补丁的镜像不接受，否则会叠两层
            var before = await RunAsync("-i \"kernel\" -l", work, L, ct);
            if (before.Output.Contains("patched=true", StringComparison.OrdinalIgnoreCase))
                return Fail("这个 boot.img 已经打过了 KernelSU / KernelPatch 补丁，请换原厂 boot.img。");

            // 5) 打补丁
            L("把 kpimg 写进内核…");
            var patch = await RunAsync("-p -i \"kernel\" -k \"kpimg\" -o \"kernel.patched\"", work, L, ct);
            var patchedKernel = Path.Combine(work, "kernel.patched");
            if (patch.ExitCode != 0 || !File.Exists(patchedKernel))
                return Fail("内核打补丁失败。");

            var after = await RunAsync("-i \"kernel.patched\" -l", work, L, ct);
            if (!after.Output.Contains("patched=true", StringComparison.OrdinalIgnoreCase))
                return Fail("补丁结果校验失败（内核里读不到 patched=true）。");

            // 6) repack 读的是固定文件名 kernel，把打过补丁的那份换上
            File.Move(patchedKernel, kernel, true);

            L("重打包 boot 镜像…");
            var repack = await RunAsync("repack \"boot.img\"", work, L, ct);
            var newBoot = Path.Combine(work, "new-boot.img");
            if (repack.ExitCode != 0 || !File.Exists(newBoot) || new FileInfo(newBoot).Length == 0)
                return Fail("重打包失败。");

            var outDir = Path.GetDirectoryName(outputImage);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.Copy(newBoot, outputImage, true);

            L($"修补完成（KernelSU 系，kpimg {version ?? "未知"}）：{outputImage}");
            return new BootPatchService.PatchResult(true, outputImage, "修补完成。");
        }
        catch (OperationCanceledException)
        {
            return new BootPatchService.PatchResult(false, string.Empty, "已取消。");
        }
        catch (Exception ex)
        {
            L($"修补出错：{ex.Message}");
            return new BootPatchService.PatchResult(false, string.Empty, $"修补出错：{ex.Message}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* 删不掉就留给系统清理 */ }
        }
    }

    /// <summary>把 APK 里的 kpimg 解到工作目录，文件名保持 kpimg（后面命令都按这个名字引用）。</summary>
    private static string ExtractKpimg(string apkPath, string work)
    {
        using var zip = ZipFile.OpenRead(apkPath);

        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("assets/kpimg", StringComparison.OrdinalIgnoreCase))
                 ?? zip.Entries.FirstOrDefault(e => e.FullName.Split('/')[^1]
                       .StartsWith("kpimg", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException("这个安装包里没有 kpimg（内核补丁镜像），无法脱机修补。");

        var target = Path.Combine(work, "kpimg");
        entry.ExtractToFile(target, true);
        return target;
    }

    /// <summary>kptools 的输出里取一个 key=value 的值，取不到返回 null。</summary>
    private static string? Regex1(string text, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string arguments,
                                                                     string workDir,
                                                                     Action<string>? log,
                                                                     CancellationToken ct)
    {
        var captured = new StringBuilder();

        void Line(string? text)
        {
            if (text is null) return;
            captured.AppendLine(text);
            log?.Invoke(text);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ExePath!,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, captured.ToString());

            proc.OutputDataReceived += (_, e) => Line(e.Data);
            proc.ErrorDataReceived  += (_, e) => Line(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* 可能已经自行退出 */ }
                throw;
            }

            return (proc.ExitCode, captured.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"错误：{ex.Message}");
            return (-1, captured.ToString());
        }
    }
}
