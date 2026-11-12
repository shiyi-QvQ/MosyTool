using System;
using System.IO;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// 安卓通用 / 扩展功能。
///
/// 与参考实现的差别：参考把整段 shell 压进一行带三四层转义的字符串，
/// 这里改成「本地生成脚本 → push → sh 执行 → 删除」，脚本是裸文本，加减一行都不用重新数转义。
/// </summary>
public static class AndroidGeneralService
{
    private const int DefaultTimeoutMs = 20000;
    private const int LongTimeoutMs    = 30000;

    public const string SlotA = "a";
    public const string SlotB = "b";

    // ==================== Fastboot 侧 ====================

    /// <summary>双清：erase userdata + erase metadata + fastboot -w。</summary>
    public static async Task<(bool Ok, string Message)> FactoryResetAsync(Action<string> log)
    {
        if (!FastbootService.IsAvailable)
            return (false, "未检测到 fastboot 设备，请先让手机进入 Fastboot / FastbootD 模式。");

        var allOk = true;
        foreach (var step in new[] { "erase userdata", "erase metadata", "-w" })
        {
            log($"> fastboot {step}");
            if (!await FastbootService.RunRawAsync(step, log)) allOk = false;
        }

        return allOk
            ? (true, "双清完成：userdata / metadata 已清空，首次开机需要重新激活。")
            : (false, "双清过程中有命令失败，请查看日志。");
    }

    /// <summary>擦除谷歌锁（FRP）分区。多数机型要在 FastbootD 里才能擦。</summary>
    public static async Task<(bool Ok, string Message)> EraseFrpAsync(Action<string> log)
    {
        if (!FastbootService.IsAvailable) return (false, "未检测到 fastboot 设备。");

        if (!FastbootService.IsUserspace())
            log("提示：FRP 通常只在 FastbootD 可擦，失败请先执行 fastboot reboot fastboot。");

        log("> fastboot erase frp");
        var ok = await FastbootService.RunRawAsync("erase frp", log);

        return ok
            ? (true, "谷歌锁（FRP）已擦除。")
            : (false, "擦除失败：分区名可能不叫 frp，或当前模式不支持。");
    }

    /// <summary>切换活动槽位（仅 A/B 设备）。</summary>
    public static async Task<(bool Ok, string Message)> SetActiveSlotAsync(string slot, Action<string> log)
    {
        if (!FastbootService.IsAvailable) return (false, "未检测到 fastboot 设备。");

        log($"> fastboot set_active {slot}");
        var ok = await FastbootService.RunRawAsync($"set_active {slot}", log);

        return ok
            ? (true, $"已切到 {slot.ToUpperInvariant()} 槽，重启后从该槽引导。")
            : (false, "切换失败：设备可能不是 A/B 双槽结构。");
    }

    // ==================== ADB 侧 ====================

    /// <summary>设备是否已授予 shell root（su）。</summary>
    public static async Task<bool> HasRootAsync(string serial)
    {
        var output = await ShellAsync(serial, "su -c \"echo ROOT_OK\"");
        return output.Contains("ROOT_OK", StringComparison.Ordinal);
    }

    /// <summary>
    /// 修复 ADB 运行环境。/data/local/tmp 的权限或 SELinux 上下文被改乱后，
    /// 典型症状是投屏起不来、push 报 permission denied，这里把它重建回干净状态。
    /// </summary>
    public static async Task<(bool Ok, string Message)> FixAdbAsync(string serial, Action<string> log)
    {
        var hasRoot = await HasRootAsync(serial);
        if (!hasRoot) log("提示：没有 root 权限，只能按当前权限做有限修复。");

        string[] steps = hasRoot
            ?
            [
                "su -c \"rm -rf /data/local/tmp/\"",
                "su -c \"mkdir -p /data/local/tmp/\"",
                "su -c \"chmod 777 /data/local/tmp/\"",
                "su -c \"chcon -R u:object_r:shell_data_file:s0 /data/local/tmp\"",
            ]
            :
            [
                "rm -rf /data/local/tmp/*",
                "mkdir -p /data/local/tmp/",
            ];

        foreach (var step in steps)
        {
            log($"> {step}");
            await ShellAsync(serial, step);
        }

        log("ADB 修复完成，重新打开投屏/文件推送即可。");
        return (true, "ADB 运行环境已重建。");
    }

    /// <summary>读一加 7/8/9 系列的 DDR 类型。</summary>
    public static async Task<(bool Ok, string Message)> ReadDdrAsync(string serial)
    {
        var value = (await ShellAsync(serial, "getprop ro.boot.ddr_type")).Trim();

        return value switch
        {
            ""  => (true, "设备未定义 DDR 类型，通常不是一加 7/8/9 系列。"),
            "0" => (true, "DDR4"),
            "1" => (true, "DDR5"),
            _   => (true, $"未识别的 DDR 类型：{value}"),
        };
    }

    /// <summary>强开 / 关闭基带调试端口（需要 root）。开启后设备会多暴露一个诊断串口。</summary>
    public static async Task<(bool Ok, string Message)> SetDiagPortAsync(string serial, bool enable)
    {
        var config = enable ? "diag,adb" : "adb";
        var output = await ShellAsync(serial, $"su -c \"setprop sys.usb.config {config}\"", LongTimeoutMs);

        if (LooksLikeFailure(output)) return (false, FirstLine(output));

        return (true, enable ? "基带调试端口已开启。" : "已恢复为仅 adb 模式。");
    }

    /// <summary>切换 USB 功能：mtp（文件传输）/ ptp（相机）/ none（仅充电）。</summary>
    public static async Task<(bool Ok, string Message)> SetUsbFunctionAsync(string serial, string function)
    {
        var output = await ShellAsync(serial, $"svc usb setFunctions {function}");

        if (LooksLikeFailure(output)) return (false, FirstLine(output));

        var label = function switch { "mtp" => "文件传输（MTP）", "ptp" => "相机（PTP）", _ => "仅充电" };
        return (true, $"USB 模式已切换为{label}。");
    }

    /// <summary>
    /// 强开小米 USB 安全设置（需要 root）：打开 persist.security.adbinput 与 persist.fastboot.enable，
    /// 再把安全中心里的「USB 安装」「安装拦截」两个开关改成允许，然后重启安全中心进程。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnableMiuiUsbAsync(string serial, Action<string> log)
    {
        if (!await HasRootAsync(serial))
            return (false, "该功能需要 root：请先在设备上授予 Shell root 权限。");

        const string remote = "/data/local/tmp/mosy_miui_usb.sh";
        var local = Path.Combine(Path.GetTempPath(), "mosy_miui_usb.sh");

        // 设备端是 toybox sh + GNU sed，换行必须是 LF
        await File.WriteAllTextAsync(local, MiuiUsbScript.Replace("\r\n", "\n"));

        log($"> adb push mosy_miui_usb.sh → {remote}");
        var pushOutput = await AdbAsync(serial, $"push \"{local}\" {remote}", LongTimeoutMs);
        if (LooksLikeFailure(pushOutput))
            return (false, $"推送脚本失败：{FirstLine(pushOutput)}");

        log($"> su -c sh {remote}");
        var output = await ShellAsync(serial, $"su -c 'sh {remote}'", LongTimeoutMs);
        log(output.Trim());

        await ShellAsync(serial, $"rm -f {remote}");

        if (LooksLikeFailure(output)) return (false, FirstLine(output));
        if (output.Contains("PREF_MISSING", StringComparison.Ordinal))
            return (true, "USB 调试与 Fastboot 常驻已开启；没有找到安全中心配置文件（可能未安装），重启后生效。");

        return (true, "小米 USB 安全设置已强开，重启设备后生效。");
    }

    /// <summary>
    /// 设备端脚本。放裸文本是有意的：参考实现把这段压成一行、四层转义，
    /// 只要 sed 里少一个反斜杠就会静默改错文件。
    /// </summary>
    private const string MiuiUsbScript = """
        setprop persist.security.adbinput 1
        setprop persist.fastboot.enable 1

        FS=/data/data/com.miui.securitycenter/shared_prefs/remote_provider_preferences.xml
        if [ -f "$FS" ]; then
          if grep -q security_adb_install_enable "$FS"; then
            sed -i 's/security_adb_install_enable" value="false"/security_adb_install_enable" value="true"/' "$FS"
          else
            sed -i '3i\    <boolean name="security_adb_install_enable" value="true" />' "$FS"
          fi

          if grep -q permcenter_install_intercept_enabled "$FS"; then
            sed -i 's/permcenter_install_intercept_enabled" value="true"/permcenter_install_intercept_enabled" value="false"/' "$FS"
          else
            sed -i '3i\    <boolean name="permcenter_install_intercept_enabled" value="false" />' "$FS"
          fi
          echo "PREF_OK"
        else
          echo "PREF_MISSING"
        fi

        PID=$(pidof com.miui.securitycenter.remote)
        if [ -n "$PID" ]; then kill -9 "$PID"; echo "KILLED $PID"; fi
        echo "SCRIPT_DONE"
        """;

    // ==================== 底层封装 ====================

    private static async Task<string> ShellAsync(string serial, string shellCommand, int timeoutMs = DefaultTimeoutMs)
        => await AdbAsync(serial, $"shell {shellCommand}", timeoutMs);

    private static async Task<string> AdbAsync(string serial, string arguments, int timeoutMs = DefaultTimeoutMs)
    {
        if (!AdbService.IsAdbAvailable) return "Error: adb 不可用";

        return await Task.Run(
            () => AdbService.Run(AdbService.AdbPath!, $"-s {serial} {arguments}", timeoutMs));
    }

    private static bool LooksLikeFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] markers = ["error:", "exception", "denied", "not found", "no such", "unknown", "failure"];
        return Array.Exists(markers, m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "设备没有返回结果。";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line.Length > 200 ? line[..200] : line;
        }

        return "未知错误";
    }
}
