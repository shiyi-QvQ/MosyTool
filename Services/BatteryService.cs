using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// 电池健康读取。采用「多节点 + 多键别名」采集：一条 adb shell 把所有可能放电池数据的节点扫回来，
/// 再按语义逐字段挑值，而不是只认 /sys/class/power_supply/battery/uevent。
///
/// 采集范围：
///   /sys/class/power_supply/*        全部节点 uevent（battery / bms / usb / ac / wireless…）
///   /sys/class/qcom-battery          高通 BMS 直连节点（soh / cycle_count 常只在这里）
///   /sys/class/power_supply/battery  裸节点文件（uevent 常被厂商裁剪）
///   /sys/class/power_supply/bms      同一批裸节点（部分 MTK 机型容量只在这）
///   dumpsys battery                  电源类型、充电限流、电压温度等系统侧数值
///
/// 健康度优先级：芯片 SOH 节点 → 实测满电容量 ÷ 出厂设计容量 → 空值（不编数据）。
/// </summary>
public static class BatteryService
{
    /// <summary>厂商标称循环寿命：800 次后容量保持 ≥ 80%。</summary>
    public const int RatedCycles = 800;

    public sealed class BatteryReport
    {
        public bool   Ok      { get; init; }
        public string Message { get; init; } = string.Empty;

        /// <summary>主数据节点（battery / bms / …）。</summary>
        public string Node  { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;

        /// <summary>本次实际读到的全部 power_supply 节点名。</summary>
        public IReadOnlyList<string> Nodes { get; init; } = Array.Empty<string>();

        // ---------- 容量 ----------
        public double? DesignMah    { get; init; }
        public double? FullMah      { get; init; }
        public double? LevelPercent { get; init; }

        /// <summary>能量口径（部分机型只给 ENERGY_*，此时 mAh 为空但 Wh 有值）。</summary>
        public double? DesignWh { get; init; }
        public double? FullWh   { get; init; }

        // ---------- 寿命 ----------
        public int?    CycleCount    { get; init; }
        public double? HealthPercent { get; init; }
        public double? LifePercent   { get; init; }

        /// <summary>芯片直接上报的 SOH 百分比。</summary>
        public double? SohPercent { get; init; }

        /// <summary>健康度怎么来的：SOH 节点 / 容量比 / 无。</summary>
        public string HealthSource { get; init; } = string.Empty;

        // ---------- 电气参数 ----------
        public double? VoltageVolt { get; init; }
        public double? CurrentAmp  { get; init; }
        public double? TempCelsius { get; init; }

        public double? VoltageMaxVolt { get; init; }
        public double? VoltageMinVolt { get; init; }

        /// <summary>电压 × 电流，仅充电时给（放电符号意义不同，容易误读）。</summary>
        public double? PowerWatt { get; init; }

        /// <summary>系统侧充电限流。</summary>
        public double? ChargeCurrentLimitAmp { get; init; }

        /// <summary>充满 / 放空所需分钟数，只有部分机型暴露 TIME_TO_*_NOW。</summary>
        public double? TimeToFullMinutes  { get; init; }
        public double? TimeToEmptyMinutes { get; init; }

        // ---------- 电源 ----------
        public string PowerSource { get; init; } = string.Empty;
        public bool   Charging    { get; init; }

        // ---------- 文本状态 ----------
        public string Status     { get; init; } = string.Empty;
        public string HealthText { get; init; } = string.Empty;
        public string Technology { get; init; } = string.Empty;

        // ---------- 电芯身份 ----------
        public string Serial       { get; init; } = string.Empty;
        public string Manufacturer { get; init; } = string.Empty;

        /// <summary>本次是否通过 su 读取。</summary>
        public bool RootUsed { get; init; }

        /// <summary>原始输出，界面折叠展示，方便核对数据来源。</summary>
        public string Raw { get; init; } = string.Empty;
    }

    public static async Task<BatteryReport> ReadAsync(string serial, bool useRoot,
                                                      Action<string>? log = null,
                                                      CancellationToken ct = default)
        => await Task.Run(() => Read(serial, useRoot, log), ct).ConfigureAwait(false);

    // ==================== 采集 ====================

    /// <summary>逐个节点直读的裸节点名。各厂商写法差异大，一次全试，读到哪个用哪个。</summary>
    private const string BareFields =
        "charge_full charge_full_design charge_full_now charge_counter " +
        "energy_full energy_full_design " +
        "cycle_count battery_cycle batt_cycle batt_cycle_count battery_cycle_count cycle " +
        "soh soh_percent state_of_health battery_soh battery_health " +
        "capacity temp voltage_now current_now status health technology " +
        "serial_number battery_serial_number manufacturer battery_manufacturer vendor " +
        "voltage_max voltage_max_design voltage_min voltage_min_design " +
        "constant_charge_current_max input_current_limit current_max " +
        "time_to_full_now time_to_full_avg time_to_empty_now time_to_empty_avg";

    /// <summary>
    /// 一条脚本读全部候选节点，用 @@TAG@@ 分段，回本地再切。
    /// 脚本内刻意不用双引号：ProcessStartInfo.Arguments 是裸字符串，双引号会被 Windows 解析吃掉。
    /// </summary>
    private static readonly string Script =
        "for d in /sys/class/power_supply/*; do echo @@NODE:$d; cat $d/uevent 2>/dev/null; done; " +
        "echo @@QCOM@@; for f in " + BareFields +
        "; do echo $f=$(cat /sys/class/qcom-battery/$f 2>/dev/null); done; " +
        "echo @@BATT@@; for f in " + BareFields +
        "; do echo $f=$(cat /sys/class/power_supply/battery/$f 2>/dev/null); done; " +
        "echo @@BMS@@; for f in " + BareFields +
        "; do echo $f=$(cat /sys/class/power_supply/bms/$f 2>/dev/null); done; " +
        "echo @@DUMPSYS@@; dumpsys battery; " +
        "echo @@PROP@@; getprop ro.product.marketname; getprop ro.product.model";

    private static BatteryReport Read(string serial, bool useRoot, Action<string>? log)
    {
        var adb = AdbService.AdbPath;
        if (adb is null) return Fail("未找到 adb.exe，无法读取电池数据。");

        var raw      = string.Empty;
        var rootUsed = false;

        if (useRoot)
        {
            log?.Invoke("以 root 身份读取 sysfs…");
            raw = RunScript(adb, serial, root: true);

            rootUsed = raw.Length > 0 && !LooksLikeNoRoot(raw);
            if (!rootUsed)
            {
                log?.Invoke("root 不可用（未授权或没装 su），改用普通 shell 读取。");
                raw = string.Empty;
            }
        }

        if (raw.Length == 0)
        {
            log?.Invoke("正在多节点读取电池管理芯片数据…");
            raw = RunScript(adb, serial, root: false);
        }

        if (raw.Trim().Length == 0)
            return Fail("读取失败：设备没有返回数据。请确认手机已开机、USB 调试已授权。");

        var report = Parse(raw, rootUsed);

        if (!report.Ok) log?.Invoke(report.Message);
        else
        {
            log?.Invoke($"读到的节点（{report.Nodes.Count} 个）：{string.Join(", ", report.Nodes)}");
            log?.Invoke($"主节点 {report.Node} · 健康度来源：{report.HealthSource}");
            if (report.DesignMah is null && report.FullMah is null && report.SohPercent is null)
                log?.Invoke("该机型没有开放容量 / SOH 节点，健康度无法计算（可尝试勾选 root 读取）。");
        }

        return report;
    }

    private static string RunScript(string adb, string serial, bool root)
    {
        var body   = root ? $"su -c '{Script}'" : Script;
        var target = string.IsNullOrWhiteSpace(serial) ? $"shell {body}" : $"-s {serial} shell {body}";

        return AdbService.Run(adb, target, root ? 20000 : 15000);
    }

    private static bool LooksLikeNoRoot(string raw)
        => raw.Contains("su: not found", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("not allowed", StringComparison.OrdinalIgnoreCase);

    private static BatteryReport Fail(string message) => new() { Ok = false, Message = message };

    // ==================== 多节点取值容器 ====================

    /// <summary>把 uevent 与裸节点合成一张大小写不敏感的键值表，uevent 优先。</summary>
    private sealed class Harvest
    {
        private readonly Dictionary<string, string> _uevent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _bare   = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _uevent.Count + _bare.Count;

        public void AddUevent(string key, string value) => _uevent.TryAdd(key, value);

        /// <summary>裸节点段是 key=value 行，空值跳过；先加的先赢（battery > bms > qcom）。</summary>
        public void AddBare(string block)
        {
            foreach (var line in block.Split('\n'))
            {
                var t  = line.Trim();
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;

                var value = t[(eq + 1)..].Trim();
                if (value.Length == 0) continue;
                _bare.TryAdd(t[..eq], value);
            }
        }

        public string? Text(params string[] keys)
        {
            foreach (var key in keys)
                if (_uevent.TryGetValue(key, out var v) && v.Length > 0) return v;

            foreach (var key in keys)
                if (_bare.TryGetValue(key, out var v) && v.Length > 0) return v;

            return null;
        }

        public double? Num(params string[] keys) => ParseNum(Text(keys));
    }

    // ==================== 解析 ====================

    private static BatteryReport Parse(string raw, bool rootUsed)
    {
        var nodes = SplitNodes(raw);
        var h     = new Harvest();
        var primary = string.Empty;

        // battery 优先，其次 bms；其余节点（usb / ac / pc_port）只补缺。
        foreach (var name in Order(nodes.Keys))
        {
            foreach (var kv in nodes[name]) h.AddUevent(kv.Key, kv.Value);

            if (primary.Length == 0
                && (nodes[name].ContainsKey("CHARGE_FULL_DESIGN")
                    || nodes[name].ContainsKey("ENERGY_FULL_DESIGN")
                    || nodes[name].ContainsKey("SOH")))
                primary = name;
        }

        if (primary.Length == 0) primary = Order(nodes.Keys).FirstOrDefault() ?? string.Empty;

        h.AddBare(Section(raw, "@@BATT@@", "@@BMS@@"));
        h.AddBare(Section(raw, "@@BMS@@", "@@DUMPSYS@@"));
        h.AddBare(Section(raw, "@@QCOM@@", "@@BATT@@"));

        if (h.Count == 0)
            return Fail("没有读到 power_supply 节点。\n该机型可能限制了 sysfs 访问，可勾选「用 root 读取」再试。");

        var dumpsys = Section(raw, "@@DUMPSYS@@", "@@PROP@@");
        var model   = Section(raw, "@@PROP@@", end: null)
            .Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? string.Empty;

        // ---------- 电压 ----------
        var voltUv = h.Num("VOLTAGE_NOW", "VOLTAGE_AVG", "VOLTAGE_OCV");
        double? volt = voltUv is > 0 ? voltUv / 1_000_000.0 : null;
        if (volt is null)
        {
            var mv = DumpNum(dumpsys, "voltage");
            if (mv is > 0) volt = mv / 1000.0;
        }

        // ---------- 温度：内核 0.1℃，个别机型直接给整数 ℃ ----------
        double? temp = null;
        var tempRaw  = h.Num("TEMP", "TEMP_TENTH", "BATT_TEMP", "BATTERY_TEMP");
        if (tempRaw is > 0) temp = tempRaw > 150 ? tempRaw / 10.0 : tempRaw;
        if (temp is null) temp = DumpNum(dumpsys, "temperature");
        if (temp is > 150) temp /= 10.0;

        // ---------- 电流：µA，负数为放电 ----------
        var currentUa = h.Num("CURRENT_NOW", "CURRENT_AVG");
        double? amp   = currentUa is not null ? currentUa / 1_000_000.0 : null;

        // ---------- 容量：µAh 除 1000；只有 mWh 时按电压折 mAh ----------
        var designUah = h.Num("CHARGE_FULL_DESIGN", "CHARGE_FULL_DESIGN_AGING");
        var designUwh = h.Num("ENERGY_FULL_DESIGN");
        var fullUah   = h.Num("CHARGE_FULL", "CHARGE_FULL_NOW");
        var fullUwh   = h.Num("ENERGY_FULL");

        var designMah = ToMah(designUah, designUwh, volt);
        var fullMah   = ToMah(fullUah,   fullUwh,   volt);

        double? designWh = designUwh is > 0 ? designUwh / 1_000_000.0 : null;
        double? fullWh   = fullUwh   is > 0 ? fullUwh   / 1_000_000.0 : null;

        // ---------- 电量 ----------
        var level = h.Num("CAPACITY") ?? DumpNum(dumpsys, "level");
        if (level is < 0 or > 100) level = null;

        // ---------- 循环次数：多别名取第一个 > 0 ----------
        int? cycle = null;
        foreach (var key in new[]
                 {
                     "CYCLE_COUNT", "BATTERY_CYCLE", "BATT_CYCLE_COUNT",
                     "BATT_CYCLE", "BATTERY_CYCLE_COUNT", "CYCLE",
                 })
        {
            var value = h.Num(key);
            if (value is > 0) { cycle = (int)value.Value; break; }
        }
        // ---------- 芯片 SOH ----------
        var soh = h.Num("SOH", "BATTERY_SOH", "STATE_OF_HEALTH", "SOH_PERCENT");
        if (soh is > 0 and <= 1) soh *= 100;           // 个别机型给 0.87 这种比例
        if (soh is <= 0 or > 100) soh = null;

        // ---------- 健康度：SOH 优先，其次容量比 ----------
        double? health;
        string  healthSource;

        if (soh is > 0)
        {
            health       = soh;
            healthSource = "芯片 SOH 节点";
        }
        else if (fullMah is > 0 && designMah is > 0)
        {
            health       = fullMah.Value / designMah.Value * 100.0;
            healthSource = "实测满电容量 ÷ 设计容量";
        }
        else
        {
            health       = null;
            healthSource = "无（未开放 SOH / 容量节点）";
        }

        // 标定误差可能算出略高于 100%，超过 105% 视为不可信
        if (health > 105) health = null;

        // ---------- 剩余寿命 ----------
        double? life = null;
        if (cycle is > 0)
            life = Clamp100(100.0 - cycle.Value / (double)RatedCycles * 100.0);
        else if (health is > 0)
            life = Clamp100((health.Value - 80.0) / 20.0 * 100.0);   // 80% 容量视为寿命终点

        // ---------- 电源与充电 ----------
        var status   = StatusText(h.Text("STATUS"), DumpNum(dumpsys, "status"));
        var charging = status == "充电中";

        var limitUa = h.Num("CONSTANT_CHARGE_CURRENT_MAX", "INPUT_CURRENT_LIMIT", "CURRENT_MAX")
                      ?? DumpNum(dumpsys, "max charging current");

        var toFullSec  = h.Num("TIME_TO_FULL_NOW", "TIME_TO_FULL_AVG");
        var toEmptySec = h.Num("TIME_TO_EMPTY_NOW", "TIME_TO_EMPTY_AVG");

        return new BatteryReport
        {
            Ok            = true,
            Message       = "读取成功",
            Node          = primary,
            Nodes         = Order(nodes.Keys).ToList(),
            Model         = model,
            DesignMah     = designMah,
            FullMah       = fullMah,
            DesignWh      = designWh,
            FullWh        = fullWh,
            LevelPercent  = level,
            CycleCount    = cycle,
            HealthPercent = health,
            LifePercent   = life,
            SohPercent    = soh,
            HealthSource  = healthSource,
            VoltageVolt   = volt,
            CurrentAmp    = amp,
            TempCelsius   = temp,
            VoltageMaxVolt = ToVolt(h.Num("VOLTAGE_MAX", "VOLTAGE_MAX_DESIGN")),
            VoltageMinVolt = ToVolt(h.Num("VOLTAGE_MIN", "VOLTAGE_MIN_DESIGN")),
            PowerWatt      = charging && volt > 0 && amp is not null
                ? Math.Abs(volt.Value * amp.Value)
                : null,
            ChargeCurrentLimitAmp = limitUa is > 0 ? limitUa / 1_000_000.0 : null,
            TimeToFullMinutes  = toFullSec  is > 0 ? toFullSec  / 60.0 : null,
            TimeToEmptyMinutes = toEmptySec is > 0 ? toEmptySec / 60.0 : null,
            PowerSource   = PowerSourceText(dumpsys, charging),
            Charging      = charging,
            Status        = status,
            HealthText    = HealthText_(h.Text("HEALTH"), DumpNum(dumpsys, "health")),
            Technology    = h.Text("TECHNOLOGY") ?? string.Empty,
            Serial        = h.Text("SERIAL_NUMBER", "BATTERY_SERIAL_NUMBER") ?? string.Empty,
            Manufacturer  = h.Text("MANUFACTURER", "BATTERY_MANUFACTURER", "VENDOR") ?? string.Empty,
            RootUsed      = rootUsed,
            Raw           = raw,
        };
    }

    /// <summary>按 @@NODE: 切块，每块第一行是节点路径，后面是 uevent 键值。</summary>
    private static Dictionary<string, Dictionary<string, string>> SplitNodes(string raw)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var body   = Section(raw, "@@NODE:", "@@QCOM@@");

        foreach (var block in body.Split("@@NODE:"))
        {
            if (block.Trim().Length == 0) continue;

            var lines = block.Split('\n');
            var name  = lines[0].Trim().Split('/').Last();
            if (name.Length == 0 || result.ContainsKey(name)) continue;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                var eq   = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq];
                if (key.StartsWith("POWER_SUPPLY_", StringComparison.OrdinalIgnoreCase))
                    key = key["POWER_SUPPLY_".Length..];

                map[key] = line[(eq + 1)..].Trim();
            }

            result[name] = map;
        }

        return result;
    }

    /// <summary>节点读取顺序：battery → bms → 其他（名字长的排后面，保证 bms_raw 不抢主节点）。</summary>
    private static IEnumerable<string> Order(IEnumerable<string> names)
        => names.OrderBy(n => n.ToLowerInvariant() switch
        {
            "battery" => 0,
            "bms"     => 1,
            _         => 2,
        }).ThenBy(n => n.Length);

    private static double? ParseNum(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ToMah(double? chargeUah, double? energyUwh, double? volt)
    {
        if (chargeUah is > 0) return chargeUah / 1000.0;
        if (energyUwh is > 0 && volt is > 1) return energyUwh / 1000.0 / volt.Value;
        return null;
    }

    private static double? ToVolt(double? microVolt) => microVolt is > 0 ? microVolt / 1_000_000.0 : null;

    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);

    /// <summary>切出标签之间的内容；end 为 null 表示切到结尾。</summary>
    private static string Section(string raw, string startTag, string? end)
    {
        var start = raw.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        start += startTag.Length;
        if (end is null) return raw[start..];

        var stop = raw.IndexOf(end, start, StringComparison.Ordinal);
        return stop < 0 ? raw[start..] : raw[start..stop];
    }

    // ---------- dumpsys battery 兜底 ----------

    /// <summary>取 dumpsys battery 里 "level: 85" 这种行的数值。</summary>
    private static double? DumpNum(string dumpsys, string key)
    {
        foreach (var line in dumpsys.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

            return ParseNum(t[(key.Length + 1)..].Trim());
        }
        return null;
    }

    private static bool DumpBool(string dumpsys, string key)
    {
        foreach (var line in dumpsys.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

            return string.Equals(t[(key.Length + 1)..].Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string PowerSourceText(string dumpsys, bool charging)
    {
        if (DumpBool(dumpsys, "ac powered"))       return "交流充电器";
        if (DumpBool(dumpsys, "usb powered"))      return "USB 供电";
        if (DumpBool(dumpsys, "wireless powered")) return "无线充电";
        if (DumpBool(dumpsys, "dock powered"))     return "底座供电";
        return charging ? "未知电源" : "未接电源";
    }

    // ---------- 文本映射 ----------

    private static string StatusText(string? sysfs, double? dump)
    {
        var value = (sysfs ?? string.Empty).Trim();

        if (value.Length > 0)
            return value.ToLowerInvariant() switch
            {
                "charging"     => "充电中",
                "discharging"  => "放电中",
                "full"         => "已充满",
                "not charging" => "未充电",
                "unknown"      => "未知",
                _              => value,
            };

        return dump switch
        {
            2 => "充电中",
            3 => "放电中",
            4 => "未充电",
            5 => "已充满",
            _ => "未知",
        };
    }

    private static string HealthText_(string? sysfs, double? dump)
    {
        var value = (sysfs ?? string.Empty).Trim();

        if (value.Length > 0)
            return value.ToLowerInvariant() switch
            {
                "good"                => "良好",
                "overheat"            => "过热",
                "dead"                => "已损坏",
                "over voltage"        => "电压过高",
                "unspecified failure" => "故障",
                "cold"                => "温度过低",
                _                     => value,
            };

        return dump switch
        {
            2 => "良好",
            3 => "过热",
            4 => "已损坏",
            5 => "电压过高",
            6 => "故障",
            7 => "温度过低",
            _ => "未知",
        };
    }
}
