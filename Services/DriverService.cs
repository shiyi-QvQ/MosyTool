using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>设备管理器里那个黄色感叹号对应的含义。</summary>
public enum DriverIssue
{
    /// <summary>驱动正常。</summary>
    Ok,

    /// <summary>代码 28：没有为该设备安装驱动程序。</summary>
    NoDriver,

    /// <summary>代码 10：设备无法启动。</summary>
    StartFailed,

    /// <summary>代码 22：设备被禁用。</summary>
    Disabled,

    /// <summary>代码 52：无法验证驱动程序的数字签名。</summary>
    Unsigned,

    /// <summary>其它问题码，原样显示在界面上。</summary>
    Unknown,
}

/// <summary>端口状态圆点的颜色档位。</summary>
public enum DriverLevel
{
    /// <summary>绿：驱动正常，且本工具能直接读写。</summary>
    Ok,

    /// <summary>黄：设备本身没坏，但驱动与工具不匹配，或设备被禁用。</summary>
    Warn,

    /// <summary>红：没装驱动 / 启动失败 / 签名无效。</summary>
    Error,
}

/// <summary>
/// 设备管理器里的一个 USB 端口，以及它当前挂在哪个驱动上。
///
/// 为什么需要这个：本工具的高通（qualcomm_ffi / nusb）和 MTK 原生层都是直接对着
/// 设备端点读写的，Windows 上只有 WinUSB / libusbK / libusb0 三种驱动允许用户态程序
/// 这么做。设备若挂在厂商驱动（qcusbser / usbser 之类）上，原生层只会报「打开失败」，
/// 用户看到的就是「设备明明连着，工具却说找不到」。
/// </summary>
public sealed class UsbPort
{
    /// <summary>设备实例 ID，形如 USB\VID_05C6&amp;PID_9008\5&amp;2A3B4C5D&amp;0&amp;2。</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>设备管理器里显示的名字。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>最具体的那条硬件 ID，形如 USB\VID_05C6&amp;PID_9008&amp;REV_0000。</summary>
    public string HardwareId { get; init; } = string.Empty;

    /// <summary>当前绑定的驱动服务名：WinUSB / libusbK / usbser / qcusbser…。为空表示没装驱动。</summary>
    public string? Service { get; init; }

    /// <summary>厂商（WMI Manufacturer）。</summary>
    public string? Manufacturer { get; init; }

    public ushort Vid { get; init; }

    public ushort Pid { get; init; }

    /// <summary>对应设备管理器里的「代码 N」。</summary>
    public int ProblemCode { get; init; }

    /// <summary>形如 "05C6:9008"。</summary>
    public string Id => Vid == 0 ? "----:----" : $"{Vid:X4}:{Pid:X4}";

    /// <summary>端口用途，命中内置端口表时是具体型号，否则按 VID 归类。</summary>
    public string Kind
    {
        get
        {
            var known = DriverService.KnownPorts.FirstOrDefault(p => p.Vid == Vid && p.Pid == Pid);
            if (!string.IsNullOrEmpty(known.Name)) return known.Name;

            return Vid switch
            {
                0x05C6 => "高通端口",
                0x0E8D or 0x1004 or 0x22D9 or 0x0FCE => "联发科端口",
                0x18D1 or 0x2717 or 0x2A70 or 0x0BB4 or 0x22B8 or 0x12D1 or 0x04E8 or 0x2207 => "手机端口",
                _ => "其它 USB 设备",
            };
        }
    }

    /// <summary>端口是否以"用户态可读写"的驱动呈现（WinUSB / libusbK / libusb0）。</summary>
    public bool DriverUsable => DriverService.IsUsableService(Service);

    public DriverIssue Issue => ProblemCode switch
    {
        0  => DriverIssue.Ok,
        28 => DriverIssue.NoDriver,
        10 => DriverIssue.StartFailed,
        22 => DriverIssue.Disabled,
        52 => DriverIssue.Unsigned,
        _  => ProblemCode == 45 ? DriverIssue.Ok : DriverIssue.Unknown,
    };

    /// <summary>状态圆点旁的文字。</summary>
    public string StatusText => Issue switch
    {
        DriverIssue.Ok          => DriverUsable ? "已就绪" : "驱动正常",
        DriverIssue.NoDriver    => "未安装驱动（代码 28）",
        DriverIssue.StartFailed => "无法启动（代码 10）",
        DriverIssue.Disabled    => "已禁用（代码 22）",
        DriverIssue.Unsigned    => "签名无效（代码 52）",
        _                       => $"异常（代码 {ProblemCode}）",
    };

    /// <summary>是否建议把它换成 WinUSB：本工具的原生层要用，而当前驱动不是那三种。</summary>
    public bool NeedsWinUsb => !DriverUsable && DriverService.NeedsWinUsb(this);

    /// <summary>列表第二行的信息串：端口用途 · VID:PID · 当前驱动 · 状态。</summary>
    public string Summary => $"{Kind} · {Id} · 驱动 {Service ?? "未安装"} · {StatusText}";

    /// <summary>状态圆点的颜色档位（绿 / 黄 / 红），界面据此换画刷。</summary>
    public DriverLevel Level => Issue switch
    {
        DriverIssue.NoDriver    => DriverLevel.Error,
        DriverIssue.StartFailed => DriverLevel.Error,
        DriverIssue.Unsigned    => DriverLevel.Error,
        DriverIssue.Disabled    => DriverLevel.Warn,
        _                       => DriverUsable ? DriverLevel.Ok : DriverLevel.Warn,
    };

    /// <summary>禁用 / 启用那个按钮该显示什么。</summary>
    public string ToggleText => Issue == DriverIssue.Disabled ? "启用" : "禁用";

    /// <summary>界面右侧的提示语，没问题时为空。</summary>
    public string Hint
    {
        get
        {
            if (NeedsWinUsb)
                return "当前驱动不允许工具直接读写，建议切换为 WinUSB";

            return Issue switch
            {
                DriverIssue.NoDriver    => "设备没有可用驱动，可切换为 WinUSB 后使用",
                DriverIssue.StartFailed => "驱动已安装但启动失败，可尝试重新安装或卸载后重插",
                DriverIssue.Unsigned    => "驱动签名无法验证，需更换为已签名驱动",
                _                       => string.Empty,
            };
        }
    }
}

/// <summary>
/// 端口驱动转换：看设备挂在什么驱动上，并在需要时把它换成 WinUSB。
///
/// 所有会写系统的动作（装驱动、卸载设备、禁用启用）都必须管理员权限，而本程序平时是
/// 普通权限启动的，所以这里不自己动手，而是把动作交给 <see cref="DriverOps"/> 在
/// 提权后的自身进程里执行（见 <see cref="RunElevatedAsync"/>）。
/// </summary>
public static class DriverService
{
    /// <summary>
    /// 内置端口表。与 <see cref="PenumbraService"/> 里的那份是同一批机型，
    /// 但那份是 MTK 专用（只认 MTK 几个 VID），这里还认高通的 9008。
    /// </summary>
    internal static readonly (ushort Vid, ushort Pid, string Name, bool NeedsWinUsb)[] KnownPorts =
    {
        (0x05C6, 0x9008, "高通 9008 (EDL)", true),
        (0x05C6, 0x900E, "高通 900E (EDL)", true),
        (0x0E8D, 0x0003, "MTK BROM",        true),
        (0x0E8D, 0x2000, "MTK Preloader",   false),
        (0x0E8D, 0x2001, "MTK Preloader",   false),
        (0x1004, 0x61F1, "LG Preloader",    false),
        (0x22D9, 0x2769, "OPPO BROM",       true),
        (0x0FCE, 0xD1DD, "Sony BROM",       true),
    };

    /// <summary>能不能被本工具的原生层直接打开。</summary>
    internal static bool IsUsableService(string? service) =>
        service is not null &&
        (service.Equals("WinUSB",  StringComparison.OrdinalIgnoreCase) ||
         service.Equals("libusbK", StringComparison.OrdinalIgnoreCase) ||
         service.Equals("libusb0", StringComparison.OrdinalIgnoreCase) ||
         service.Equals("usbhub3", StringComparison.OrdinalIgnoreCase));

    /// <summary>这个端口是否属于"本工具需要，但驱动不对"的情形。</summary>
    internal static bool NeedsWinUsb(UsbPort port)
    {
        // 端口表里标注需要 WinUSB 的（9008 / BROM 这类 vendor-specific 端口）
        var known = KnownPorts.FirstOrDefault(p => p.Vid == port.Vid && p.Pid == port.Pid);
        if (!string.IsNullOrEmpty(known.Name)) return known.NeedsWinUsb;

        // 表外的端口：只要它是个厂商自定义端口、且驱动是串口那类，就建议换
        return port.Service is not null &&
               (port.Service.StartsWith("qc", StringComparison.OrdinalIgnoreCase) ||
                port.Service.Equals("usbser", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>当前进程是否已经是管理员。界面据此决定要不要弹 UAC。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>用户自备驱动的存放目录（tools\drivers，不存在时自动建）。</summary>
    public static string DriversDir
    {
        get
        {
            var dir = Path.Combine(ToolPaths.Dir, "drivers");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>
    /// 枚举所有 USB 端口。走 WMI 而不是 SetupAPI，因为原生层和 MTK 页本来就在用
    /// Win32_PnPEntity（见 PenumbraService.DriverOf），保持一致且无需 P/Invoke。
    /// 这个查询要一两百毫秒，调用方应当放到后台线程。
    /// </summary>
    public static List<UsbPort> EnumeratePorts()
    {
        var ports = new List<UsbPort>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Service, ConfigManagerErrorCode, HardwareID, Manufacturer " +
                "FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\%'");

            foreach (var obj in searcher.Get())
            {
                var instanceId = obj["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(instanceId)) continue;

                var hardwareIds = (obj["HardwareID"] as string[]) ?? Array.Empty<string>();
                var hardwareId  = hardwareIds.FirstOrDefault() ?? instanceId;

                if (!TryParseVidPid(hardwareId, out var vid, out var pid)) continue;

                var service = obj["Service"]?.ToString();
                if (string.IsNullOrWhiteSpace(service)) service = null;

                var problem = 0;
                try { problem = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0); } catch { }

                ports.Add(new UsbPort
                {
                    InstanceId   = instanceId,
                    Name         = obj["Name"]?.ToString() ?? instanceId,
                    HardwareId   = hardwareId,
                    Service      = service,
                    Manufacturer = obj["Manufacturer"]?.ToString(),
                    Vid          = vid,
                    Pid          = pid,
                    ProblemCode  = problem,
                });
            }
        }
        catch
        {
            // WMI 不可用（极少见）时返回空列表，界面会如实显示"未发现端口"
        }

        // 本工具要用到的端口排前面，其次按 VID/PID 排，保证刷新后顺序稳定不跳动
        return ports
            .OrderByDescending(p => !string.IsNullOrEmpty(KnownPorts.FirstOrDefault(k => k.Vid == p.Vid && k.Pid == p.Pid).Name))
            .ThenByDescending(p => p.NeedsWinUsb)
            .ThenBy(p => p.Vid)
            .ThenBy(p => p.Pid)
            .ToList();
    }

    /// <summary>从硬件 ID / 实例 ID 里抠出 VID 和 PID。</summary>
    internal static bool TryParseVidPid(string text, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;

        var m = Regex.Match(text ?? string.Empty, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})",
                            RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        return ushort.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    /// <summary>打开自备驱动目录，并把该放什么讲清楚。</summary>
    public static void OpenDriversDir()
    {
        var dir = DriversDir;
        try
        {
            Directory.CreateDirectory(dir);

            // 放一份说明在目录里：这个目录是给用户丢文件的，空目录会让人不知道该干什么
            var readme = Path.Combine(dir, "说明.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "把要安装的驱动解压到这个目录即可（可以带子目录）。\r\n\r\n" +
                    "程序会扫描下面所有 .inf 文件，连同同目录的 .sys / .cat 一起装进系统的\r\n" +
                    "驱动仓库，然后再绑到选中的端口上。\r\n\r\n" +
                    "常见驱动：\r\n" +
                    "  高通 9008  QDLoader / qcusb  驱动\r\n" +
                    "  MTK        VCOM / Preloader  驱动\r\n" +
                    "  libusbK / libusb-win32       驱动（Zadig 同款）\r\n");
            }

            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不算错误，用户自己找得到路径
        }
    }

    /// <summary>目录里一共有几个 .inf。</summary>
    public static int CountLocalInfs()
    {
        try
        {
            return Directory.EnumerateFiles(DriversDir, "*.inf", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 在提权进程里执行一次驱动操作，并把结果拿回来。
    ///
    /// 为什么是"重新拉起自己"：Verb=runas 必须 UseShellExecute=true，这样就没法重定向
    /// 子进程的输出，所以结果不走管道，而是让子进程写进一个临时文件，父进程读完删掉。
    /// </summary>
    public static async Task<DriverOpResult> RunElevatedAsync(DriverOpKind kind, UsbPort? port = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return DriverOpResult.Fail("拿不到程序路径，无法提权执行。");

        var resultFile = Path.Combine(Path.GetTempPath(),
                                      $"mosytool-driver-{Guid.NewGuid():N}.txt");

        var args = $"{DriverOps.OpArg} {kind}";
        if (port is not null) args += $" {DriverOps.PortArg} \"{port.InstanceId}\"";
        args += $" {DriverOps.ResultArg} \"{resultFile}\"";

        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                Verb           = "runas",   // 弹 UAC
                UseShellExecute = true,
            });

            if (proc is null)
                return DriverOpResult.Fail("提权进程没有启动。");

            await proc.WaitForExitAsync();

            if (File.Exists(resultFile))
            {
                var text = await File.ReadAllTextAsync(resultFile);
                return DriverOpResult.Parse(text);
            }

            // 没有结果文件：子进程在写出结果之前就退了（通常是在 UAC 之后自身崩溃）
            return proc.ExitCode == 0
                ? DriverOpResult.Ok("操作已完成。")
                : DriverOpResult.Fail($"操作失败（退出码 {proc.ExitCode}）。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 弹窗上点了"否"
            return DriverOpResult.Fail("已取消：该操作需要管理员权限。");
        }
        catch (Exception ex)
        {
            return DriverOpResult.Fail("执行失败：" + ex.Message);
        }
        finally
        {
            try { File.Delete(resultFile); } catch { }
        }
    }
}

/// <summary>可以执行的一次驱动操作。</summary>
public enum DriverOpKind
{
    /// <summary>把端口切换到系统自带的 WinUSB 驱动。</summary>
    SwitchWinUsb,

    /// <summary>卸载设备节点（连驱动软件一起删），用于把装乱的驱动清干净。</summary>
    RemoveDevice,

    /// <summary>禁用设备。</summary>
    DisableDevice,

    /// <summary>启用设备。</summary>
    EnableDevice,

    /// <summary>扫描硬件改动，让系统重新认一遍设备。</summary>
    ScanDevices,

    /// <summary>把 tools\drivers 里的所有 .inf 装进驱动仓库。</summary>
    LoadLocalInfs,
}

/// <summary>一次驱动操作的结果。</summary>
public sealed class DriverOpResult
{
    /// <summary>操作是否成功。</summary>
    public bool Success { get; private init; }

    /// <summary>给用户看的一句话说明（成功时是结果，失败时是原因）。</summary>
    public string Message { get; private init; } = string.Empty;

    public static DriverOpResult Ok(string message)
        => new() { Success = true, Message = message };

    public static DriverOpResult Fail(string message)
        => new() { Success = false, Message = message };

    /// <summary>子进程把结果按 "OK/FAIL + 换行 + 正文" 写文件，这里还原。</summary>
    internal static DriverOpResult Parse(string text)
    {
        var index = text.IndexOf('\n');
        var head  = index < 0 ? text : text[..index];
        var body  = index < 0 ? string.Empty : text[(index + 1)..].Trim();

        return head.Trim().StartsWith("OK", StringComparison.OrdinalIgnoreCase)
            ? Ok(body.Length == 0 ? "操作已完成。" : body)
            : Fail(body.Length == 0 ? "操作失败。" : body);
    }
}
