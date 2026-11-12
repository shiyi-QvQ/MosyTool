using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

public static class AdbService
{
    public static string? AdbPath { get; private set; }
    public static string? FastbootPath { get; private set; }

    public static bool IsAdbAvailable      => AdbPath is not null;
    public static bool IsFastbootAvailable => FastbootPath is not null;

    public static void Initialize()
    {
        AdbPath      = FindTool("adb.exe");
        FastbootPath = FindTool("fastboot.exe");
    }

    private static string? FindTool(string fileName)
    {
        // ToolPaths.Dirs = exe 同级的 tools\ → 首启解压出的数据目录（单文件发布）→ exe 同级目录
        var candidates = new List<string>();
        foreach (var dir in ToolPaths.Dirs) candidates.Add(Path.Combine(dir, fileName));

        candidates.AddRange(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Android", "Sdk", "platform-tools", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "platform-tools", fileName),
            Path.Combine("C:\\platform-tools", fileName),
        });

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    /// <summary>
    /// 同步执行命令，返回 stdout + stderr 合并文本；超时会杀掉进程并交回已读到的内容。
    ///
    /// 关键：两个管道必须并发读取。fastboot getvar all 的输出几乎全在 stderr 上，
    /// 若先 ReadToEnd(stdout) 再读 stderr，stderr 管道写满（约 4KB）子进程就会阻塞在写，
    /// 进程不退出 → stdout 不关闭 → 界面永久卡死，超时也救不回来。
    /// </summary>
    public static string Run(string toolPath, string arguments, int timeoutMs = 12000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = toolPath,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // adb / fastboot 的输出都是 UTF-8，按系统代码页解码会把中文设备路径解成乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (proc.WaitForExit(timeoutMs))
            {
                proc.WaitForExit();   // 等异步读取把管道里剩余内容收干净
                return Read(stdout) + Read(stderr);
            }

            // 超时：先杀进程让管道关闭，再尽量取回已经读到的内容。
            try { proc.Kill(true); } catch { /* 进程可能已自行退出 */ }
            try { proc.WaitForExit(2000); } catch { /* 忽略 */ }

            return Read(stdout) + Read(stderr);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>回收读取任务的结果，最多等一小会儿，避免管道没关时线程一直挂着。</summary>
    private static string Read(Task<string> task)
    {
        try
        {
            return task.Wait(3000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 逐行实时回调执行外部命令，支持取消（取消时直接杀掉进程），返回退出码。
    ///
    /// 给 adb push 这类耗时且需要进度反馈的操作使用：
    /// - 两个管道都用事件回调异步抽干，不会像同步读那样互相堵住；
    /// - 取消时杀掉进程而不是抛异常就撒手，避免留下还在传文件的 adb。
    /// </summary>
    public static async Task<int> RunStreamingAsync(string toolPath, string arguments,
                                                    Action<string> onLine, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = toolPath,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // 少了这两行就按系统代码页解码：adb push 输出的中文进度与设备路径会变成乱码，
                // 用户看到的就是日志里一串莫名其妙的符号。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 取消时结束进程：这样管道会关闭，读取回调自然收尾。
            await using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { /* 进程可能刚退出 */ }
            });

            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            onLine($"执行失败: {ex.Message}");
            return -1;
        }
    }

    /// <summary>异步执行 adb 命令，逐行实时回调输出，返回退出码。</summary>
    public static async Task<int> RunAsync(string arguments, Action<string> onOutput, CancellationToken ct = default)
    {
        if (AdbPath is null) return -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = AdbPath,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            onOutput($"错误: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 退出程序时收尾：关掉 adb server，并结束本程序启动后残留的 adb / fastboot 进程。
    ///
    /// 以前直接关窗口，<c>adb start-server</c> 拉起的后台 server 会一直驻留（占着 5037 端口），
    /// 任务管理器里也能看到 adb.exe / fastboot.exe 不退出。
    /// </summary>
    public static void Shutdown()
    {
        StopAdbServer();
        KillOwnProcesses("adb", "fastboot", "scrcpy", "magiskboot", "kptools", "ksud");
    }

    /// <summary>关掉 adb server（会顺带结束它拉起的 adb 客户端）。</summary>
    public static void StopAdbServer()
    {
        if (AdbPath is not null) Run(AdbPath, "kill-server", 4000);
    }

    /// <summary>
    /// 按进程名结束<b>本程序 tools 目录下</b>的可执行文件，不碰用户其它工具链的同名进程。
    /// 退出流程里分步调用（先 adb、再 fastboot、最后清理其余），每一步都能给出提示。
    /// </summary>
    public static void KillOwnProcesses(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is null) continue;
                    if (!IsOwnTool(path)) continue;

                    proc.Kill(true);
                    proc.WaitForExit(2000);
                }
                catch
                {
                    // 进程已退出、或无权限读 MainModule（其它用户进程），忽略即可
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    /// <summary>判断进程是否来自本程序的 tools 目录。</summary>
    private static bool IsOwnTool(string exePath)
    {
        if (SafeEquals(exePath, AdbPath) || SafeEquals(exePath, FastbootPath)) return true;

        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return false;

        foreach (var toolDir in ToolPaths.Dirs)
        {
            if (!string.IsNullOrEmpty(toolDir) &&
                string.Equals(Path.GetFullPath(dir), Path.GetFullPath(toolDir), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;

        static bool SafeEquals(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
