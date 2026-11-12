using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// magiskboot 的定位与调用。
///
/// 它和 adb / fastboot 一样是独立进程（tools\magiskboot.exe，编译期内嵌进主程序，
/// 首次运行时由 ToolPaths 解压到 %LOCALAPPDATA%\Mosytool\tools），但用法上有个关键差别：
/// unpack / repack 读写的是「当前工作目录」下的固定文件名（kernel、ramdisk.cpio、
/// header、dtb …），所以每次调用都必须显式指定工作目录，绝不能默认落在 exe 自己旁边，
/// 否则解包产物会散进程序目录、多任务之间还会互相覆盖。
///
/// 另外它的退出码带语义（unpack：0=识别成功 1=失败 2=ChromeOS 镜像），
/// 因此这里不把非 0 一律当失败，而是把 ExitCode 原样交给调用方判断。
/// </summary>
public static class MagiskbootService
{
    /// <summary>magiskboot.exe 的完整路径；未内置或解压失败时为 null。</summary>
    public static string? ExePath { get; private set; }

    public static bool IsAvailable => ExePath is not null;

    private static string? _version;
    private static bool _versionProbed;

    /// <summary>版本号（形如 30.1）。不同移植版的 usage 打印格式不一样，抓不到就是 null。</summary>
    public static string? Version
    {
        get
        {
            if (!_versionProbed)
            {
                _versionProbed = true;
                _version       = ProbeVersion();
            }

            return _version;
        }
    }

    public static void Initialize() => ExePath = ToolPaths.Find("magiskboot.exe");

    /// <summary>一次调用的结果。Output 是 stdout + stderr 合并后的原始文本，便于界面直接贴进日志。</summary>
    public readonly record struct RunResult(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    // ==================== 基础执行 ====================

    /// <summary>
    /// 跑一条 magiskboot 命令。输出逐行实时回调（解包大镜像时能立刻看到进度），
    /// 取消时直接杀进程而不是抛异常就走，避免留下半途的 magiskboot 还在写文件。
    /// </summary>
    public static async Task<RunResult> RunAsync(string arguments,
                                                 string? workingDirectory = null,
                                                 Action<string>? log = null,
                                                 CancellationToken ct = default,
                                                 IReadOnlyDictionary<string, string>? environment = null)
    {
        // 延迟定位：App 启动时调过 Initialize 就直接用；没调过（或工具是后来才落盘的）这里补一次
        if (ExePath is null) ExePath = ToolPaths.Find("magiskboot.exe");

        if (ExePath is null)
        {
            log?.Invoke("错误：未找到 magiskboot.exe，boot 镜像相关功能不可用。");
            return new RunResult(-1, string.Empty);
        }

        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(ExePath)!
            : workingDirectory;

        try
        {
            Directory.CreateDirectory(workDir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"错误：无法建立工作目录 {workDir}（{ex.Message}）。");
            return new RunResult(-1, string.Empty);
        }

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
                FileName               = ExePath,
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // 回显里会出现用户选的镜像路径（可能含中文），按系统代码页解会成乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            // cpio patch 这类动作是靠环境变量开关的（KEEPVERITY / KEEPFORCEENCRYPT）。
            // 子进程默认继承本进程的环境，这里只把调用方额外指定的项叠加进去。
            if (environment is not null)
                foreach (var pair in environment)
                    psi.Environment[pair.Key] = pair.Value;

            using var proc = Process.Start(psi);
            if (proc is null) return new RunResult(-1, captured.ToString());

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
                log?.Invoke("已取消。");
                return new RunResult(-1, captured.ToString());
            }

            return new RunResult(proc.ExitCode, captured.ToString());
        }
        catch (Exception ex)
        {
            log?.Invoke($"错误：{ex.Message}");
            return new RunResult(-1, captured.ToString());
        }
    }

    // ==================== 镜像解包 / 重打包 ====================

    /// <summary>
    /// 解包 boot / init_boot / recovery 等镜像到 <paramref name="workDir"/>，
    /// 在该目录生成 kernel、ramdisk.cpio、第二段、dtb、header 等组件。
    /// </summary>
    /// <param name="skipDecompress">true 时各组件保持原始压缩格式（-n），适合只做二进制替换的场合。</param>
    /// <param name="dumpHeader">true 时额外把 header 文本导出来（-h），改 header 配置重打包要用。</param>
    public static Task<RunResult> UnpackAsync(string imagePath,
                                              string workDir,
                                              bool skipDecompress = false,
                                              bool dumpHeader = false,
                                              Action<string>? log = null,
                                              CancellationToken ct = default)
    {
        var args = new StringBuilder("unpack");
        if (skipDecompress) args.Append(" -n");
        if (dumpHeader) args.Append(" -h");
        args.Append(' ').Append(Quote(imagePath));

        return RunAsync(args.ToString(), workDir, log, ct);
    }

    /// <summary>
    /// 用 <paramref name="workDir"/> 里的组件重新打包成镜像。
    /// <paramref name="originalImage"/>（原镜像）决定压缩格式与 header 配置，必须传对；
    /// 不指定输出文件名时会在工作目录生成 new-boot.img。
    /// </summary>
    public static Task<RunResult> RepackAsync(string originalImage,
                                              string workDir,
                                              string? outputImage = null,
                                              bool skipCompress = false,
                                              Action<string>? log = null,
                                              CancellationToken ct = default,
                                              IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new StringBuilder("repack");
        if (skipCompress) args.Append(" -n");
        args.Append(' ').Append(Quote(originalImage));
        if (!string.IsNullOrWhiteSpace(outputImage)) args.Append(' ').Append(Quote(outputImage));

        return RunAsync(args.ToString(), workDir, log, ct, environment);
    }

    /// <summary>清掉工作目录里的解包产物（kernel / ramdisk.cpio / header …），目录里的其它文件不动。</summary>
    public static Task<RunResult> CleanupAsync(string workDir,
                                               Action<string>? log = null,
                                               CancellationToken ct = default) =>
        RunAsync("cleanup", workDir, log, ct);

    // ==================== 签名校验 ====================

    /// <summary>校验镜像的 AVB 1.0 签名。ExitCode 0 表示签名有效（镜像被改过就会变成 1）。</summary>
    public static Task<RunResult> VerifyAsync(string imagePath,
                                              string? x509Pem = null,
                                              Action<string>? log = null,
                                              CancellationToken ct = default)
    {
        var args = new StringBuilder("verify ").Append(Quote(imagePath));
        if (!string.IsNullOrWhiteSpace(x509Pem)) args.Append(' ').Append(Quote(x509Pem!));

        return RunAsync(args.ToString(), null, log, ct);
    }

    /// <summary>
    /// 给镜像打 AVB 1.0 签名。不给密钥对时用 magiskboot 内置的 AOSP verity key，
    /// 想用自己的 key（例如厂商 key）就同时传 <paramref name="x509Pem"/> 与 <paramref name="pk8"/>。
    /// </summary>
    public static Task<RunResult> SignAsync(string imagePath,
                                            string? name = null,
                                            string? x509Pem = null,
                                            string? pk8 = null,
                                            Action<string>? log = null,
                                            CancellationToken ct = default)
    {
        var args = new StringBuilder("sign ").Append(Quote(imagePath));
        if (!string.IsNullOrWhiteSpace(name)) args.Append(' ').Append(Quote(name!));
        if (!string.IsNullOrWhiteSpace(x509Pem) && !string.IsNullOrWhiteSpace(pk8))
            args.Append(' ').Append(Quote(x509Pem!)).Append(' ').Append(Quote(pk8!));

        return RunAsync(args.ToString(), null, log, ct);
    }

    // ==================== ramdisk（cpio）操作 ====================

    /// <summary>
    /// 对 ramdisk.cpio 原地执行一组 cpio 命令，每条命令作为独立参数传入，例如：
    /// "rm -r .backup"、"mkdir 0750 overlay.d"、"extract magisk init"。
    ///
    /// 命令里的源文件名（如 add 0750 init <b>magiskinit</b>）是相对路径，
    /// 相对的是工作目录而不是 cpio 文件所在目录，所以必须把 workDir 一并传对。
    /// </summary>
    public static Task<RunResult> CpioAsync(string cpioPath,
                                            IEnumerable<string> commands,
                                            Action<string>? log = null,
                                            CancellationToken ct = default,
                                            IReadOnlyDictionary<string, string>? environment = null,
                                            string? workingDirectory = null)
    {
        var args = new StringBuilder("cpio ").Append(Quote(cpioPath));
        foreach (var command in commands) args.Append(' ').Append(Quote(command));

        return RunAsync(args.ToString(), workingDirectory, log, ct, environment);
    }

    // ==================== 从 payload.bin 抽分区 ====================

    /// <summary>
    /// 直接从 OTA 的 payload.bin 里抽出某个分区镜像，省掉整套 payload 解包流程。
    /// 不指定分区时 magiskboot 会优先找 init_boot，其次 boot，并写到工作目录下。
    /// </summary>
    public static Task<RunResult> ExtractFromPayloadAsync(string payloadPath,
                                                          string? partition = null,
                                                          string? outputImage = null,
                                                          string workDir = "",
                                                          Action<string>? log = null,
                                                          CancellationToken ct = default)
    {
        var args = new StringBuilder("extract ").Append(Quote(payloadPath));
        if (!string.IsNullOrWhiteSpace(partition)) args.Append(' ').Append(Quote(partition!));
        if (!string.IsNullOrWhiteSpace(outputImage)) args.Append(' ').Append(Quote(outputImage!));

        return RunAsync(args.ToString(),
                        string.IsNullOrWhiteSpace(workDir) ? null : workDir,
                        log, ct);
    }

    // ==================== 校验值 / 特征码 / 压缩 ====================

    /// <summary>输出文件的 SHA1（结果在 Output 里）。</summary>
    public static Task<RunResult> Sha1Async(string filePath,
                                            Action<string>? log = null,
                                            CancellationToken ct = default) =>
        RunAsync($"sha1 {Quote(filePath)}", null, log, ct);

    /// <summary>十六进制特征码替换，用于二进制补丁（找 hexpattern1 换成 hexpattern2）。</summary>
    public static Task<RunResult> HexpatchAsync(string filePath,
                                                string pattern,
                                                string replacement,
                                                Action<string>? log = null,
                                                CancellationToken ct = default) =>
        RunAsync($"hexpatch {Quote(filePath)} {Quote(pattern)} {Quote(replacement)}", null, log, ct);

    /// <summary>压缩单个文件。format 可给 gzip / zopfli / xz / lzma / bzip2 / lz4 / lz4_legacy / lz4_lg，不给按 gzip。</summary>
    public static Task<RunResult> CompressAsync(string inputPath,
                                                string? outputPath = null,
                                                string? format = null,
                                                Action<string>? log = null,
                                                CancellationToken ct = default)
    {
        // 形态是 compress[=format]，format 紧贴在动作名后面
        var args = new StringBuilder(string.IsNullOrWhiteSpace(format) ? "compress" : $"compress={format}");
        args.Append(' ').Append(Quote(inputPath));
        if (!string.IsNullOrWhiteSpace(outputPath)) args.Append(' ').Append(Quote(outputPath!));

        return RunAsync(args.ToString(), null, log, ct);
    }

    /// <summary>自动识别格式并解压单个文件，输出名省略时会去掉原扩展名。</summary>
    public static Task<RunResult> DecompressAsync(string inputPath,
                                                  string? outputPath = null,
                                                  Action<string>? log = null,
                                                  CancellationToken ct = default)
    {
        var args = new StringBuilder("decompress ").Append(Quote(inputPath));
        if (!string.IsNullOrWhiteSpace(outputPath)) args.Append(' ').Append(Quote(outputPath!));

        return RunAsync(args.ToString(), null, log, ct);
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 按 Windows 命令行规则给参数加引号。含空格或中文的路径必须处理，
    /// 且结尾的反斜杠要翻倍 —— 否则它会把收尾的引号转义掉，
    /// 参数就吞掉了后面一整段（"D:\out\" 这种路径最容易踩）。
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0) return value;

        var sb = new StringBuilder("\"");

        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\') { backslashes++; i++; }

            if (i == value.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(value[i]);
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// 无参数运行一次，从 usage 输出里抠版本号。这条命令本来就会往 stderr 打一堆说明、
    /// 并以非 0 退出，所以这里不看退出码，只找版本字样。
    /// </summary>
    private static string? ProbeVersion()
    {
        if (ExePath is null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ExePath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // 两个管道必须并发读：只读一边的话另一边写满 4KB，子进程会阻塞在写、永不退出
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
            }

            var text = Drain(stdout) + Drain(stderr);
            var match = Regex.Match(text, @"MagiskBoot\s+v?(\d+(?:\.\d+)+)");

            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取回读取任务的结果，最多等一小会儿，避免管道没关时线程一直挂着。</summary>
    private static string Drain(Task<string> task)
    {
        try
        {
            return task.Wait(2000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
