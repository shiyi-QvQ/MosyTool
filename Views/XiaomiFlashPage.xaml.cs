using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosytool.Controls;
using Mosytool.Services;
using XiaomiFlashPlan = Mosytool.Services.XiaomiFlashService.FlashPlan;
using XiaomiRomScript = Mosytool.Services.XiaomiFlashService.RomScript;

namespace Mosytool.Views;

/// <summary>
/// 小米官方线刷页。选包 → 解析脚本 → 核对步骤 → 逐条执行。
/// </summary>
public partial class XiaomiFlashPage : Page
{
    private XiaomiFlashPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public XiaomiFlashPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await DetectAsync();
    }

    // ==================== 设备状态 ====================

    private async Task DetectAsync()
    {
        if (_busy) return;

        SetState("Mt.Status.BusyBrush", "检测中…");
        ModelText.Text  = "-";
        UnlockText.Text = "-";
        SlotText.Text   = "-";

        var result = await Task.Run(() =>
        {
            var serial = FastbootService.DetectDevice();

            return serial is null
                ? (Serial: (string?)null, Table: FastbootService.FastbootPartitionTable.Empty, Unlocked: (bool?)null)
                : (Serial: serial,
                   Table: FastbootService.GetPartitionTable(),
                   Unlocked: FastbootService.GetUnlockedState());
        });

        if (result.Serial is null)
        {
            SetState("Mt.Status.IdleBrush", "未检测到 Fastboot 设备");
            return;
        }

        SetState("Mt.Status.OkBrush", "已连接");

        ModelText.Text = string.IsNullOrWhiteSpace(result.Table.Product)
            ? $"未知（{result.Serial}）"
            : $"{result.Table.Product}（{result.Serial}）";

        UnlockText.Text = result.Unlocked switch
        {
            true  => "已解锁",
            false => "已锁定 —— 刷机前需先解锁 Bootloader",
            null  => "读取失败",
        };

        SlotText.Text = result.Table.IsAbDevice
            ? $"A/B 双槽，当前 {result.Table.CurrentSlot?.ToUpperInvariant() ?? "未知"}"
            : "单槽设备";
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private void SetState(string brushKey, string text)
    {
        StateDot.Background = TryFindResource(brushKey) as Brush
                              ?? (Brush)FindResource("Mt.Status.IdleBrush");
        StateText.Text = text;
    }

    // ==================== 选包 ====================

    private void BrowseRom_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description            = "选择已解压的小米 fastboot 线刷包目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton    = false,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        LoadRom(dialog.SelectedPath);
    }

    private void LoadRom(string selected)
    {
        _plan = null;
        StepGrid.ItemsSource = null;
        ModeCombo.ItemsSource = null;
        ParseButton.IsEnabled = false;
        ExportPlanButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StepCountText.Text = "解析后在这里列出将要执行的每一条 fastboot 命令。";

        var romRoot = XiaomiFlashService.ResolveRomRoot(selected);
        if (romRoot is null)
        {
            RomPathBox.Text = selected;
            PlanSummaryText.Text = "这个目录里没找到 flash_all*.bat / .sh。请选解压后的 fastboot 包目录（含 images\\ 与 flash_all.bat）。";
            WarningsText.Visibility = Visibility.Collapsed;
            return;
        }

        var scripts = XiaomiFlashService.FindScripts(romRoot);

        RomPathBox.Text         = romRoot;
        ModeCombo.ItemsSource   = scripts;
        ParseButton.IsEnabled   = scripts.Count > 0;
        PlanSummaryText.Text    = $"识别到 {scripts.Count} 个刷写脚本。";

        // 选中第一项会触发 SelectionChanged，把摘要换成更具体的一句
        ModeCombo.SelectedIndex = 0;

        WarningsText.Visibility = Visibility.Collapsed;
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 换模式后旧的步骤清单作废，必须重新解析，避免照着上一种模式的清单去刷
        _plan = null;
        StepGrid.ItemsSource = null;
        ExportPlanButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StepCountText.Text = "解析后在这里列出将要执行的每一条 fastboot 命令。";
        WarningsText.Visibility = Visibility.Collapsed;

        if (ModeCombo.SelectedItem is XiaomiRomScript script)
            PlanSummaryText.Text = $"{script.Label}\n脚本：{script.Name}　点「解析脚本」生成步骤清单。";
    }

    // ==================== 解析 ====================

    private void Parse_Click(object sender, RoutedEventArgs e)
    {
        if (ModeCombo.SelectedItem is not XiaomiRomScript script) return;

        LogBox.Reset($"> 正在解析 {script.Name}");

        XiaomiFlashPlan plan;
        try
        {
            plan = XiaomiFlashService.Parse(script.Path);
        }
        catch (Exception ex)
        {
            PlanSummaryText.Text = $"解析失败：{ex.Message}";
            StartButton.IsEnabled = false;
            return;
        }

        _plan = plan;
        StepGrid.ItemsSource = plan.Steps;

        StepCountText.Text = $"共 {plan.Steps.Count} 步：刷入 {plan.FlashCount}、擦除/清空 {plan.WipeCount}、"
                           + $"镜像合计 {FastbootService.FormatSize((ulong)plan.TotalBytes)}";

        PlanSummaryText.Text = $"{plan.ModeLabel}\n{StepCountText.Text}";

        // 解析期间被跳过的项一定要让用户看见，否则会以为「刷完了」
        if (plan.Warnings.Count > 0)
        {
            WarningsText.Text = $"以下 {plan.Warnings.Count} 项未纳入执行：\n"
                              + string.Join("\n", plan.Warnings.Take(12))
                              + (plan.Warnings.Count > 12 ? $"\n… 另有 {plan.Warnings.Count - 12} 项" : string.Empty);
            WarningsText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningsText.Visibility = Visibility.Collapsed;
        }

        foreach (var warning in plan.Warnings)
            LogBox.Append("[警告] " + warning, LogLevel.Warning);

        LogBox.Append($"[成功] 解析完成，{plan.Steps.Count} 步待执行。", LogLevel.Success);

        ExportPlanButton.IsEnabled = plan.Steps.Count > 0;
        StartButton.IsEnabled      = plan.Steps.Count > 0 && !_busy;
    }

    private void ExportPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { Steps.Count: > 0 } plan) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "导出步骤清单",
            FileName         = Path.GetFileNameWithoutExtension(plan.ScriptName) + "_步骤清单.txt",
            Filter           = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt       = ".txt",
            InitialDirectory = plan.RomRoot,
        };

        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("# 小米官方线刷 · 步骤清单");
        sb.AppendLine($"# 包目录：{plan.RomRoot}");
        sb.AppendLine($"# 脚本　：{plan.ScriptName}");
        sb.AppendLine($"# 模式　：{plan.ModeLabel}");
        sb.AppendLine($"# 步骤　：{plan.Steps.Count}");
        sb.AppendLine();

        foreach (var step in plan.Steps)
            sb.AppendLine($"[{step.Index:D3}] {step.KindText,-4} {step.Note,-22} fastboot {step.Arguments}");

        if (plan.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 已跳过：");
            foreach (var warning in plan.Warnings) sb.AppendLine("#   " + warning);
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            LogBox.Append($"[成功] 已导出：{dialog.FileName}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            LogBox.Append($"[错误] 导出失败：{ex.Message}", LogLevel.Error);
        }
    }

    // ==================== 执行 ====================

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is not { Steps.Count: > 0 } plan) return;

        if (FastbootService.DetectDevice() is null)
        {
            MessageBox.Show("未检测到 Fastboot 设备。\n\n请让手机进入 Fastboot（bootloader）模式后再试。",
                            "无法开始", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = BuildConfirmText(plan);

        if (MessageBox.Show(confirm, "确认开始线刷", MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        _busy = true;
        _cts  = new CancellationTokenSource();

        StartButton.IsEnabled = false;
        AbortButton.IsEnabled = true;
        ParseButton.IsEnabled = false;
        BrowseRomButton.IsEnabled = false;
        ModeCombo.IsEnabled = false;
        FlashProgress.Value = 0;

        LogBox.Reset();

        // fastboot 的输出是在后台线程逐行回调过来的，这里统一切回 UI 线程再落日志
        void Write(string line, LogLevel? level = null)
        {
            void Apply()
            {
                if (level is null) LogBox.Append(line);
                else LogBox.Append(line, level.Value);
            }

            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply));
        }

        Action<string> log = line => Write(line);

        var progress = new Progress<XiaomiFlashService.FlashProgress>(p =>
        {
            FlashProgress.Value = p.Total == 0 ? 100 : p.Index * 100.0 / p.Total;
            CurrentStepText.Text = $"({p.Index}/{p.Total}) {p.Text}";
        });

        XiaomiFlashService.FlashResult result;
        try
        {
            result = await XiaomiFlashService.ExecuteAsync(
                plan, log, progress, StopOnErrorCheck.IsChecked != false, _cts.Token);
        }
        catch (Exception ex)
        {
            log($"[错误] 刷写中断：{ex.Message}");
            result = new XiaomiFlashService.FlashResult(false, 0, 0, 0, new[] { ex.Message }, false);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;

            AbortButton.IsEnabled     = false;
            BrowseRomButton.IsEnabled = true;
            ModeCombo.IsEnabled       = true;
            ParseButton.IsEnabled     = true;
            StartButton.IsEnabled     = true;
        }

        FlashProgress.Value = result.Failed == 0 && !result.Aborted ? 100 : FlashProgress.Value;

        log($"> 刷写结束");
        log($"[信息] 成功 {result.Done} 步，失败 {result.Failed} 步，跳过 {result.Skipped} 步。"
            + (result.Aborted ? "（已中止）" : string.Empty));

        foreach (var failure in result.Failures)
            log("[失败] " + failure);

        if (result.Ok)
        {
            CurrentStepText.Text = "全部步骤执行完毕。";
            Write("[成功] 线刷完成。首次开机会比较久，请耐心等待。", LogLevel.Success);
        }
        else
        {
            CurrentStepText.Text = result.Aborted
                ? $"已中止（成功 {result.Done} 步，失败 {result.Failed} 步）。"
                : $"结束：成功 {result.Done} 步，失败 {result.Failed} 步。";

            if (result.Failed > 0)
                Write("[建议] 请把上方报错行保存下来。若为「未解锁」或「分区不存在」，处理后再重新执行。",
                      LogLevel.Detail);
        }

        await DetectAsync();
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;

        AbortButton.IsEnabled = false;
        CurrentStepText.Text  = "正在中止…（当前命令跑完后停止）";
        LogBox.Append("[停止] 已请求中止，不再下发新命令。", LogLevel.Warning);
        _cts.Cancel();
    }

    /// <summary>拼确认框文本：把会清数据、会回锁、未解锁这些风险都摆在按钮前面。</summary>
    private static string BuildConfirmText(XiaomiFlashPlan plan)
    {
        var sb = new StringBuilder();

        sb.AppendLine("即将开始线刷，请核对：");
        sb.AppendLine();
        sb.AppendLine($"模式　：{plan.ModeLabel}");
        sb.AppendLine($"脚本　：{plan.ScriptName}");
        sb.AppendLine($"步骤　：{plan.Steps.Count} 步（刷入 {plan.FlashCount}，清空/擦除 {plan.WipeCount}）");
        sb.AppendLine($"镜像　：{FastbootService.FormatSize((ulong)plan.TotalBytes)}");
        sb.AppendLine();

        if (plan.Relocks)
            sb.AppendLine("注意：该模式会在刷完后重新锁定 Bootloader，之后想再刷机需重新解锁。");

        sb.AppendLine("刷写可能清空手机全部数据；过程中请勿拔线、勿断电。");
        sb.AppendLine();
        sb.Append("确认开始？");

        return sb.ToString();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Reset("等待操作…");
}
