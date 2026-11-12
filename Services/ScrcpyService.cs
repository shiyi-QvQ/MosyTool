using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// scrcpy 投屏（把手机画面实时投到电脑上操作手机）。
///
/// exe 由 ScrcpyInstaller 装到程序目录的 tools\scrcpy\ 下（也认 PATH 里的），
/// 这里负责定位、拼参数、启停，并在本程序退出时把投屏一起带走。
/// </summary>
public static class ScrcpyService
{
    /// <summary>scrcpy 官方发布页（找不到 exe 时引导用户去这里拿）。</summary>
    public const string DownloadUrl = "https://github.com/Genymobile/scrcpy/releases";

    public static string? ScrcpyPath { get; private set; }

    /// <summary>scrcpy 主版本号（GetVersion 时会填上）；0 表示还没读到。</summary>
    public static int MajorVersion { get; private set; }

    public static bool IsAvailable => ScrcpyPath is not null;

    /// <summary>当前正在跑的投屏进程；没有投屏时为 null。</summary>
    private static Process? _running;

    /// <summary>正在跑的投屏进程，已经退出的会顺手清成 null。</summary>
    public static Process? Running
    {
        get
        {
            var proc = _running;
            if (proc is null) return null;

            if (HasExited(proc))
            {
                _running = null;
                return null;
            }

            return proc;
        }
    }

    /// <summary>
    /// 结束正在跑的投屏。主窗口关闭、程序被结束进程、系统注销都会走到这里，
    /// 免得桌面上留下一个没人管的 scrcpy 窗口。
    /// </summary>
    public static void StopRunning()
    {
        var proc = Interlocked.Exchange(ref _running, null);
        if (proc is null) return;

        try { if (!HasExited(proc)) proc.Kill(true); }
        catch { /* 已经退出或权限不足；进程级的兜底见 ChildProcessJob */ }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>懒加载：第一次用到时再找，省得启动阶段多一次磁盘扫描。</summary>
    private static bool _initialized;

    public static void Initialize(bool force = false)
    {
        if (_initialized && !force) return;
        _initialized = true;
        ScrcpyPath   = FindScrcpy();
    }

    /// <summary>重新查一次 scrcpy（用户可能刚把文件拷进 tools）。</summary>
    public static void Refresh() => Initialize(force: true);

    /// <summary>用系统默认浏览器打开 scrcpy 官方发布页；成功返回 true。</summary>
    public static bool OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = DownloadUrl, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindScrcpy()
    {
        // 官方 release 解压出来就是 scrcpy-win64-vX.Y，所以常见两种放法都认；
        // ToolPaths.Dirs 同时覆盖 exe 同级的 tools\（-WithScrcpy 离线包、绿色目录）和
        // 单文件发布时按需下载到的数据目录
        var candidates = new List<string>();
        foreach (var dir in ToolPaths.Dirs)
        {
            candidates.Add(Path.Combine(dir, "scrcpy", "scrcpy.exe"));
            candidates.Add(Path.Combine(dir, "scrcpy.exe"));
        }

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), "scrcpy.exe");
            if (File.Exists(full)) return full;
        }

        return null;
    }

    /// <summary>读 scrcpy 版本号，例如 "3.1"；读不到时返回 null。</summary>
    public static string? GetVersion()
    {
        if (ScrcpyPath is null) return null;

        // `scrcpy --version` 的首行形如 "scrcpy 3.1 <https://github.com/...>"
        var output = AdbService.Run(ScrcpyPath, "--version", 8000);
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("scrcpy", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(text, @"(\d+)\.\d+");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
                    MajorVersion = major;

                return text;
            }
        }

        return output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary>
    /// 启动投屏，返回进程句柄；scrcpy 不可用或启动失败返回 null。
    ///
    /// 说明：scrcpy 自己也要调 adb，所以启动前把 ADB 环境变量指向本工具自带的 adb，
    /// 避免用户机器上另有一份版本不一致的 adb 时连不上设备。
    /// </summary>
    public static Process? Start(string? serial, ScrcpyOptions options,
                                 Action<string>? onLine = null, Action? onExit = null)
    {
        Initialize();
        if (ScrcpyPath is null)
        {
            onLine?.Invoke("未找到 scrcpy.exe，无法投屏。");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ScrcpyPath,
                Arguments              = BuildArguments(serial, options),
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // scrcpy 会把设备名 / 中文路径原样回显，按系统代码页解码会成乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            if (AdbService.AdbPath is not null)
                psi.Environment["ADB"] = AdbService.AdbPath;

            var proc = Process.Start(psi);
            if (proc is null) return null;

            // scrcpy 的信息/报错都在这里出来，顺手接进日志，用户能看到「为什么连不上」
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 同一时刻只留一个投屏：万一上一个还没收干净，先把它结束掉
            StopRunning();

            _running = proc;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                Interlocked.CompareExchange(ref _running, null, proc);
                onExit?.Invoke();
            };

            // 兜底：本程序被强杀或崩溃时，由系统连带结束这个 job 里的 scrcpy
            ChildProcessJob.Add(proc);

            return proc;
        }
        catch (Exception ex)
        {
            onLine?.Invoke($"启动 scrcpy 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>按选项拼 scrcpy 命令行。</summary>
    public static string BuildArguments(string? serial, ScrcpyOptions options)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(serial))
        {
            args.Add("-s");
            args.Add(serial.Trim());
        }

        if (options.MaxSize > 0)
            args.Add($"--max-size={options.MaxSize}");

        if (!string.IsNullOrWhiteSpace(options.VideoBitRate))
            args.Add($"--video-bit-rate={options.VideoBitRate.Trim()}");

        if (options.MaxFps > 0)
            args.Add($"--max-fps={options.MaxFps}");

        // --no-audio 是 scrcpy 2.0 才有的参数，1.x 上带过去会直接报未知参数退出；
        // 版本读不到（0）时按新版处理。
        if (options.NoAudio && MajorVersion is 0 or >= 2)
            args.Add("--no-audio");

        if (options.TurnScreenOff)
            args.Add("--turn-screen-off");

        if (options.StayAwake)
            args.Add("--stay-awake");

        if (options.AlwaysOnTop)
            args.Add("--always-on-top");

        if (options.Borderless)
            args.Add("--window-borderless");

        if (options.Fullscreen)
            args.Add("--fullscreen");

        if (!string.IsNullOrWhiteSpace(options.WindowTitle))
            args.Add($"--window-title={Quote(options.WindowTitle.Trim())}");

        if (!string.IsNullOrWhiteSpace(options.RecordPath))
            args.Add($"--record={Quote(options.RecordPath.Trim())}");

        if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
            args.Add(options.ExtraArguments.Trim());

        return string.Join(" ", args);
    }

    /// <summary>
    /// 把子进程放进一个「随本进程一起结束」的 Windows 作业对象。
    ///
    /// 正常退出时 App.OnExit 里的 StopRunning 就够了；但程序被任务管理器强杀、
    /// 或者崩到连退出代码都没跑完时，只有作业对象能保证 scrcpy 不留在桌面上。
    /// 句柄故意一直不释放（进程结束时由系统回收），否则它会提前把 scrcpy 带走。
    /// </summary>
    private static class ChildProcessJob
    {
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint LimitKillOnJobClose               = 0x2000;

        private static readonly IntPtr Handle = Create();

        public static void Add(Process process)
        {
            try
            {
                if (Handle == IntPtr.Zero) return;
                AssignProcessToJobObject(Handle, process.Handle);
            }
            catch
            {
                // 加不进去不影响投屏本身，退回只有 StopRunning 的普通路径
            }
        }

        private static IntPtr Create()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = LimitKillOnJobClose,
                    },
                };

                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr  = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                return job;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long    PerProcessUserTimeLimit;
            public long    PerJobUserTimeLimit;
            public uint    LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint    ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint    PriorityClass;
            public uint    SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS                       IoInfo;
            public UIntPtr                           ProcessMemoryLimit;
            public UIntPtr                           JobMemoryLimit;
            public UIntPtr                           PeakProcessMemoryUsed;
            public UIntPtr                           PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, uint infoClass,
                                                          IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    /// <summary>给命令行参数加引号；结尾的反斜杠要翻倍，否则会把引号转义掉。</summary>
    private static string Quote(string value)
    {
        if (!value.Contains(' ')) return value;

        var escaped = value.EndsWith("\\", StringComparison.Ordinal)
            ? value + "\\"
            : value;

        return $"\"{escaped}\"";
    }
}
