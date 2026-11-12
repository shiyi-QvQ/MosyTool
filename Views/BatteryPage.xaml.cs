using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mosytool.Controls;
using Mosytool.Services;

namespace Mosytool.Views;

/// <summary>
/// 电池健康页。数值来自 BatteryService（内核 sysfs + dumpsys），
/// 页面只负责画：健康度环、容量对比条、寿命条，以及按阈值给结论。
/// </summary>
public partial class BatteryPage : Page
{
    private static readonly Color Green = Color.FromRgb(0x2E, 0x9E, 0x6B);
    private static readonly Color Amber = Color.FromRgb(0xCC, 0x8A, 0x2F);
    private static readonly Color Red   = Color.FromRgb(0xD1, 0x50, 0x4E);

    private string _serial = string.Empty;
    private bool   _adbOnline;
    private bool   _busy;

    public BatteryPage()
    {
        InitializeComponent();

        LogBox.Reset("等待操作…");
        Loaded += async (_, _) => await DetectAsync(autoRead: true);
    }

    // ==================== 设备检测 ====================

    private async Task DetectAsync(bool autoRead)
    {
        if (_busy) return;

        Models.DeviceInfo info;
        try
        {
            info = await Task.Run(DeviceService.Detect);
        }
        catch (Exception ex)
        {
            Log("检测设备失败：" + ex.Message);
            return;
        }

        _serial    = info.StateKind == "disconnected" ? string.Empty : info.Serial;
        _adbOnline = info.StateKind == "online";

        var (text, color) = info.StateKind switch
        {
            "online"       => ("已连接（ADB）", Green),
            "unauthorized" => ("未授权 USB 调试", Amber),
            "offline"      => ("离线", Red),
            "fastboot"     => ("Fastboot 模式", Amber),
            _              => ("未连接", Color.FromRgb(0x98, 0xA1, 0xAB)),
        };

        StateText.Text        = text;
        DeviceText.Text       = info.StateKind == "online" ? $"{info.Name} · {info.Serial}" : "-";
        StateDot.Background   = new SolidColorBrush(color);

        if (!_adbOnline)
        {
            Log(info.StateKind == "unauthorized"
                ? "手机已连接但未授权，请在手机上点「允许 USB 调试」。"
                : "读取电池需要设备开机并处于 ADB 在线状态。");
            return;
        }

        if (autoRead) await ReadAsync();
    }

    // ==================== 读取 ====================

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_adbOnline)
        {
            // 先重新检测一次，避免刚插上线时页面还停在"未连接"
            await DetectAsync(autoRead: false);
            if (!_adbOnline) return;
        }

        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        if (_busy) return;

        if (!AdbService.IsAdbAvailable)
        {
            Log("未找到 adb.exe，无法读取电池数据。");
            return;
        }

        SetBusy(true);
        try
        {
            var report = await BatteryService.ReadAsync(_serial, RootCheck.IsChecked == true, Log);
            Apply(report);
        }
        catch (Exception ex)
        {
            Log("读取异常：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ==================== 呈现 ====================

    private void Apply(BatteryService.BatteryReport r)
    {
        RawText.Text = r.Raw.Trim().Length == 0 ? "无数据" : r.Raw.Trim();

        if (!r.Ok)
        {
            VerdictPillText.Text = "读取失败";
            VerdictText.Text     = r.Message;
            Log(r.Message);
            return;
        }

        ApplyMetrics(r);
        ApplyInfo(r);
        ApplyCapacity(r);
        ApplyLife(r);
        ApplyVerdict(r);

        TechText.Text   = $"{(r.Technology.Length > 0 ? r.Technology : "未知电芯")} · 节点 {r.Node}"
                        + (r.RootUsed ? " · root" : string.Empty);
        StatusText.Text = r.Status;
    }

    private void ApplyMetrics(BatteryService.BatteryReport r)
    {
        DesignValue.Text  = Capacity(r.DesignMah, r.DesignWh);
        FullValue.Text    = Capacity(r.FullMah, r.FullWh);
        CycleValue.Text   = r.CycleCount?.ToString() ?? "--";

        VoltageValue.Text = r.VoltageVolt is > 0 ? $"{r.VoltageVolt.Value:0.00} V" : "--";
        TempValue.Text    = r.TempCelsius is > -40 ? $"{r.TempCelsius.Value:0.0} ℃" : "--";
        CurrentValue.Text = r.CurrentAmp is { } amp ? $"{amp * 1000:0} mA" : "--";
    }

    /// <summary>多节点读到的电源 / 充电 / 电芯身份信息。</summary>
    private void ApplyInfo(BatteryService.BatteryReport r)
    {
        PowerSourceValue.Text = r.PowerSource;

        PowerValue.Text = r.PowerWatt is > 0 ? $"{r.PowerWatt.Value:0.0} W" : "--";
        LimitValue.Text = r.ChargeCurrentLimitAmp is > 0
            ? $"{r.ChargeCurrentLimitAmp.Value * 1000:0} mA"
            : "--";

        FullTimeValue.Text  = Minutes(r.TimeToFullMinutes);
        EmptyTimeValue.Text = Minutes(r.TimeToEmptyMinutes);

        VoltageRangeValue.Text = r.VoltageMinVolt is > 0 && r.VoltageMaxVolt is > 0
            ? $"{r.VoltageMinVolt.Value:0.00}–{r.VoltageMaxVolt.Value:0.00} V"
            : "--";

        MakerValue.Text      = r.Manufacturer.Length > 0 ? r.Manufacturer : "--";
        SerialValue.Text     = r.Serial.Length > 0 ? r.Serial : "--";
        EnergyValue.Text     = Energy(r.FullWh, r.DesignWh);

        NodeHint.Text    = r.Nodes.Count > 0 ? $"{r.Nodes.Count} 个节点 · 主节点 {r.Node}" : "-";
        NodeHint.ToolTip = r.Nodes.Count > 0 ? string.Join("\n", r.Nodes) : null;

        HealthSourceNote.Text = r.SohPercent is > 0
            ? $"健康度取自电池芯片直接上报的 SOH 节点（{r.SohPercent.Value:0.0}%），比容量折算更准确。"
            : r.HealthPercent is > 0
                ? "健康度按「实测满电容量 ÷ 出厂设计容量」计算，该机型未开放 SOH 节点。"
                : "该机型既没开放 SOH 节点也没有完整容量节点，健康度无法计算。";
    }

    private static string Capacity(double? mah, double? wh)
        => mah is > 0 ? $"{mah.Value:0} mAh"
         : wh  is > 0 ? $"{wh.Value:0.0} Wh"
         : "--";

    private static string Energy(double? fullWh, double? designWh)
        => fullWh is > 0 && designWh is > 0 ? $"{fullWh.Value:0.0} / {designWh.Value:0.0} Wh"
         : designWh is > 0 ? $"设计 {designWh.Value:0.0} Wh"
         : fullWh   is > 0 ? $"{fullWh.Value:0.0} Wh"
         : "--";

    private static string Minutes(double? minutes)
    {
        if (minutes is not > 0) return "--";

        var total = (int)Math.Round(minutes.Value);
        return total < 60 ? $"{total} 分钟" : $"{total / 60} 小时 {total % 60} 分";
    }

    private void ApplyCapacity(BatteryService.BatteryReport r)
    {
        Anim.To(DesignBar, RangeBase.ValueProperty, r.DesignMah is > 0 ? 100 : 0);

        if (r.HealthPercent is > 0)
            Anim.To(FullBar, RangeBase.ValueProperty, Math.Clamp(r.HealthPercent.Value, 0, 100));
        else
            Anim.To(FullBar, RangeBase.ValueProperty, 0);

        DesignBarText.Text = Capacity(r.DesignMah, r.DesignWh);
        FullBarText.Text   = r.FullMah is > 0
            ? (r.HealthPercent is > 0
                ? $"{Capacity(r.FullMah, r.FullWh)} · {r.HealthPercent.Value:0.0}%"
                : Capacity(r.FullMah, r.FullWh))
            : r.SohPercent is > 0
                ? $"芯片 SOH {r.SohPercent.Value:0.0}%"
                : "--";

        if (r.DesignMah is > 0 && r.FullMah is > 0)
        {
            var lost = Math.Max(0, r.DesignMah.Value - r.FullMah.Value);
            CapacityNote.Text = $"相比出厂标定少了 {lost:0} mAh。实测容量低于设计容量即为正常老化，差值越大满电续航越短。";
            FullBar.Foreground = new SolidColorBrush(ColorOf(r.HealthPercent));
        }
        else
        {
            CapacityNote.Text = "该机型没有开放容量节点（或需要 root），无法计算容量衰减。";
            FullBar.ClearValue(Control.ForegroundProperty);
        }
    }

    private void ApplyLife(BatteryService.BatteryReport r)
    {
        if (r.LifePercent is { } life)
        {
            Anim.To(LifeBar, RangeBase.ValueProperty, life);
            LifeText.Text      = $"{life:0}%";
            LifeBar.Foreground = new SolidColorBrush(ColorOf(r.LifePercent));
            LifeNote.Text      = r.CycleCount is { } cycles
                ? $"已循环 {cycles} 次，按 {BatteryService.RatedCycles} 次标定折算剩余寿命；循环次数由电池芯片累计，不是软件计数。"
                : "未读到循环次数，剩余寿命按容量衰减到 80% 折算。";
        }
        else
        {
            Anim.To(LifeBar, RangeBase.ValueProperty, 0);
            LifeText.Text = "--";
            LifeBar.ClearValue(Control.ForegroundProperty);
            LifeNote.Text = "没有循环次数也没有容量数据，无法估算寿命。";
        }

        if (r.LevelPercent is { } level)
        {
            Anim.To(LevelBar, RangeBase.ValueProperty, Math.Clamp(level, 0, 100));
            LevelText.Text = $"{level:0}%";
        }
        else
        {
            Anim.To(LevelBar, RangeBase.ValueProperty, 0);
            LevelText.Text = "--";
        }
    }

    private void ApplyVerdict(BatteryService.BatteryReport r)
    {
        if (r.HealthPercent is not > 0)
        {
            // 容量节点缺失：退回芯片自报的 HEALTH 枚举，至少给个定性结论
            var text = r.HealthText.Length > 0 ? r.HealthText : "数据不足";
            VerdictPillText.Text = text;
            VerdictText.Text     = r.HealthText is "良好" or "未知" or ""
                ? "该机型没有开放容量节点，只能读到电气参数。想算健康度可以勾选「用 root 读取」再试。"
                : $"电池芯片上报的状态是「{r.HealthText}」，建议到售后检测。";
            Anim.To(HealthRing, Wpf.Ui.Controls.ProgressRing.ProgressProperty, 0);
            HealthValue.Text = "--";
            return;
        }

        var health = r.HealthPercent.Value;
        var (label, advice) = health switch
        {
            >= 90 => ("优秀", "容量几乎没有衰减，正常使用即可。"),
            >= 80 => ("良好", "轻微老化，属于正常范围，日常续航基本无感。"),
            >= 70 => ("一般", "已有可感知的容量衰减，重度使用建议随身带充电宝。"),
            >= 60 => ("偏差", "衰减明显、续航明显缩短，可以开始考虑换电池。"),
            _     => ("建议更换", "容量低于 60%，可能出现低电关机，建议尽快更换电池。"),
        };

        Anim.To(HealthRing, Wpf.Ui.Controls.ProgressRing.ProgressProperty, Math.Clamp(health, 0, 100));
        HealthValue.Text = $"{health:0.0}%";

        var color = ColorOf(r.HealthPercent);
        HealthRing.Foreground = new SolidColorBrush(color);
        HealthCaption.Text    = label;

        VerdictPillText.Text = $"{label} · {health:0.0}%";
        VerdictPill.Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));
        VerdictText.Text = advice;
    }

    // ==================== 工具 ====================

    private static string Mah(double? value) => value is > 0 ? $"{value.Value:0} mAh" : "--";

    /// <summary>按健康度 / 剩余寿命取色，与首页电量环使用同一组颜色。</summary>
    private static Color ColorOf(double? percent) => percent switch
    {
        >= 80 => Green,
        >= 60 => Amber,
        null  => Color.FromRgb(0x98, 0xA1, 0xAB),
        _     => Red,
    };

    private void SetBusy(bool busy)
    {
        _busy                = busy;
        ReadButton.IsEnabled = !busy;
        RootCheck.IsEnabled  = !busy;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Log(string message)
    {
        // BatteryService 在 Task.Run 里回调日志，必须封送到 UI 线程再碰控件
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }

        // LogView 自带按等级着色与自动滚到底，不要用 AppendText（那会丢掉分级）
        LogBox.Append($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
