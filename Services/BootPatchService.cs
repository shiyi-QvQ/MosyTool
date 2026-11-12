using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// boot / init_boot 的离线修补（脱机修补）。
/// 流程对照 Magisk 官方 scripts/boot_patch.sh：
/// unpack → 判定 ramdisk 状态 → 备份 → 压缩组件 → 写 config → cpio 注入 → 二进制补丁 → repack。
/// Magisk 组件不随程序分发，从用户自选的安装包（APK）里现取，版本跟着用户的包走。
/// </summary>
public static class BootPatchService
{
    /// <summary>三条互不兼容的修补路线，选错等于白刷。</summary>
    public enum PatchScheme
    {
        /// <summary>按安装包自动识别：Magisk 组件 → Magisk，kpimg → KernelSU 内核补丁。</summary>
        Auto,

        /// <summary>强制走 Magisk 的 ramdisk 注入（boot_patch.sh 那套）。</summary>
        Magisk,

        /// <summary>KernelSU 系内核补丁：把 APK 里的 kpimg 嵌进 kernel，要求镜像带 kernel。</summary>
        KernelSuGki,

        /// <summary>KernelSU 系 LKM：把预编译的 kernelsu.ko 打进 ramdisk，不看内核符号表。</summary>
        Lkm,
    }

    /// <summary>对应 boot_patch.sh 里的几个环境变量。</summary>
    public sealed class PatchOptions
    {
        public PatchScheme Scheme { get; set; } = PatchScheme.Auto;

        /// <summary>LKM 模式的管理器家族：KernelSU / SukiSU / ReSukiSU。</summary>
        public string LkmFamily { get; set; } = LkmPatchService.FamilySukiSU;

        /// <summary>LKM 模式的 KMI，形如 android15-6.6。</summary>
        public string LkmKmi { get; set; } = string.Empty;

        public string Architecture { get; set; } = "arm64-v8a";
        public bool KeepVerity { get; set; }          // KEEPVERITY
        public bool KeepForceEncrypt { get; set; }    // KEEPFORCEENCRYPT
        public bool PatchVbmetaFlag { get; set; }     // PATCHVBMETAFLAG
        public bool RecoveryMode { get; set; }        // RECOVERYMODE
        public bool LegacySar { get; set; }           // LEGACYSAR
        public bool PatchKernelBinaries { get; set; } = true;
    }

    public sealed record PatchResult(bool Success, string OutputImage, string Message);

    /// <summary>ramdisk 在工作目录里可能出现的位置，顺序同官方脚本。</summary>
    private static readonly string[] RamdiskCandidates =
    {
        "ramdisk.cpio",
        "vendor_ramdisk/init_boot.cpio",
        "vendor_ramdisk/ramdisk.cpio",
    };

    private static readonly (string Pattern, string Replacement, string Name)[] SamsungKernelPatches =
    {
        ("49010054011440B93FA00F71E9000054010840B93FA00F7189000054001840B91FA00F7188010054",
         "A1020054011440B93FA00F7140020054010840B93FA00F71E0010054001840B91FA00F7181010054",
         "移除三星 RKP"),
        ("821B8012", "E2FF8F12", "移除三星 defex"),
        ("70726F63615F636F6E66696700", "70726F63615F6D616769736B00", "禁用三星 PROCA"),
    };

    private const string LegacySarPattern     = "736B69705F696E697472616D667300";
    private const string LegacySarReplacement = "77616E745F696E697472616D667300";

    /// <summary>把 boot / init_boot 镜像修补成带 Magisk 的镜像。原厂镜像或被 Magisk 修补过的镜像都可以。</summary>
    public static async Task<PatchResult> PatchAsync(string bootImage,
                                                     string magiskApk,
                                                     string outputImage,
                                                     PatchOptions options,
                                                     string workDir,
                                                     Action<string>? log = null,
                                                     CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        PatchResult Fail(string message)
        {
            L(message);
            return new PatchResult(false, string.Empty, message);
        }

        if (!File.Exists(bootImage)) return Fail($"boot 镜像不存在：{bootImage}");

        // LKM 走自己的工具（ksud），既不需要 Magisk 也不读安装包，所以放在最前面分流
        if (options.Scheme == PatchScheme.Lkm)
        {
            L("按 LKM 模式修补：把 kernelsu.ko 打进 ramdisk，不改内核本体。");

            var kmi = await ResolveKmiAsync(options.LkmKmi, log);
            if (kmi.Length == 0)
                return Fail("LKM 的 .ko 必须和手机内核严格对应，但没能自动识别出 KMI。\n"
                          + "请在「LKM KMI」里选一下（手机 设置 → 关于手机 → 内核版本，或 adb shell uname -r）。");

            return await LkmPatchService.PatchAsync(bootImage, outputImage, options.LkmFamily, kmi, log, ct);
        }

        if (!MagiskbootService.IsAvailable)  return Fail("未找到 magiskboot.exe，脱机修补不可用。");

        // KernelSU 系（KernelSU / SukiSU / ReSukiSU）的包结构和 Magisk 完全不同，
        // 打补丁也不是注入 ramdisk，而是把 kpimg 嵌进 kernel，交给另一个服务处理。
        // 注意：这一段不能要求安装包存在 —— 走 LKM 时压根不需要安装包。
        var isKernelSu = options.Scheme == PatchScheme.KernelSuGki
                      || (options.Scheme == PatchScheme.Auto && DetectPackage(magiskApk) == PackageKind.KernelSu);

        if (isKernelSu)
        {
            // 光看 APK 不够，还得看镜像：GKI 改的是 kernel 本体，镜像里必须真的有内核。
            // Android 13 之后内核以外的启动件挪进了 init_boot.img，那里 kernel_size 恒为 0；
            // 手动选了 GKI 却选到 init_boot / vendor_boot 时同样改道 LKM ——
            // 否则 kptools 只会解出一个空 kernel，再往下全是看不懂的报错。
            var image = BootImageInspector.Probe(bootImage);

            if (!image.KernelPatchable && image.Kind != BootImageKind.Unknown)
            {
                L($"选中的是 {image.Describe()}。");
                L("KernelSU 系 GKI 方案改的是内核本体，这个镜像里没有内核，改用 LKM：把 kernelsu.ko 打进 ramdisk。");
                L("LKM 不用安装包，但 .ko 必须和手机内核的 KMI 完全一致。");

                var autoKmi = await ResolveKmiAsync(options.LkmKmi, log);
                if (autoKmi.Length == 0)
                    return Fail("LKM 的 .ko 必须和手机内核严格对应，但没能自动识别出 KMI。\n"
                              + "请在「LKM KMI」里选一下（手机 设置 → 关于手机 → 内核版本，或 adb shell uname -r）。");

                return await LkmPatchService.PatchAsync(bootImage, outputImage,
                                                        options.LkmFamily, autoKmi, log, ct);
            }

            if (!File.Exists(magiskApk))
                return Fail("KernelSU 系 GKI 需要安装包（kpimg 从里面取）。\n"
                          + "不想选安装包就改选「KernelSU 系 LKM」—— 它用随程序发布的 .ko，只要求 KMI 对得上。");

            if (image.Kind == BootImageKind.Unknown)
                L("镜像头不是标准 Android 格式，跳过类型判断，交给 kptools 自己识别。");
            else
                L($"选中的是 {image.Describe()}，内核补丁可行。");

            L("走 KernelSU 系内核补丁流程（unpack → kpimg → repack）。");
            L("该流程只改内核、不注入 ramdisk，下面这些 Magisk 专用选项（架构 / KEEPVERITY / KEEPFORCEENCRYPT / 恢复模式）不适用。");
            return await KernelSuPatchService.PatchAsync(bootImage, magiskApk, outputImage, log, ct);
        }

        // Magisk 流程的组件全部从安装包里取（KernelSU 系在上面已经分流走了）
        if (!File.Exists(magiskApk)) return Fail($"安装包不存在：{magiskApk}");

        var work = string.IsNullOrWhiteSpace(workDir)
            ? Path.Combine(Path.GetTempPath(), "Mosytool-patch-" + Guid.NewGuid().ToString("N")[..8])
            : workDir;

        try
        {
            Directory.CreateDirectory(work);

            // 1) 取组件
            var usedArch = ExtractComponents(magiskApk, work, options.Architecture, log);

            // 2) 解包（magiskboot 退出码带语义：3 = vendor_boot）
            L("解包 boot 镜像…");
            var unpack     = await MagiskbootService.UnpackAsync(bootImage, work, false, false, log, ct);
            var vendorBoot = unpack.ExitCode == 3;
            if (unpack.ExitCode != 0 && !vendorBoot)
                return Fail($"解包失败（magiskboot 退出码 {unpack.ExitCode}）。");
            if (vendorBoot) L("检测到 vendor_boot 镜像。");

            // 3) 定位 ramdisk 并判定状态
            var ramdiskRel = RamdiskCandidates.FirstOrDefault(rel => File.Exists(Path.Combine(work, rel)));
            var ramdisk    = Path.Combine(work, ramdiskRel ?? RamdiskCandidates[0]);
            var skipBackup = ramdiskRel is null;   // 没有 ramdisk：从零建一个

            int status;
            if (skipBackup)
            {
                L("镜像里没有 ramdisk，将从零创建一个。");
                status = 0;
            }
            else
            {
                L("检查 ramdisk 状态…");
                var test = await MagiskbootService.CpioAsync(ramdisk, new[] { "test" }, log, ct, null, work);
                status = test.ExitCode;   // 0=原厂 1=已被 Magisk 修补 2=被其它程序修补
            }

            // 4) 备份 / 还原
            var ramdiskOrig = Path.Combine(work, "ramdisk.cpio.orig");
            var sha1        = string.Empty;
            string? preinitDevice = null;

            if (status == 0)
            {
                L("原厂 boot 镜像。");
                var sha = await MagiskbootService.Sha1Async(bootImage, log, ct);
                var m   = Regex.Match(sha.Output ?? string.Empty, "[0-9a-fA-F]{40}");
                sha1    = m.Success ? m.Value.ToLowerInvariant() : string.Empty;
                File.Copy(bootImage, Path.Combine(work, "stock_boot.img"), true);
                if (!skipBackup) File.Copy(ramdisk, ramdiskOrig, true);
            }
            else if (status == 1)
            {
                L("检测到已被 Magisk 修补过的镜像，先还原原始 ramdisk。");
                await MagiskbootService.CpioAsync(ramdisk,
                    new[] { "extract .backup/.magisk config.orig", "restore" }, log, ct, null, work);
                File.Copy(ramdisk, ramdiskOrig, true);
            }
            else
            {
                return Fail("该镜像被其它不兼容的程序修补过，请先刷回原厂 boot 再修补。");
            }

            // 还原出来的 config 里带着上次的 SHA1 / PREINITDEVICE，沿用
            var origConfig = Path.Combine(work, "config.orig");
            if (File.Exists(origConfig))
            {
                var map = ReadKeyValues(origConfig);
                if (map.TryGetValue("SHA1", out var s) && !string.IsNullOrWhiteSpace(s)) sha1 = s;
                if (map.TryGetValue("PREINITDEVICE", out var p) && !string.IsNullOrWhiteSpace(p)) preinitDevice = p;
                try { File.Delete(origConfig); } catch { /* 删不掉不影响 */ }
            }

            // 5) 压缩组件
            L("压缩 Magisk 组件…");
            await CompressXzAsync(work, "libmagisk.so",  "magisk.xz",  log, ct);
            await CompressXzAsync(work, "stub.apk",      "stub.xz",    log, ct);
            await CompressXzAsync(work, "libinit-ld.so", "init-ld.xz", log, ct);

            // 6) 写 config
            var configLines = new List<string>
            {
                $"KEEPVERITY={Bool(options.KeepVerity)}",
                $"KEEPFORCEENCRYPT={Bool(options.KeepForceEncrypt)}",
                $"RECOVERYMODE={Bool(options.RecoveryMode)}",
                $"VENDORBOOT={Bool(vendorBoot)}",
            };
            if (!string.IsNullOrWhiteSpace(preinitDevice)) configLines.Add($"PREINITDEVICE={preinitDevice}");
            if (!string.IsNullOrWhiteSpace(sha1))          configLines.Add($"SHA1={sha1}");

            File.WriteAllText(Path.Combine(work, "config"),
                              string.Join("\n", configLines) + "\n",
                              new UTF8Encoding(false));

            // 7) 注入 ramdisk
            L("注入 Magisk 到 ramdisk…");
            var patchEnv = new Dictionary<string, string>
            {
                ["KEEPVERITY"]       = Bool(options.KeepVerity),
                ["KEEPFORCEENCRYPT"] = Bool(options.KeepForceEncrypt),
            };
            if (options.LegacySar) patchEnv["LEGACYSAR"] = "true";

            var cpioCommands = new List<string>
            {
                "add 0750 init magiskinit",
                "mkdir 0750 overlay.d",
                "mkdir 0750 overlay.d/sbin",
                "add 0644 overlay.d/sbin/magisk.xz magisk.xz",
                "add 0644 overlay.d/sbin/stub.xz stub.xz",
                "add 0644 overlay.d/sbin/init-ld.xz init-ld.xz",
                "patch",
            };
            if (!skipBackup) cpioCommands.Add("backup ramdisk.cpio.orig");
            cpioCommands.Add("mkdir 000 .backup");
            cpioCommands.Add("add 000 .backup/.magisk config");

            var inject = await MagiskbootService.CpioAsync(ramdisk, cpioCommands, log, ct, patchEnv, work);
            if (!inject.Ok) return Fail("注入 ramdisk 失败。");

            // 8) dtb / 内核二进制补丁
            if (options.PatchKernelBinaries)
                await PatchBinariesAsync(work, options, log, ct);

            // 9) 重打包
            L("重打包 boot 镜像…");
            var repackEnv = new Dictionary<string, string>(patchEnv);
            if (options.PatchVbmetaFlag) repackEnv["PATCHVBMETAFLAG"] = "true";

            var repack = await MagiskbootService.RepackAsync(bootImage, work, outputImage, false, log, ct, repackEnv);
            if (!repack.Ok || !File.Exists(outputImage))
                return Fail($"重打包失败（magiskboot 退出码 {repack.ExitCode}）。");

            L($"修补完成（组件架构 {usedArch}）：{outputImage}");
            return new PatchResult(true, outputImage, "修补完成。");
        }
        catch (OperationCanceledException)
        {
            return new PatchResult(false, string.Empty, "已取消。");
        }
        catch (Exception ex)
        {
            L($"修补出错：{ex.Message}");
            return new PatchResult(false, string.Empty, $"修补出错：{ex.Message}");
        }
    }

    // ==================== 内部步骤 ====================

    /// <summary>
    /// KMI 优先用界面选的；没选就从已连接设备读 uname -r。
    /// 都拿不到返回空串，由调用方给出明确提示——绝不猜一个默认值打进去。
    ///
    /// 读设备是同步等 adb 返回的，必须挪到线程池：本方法前半段跑在 UI 线程上，
    /// 直接等最多 8 秒，界面会假死。
    /// </summary>
    private static async Task<string> ResolveKmiAsync(string? selected, Action<string>? log)
    {
        if (!string.IsNullOrWhiteSpace(selected)) return selected.Trim();

        var fromDevice = await Task.Run(KmiResolver.FromDevice) ?? string.Empty;
        if (fromDevice.Length > 0) log?.Invoke($"未指定 KMI，从已连接设备读到 {fromDevice}。");
        return fromDevice;
    }

    private enum PackageKind
    {
        Magisk,
        KernelSu,
    }

    /// <summary>
    /// 界面判分流用：这个安装包是不是 KernelSU 系（带 kpimg 或 libksud.so）。
    /// 包不存在 / 读不了时返回 false，交给后面的流程报具体原因。
    /// </summary>
    public static bool IsKernelSuPackage(string? apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath)) return false;
        return DetectPackage(apkPath) == PackageKind.KernelSu;
    }

    /// <summary>
    /// 判断选中的安装包属于哪一套。两套的组件和流程完全不通用，认错包只会得到一堆看不懂的报错。
    /// Magisk 组件优先：有些第三方包会同时带 magisk 组件和 kpimg，这时按 Magisk 处理更符合预期。
    /// </summary>
    private static PackageKind DetectPackage(string apkPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(apkPath);

            var names = zip.Entries.Select(e => e.FullName).ToList();

            if (names.Any(n => n.Split('/')[^1].Equals("libmagiskinit.so", StringComparison.OrdinalIgnoreCase)))
                return PackageKind.Magisk;

            if (names.Any(n => n.Equals("assets/kpimg", StringComparison.OrdinalIgnoreCase))
                || names.Any(n => n.Split('/')[^1].Equals("libksud.so", StringComparison.OrdinalIgnoreCase)))
                return PackageKind.KernelSu;
        }
        catch
        {
            // 读不了就当普通包，后面按 Magisk 流程报具体的缺失原因
        }

        return PackageKind.Magisk;
    }

    /// <summary>
    /// 从安装包里取出组件放到工作目录，返回实际使用的架构。
    /// 不写死 lib/&lt;arch&gt;/ 路径：条目不区分大小写，老版本的 libmagisk32/64.so 也认，
    /// 所选架构在包里不存在时自动退到包里真正齐全的那个并提示。
    /// </summary>
    private static string ExtractComponents(string apkPath, string work, string arch, Action<string>? log)
    {
        using var zip = ZipFile.OpenRead(apkPath);
        var entries = zip.Entries.ToList();

        static string FileNameOf(ZipArchiveEntry e) => e.FullName.Split('/')[^1];
        static bool NameIs(ZipArchiveEntry e, string name)
            => string.Equals(FileNameOf(e), name, StringComparison.OrdinalIgnoreCase);

        var abis = entries
            .Select(e => e.FullName.Split('/'))
            .Where(p => p.Length >= 3 && p[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 一组完整的 Magisk 组件：magiskinit + magisk（新版 libmagisk.so，老版 libmagisk64/32.so）
        bool AbiComplete(string abi)
        {
            var dir = $"lib/{abi}/";
            var hasInit = entries.Any(e => e.FullName.Equals(dir + "libmagiskinit.so", StringComparison.OrdinalIgnoreCase));
            var hasMagisk = entries.Any(e =>
                e.FullName.Equals(dir + "libmagisk.so",   StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Equals(dir + "libmagisk64.so", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Equals(dir + "libmagisk32.so", StringComparison.OrdinalIgnoreCase));

            return hasInit && hasMagisk;
        }

        var hasStub = entries.Any(e => NameIs(e, "stub.apk"));
        var useArch = abis.FirstOrDefault(a => a.Equals(arch, StringComparison.OrdinalIgnoreCase) && AbiComplete(a))
                      ?? abis.FirstOrDefault(AbiComplete);

        if (useArch is null || !hasStub)
            throw new InvalidOperationException(DescribeMissingPackage(entries, abis));

        var baseDir = $"lib/{useArch}/";
        void TakeExact(string name, string target)
        {
            entries.FirstOrDefault(e => e.FullName.Equals(baseDir + name, StringComparison.OrdinalIgnoreCase))
                   ?.ExtractToFile(Path.Combine(work, target), true);
        }

        TakeExact("libmagiskinit.so", "magiskinit");

        // 新版把 magisk 本体做成单个 libmagisk.so，老版本按位数拆成 libmagisk64/32.so，
        // 统一落地成 libmagisk.so，后面压缩和 config 都不用关心来源名。
        if (entries.Any(e => e.FullName.Equals(baseDir + "libmagisk.so", StringComparison.OrdinalIgnoreCase)))
            TakeExact("libmagisk.so", "libmagisk.so");
        else if (entries.Any(e => e.FullName.Equals(baseDir + "libmagisk64.so", StringComparison.OrdinalIgnoreCase)))
            TakeExact("libmagisk64.so", "libmagisk.so");
        else
            TakeExact("libmagisk32.so", "libmagisk.so");

        TakeExact("libinit-ld.so", "libinit-ld.so");   // 旧版没有，缺了不影响
        entries.FirstOrDefault(e => NameIs(e, "stub.apk"))
               ?.ExtractToFile(Path.Combine(work, "stub.apk"), true);

        if (!string.Equals(useArch, arch, StringComparison.OrdinalIgnoreCase))
            log?.Invoke($"安装包里没有 {arch} 的组件，改用 {useArch}（请确认与设备架构一致）。");

        log?.Invoke($"已从安装包提取 Magisk 组件（{useArch}）。");
        return useArch;
    }

    /// <summary>
    /// 组件缺失时给出能直接行动的结论。KernelSU 系的包在 DetectPackage 里已经被分走了，
    /// 走到这里的都是「号称 Magisk 但又取不出组件」的包，直接把包内实际内容摊开，
    /// 比只说一句"架构不对"有用得多。
    /// </summary>
    private static string DescribeMissingPackage(List<ZipArchiveEntry> entries, List<string> abis)
    {
        var libs = entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FullName)
            .Take(12)
            .ToList();

        var sb = new StringBuilder("这个 APK 里找不到 Magisk 组件（libmagiskinit.so / libmagisk.so / stub.apk）。");
        if (abis.Count > 0) sb.Append($"\n包内含架构：{string.Join("、", abis)}");
        if (libs.Count > 0) sb.Append($"\nlib/ 目录：{string.Join("、", libs)}");
        sb.Append("\n请确认选的是 Magisk 官方安装包。");
        return sb.ToString();
    }

    private static async Task CompressXzAsync(string work, string input, string output,
                                              Action<string>? log, CancellationToken ct)
    {
        var src = Path.Combine(work, input);
        if (!File.Exists(src))
        {
            log?.Invoke($"跳过压缩：没有 {input}。");
            return;
        }

        var result = await MagiskbootService.CompressAsync(src, Path.Combine(work, output), "xz", log, ct);
        if (!result.Ok) throw new InvalidOperationException($"压缩 {input} 失败。");
    }

    /// <summary>dtb 里的 fstab 补丁 + 三星内核二进制补丁。</summary>
    private static async Task PatchBinariesAsync(string work, PatchOptions options,
                                                 Action<string>? log, CancellationToken ct)
    {
        foreach (var name in new[] { "dtb", "kernel_dtb", "extra" })
        {
            if (!File.Exists(Path.Combine(work, name))) continue;

            var test = await MagiskbootService.RunAsync($"dtb {name} test", work, log, ct);
            if (!test.Ok)
            {
                log?.Invoke($"警告：{name} 已被旧版 Magisk 以不支持的方式改过。");
                continue;
            }

            var patch = await MagiskbootService.RunAsync($"dtb {name} patch", work, log, ct);
            if (patch.Ok) log?.Invoke($"已修补 {name} 中的 fstab。");
        }

        var kernel = Path.Combine(work, "kernel");
        if (!File.Exists(kernel)) return;

        var patched = false;

        foreach (var (pattern, replacement, name) in SamsungKernelPatches)
        {
            var r = await MagiskbootService.HexpatchAsync(kernel, pattern, replacement, log, ct);
            if (r.Ok) { patched = true; log?.Invoke($"内核补丁：{name}。"); }
        }

        if (options.LegacySar)
        {
            var r = await MagiskbootService.HexpatchAsync(kernel, LegacySarPattern, LegacySarReplacement, log, ct);
            if (r.Ok) { patched = true; log?.Invoke("内核补丁：skip_initramfs → want_initramfs。"); }
        }

        // 内核完全不需要改时删掉解出来的 kernel，让 repack 沿用原镜像里那份（部分机型改了反而起不来）
        if (!patched)
        {
            try { File.Delete(kernel); } catch { /* 删不掉就让 repack 自己处理 */ }
        }
    }

    private static Dictionary<string, string> ReadKeyValues(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            map[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return map;
    }

    // ==================== 制作 GKI（boot + AnyKernel3） ====================

    public sealed class GkiOptions
    {
        /// <summary>是否对 dtb / kernel_dtb 打去 verity/avb 补丁。</summary>
        public bool PatchDtb { get; set; } = true;

        /// <summary>是否把内核 skip_initramfs 改成 want_initramfs。</summary>
        public bool WantInitramfs { get; set; }
    }

    public sealed record GkiResult(bool Success, string OutputImage, string Message);

    /// <summary>内核文件候选，顺序同 GKIPatch.bat。</summary>
    private static readonly string[] KernelCandidates = { "Image", "Image.gz", "Image.lz4", "Image.xz", "zImage" };

    /// <summary>
    /// 用 AnyKernel3 里的内核替换 boot 镜像里的内核，重新打包成新 boot（工具箱里的"制作 GKI"）。
    /// </summary>
    public static async Task<GkiResult> BuildGkiAsync(string bootImage,
                                                      string anyKernelZip,
                                                      string outputImage,
                                                      GkiOptions options,
                                                      string workDir,
                                                      Action<string>? log = null,
                                                      CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        GkiResult Fail(string message)
        {
            L(message);
            return new GkiResult(false, string.Empty, message);
        }

        if (!MagiskbootService.IsAvailable) return Fail("未找到 magiskboot.exe，无法制作 GKI。");
        if (!File.Exists(bootImage))        return Fail($"boot 镜像不存在：{bootImage}");
        if (!File.Exists(anyKernelZip))     return Fail($"AnyKernel3 压缩包不存在：{anyKernelZip}");

        var ak3Dir = string.IsNullOrWhiteSpace(workDir)
            ? Path.Combine(Path.GetTempPath(), "Mosytool-gki-" + Guid.NewGuid().ToString("N")[..8])
            : workDir;

        try
        {
            Directory.CreateDirectory(ak3Dir);

            L("解压 AnyKernel3…");
            ZipFile.ExtractToDirectory(anyKernelZip, ak3Dir, true);

            var bootCopy = Path.Combine(ak3Dir, "boot.img");
            File.Copy(bootImage, bootCopy, true);

            L("解包 boot 镜像…");
            var unpack = await MagiskbootService.UnpackAsync(bootCopy, ak3Dir, false, false, log, ct);
            if (unpack.ExitCode != 0 && unpack.ExitCode != 3)
                return Fail($"解包失败（magiskboot 退出码 {unpack.ExitCode}）。");

            if (options.PatchDtb)
            {
                foreach (var name in new[] { "dtb", "kernel_dtb" })
                {
                    if (!File.Exists(Path.Combine(ak3Dir, name))) continue;

                    var patch = await MagiskbootService.RunAsync($"dtb {name} patch", ak3Dir, log, ct);
                    if (patch.Ok) L($"已修补 {name} 中的 fstab。");
                }
            }

            // 用 AK3 自带的内核替换解包出来的 kernel
            L("替换内核…");
            var kernelPath = Path.Combine(ak3Dir, "kernel");
            var replaced   = false;

            foreach (var candidate in KernelCandidates)
            {
                var source = Path.Combine(ak3Dir, candidate);
                if (!File.Exists(source)) continue;

                if (candidate == "Image")
                {
                    File.Copy(source, kernelPath, true);
                }
                else
                {
                    // Image.gz / lz4 / xz / zImage 都先解压成裸内核
                    var decompressed = await MagiskbootService.DecompressAsync(source, kernelPath, log, ct);
                    if (!decompressed.Ok) return Fail($"解压 {candidate} 失败。");
                }

                L($"使用 {candidate} 作为内核。");
                replaced = true;
                break;
            }

            if (!replaced)
                return Fail("AnyKernel3 里没有可用的内核文件（Image / Image.gz / Image.lz4 / Image.xz / zImage）。");

            if (options.WantInitramfs)
            {
                var r = await MagiskbootService.HexpatchAsync(kernelPath, LegacySarPattern, LegacySarReplacement, log, ct);
                if (r.Ok) L("内核补丁：skip_initramfs → want_initramfs。");
            }

            L("重新打包…");
            var repack = await MagiskbootService.RepackAsync(bootCopy, ak3Dir, outputImage, false, log, ct);
            if (!repack.Ok || !File.Exists(outputImage))
                return Fail($"打包失败（magiskboot 退出码 {repack.ExitCode}）。");

            L($"已生成：{outputImage}");
            return new GkiResult(true, outputImage, "制作完成。");
        }
        catch (OperationCanceledException)
        {
            return new GkiResult(false, string.Empty, "已取消。");
        }
        catch (Exception ex)
        {
            L($"制作出错：{ex.Message}");
            return new GkiResult(false, string.Empty, $"制作出错：{ex.Message}");
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
