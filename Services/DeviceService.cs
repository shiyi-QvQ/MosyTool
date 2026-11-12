using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Mosytool.Models;

namespace Mosytool.Services;

public static class DeviceService
{
    public static DeviceInfo Detect()
    {
        var info = new DeviceInfo();

        // 9008 (高通 EDL 紧急下载模式)
        if (IsEdlMode())
        {
            info.State     = "9008 模式";
            info.StateKind = "edl";
            return info;
        }

        // MTK BROM / Preloader（联发科）：此模式下 adb / fastboot 均不可用，
        // 只能由 penumbra（内置原生层）通过 USB 与设备通信
        var mtkPort = PenumbraService.EnumeratePorts()
                                      .FirstOrDefault(p => p.Online && p.Recognized);
        if (mtkPort is not null)
        {
            info.State     = $"{mtkPort.Mode} 模式";
            info.StateKind = "mtk";
            return info;
        }

        // Fastboot 模式
        if (AdbService.IsFastbootAvailable)
        {
            var fb = AdbService.Run(AdbService.FastbootPath!, "devices").Trim();
            var serial = ParseFastbootSerial(fb);
            if (serial is not null)
            {
                info.Serial    = serial;
                info.State     = "Fastboot";
                info.StateKind = "fastboot";
                return info;
            }
        }

        // adb 正常连接
        if (AdbService.IsAdbAvailable)
        {
            var list = AdbService.Run(AdbService.AdbPath!, "devices").Trim();
            var (serial, state) = ParseAdbDevices(list);

            if (serial is not null)
            {
                info.Serial = serial;
                switch (state)
                {
                    case "unauthorized":
                        info.State     = "未授权调试";
                        info.StateKind = "unauthorized";
                        return info;

                    case "offline":
                        info.State     = "离线";
                        info.StateKind = "offline";
                        return info;

                    default:
                        info.State     = "已连接";
                        info.StateKind = "online";
                        FillFromAdb(info, serial);
                        return info;
                }
            }
        }

        info.State = AdbService.IsAdbAvailable ? "未连接设备" : "未检测到 adb";
        info.StateKind = "disconnected";
        return info;
    }

    private static void FillFromAdb(DeviceInfo info, string serial)
    {
        // 合并成一次 adb shell：原来要起 6 个 adb 进程，现在 1 个，5 秒轮询轻很多
        const string script =
            "getprop; echo =====WM=====; wm size; echo =====BAT=====; dumpsys battery; " +
            "echo =====DF=====; df /data; echo =====MEM=====; cat /proc/meminfo; " +
            "echo =====KRN=====; uname -r; echo =====PART=====; cat /proc/partitions";

        var raw = AdbService.Run(AdbService.AdbPath!, $"-s {serial} shell {script}");

        var props   = Section(raw, "WM", head: true);
        var wm      = Section(raw, "WM");
        var battery = Section(raw, "BAT");
        var df      = Section(raw, "DF");
        var mem     = Section(raw, "MEM");
        var kernel  = Section(raw, "KRN");
        var parts   = Section(raw, "PART");

        info.Name     = GetProp(props, "ro.product.marketname") ?? GetProp(props, "ro.product.model") ?? "-";
        info.Codename = GetProp(props, "ro.product.device") ?? GetProp(props, "ro.product.name") ?? "-";

        var release = GetProp(props, "ro.build.version.release");
        var sdk     = GetProp(props, "ro.build.version.sdk");
        info.AndroidVersion = release is null
            ? "-"
            : (sdk is null ? $"Android {release}" : $"Android {release} · API {sdk}");

        info.Hardware = GetProp(props, "ro.hardware") ?? "-";
        info.Cpu      = GetProp(props, "ro.board.platform") ?? GetProp(props, "ro.soc.model") ?? "-";

        // BL 锁状态：不用进 fastboot，系统里就能读出来
        info.BootloaderUnlocked = ParseUnlocked(props);

        // 系统版本：优先厂商 ROM 版本号，退回系统构建版本
        info.RomVersion = GetProp(props, "ro.build.display.id")
                       ?? GetProp(props, "ro.build.version.incremental") ?? "-";

        // 构建：指纹最完整，退回 build id
        info.BuildNumber = GetProp(props, "ro.build.fingerprint")
                        ?? GetProp(props, "ro.build.id") ?? "-";

        info.KernelVersion = kernel.Trim().Length > 0 ? kernel.Trim() : "-";

        info.Resolution = ParseWmSize(wm) ?? "-";

        if (ParseBattery(battery, out var level, out var charging))
        {
            info.BatteryPercent  = level;
            info.BatteryCharging = charging;
            info.Battery         = charging ? $"{level}% · 充电中" : $"{level}%";
        }

        // 存储：口径与手机设置一致 —— 已用 = 分区总量 - 可用
        // （df 的 used 字段不计 root 保留块，会比手机显示的"已用"偏小）
        if (ParseSize(df, out var totalKb, out _, out var availKb) && totalKb > 0)
        {
            info.StorageTotalGb  = totalKb / 1024.0 / 1024.0;
            info.StorageFreeGb   = availKb / 1024.0 / 1024.0;
            info.StorageUsedGb   = Math.Max(0, info.StorageTotalGb - info.StorageFreeGb);
            info.InternalStorage = $"已用 {info.StorageUsedGb:0.##} / 共 {info.StorageTotalGb:0.##} GB";
        }

        if (ParseDeviceCapacity(parts, out var capacityGb))
            info.StorageNominal = $"整机 {capacityGb:0.#} GB";

        info.TotalRam     = ParseMeminfo(mem, "MemTotal") ?? "-";
        info.AvailableRam = ParseMeminfo(mem, "MemAvailable") ?? "-";

        // 内存的数值口径单独再取一次：上面那两行是给人看的文本，图形化要的是 GB 数字
        if (ParseMeminfoKb(mem, "MemTotal") is long totalRamKb and > 0)
        {
            info.RamTotalGb = totalRamKb / 1024.0 / 1024.0;
            info.RamAvailGb = (ParseMeminfoKb(mem, "MemAvailable") ?? 0) / 1024.0 / 1024.0;
            info.RamNominal = $"标称 {RoundCapacity(info.RamTotalGb):0.#} GB";
        }
    }

    /// <summary>把内存总量向上取整到常见标称档位（6/8/12/16…），避免内核保留导致标称偏低。</summary>
    private static double RoundCapacity(double gb) => gb switch
    {
        <= 1.5 => 2,
        <= 3   => 4,
        <= 6   => 8,
        <= 8   => 12,
        <= 12  => 16,
        <= 16  => 24,
        <= 24  => 32,
        _      => Math.Ceiling(gb / 8) * 8,
    };

    /// <summary>按 =====TAG===== 分隔符切出某段命令的输出；head 为 true 时取第一个分隔符之前的内容。</summary>
    private static string Section(string raw, string tag, bool head = false)
    {
        var marker = $"====={tag}=====";
        var start  = raw.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return head ? raw : string.Empty;
        if (head) return raw[..start];

        start += marker.Length;
        var end = raw.IndexOf("=====", start, StringComparison.Ordinal);
        return end < 0 ? raw[start..] : raw[start..end];
    }

    /// <summary>从 df 输出解析 1K-blocks 口径的 总量 / 已用 / 可用。</summary>
    private static bool ParseSize(string output, out long totalKb, out long usedKb, out long availKb)
    {
        totalKb = usedKb = availKb = 0;

        string? line = null;
        foreach (var l in output.Split('\n'))
        {
            var t = l.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("Filesystem") || t.StartsWith("File system")) continue;
            line = t;
        }
        if (line is null) return false;

        var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4) return false;

        var okTotal = long.TryParse(p[1], out totalKb);
        var okUsed  = long.TryParse(p[2], out usedKb);
        var okAvail = long.TryParse(p[3], out availKb);
        return okTotal & okUsed & okAvail;
    }

    /// <summary>从 /proc/partitions 取整机容量（十进制 GB，便于与厂商标称容量对照）。</summary>
    private static bool ParseDeviceCapacity(string output, out double gb)
    {
        gb = 0;
        long best = 0;

        foreach (var line in output.Split('\n'))
        {
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) continue;

            // 只取整块设备（mmcblk0 / sda / nvme0n1），排除 mmcblk0p1 这类分区
            if (!Regex.IsMatch(p[3], @"^(mmcblk\d+|nvme\d+n\d+|sd[a-z])$")) continue;
            if (long.TryParse(p[2], out var blocks) && blocks > best) best = blocks;
        }

        if (best <= 0) return false;
        gb = best * 1024.0 / 1000.0 / 1000.0;   // #blocks 以 1024 字节计
        return true;
    }

    // ---------- 解析 ----------

    private static string? GetProp(string getpropOutput, string key)
    {
        var pattern = $@"\[{Regex.Escape(key)}\]:\s*\[(.*?)\]";
        var match = Regex.Match(getpropOutput, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// 从 getprop 判断 BL 锁状态，不用进 fastboot：
    /// ro.boot.flash.locked 最准（1=已锁 0=已解），退回 ro.boot.verifiedbootstate（orange=已解）。
    /// </summary>
    private static bool? ParseUnlocked(string props)
    {
        var locked = GetProp(props, "ro.boot.flash.locked");
        if (locked is "1" or "true")  return false;   // 已锁
        if (locked is "0" or "false") return true;    // 已解锁

        var verified = GetProp(props, "ro.boot.verifiedbootstate");
        if (verified is not null)
        {
            if (verified.Equals("orange", StringComparison.OrdinalIgnoreCase)) return true;
            if (verified.Equals("green", StringComparison.OrdinalIgnoreCase)
                || verified.Equals("yellow", StringComparison.OrdinalIgnoreCase)) return false;
        }

        // 部分国产 ROM 用 ro.secureboot.lockstate / ro.boot.veritymode
        var lockState = GetProp(props, "ro.secureboot.lockstate");
        if (lockState is not null)
        {
            if (lockState.Equals("locked", StringComparison.OrdinalIgnoreCase)) return false;
            if (lockState.Equals("unlocked", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return null;
    }

    private static (string? serial, string? state) ParseAdbDevices(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices")) continue;
            var parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0], parts[1]);
        }
        return (null, null);
    }

    private static string? ParseFastbootSerial(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Equals("fastboot", StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }

    private static string? ParseWmSize(string output)
    {
        var match = Regex.Match(output, @"(?:Physical|Override) size:\s*(\d+x\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool ParseBattery(string output, out int level, out bool charging)
    {
        level    = -1;
        charging = false;

        var match = Regex.Match(output, @"level:\s*(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out level)) return false;

        // status: 2=充电中 5=已充满
        var status = Regex.Match(output, @"status:\s*(\d+)");
        charging = status.Success && (status.Groups[1].Value == "2" || status.Groups[1].Value == "5");
        return true;
    }

    private static string? ParseMeminfo(string output, string key)
    {
        var kb = ParseMeminfoKb(output, key);
        return kb is null ? null : FormatKb(kb.Value);
    }

    /// <summary>/proc/meminfo 的原始 kB 值，取不到返回 null。</summary>
    private static long? ParseMeminfoKb(string output, string key)
    {
        var match = Regex.Match(output, $@"{key}:\s*(\d+)\s*kB");
        if (!match.Success) return null;
        return long.TryParse(match.Groups[1].Value, out var kb) ? kb : null;
    }

    private static string FormatKb(long kb)
    {
        double gb = kb / 1024.0 / 1024.0;
        if (gb >= 1.0) return $"{gb:0.##} GB";
        double mb = kb / 1024.0;
        return $"{mb:0.#} MB";
    }

    private static bool IsEdlMode()
    {
        try
        {
            const string query =
                "SELECT DeviceID FROM Win32_PnPEntity WHERE " +
                "DeviceID LIKE '%VID_05C6&PID_9008%' OR " +
                "DeviceID LIKE '%VID_05C6&PID_900E%' OR " +
                "DeviceID LIKE '%QDLOADER 9008%'";

            using var searcher = new ManagementObjectSearcher(query);
            foreach (var _ in searcher.Get())
                return true;
        }
        catch { /* WMI 不可用时忽略 */ }
        return false;
    }

    /// <summary>
    /// 检测 Windows 设备管理器中是否存在 Android 相关设备（ADB/Fastboot 驱动）。
    /// </summary>
    public static bool HasAndroidDevice()
    {
        try
        {
            const string query =
                "SELECT Name FROM Win32_PnPEntity WHERE " +
                "Name LIKE '%Android%' OR Name LIKE '%ADB%' OR Name LIKE '%Fastboot%' OR Name LIKE '%Bootloader%'";

            using var searcher = new ManagementObjectSearcher(query);
            foreach (var _ in searcher.Get())
                return true;
        }
        catch { /* WMI 不可用时忽略 */ }
        return false;
    }

    // ---------- 模式切换 ----------

    /// <summary>目标模式的中文名。</summary>
    public static string ModeLabel(string target) => target switch
    {
        "system"    => "系统",
        "fastboot"  => "Fastboot",
        "fastbootd" => "FastbootD",
        "recovery"  => "Recovery",
        "edl"       => "EDL(9008)",
        _           => target,
    };

    /// <summary>
    /// 把设备切到目标模式。stateKind 为当前状态（online / fastboot / edl），自动选通道：
    /// adb → adb 指令；fastboot / fastbootd → fastboot 指令；edl → 需高通 firehose 工具。
    /// </summary>
    public static (bool Ok, string Message) RebootTo(string serial, string stateKind, string target)
    {
        var label = ModeLabel(target);

        if (stateKind == "online")
        {
            if (!AdbService.IsAdbAvailable) return (false, "未找到 adb。");

            var arg = target switch
            {
                "system"    => "reboot",
                "fastboot"  => "reboot bootloader",
                "fastbootd" => "reboot fastboot",
                "recovery"  => "reboot recovery",
                "edl"       => "reboot edl",
                _           => string.Empty,
            };
            if (arg.Length == 0) return (false, "不支持的目标模式。");

            AdbService.Run(AdbService.AdbPath!, $"-s {serial} {arg}");
            return (true, $"已通过 adb 发送指令，设备正在进入{label}。");
        }

        if (stateKind == "fastboot")
        {
            if (!AdbService.IsFastbootAvailable) return (false, "未找到 fastboot。");

            var arg = target switch
            {
                "system"    => "reboot",
                "fastboot"  => "reboot bootloader",
                "fastbootd" => "reboot fastboot",
                "recovery"  => "reboot recovery",
                "edl"       => "oem edl",
                _           => string.Empty,
            };
            if (arg.Length == 0) return (false, "不支持的目标模式。");

            var output = AdbService.Run(AdbService.FastbootPath!, arg);

            // 部分机型不认 oem edl，退回 reboot edl 再试一次
            if (target == "edl" && IsFailed(output))
            {
                output = AdbService.Run(AdbService.FastbootPath!, "reboot edl");
                if (IsFailed(output))
                    return (false,
                        "设备拒绝了进入 EDL 的指令。\n\n" +
                        "常见原因：该机型未开放 oem edl，或 bootloader 未解锁。\n" +
                        "可以改用「音量上 + 音量下 + 电源」硬进 9008。");
            }

            return (true, $"已通过 fastboot 发送指令，设备正在进入{label}。");
        }

        if (stateKind == "edl")
            return ResetFromEdl(target);

        return (false, "当前没有可用的通信通道，请先连接设备。");
    }

    /// <summary>EDL(9008) 下 adb / fastboot 全部不可用，只能靠高通 firehose 工具复位。</summary>
    private static (bool Ok, string Message) ResetFromEdl(string target)
    {
        if (target != "system")
            return (false,
                $"EDL 模式下无法直接切入{ModeLabel(target)}。\n\n" +
                "请先回到系统，再从系统里切到该模式。");

        var edlTool = FindTool("edl.exe") ?? FindTool("edl.py");
        if (edlTool is not null)
        {
            AdbService.Run(edlTool, "reset", 30000);
            return (true, "已调用 edl reset。若设备没反应，请长按电源键 10~20 秒强制重启。");
        }

        return (false,
            "EDL(9008) 模式下 adb / fastboot 都无法通信，回系统的办法：\n\n" +
            "1. 长按电源键 10~20 秒强制重启（最常用）；\n" +
            "2. 把 edl.exe（bkerler EDL 工具）放进 tools 目录，本工具会自动调用 edl reset；\n" +
            "3. 或用 QFIL / QDL 刷入镜像，刷完会自动重启回系统。");
    }

    private static bool IsFailed(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;   // fastboot 成功时常常没有输出

        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            if (t.Contains("failed") || t.Contains("not supported") || t.Contains("unknown")
                || t.Contains("no devices") || t.Contains("error") || t.Contains("cannot"))
                return true;
        }
        return false;
    }

    private static string? FindTool(string fileName)
        // 同样认 exe 同级的 tools\ 和首启解压出来的数据目录（单文件发布）
        => ToolPaths.Find(fileName);
}
