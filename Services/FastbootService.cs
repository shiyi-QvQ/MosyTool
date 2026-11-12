using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mosytool.Models;

namespace Mosytool.Services;

public static class FastbootService
{
    /// <summary>常见分区名。设备读不到分区表时用它兜底，方便手输分区名。</summary>
    public static readonly string[] CommonPartitions =
    {
        "boot", "init_boot", "recovery", "vendor_boot", "dtbo",
        "vbmeta", "vbmeta_system", "vbmeta_vendor",
        "system", "system_ext", "vendor", "product", "odm",
        "bootloader", "radio", "modem", "persist", "cache",
        "userdata", "metadata", "super", "misc", "logo", "splash",
        "abl", "xbl", "tz", "hyp", "rpm",
    };

    public static bool IsAvailable => AdbService.IsFastbootAvailable;

    /// <summary>检测是否处于 Fastboot 模式，返回设备序列号。</summary>
    public static string? DetectDevice()
    {
        if (!IsAvailable) return null;
        var output = AdbService.Run(AdbService.FastbootPath!, "devices");

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

    /// <summary>获取当前槽位（A/B 分区设备），返回 a / b / _a / _b 或 null。</summary>
    public static string? GetCurrentSlot()
    {
        if (!IsAvailable) return null;
        var output = AdbService.Run(AdbService.FastbootPath!, "getvar current-slot");
        var match  = Regex.Match(output, @"current-slot:\s*(\S+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>是否处于 FastbootD（用户空间 fastboot）模式。该模式下静态分区（logo/boot 等）不可见。</summary>
    public static bool IsUserspace()
    {
        if (!IsAvailable) return false;
        var output = AdbService.Run(AdbService.FastbootPath!, "getvar is-userspace");
        return Regex.IsMatch(output, @"is-userspace:\s*yes", RegexOptions.IgnoreCase);
    }

    /// <summary>重启到 bootloader（传统 fastboot）模式。</summary>
    public static void RebootToBootloader(Action<string>? log = null)
    {
        if (!IsAvailable) return;
        log?.Invoke("> fastboot reboot bootloader");
        AdbService.Run(AdbService.FastbootPath!, "reboot bootloader");
    }

    /// <summary>读取 bootloader 解锁状态：true=已解锁，false=已锁，null=机型不支持查询。</summary>
    public static bool? GetUnlockedState()
    {
        if (!IsAvailable) return null;

        var output = AdbService.Run(AdbService.FastbootPath!, "getvar unlocked");
        var match  = Regex.Match(output, @"unlocked:\s*(yes|no|true|false)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.ToLowerInvariant() is "yes" or "true";

        // 老机型走 oem device-info
        var info = AdbService.Run(AdbService.FastbootPath!, "oem device-info");
        var m2   = Regex.Match(info, @"Device unlocked:\s*(true|false)", RegexOptions.IgnoreCase);
        if (m2.Success) return m2.Groups[1].Value.ToLowerInvariant() == "true";

        return null;
    }

    /// <summary>
    /// 解 / 回锁 bootloader。各厂商指令不统一，按顺序依次尝试直到有一条生效。
    /// 注意：解锁会清空全部用户数据。
    /// </summary>
    public static async Task<(bool Ok, string Message)> SetLockStateAsync(bool unlock, Action<string> log)
    {
        if (!IsAvailable) return (false, "未找到 fastboot 工具。");

        var commands = unlock
            ? new[] { "flashing unlock", "oem unlock", "oem unlock-go", "flashing unlock-critical" }
            : new[] { "flashing lock", "oem lock" };

        var lastText = string.Empty;

        foreach (var cmd in commands)
        {
            log($"> fastboot {cmd}");

            var collected = new List<string>();
            int code = await RunWithOutputAsync(cmd, line =>
            {
                collected.Add(line);
                log(line);
            });

            var text = string.Join("\n", collected);
            lastText = text;

            var failed = LooksFailed(text);
            if (code == 0 && !failed)
            {
                return (true, unlock
                    ? "解锁指令已下发。请在手机屏幕上用音量键选择 Unlock / 确认，再按电源键确认（部分机型会重启并清空数据）。"
                    : "回锁指令已下发。请在手机屏幕上确认（回锁通常也会清空数据）。");
            }

            if (!failed && text.Trim().Length == 0)
                return (true, "指令已下发，请留意手机屏幕上的确认提示。");

            log("  该指令未生效，换下一条重试…");
        }

        return (false, $"所有指令都未生效，设备最后返回：\n{FirstLine(lastText)}");
    }

    private static bool LooksFailed(string output)
    {
        var t = output.ToLowerInvariant();
        return t.Contains("failed") || t.Contains("not allowed") || t.Contains("not supported")
            || t.Contains("unknown command") || t.Contains("no devices") || t.Contains("error")
            || t.Contains("cannot") || t.Contains("is not enabled in settings");
    }

    private static string FirstLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return "（无输出）";
    }

    /// <summary>
    /// 读取设备分区表。
    ///
    /// 数据来自 `fastboot getvar all`：它会把 partition-type / partition-size / has-slot
    /// 逐条吐出来，A/B 双槽位设备上 boot 会以 boot_a / boot_b 两条出现。
    /// 所以这里顺带把 slot-count / current-slot 也解析出来，一起交给界面标注槽位。
    /// </summary>
    public static FastbootPartitionTable GetPartitionTable()
    {
        if (!IsAvailable) return FastbootPartitionTable.Empty;

        var output = AdbService.Run(AdbService.FastbootPath!, "getvar all", 20000);

        var types   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sizes   = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var slotted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 基名 → 实际见到过的槽位字母（用来判断是不是「两个槽都在」）
        var letters = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var slotCount = 0;
        string? currentSlot = null;
        string? product     = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // 输出可能带 "(bootloader) " / "(fastbootd) " 前缀，先剥掉。
            var text = Regex.Replace(line, @"^\([A-Za-z]+\)\s*", string.Empty);

            // 同一份 getvar all 输出里就能拿到产品名，界面的「设备名称」不用再问一次。
            var m = Regex.Match(text, @"^product\s*:\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                product = m.Groups[1].Value.Trim();
                continue;
            }

            m = Regex.Match(text, @"^slot-count\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, out var n)) slotCount = n;
                continue;
            }

            m = Regex.Match(text, @"^current-slot\s*:\s*([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                currentSlot = m.Groups[1].Value.ToLowerInvariant();
                continue;
            }

            m = Regex.Match(text, @"^has-slot\s*:\s*([A-Za-z0-9_\-]+)\s*:\s*(yes|no)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                if (m.Groups[2].Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    slotted.Add(m.Groups[1].Value);
                continue;
            }

            m = Regex.Match(text, @"^partition-size\s*:\s*([A-Za-z0-9_\-]+)\s*:\s*(0x[0-9A-Fa-f]+|\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                sizes[m.Groups[1].Value] = ParseSize(m.Groups[2].Value);
                continue;
            }

            m = Regex.Match(text, @"^partition-type\s*:\s*([A-Za-z0-9_\-]+)\s*:?\s*(\S*)", RegexOptions.IgnoreCase);
            if (m.Success) types[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // partition-type 与 partition-size 里出现过的名字都算分区，按出现顺序去重。
        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in types.Keys)
            if (seen.Add(name)) names.Add(name);
        foreach (var name in sizes.Keys)
            if (seen.Add(name)) names.Add(name);

        if (names.Count == 0) return FastbootPartitionTable.Empty;

        // 有些 bootloader 不给 slot-count：只要见到 _b 分区就按双槽位算。
        if (slotCount <= 0)
            slotCount = names.Any(n => n.EndsWith("_b", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;

        foreach (var name in names)
        {
            var letter = SlotOf(name);
            if (letter.Length == 0) continue;

            var baseName = name[..^2];
            if (!letters.TryGetValue(baseName, out var set))
                letters[baseName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(letter);
        }

        var partitions = new List<FastbootPartition>(names.Count);

        foreach (var name in names)
        {
            var letter   = SlotOf(name);
            var baseName = letter.Length == 0 ? name : name[..^2];

            sizes.TryGetValue(name, out var size);
            types.TryGetValue(name, out var type);

            // 受槽位管理：名字自带 _a/_b，或者设备声明了 has-slot:<基名>:yes。
            bool isSlotted = slotCount >= 2 && (letter.Length > 0 || slotted.Contains(baseName));

            string slotInfo;
            if (!isSlotted)
            {
                slotInfo = "无槽位";
            }
            else if (slotted.Contains(baseName)
                     || (letters.TryGetValue(baseName, out var seenLetters) && seenLetters.Count >= 2))
            {
                slotInfo = "A/B 双槽";
            }
            else
            {
                slotInfo = letter.Length > 0 ? $"仅 {letter} 槽" : "无槽位";
            }

            partitions.Add(new FastbootPartition
            {
                Name      = name,
                BaseName  = baseName,
                Slot      = letter,
                IsSlotted = isSlotted,
                SlotInfo  = slotInfo,
                IsActive  = letter.Length > 0 && currentSlot is not null
                            && letter.Equals(currentSlot, StringComparison.OrdinalIgnoreCase),
                Size      = size,
                SizeHuman = FormatSize(size),
                Type      = type ?? string.Empty,
            });
        }

        // 同一分区的两个槽位挨在一起，读起来才顺。
        partitions.Sort(static (a, b) =>
        {
            int byBase = string.Compare(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase);
            return byBase != 0 ? byBase : string.Compare(a.Slot, b.Slot, StringComparison.OrdinalIgnoreCase);
        });

        return new FastbootPartitionTable
        {
            Partitions  = partitions,
            Product     = product,
            SlotCount   = slotCount,
            CurrentSlot = currentSlot,
        };
    }

    /// <summary>分区名列表。读不到设备分区表时退回预定义常见分区，方便手输。</summary>
    public static List<string> GetPartitions()
    {
        var names = GetPartitionTable().Partitions.Select(p => p.Name).ToList();
        if (names.Count == 0) names.AddRange(CommonPartitions);

        return names.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>取分区名的槽位后缀：以 _a / _b 结尾返回 "A" / "B"，其它返回空串。</summary>
    private static string SlotOf(string name)
    {
        if (name.Length < 3 || name[^2] != '_') return string.Empty;

        return name[^1] switch
        {
            'a' or 'A' => "A",
            'b' or 'B' => "B",
            _          => string.Empty,
        };
    }

    private static ulong ParseSize(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0;

        return ulong.TryParse(value, out var dec) ? dec : 0;
    }

    /// <summary>把字节数格式化成可读大小，例如 "64.00 MB"；0 表示设备未上报。</summary>
    public static string FormatSize(ulong bytes)
    {
        if (bytes == 0) return "—";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double   value = bytes;
        int      unit  = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.00} {units[unit]}";
    }

    /// <summary>分区表读取结果：分区列表 + 槽位信息。</summary>
    public sealed class FastbootPartitionTable
    {
        public static readonly FastbootPartitionTable Empty = new();

        public IReadOnlyList<FastbootPartition> Partitions { get; init; } = Array.Empty<FastbootPartition>();

        /// <summary>设备产品名（fastboot 的 product），未读到时为 null。</summary>
        public string? Product { get; init; }

        /// <summary>槽位数量（fastboot 的 slot-count）。</summary>
        public int SlotCount { get; init; } = 1;

        /// <summary>当前活动槽位（小写 a / b），未知时为 null。</summary>
        public string? CurrentSlot { get; init; }

        /// <summary>是否为 A/B 双槽位设备。</summary>
        public bool IsAbDevice => SlotCount >= 2;

        /// <summary>受槽位管理的分区组数，例如 boot_a/boot_b 算一组。</summary>
        public int SlottedGroupCount => Partitions
            .Where(p => p.IsSlotted)
            .Select(p => p.BaseName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        /// <summary>不受槽位管理的公共分区数量。</summary>
        public int CommonCount => Partitions.Count(p => !p.IsSlotted);
    }

    /// <summary>备份分区到指定文件（使用 fastboot fetch）。</summary>
    public static async Task<bool> BackupPartitionAsync(string partition, string outputPath, Action<string> log)
    {
        log($"> fastboot fetch {partition} \"{outputPath}\"");
        int code = await RunWithOutputAsync($"fetch {partition} \"{outputPath}\"", log);
        return code == 0;
    }

    /// <summary>刷入分区镜像（使用 fastboot flash）。</summary>
    public static async Task<bool> FlashPartitionAsync(string partition, string imagePath, Action<string> log)
    {
        log($"> fastboot flash {partition} \"{imagePath}\"");
        int code = await RunWithOutputAsync($"flash {partition} \"{imagePath}\"", log);
        return code == 0;
    }

    /// <summary>直接执行任意 fastboot 子命令，把输出逐行回调。</summary>
    public static async Task<bool> RunRawAsync(string arguments, Action<string> onOutput) =>
        await RunWithOutputAsync(arguments, onOutput) == 0;

    private static async Task<int> RunWithOutputAsync(string arguments, Action<string> onOutput)
    {
        if (!IsAvailable) return -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = AdbService.FastbootPath!,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // fastboot 的输出同样是 UTF-8，用系统代码页解码会把设备名里的中文解成乱码
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

            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            onOutput($"错误: {ex.Message}");
            return -1;
        }
    }
}
