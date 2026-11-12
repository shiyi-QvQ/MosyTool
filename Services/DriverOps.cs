using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Mosytool.Services;

/// <summary>
/// 真正的驱动操作，跑在提权后的自身进程里（普通进程点按钮 → 弹 UAC → 本类执行一次就退出）。
///
/// 两条通路：
///   pnputil  —— 装驱动、卸载/禁用/启用设备、扫描硬件改动，都是系统自带的命令行；
///   newdev   —— UpdateDriverForPlugAndPlayDevices，把指定 INF 强制绑到一个设备上，
///               这是"把端口切成 WinUSB"能成立的关键（pnputil 没有对应命令）。
///
/// 官方文档里 Zadig 那类工具做的就是这件事：不新增驱动二进制，只把系统 in-box 的
/// winusb.inf 绑到目标设备上，所以不需要额外的签名证书。
/// </summary>
internal static class DriverOps
{
    public const string OpArg     = "--driver-op";
    public const string PortArg   = "--port";
    public const string ResultArg = "--driver-result";

    /// <summary>让系统跳过硬件 ID 匹配，直接用给定 INF 安装。</summary>
    private const uint InstallFlagForce = 0x00000001;

    private static string PnPUtilPath =>
        Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>
    /// 启动时的命令行分支。返回 true 表示这是一次驱动操作（执行完必须直接退出，
    /// 不能创建主窗口，否则会闪出一个完整的界面）。
    /// </summary>
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;

        var op = Value(args, OpArg);
        if (string.IsNullOrWhiteSpace(op)) return false;

        var port       = Value(args, PortArg);
        var resultPath = Value(args, ResultArg);

        DriverOpResult result;
        try
        {
            result = Execute(op, port);
        }
        catch (Exception ex)
        {
            result = DriverOpResult.Fail(ex.Message);
        }

        // 结果走临时文件回传：Verb=runas 必须 UseShellExecute=true，拿不到子进程的 stdout
        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            try
            {
                File.WriteAllText(resultPath, (result.Success ? "OK" : "FAIL") + "\n" + result.Message);
            }
            catch
            {
                // 写不进去时父进程会按"没有结果文件"处理
            }
        }

        exitCode = result.Success ? 0 : 1;
        return true;
    }

    private static DriverOpResult Execute(string op, string? instanceId) => op switch
    {
        nameof(DriverOpKind.SwitchWinUsb) => SwitchWinUsb(instanceId),
        nameof(DriverOpKind.RemoveDevice) => ThroughPnPUtil("remove-device", instanceId,
                                            "已卸载该设备，请拔插一次数据线让系统重新识别。"),
        nameof(DriverOpKind.DisableDevice) => ThroughPnPUtil("disable-device", instanceId,
                                            "已禁用该设备。"),
        nameof(DriverOpKind.EnableDevice) => ThroughPnPUtil("enable-device", instanceId,
                                            "已启用该设备。"),
        nameof(DriverOpKind.ScanDevices)  => ScanDevices(),
        nameof(DriverOpKind.LoadLocalInfs) => LoadLocalInfs(),
        _ => DriverOpResult.Fail($"未知操作：{op}"),
    };

    // ---------- 切 WinUSB ----------

    private static DriverOpResult SwitchWinUsb(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return DriverOpResult.Fail("没有选中端口。");

        var winUsbInf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf", "winusb.inf");

        if (!File.Exists(winUsbInf))
            return DriverOpResult.Fail("系统里找不到 winusb.inf，无法切换到 WinUSB。");

        // 先把它包装进驱动仓库。已经装过时 pnputil 会返回非 0（"已存在"），不影响后续步骤
        Run(PnPUtilPath, $"/add-driver \"{winUsbInf}\" /install");

        if (UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, instanceId, winUsbInf, InstallFlagForce, out _))
        {
            return DriverOpResult.Ok("已切换为 WinUSB。若设备没有立刻可用，请拔插一次数据线。");
        }

        var code = Marshal.GetLastWin32Error();

        return DriverOpResult.Fail(
            $"切换失败：{DescribeWin32(code)}（错误 {code}）。\n" +
            "可以试试：把该机型的驱动（.inf/.sys/.cat）放进驱动目录后重新装载，再切一次。");
    }

    private static string DescribeWin32(int code) => code switch
    {
        5   => "拒绝访问，提权没有生效",
        87  => "系统不接受系统自带的 winusb.inf 参数",
        1326 => "登录凭据无效",
        0x000004BB => "驱动被阻止策略拦截",
        _   => new Win32Exception(code).Message,
    };

    // ---------- 其余操作 ----------

    private static DriverOpResult ThroughPnPUtil(string verb, string? instanceId, string okMessage)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return DriverOpResult.Fail("没有选中端口。");

        var code = Run(PnPUtilPath, $"/{verb} /instanceid \"{instanceId}\"");
        if (code != 0)
            return DriverOpResult.Fail($"pnputil /{verb} 失败（退出码 {code}）。");

        return DriverOpResult.Ok(okMessage);
    }

    private static DriverOpResult ScanDevices()
    {
        var code = Run(PnPUtilPath, "/scan-devices");
        return code == 0
            ? DriverOpResult.Ok("已重新扫描硬件改动。")
            : DriverOpResult.Fail($"pnputil /scan-devices 失败（退出码 {code}）。");
    }

    /// <summary>把驱动目录里的 .inf 全部装进系统驱动仓库（同目录的 .sys/.cat 会一起被带走）。</summary>
    private static DriverOpResult LoadLocalInfs()
    {
        List<string> infs;
        try
        {
            infs = Directory.EnumerateFiles(DriverService.DriversDir, "*.inf", SearchOption.AllDirectories)
                            .ToList();
        }
        catch (Exception ex)
        {
            return DriverOpResult.Fail("读取驱动目录失败：" + ex.Message);
        }

        if (infs.Count == 0)
            return DriverOpResult.Fail($"驱动目录里没有 .inf 文件：\n{DriverService.DriversDir}");

        var failed = new List<string>();
        foreach (var inf in infs)
        {
            if (Run(PnPUtilPath, $"/add-driver \"{inf}\" /install") != 0)
                failed.Add(Path.GetFileName(inf));
        }

        if (failed.Count == 0)
            return DriverOpResult.Ok($"已装载 {infs.Count} 个驱动包。");

        return failed.Count == infs.Count
            ? DriverOpResult.Fail("全部驱动包都装载失败，请确认文件完整且与系统位数一致。")
            : DriverOpResult.Ok($"成功 {infs.Count - failed.Count} 个，失败：{string.Join("、", failed)}。");
    }

    // ---------- 底层 ----------

    /// <summary>
    /// 跑一个系统命令并拿退出码。
    ///
    /// 刻意不重定向输出：pnputil 按控制台 OEM 代码页输出中文，进程内按 UTF-8 解码会变成
    /// 乱码，读回来也只会污染错误提示。这里只用退出码判断成败，正文由本类自己写。
    /// </summary>
    private static int Run(string file, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });

            if (proc is null) return -1;

            proc.WaitForExit(60_000);
            return proc.HasExited ? proc.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>取 --xxx value 形式的参数值。</summary>
    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
}
