using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// KernelSU 系的 LKM 模式修补：把 kernelsu.ko 塞进 ramdisk，由 init 在启动时加载。
///
/// 和 GKI 内核补丁的区别：LKM 不碰内核本体，所以 init_boot.img 也能用，也不要求内核开
/// CONFIG_KALLSYMS；代价是要和内核 KMI（android版本-内核版本）严格对上，.ko 是预编译的。
/// 用 ksud.exe 的 boot-patch 一条命令完成，模块和修补器都随程序发布，不需要联网。
/// </summary>
public static class LkmPatchService
{
    /// <summary>管理器家族。.ko 由各家自己编译，装错管理器会认不出内核里的模块。</summary>
    public const string FamilyKernelSu = "KernelSU";
    public const string FamilySukiSU   = "SukiSU";
    public const string FamilyReSukiSU = "ReSukiSU";

    public static IReadOnlyList<string> Families { get; } = new[]
    {
        FamilyKernelSu, FamilySukiSU, FamilyReSukiSU,
    };

    /// <summary>tools\lkm 目录，随程序发布（内嵌解压或直接拷贝）。</summary>
    private static string? _root;

    public static bool IsAvailable => _root is not null && File.Exists(Path.Combine(_root, "ksud.exe"));

    public static void Initialize()
    {
        foreach (var dir in ToolPaths.Dirs)
        {
            try
            {
                var root = Path.Combine(dir, "lkm");
                if (File.Exists(Path.Combine(root, "ksud.exe")))
                {
                    _root = root;
                    return;
                }
            }
            catch
            {
                // 目录名非法就当这个候选不存在
            }
        }

        _root = null;
    }

    /// <summary>家族对应的模块子目录（SukiSU 的 .ko 取自上流 SukiSU_Ultra 分支）。</summary>
    private static string FamilyFolder(string family) => family switch
    {
        FamilyReSukiSU => "ReSukiSU",
        FamilySukiSU   => "SukiSU_Ultra",
        _              => "KernelSU",
    };

    /// <summary>
    /// 该家族实际带了的 KMI 列表，直接按目录里的 .ko 文件名列举。
    /// 各家带的版本并不一致（比如 ReSukiSU 没有 android17-6.18），硬编码一份反而会让用户选到打不了的组合。
    /// </summary>
    public static IReadOnlyList<string> GetKmis(string family)
    {
        if (_root is null) return Array.Empty<string>();

        var folder = Path.Combine(_root, FamilyFolder(family));
        if (!Directory.Exists(folder)) return Array.Empty<string>();

        const string suffix = "_kernelsu.ko";

        return Directory.GetFiles(folder, "*" + suffix)
                        .Select(p => Path.GetFileName(p))
                        .Where(n => n.Length > suffix.Length)
                        .Select(n => n[..^suffix.Length])
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
    }

    /// <summary>KMI 形如 android15-6.6，从内核版本号里就能读出来（设置 → 关于手机）。</summary>
    public static bool IsValidKmi(string? kmi) =>
        !string.IsNullOrWhiteSpace(kmi)
        && System.Text.RegularExpressions.Regex.IsMatch(kmi, @"^android\d+-\d+\.\d+$",
                                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static async Task<BootPatchService.PatchResult> PatchAsync(string bootImage,
                                                                     string outputImage,
                                                                     string family,
                                                                     string kmi,
                                                                     Action<string>? log = null,
                                                                     CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        BootPatchService.PatchResult Fail(string message)
        {
            L(message);
            return new BootPatchService.PatchResult(false, string.Empty, message);
        }

        if (!IsAvailable) return Fail("未找到 ksud.exe（tools\\lkm\\），LKM 修补不可用。");
        if (!File.Exists(bootImage)) return Fail($"boot 镜像不存在：{bootImage}");
        if (!IsValidKmi(kmi))        return Fail($"KMI 格式不对：{kmi}（应形如 android15-6.6，可在手机上用 uname -r 查看）");

        // ksud 的 boot-patch 只声明支持 boot / init_boot；vendor_boot 里是 vendor ramdisk，
        // 不拦但先提醒一句，免得刷完没反应还找不到原因。
        if (BootImageInspector.Probe(bootImage).Kind == BootImageKind.VendorBoot)
            L("警告：vendor_boot 不是 KernelSU 的常规落点（ksud 只声明支持 boot / init_boot），失败请改选 init_boot.img。");

        var modulePath = Path.Combine(_root!, FamilyFolder(family), kmi + "_kernelsu.ko");
        if (!File.Exists(modulePath))
        {
            var available = string.Join("、", GetKmis(family));
            return Fail($"{family} 没有 KMI {kmi} 对应的模块。\n它支持的 KMI：{available}。\n"
                      + "选错了会刷入后无法启动，请按手机内核版本重新选（设置 → 关于手机 → 内核版本，或 adb shell uname -r）。");
        }

        // ksud 自带 magiskboot 逻辑，工作目录放到纯 ASCII 的临时路径，省得中文路径惹麻烦
        var work = Path.Combine(Path.GetTempPath(), "Mosytool-lkm", Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(work);
            File.Copy(bootImage, Path.Combine(work, "boot.img"), true);

            L($"使用 {family} 的 {Path.GetFileName(modulePath)}（KMI {kmi}）。");
            L("把模块打进 ramdisk…");

            var args = $"boot-patch -b \"boot.img\" -m \"{modulePath}\" --kmi {kmi} "
                     + $"--out \"{work}\" --out-name \"patched.img\"";

            var exitCode = await RunAsync(args, work, L, ct);

            var patched = Path.Combine(work, "patched.img");
            if (exitCode != 0 || !File.Exists(patched) || new FileInfo(patched).Length == 0)
                return Fail("LKM 修补失败（ksud 没有生成镜像）。");

            var outDir = Path.GetDirectoryName(outputImage);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.Copy(patched, outputImage, true);

            L($"修补完成（{family} LKM，KMI {kmi}）：{outputImage}");
            L($"记得装对应管理器：{family} 的 {kmi} 模块是配套版本编译的，管理器版本太旧会认不出。");

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

    private static async Task<int> RunAsync(string arguments,
                                           string workDir,
                                           Action<string>? log,
                                           CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = Path.Combine(_root!, "ksud.exe"),
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
            if (proc is null) return -1;

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };
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

            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"错误：{ex.Message}");
            return -1;
        }
    }
}
