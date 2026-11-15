using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class MtkPage : Page
{
    /// <summary>分区表视图中当前显示的行（受筛选框影响），绑定给 DataGrid。</summary>
    private readonly ObservableCollection<MtkPartition> _partitions = new();

    /// <summary>设备实际读回来的全部分区，筛选只改变 <see cref="_partitions"/> 的显示内容。</summary>
    private readonly List<MtkPartition> _loadedPartitions = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly List<string> _logBatch = new();
    private bool _logFlushQueued;
    private bool _nativeLogHooked;

    /// <summary>单次刷新最多写入的行数，避免一次灌太多造成掉帧。</summary>
    private const int LogFlushBatch = 256;

    /// <summary>当前是否处于窄窗口布局（日志挪到底部）。null 表示还没算过。</summary>
    private bool? _narrowLayout;

    public MtkPage()
    {
        InitializeComponent();

        // 窗口能被拖到 1020×680（侧边栏展开时内容区只剩 ~750px），
        // 三栏并排必被裁掉，所以按宽度在「右侧日志」和「底部日志」之间切换。
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        PartitionGrid.ItemsSource = _partitions;

        // NativeLog 是静态事件：只订阅不退订的话，页面每被导航到一次就多留一个处理器，
        // 同一条原生日志会被重复追加 N 遍，越用越像「刷屏」。所以成对地挂在 Loaded/Unloaded 上。
        Loaded += (_, _) =>
        {
            if (!_nativeLogHooked)
            {
                PenumbraService.NativeLog += OnNativeLog;
                _nativeLogHooked = true;
            }

            LoadOptions();
            _ = RefreshStatusAsync();
        };
        Unloaded += (_, _) =>
        {
            if (_nativeLogHooked)
            {
                PenumbraService.NativeLog -= OnNativeLog;
                _nativeLogHooked = false;
            }
        };
    }

    private void OnNativeLog(string line) => Append(line);

    /// <summary>
    /// 按页面宽度切换日志位置。宽窗（侧边栏收起或窗口拉大）时日志在右栏；
    /// 窄窗时日志落到操作区下方，避免右栏被挤到窗口外面看不见。
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        // 三栏最小总宽 214+340+16+250 = 820，加上页面左右 24 的边距约需 870。
        const double NarrowThreshold = 880;

        bool narrow = width < NarrowThreshold;
        if (_narrowLayout == narrow) return;
        _narrowLayout = narrow;

        LogRow.Height = narrow ? GridLength.Auto : new GridLength(0);

        // 窄窗要把日志列彻底收掉：只改 Width 不够，MinWidth 还会把列撑住
        LogColumn.MinWidth = narrow ? 0 : 250;
        LogColumn.MaxWidth = narrow ? double.PositiveInfinity : 420;
        LogColumn.Width    = narrow ? new GridLength(0) : new GridLength(0.9, GridUnitType.Star);
        GapColumn.Width    = narrow ? new GridLength(0) : new GridLength(16);

        Grid.SetRow(LogCard, narrow ? 2 : 1);
        Grid.SetColumn(LogCard, narrow ? 0 : 3);
        Grid.SetColumnSpan(LogCard, narrow ? 4 : 1);
        LogCard.Height = narrow ? 180 : double.NaN;
        LogCard.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        LogHead.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- 连接选项 ----------

    private MtkOptions CurrentOptions => new()
    {
        DaFile        = NullIfEmpty(DaFileBox.Text),
        PreloaderFile = NullIfEmpty(PlFileBox.Text),
        AuthFile      = NullIfEmpty(AuthFileBox.Text),
        UsbLog        = UsbLogCheck.IsChecked == true,
    };

    private void LoadOptions()
    {
        DaFileBox.Text         = SettingsService.MtkDaFile        ?? string.Empty;
        PlFileBox.Text         = SettingsService.MtkPreloaderFile ?? string.Empty;
        AuthFileBox.Text       = SettingsService.MtkAuthFile      ?? string.Empty;
        VerboseCheck.IsChecked = SettingsService.MtkVerbose;
        UsbLogCheck.IsChecked  = SettingsService.MtkUsbLog;

      
        BootFilesExpander.IsExpanded = string.IsNullOrEmpty(SettingsService.MtkDaFile);

        ApplyVerbose();
    }

    /// <summary>「详细日志」直接决定原生层的日志上限，无须重开设备会话，改完即时生效。</summary>
    private void ApplyVerbose() => PenumbraService.VerboseLogging = VerboseCheck.IsChecked == true;

    private void SaveOptions()
    {
        SettingsService.SaveMtkOptions(
            NullIfEmpty(DaFileBox.Text),
            NullIfEmpty(PlFileBox.Text),
            NullIfEmpty(AuthFileBox.Text),
            VerboseCheck.IsChecked == true,
            UsbLogCheck.IsChecked  == true);
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        SaveOptions();
        ApplyVerbose();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------- 状态 ----------

    private async Task RefreshStatusAsync()
    {
        if (!PenumbraService.IsAvailable)
        {
            ToolStatusText.Text   = "未就绪";
            DeviceStatusText.Text = "当前设备端口：无";
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            ToolStatusText.Text = "已就绪";

            var (ready, text, _) = await Task.Run(PenumbraService.InspectDevice);
            DeviceStatusText.Text = text;

            // 设备未就绪时用警示色，避免用户对着「Waiting for MTK device...」白等。
            if (ready)
                DeviceStatusText.ClearValue(TextBlock.ForegroundProperty);
            else if (TryFindResource("SystemFillColorCautionBrush") is Brush caution)
                DeviceStatusText.Foreground = caution;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    // ---------- 执行 ----------

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var cts = _cts;
        if (cts is null)
        {
            Append("[提示] 当前没有正在执行的操作。");
            return;
        }

    
        StopButton.IsEnabled = false;
        Append("[停止] 已请求中断，正在等待当前原生调用返回…（最长 30 秒，无法强制打断）");

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 操作刚好结束并释放了令牌，忽略 */ }
        catch (AggregateException) { /* 取消回调里抛出的异常不影响停止本身 */ }
    }

    /// <summary>
    /// 所有设备操作的统一外壳：忙检查 → 置运行标志 → 设备体检 → 执行体 → 复位。
    /// 置位必须排在体检之前，否则体检的 await 期间可以重入（见方法内注释）。
    /// run 接到取消令牌与输出回调；返回 0 表示成功，非 0 表示失败或已取消。
    /// force 为 true 时跳过「设备未就绪」确认，见 <see cref="ReadOnlyOps"/>。
    /// </summary>
    private async Task<int> RunGuardedAsync(string title,
                                            Func<CancellationToken, Action<string>, Task<int>> run,
                                            bool force = false)
    {
        if (_running)
        {
            Dialogs.Show("已有操作正在执行，请等待完成或先点击「停止」。", "忙",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return -1;
        }

     
        _running             = true;
        StopButton.IsEnabled = true;

        try
        {

            var (deviceReady, deviceText, deviceHint) = await Task.Run(PenumbraService.InspectDevice);
            if (!deviceReady && !force &&
                !Confirm($"{deviceText}\n\n{deviceHint}\n\n" +
                         "直接执行的话，会等满 30 秒超时后才报错。\n\n" +
                         "是否仍然尝试？", "设备未就绪"))
            {
                Append(string.Empty);
                Append($"[已取消] {deviceText}");
                Append($"[建议] {deviceHint}");
                return -1;
            }

            SaveOptions();

            Append(string.Empty);
            Append($"> {title}");

            if (!deviceReady)
                Append($"[警告] {deviceText} {deviceHint}");

            _cts = new CancellationTokenSource();
            return await run(_cts.Token, Append);
        }
        finally
        {
            _cts?.Dispose();
            _cts                 = null;
            _running             = false;
            StopButton.IsEnabled = false;
        }
    }

 
    private static readonly HashSet<string> ReadOnlyOps = new(StringComparer.Ordinal)
    {
        "info", "storage_info", "keys", "get_active_slot", "read_efuses",
        "read_partition", "read_flash", "read_all", "read_offset",
        "peek", "peek32", "rpmb_read",
    };

    /// <summary>从操作描述（匿名对象）里取出 op 名，判断是不是只读操作。</summary>
    private static bool IsReadOnlyOp(object op)
        => op.GetType().GetProperty("op")?.GetValue(op) is string name
           && ReadOnlyOps.Contains(name);

    /// <summary>
    /// 设备操作的统一入口：把一份 JSON 操作描述交给原生层执行。
    /// 参数只描述操作本身，不再有 CLI 命令串。
    /// </summary>
    private Task<int> ExecuteAsync(string title, object op)
    {
        if (!PenumbraService.IsAvailable) return ReportNativeMissingAsync();

        return RunGuardedAsync(title, async (ct, sink) =>
        {
            var (code, result) = await PenumbraService.ExecuteNativeAsync(
                CurrentOptions, op, sink, ct,
                UseLibusbCheck.IsChecked == true);

            if (code == 0 && !string.IsNullOrWhiteSpace(result))
                sink($"结果: {result}");

            sink(code == 0 ? $"[{title} 完成]" : $"[{title} 失败，原因见上方日志]");
            return code;
        }, force: IsReadOnlyOp(op));
    }

    private Task<int> ReportNativeMissingAsync()
    {
        Append(string.Empty);
        Append("[无法执行] 未找到原生层 penumbra_ffi.dll，请确认 tools 目录完整。");

        Warn("未找到原生层 penumbra_ffi.dll。\n\n" +
             "本程序已不再使用 antumbra.exe 子进程，请确认安装包的 tools 目录完整后重试。",
             "原生层不可用");

        return Task.FromResult(1);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logQueue.Clear();
        OutputBox.Clear();
    }

    /// <summary>
    /// 日志出口。可能从任意线程调用（原生日志回调 / 执行体）。
    /// 只做入队并安排一次刷新，绝不在调用线程上碰 UI，这样原生线程不会被界面拖住。
    /// </summary>
    private void Append(string line)
    {
        _logQueue.Enqueue(line);
        ScheduleLogFlush();
    }

    private void ScheduleLogFlush()
    {
        if (_logFlushQueued) return;
        _logFlushQueued = true;

        try
        {
            // 只保留一条待执行的刷新任务；Background(4) 低于 Input(5)/Render(7)，
            // 日志洪水让路给输入与重绘，界面保持可点。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushLogQueue));
        }
        catch (InvalidOperationException) { _logFlushQueued = false; }  // Dispatcher 正在关闭
        catch (TaskCanceledException) { _logFlushQueued = false; }
    }

    private void FlushLogQueue()
    {
        _logFlushQueued = false;

        _logBatch.Clear();
        for (var n = 0; n < LogFlushBatch && _logQueue.TryDequeue(out var line); n++)
            _logBatch.Add(line);

        // 分级着色、相邻同级合并、超上限裁剪都在 LogView 内部做，这里只管把这一批灌进去
        if (_logBatch.Count > 0) OutputBox.AppendBatch(_logBatch);

        // 队列里还有积压就再排一次，直到追平
        if (!_logQueue.IsEmpty) ScheduleLogFlush();
    }

    // ---------- 交互辅助 ----------

    private static bool Confirm(string message, string title = "危险操作确认")
        => Dialogs.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    private static void Warn(string message, string title = "提示")
        => Dialogs.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static string? PickOpenFile(string title, string filter = "所有文件|*.*")
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickSaveFile(string title, string defaultName, string filter = "所有文件|*.*")
    {
        var dlg = new SaveFileDialog
        {
            Title           = title,
            Filter          = filter,
            FileName        = defaultName,
            OverwritePrompt = false,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickFolder(string description)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description           = description,
            UseDescriptionForTitle = true,
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// 解析地址/长度类参数。与 penumbra 的 maybe_hex 保持一致：
    /// 带 0x 前缀按十六进制，否则按十进制。
    /// </summary>
    private static bool TryParseNumber(string? text, string fieldName, out ulong value)
    {
        value = 0;
        var s = (text ?? string.Empty).Trim();
        var isHex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (isHex) s = s[2..];

        if (s.Length == 0)
        {
            Warn($"{fieldName}不能为空。");
            return false;
        }

        var style = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!ulong.TryParse(s, style, CultureInfo.InvariantCulture, out value))
        {
            Warn($"{fieldName}格式不正确。十六进制请写 0x1000，十进制直接写 4096。");
            return false;
        }

        return true;
    }

    private static bool TryParseUInt(string? text, string fieldName, out uint value)
    {
        value = 0;
        if (!TryParseNumber(text, fieldName, out var wide)) return false;
        if (wide > uint.MaxValue)
        {
            Warn($"{fieldName}超出范围。");
            return false;
        }

        value = (uint)wide;
        return true;
    }

    private bool RequirePartition(out string partition)
    {
        partition = (PartNameBox.Text ?? string.Empty).Trim();
        if (partition.Length > 0) return true;

        Warn("请先填写分区名，或在上方分区表中点击一行。");
        return false;
    }

    private bool RequireFile(string? path, string fieldName, bool mustExist)
    {
        var value = (path ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            Warn($"{fieldName}不能为空。");
            return false;
        }

        if (mustExist && !File.Exists(value))
        {
            Warn($"{fieldName}不存在：{value}");
            return false;
        }

        return true;
    }

    // ---------- 连接选项：文件选择 ----------

    private void DaBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 DA 文件");
        if (file is null) return;
        DaFileBox.Text = file;
        SaveOptions();
    }

    private void PlBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 Preloader 文件");
        if (file is null) return;
        PlFileBox.Text = file;
        SaveOptions();
    }

    private void AuthBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 Auth 文件");
        if (file is null) return;
        AuthFileBox.Text = file;
        SaveOptions();
    }

    // ---------- 分区表 ----------

    private async void LoadPartition_Click(object sender, RoutedEventArgs e)
    {
        if (!PenumbraService.IsAvailable)
        {
            await ReportNativeMissingAsync();
            return;
        }

        var loaded = new List<MtkPartition>();

        var code = await RunGuardedAsync("读取分区表", async (ct, sink) =>
        {
            // 后端要和下面读/写分区时传的完全一致，否则会被算成两个会话，每次换操作都要重握手
            var (partitions, exit) = await PenumbraService.GetPartitionsAsync(
                CurrentOptions, sink, ct, UseLibusbCheck.IsChecked == true);
            loaded = partitions;

            // 原生侧读分区表失败时会静默返回空表（exit 仍是 0），此时打「完成」会把真正的原因
            // 盖住 —— 界面下方的提示又会说「确认设备是否已进入 DA 模式」，把人往错的方向带。
            // 这里按结果区分，空表一律当失败报。
            sink(exit != 0
                ? "[读取分区表 失败，原因见上方日志]"
                : partitions.Count > 0
                    ? $"[读取分区表 完成，共 {partitions.Count} 个分区]"
                    : "[读取分区表 失败：设备没有返回分区表，原因见上方日志]");

            return exit;
        }, force: true);

        _loadedPartitions.Clear();
        if (code == 0)
            _loadedPartitions.AddRange(loaded);

        ApplyPartitionFilter();
        BackupSelectedButton.IsEnabled = false;
        EraseSelectedButton.IsEnabled  = false;
    }

    /// <summary>
    /// 按筛选框里的关键字重建分区表视图。
    ///
    /// 一台机器的分区表动辄上百行，而表格只有 300px 高，想找 super / boot 这类目标
    /// 只能靠肉眼在滚动条里翻。这里让关键字直接过滤：空格、半角逗号、全角逗号或制表符
    /// 分隔多个词，全部命中才算匹配（AND），匹配范围是分区名与类型。
    ///
    /// 筛选只影响显示，不改变 <see cref="_loadedPartitions"/>；导出始终导出全表，
    /// 免得筛着筛着就导出一份残缺的分区表。
    /// </summary>
    private void ApplyPartitionFilter()
    {
        var terms = (PartFilterBox.Text ?? string.Empty)
            .Split(new[] { ' ', ',', '，', '\t' },
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _partitions.Clear();
        foreach (var partition in _loadedPartitions)
        {
            if (MatchesFilter(partition, terms)) _partitions.Add(partition);
        }

        PartitionCountText.Text = _loadedPartitions.Count == 0
            ? "未读到分区，原因见下方日志。"
            : terms.Length == 0
                ? $"共 {_loadedPartitions.Count} 个分区。"
                : $"筛选出 {_partitions.Count} / {_loadedPartitions.Count} 个分区（关键字：{string.Join(" ", terms)}）。";

        ExportPartitionButton.IsEnabled = _loadedPartitions.Count > 0;

        // 过滤后原选中行可能已经不在视图里，批量按钮的可用性要跟着重算
        SyncSelection();
    }

    private static bool MatchesFilter(MtkPartition partition, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!partition.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !partition.Section.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void PartFilter_Changed(object sender, TextChangedEventArgs e)
    {
        // 还没读过分区表时筛选没有意义，也不该把「尚未读取」的提示覆盖成「未读到分区」。
        if (_loadedPartitions.Count == 0) return;

        ApplyPartitionFilter();
    }

    private void ExportPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedPartitions.Count == 0) return;

        var path = PickSaveFile("导出分区表", "partition_table.csv", "CSV 文件|*.csv");
        if (path is null) return;

        try
        {
            // 导出全表而不是当前筛选结果：按钮叫「导出分区表」，导出一份被筛过的表很容易被误当成全量备份依据。
            var sb = new StringBuilder();
            sb.AppendLine("Name,Address,Size,SizeHuman,Section");
            foreach (var p in _loadedPartitions)
                sb.AppendLine($"{p.Name},{p.AddressHex},{p.Size},{p.SizeHuman},{p.Section}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Warn($"已导出 {_loadedPartitions.Count} 条记录到：\n{path}", "导出完成");
        }
        catch (Exception ex)
        {
            Warn($"导出失败：{ex.Message}", "错误");
        }
    }

    private void PartitionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection();

    /// <summary>按当前选中行更新分区名输入框与批量按钮的可用性。</summary>
    private void SyncSelection()
    {
        // 单选时顺手把分区名填进「分区读写」；多选时只更新批量按钮的可用性。
        var selected = SelectedPartitions();

        if (selected.Count == 1)
            PartNameBox.Text = selected[0].Name;

        BackupSelectedButton.IsEnabled = selected.Count > 0;
        EraseSelectedButton.IsEnabled  = selected.Count > 0;
    }

    /// <summary>取分区表里当前选中的分区（支持 Ctrl / Shift 多选）。</summary>
    private List<MtkPartition> SelectedPartitions()
        => PartitionGrid.SelectedItems.OfType<MtkPartition>().ToList();

    /// <summary>
    /// 批量操作外壳：体检设备 → 开一个会话跑完全部项。
    /// 逐项共用会话，避免「每个分区各握一次 BROM 手」。
    /// </summary>
    private Task<int> ExecuteBatchAsync(string title, IReadOnlyList<PenumbraService.NativeBatchItem> items)
    {
        if (!PenumbraService.IsAvailable) return ReportNativeMissingAsync();

        return RunGuardedAsync(title, async (ct, sink) =>
        {
            var (ok, bad) = await PenumbraService.ExecuteBatchAsync(
                CurrentOptions, items, sink, ct,
                UseLibusbCheck.IsChecked == true);

            sink($"[{title} 结束：成功 {ok} 项，失败 {bad} 项]");
            return bad == 0 ? 0 : 1;
        });
    }

    /// <summary>
    /// 把选中分区备份成一个「备份集」：镜像统一落在 backup_&lt;时间戳&gt;\ 下，
    /// 同时写一份清单，记录每个镜像对应的分区 / 地址 / 大小，便于日后核对与还原。
    /// </summary>
    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPartitions();
        if (selected.Count == 0)
        {
            Warn("请先在分区表中选中至少一个分区（按住 Ctrl / Shift 可多选）。");
            return;
        }

        var root = PickFolder("选择备份集保存位置");
        if (root is null) return;

        var preview = string.Join('\n', selected.Take(8).Select(p => $"  {p.Name}（{p.SizeHuman}）"));
        if (selected.Count > 8) preview += $"\n  ……等共 {selected.Count} 个分区";

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir   = Path.Combine(root, $"backup_{stamp}");

        if (!Confirm($"即将备份以下分区：\n\n{preview}\n\n保存到：\n{dir}\n\n是否继续？", "备份备份集"))
            return;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Warn($"无法创建目录：\n{ex.Message}", "错误");
            return;
        }

        var items = selected
            .Select(p => new PenumbraService.NativeBatchItem(
                $"备份 {p.Name}",
                new { op = "read_partition", part = p.Name, file = Path.Combine(dir, $"{p.Name}.bin") }))
            .ToList();

        var code = await ExecuteBatchAsync($"备份选中分区（{selected.Count} 个）", items);

        WriteBackupManifest(dir, selected, stamp, code == 0);
    }

    /// <summary>写备份集清单。清单本身写不出来不影响已落盘的镜像，所以只静默忽略。</summary>
    private static void WriteBackupManifest(string dir, IEnumerable<MtkPartition> partitions,
                                            string stamp, bool completed)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Address,Size,SizeHuman,Section,File");
            foreach (var p in partitions)
                sb.AppendLine($"{p.Name},{p.AddressHex},{p.Size},{p.SizeHuman},{p.Section},{p.Name}.bin");

            File.WriteAllText(Path.Combine(dir, "backup-manifest.csv"), sb.ToString(), Encoding.UTF8);

            File.WriteAllText(Path.Combine(dir, "backup-readme.txt"),
                $"备份集：{stamp}\r\n状态：{(completed ? "全部成功" : "部分失败，请对照日志确认")}\r\n\r\n" +
                "还原方式：在「整机写入」中选择本目录，程序会按 <分区名>.bin 逐个写回对应分区。\r\n" +
                "请勿重命名 .bin 文件——文件名与分区名的对应关系就是还原依据。\r\n",
                Encoding.UTF8);
        }
        catch { /* 清单非关键，忽略 */ }
    }

    private async void EraseSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPartitions();
        if (selected.Count == 0)
        {
            Warn("请先在分区表中选中至少一个分区（按住 Ctrl / Shift 可多选）。");
            return;
        }

        var names = string.Join("、", selected.Select(p => p.Name));
        if (!Confirm($"即将擦除以下 {selected.Count} 个分区，数据不可恢复：\n\n{names}\n\n" +
                     "注意：擦除 boot / preloader 等关键分区会让设备无法开机。是否继续？",
                     "批量擦除确认"))
            return;

        var items = selected
            .Select(p => new PenumbraService.NativeBatchItem(
                $"擦除 {p.Name}", new { op = "erase_flash", part = p.Name }))
            .ToList();

        await ExecuteBatchAsync($"擦除选中分区（{selected.Count} 个）", items);
    }

    // ---------- 分区读写 ----------

    private void ImageBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择镜像文件", "镜像文件|*.img;*.bin|所有文件|*.*");
        if (file is not null) ImageFileBox.Text = file;
    }

    /// <summary>读取类操作：文件框为空时自动弹出保存对话框。</summary>
    private static string? ResolveReadTarget(string? boxText, string defaultName)
    {
        var current = (boxText ?? string.Empty).Trim();
        return current.Length > 0
            ? current
            : PickSaveFile("选择保存位置", defaultName, "镜像文件|*.img;*.bin|所有文件|*.*");
    }

    private async void ReadPart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;

        var target = ResolveReadTarget(ImageFileBox.Text, $"{partition}.img");
        if (target is null) return;
        ImageFileBox.Text = target;

        await ExecuteAsync($"读取分区 {partition}",
            new { op = "read_partition", part = partition, file = target });
    }

    private async void ReadFlashPart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;

        var target = ResolveReadTarget(ImageFileBox.Text, $"{partition}.img");
        if (target is null) return;
        ImageFileBox.Text = target;

        await ExecuteAsync($"读取分区(read-flash) {partition}",
            new { op = "read_flash", part = partition, file = target });
    }

    private async void WritePart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;
        if (!RequireFile(ImageFileBox.Text, "镜像文件", mustExist: true)) return;

        var file = ImageFileBox.Text.Trim();
        if (!Confirm($"即将把镜像写入设备分区，请最后确认：\n\n分区：{partition}\n镜像：{file}\n\n写入错误的分区可能导致设备无法开机。是否继续？"))
            return;

        await ExecuteAsync($"写入分区 {partition}",
            new { op = "download", part = partition, file });
    }

    private async void WriteFlashPart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;
        if (!RequireFile(ImageFileBox.Text, "镜像文件", mustExist: true)) return;

        var file = ImageFileBox.Text.Trim();
        if (!Confirm($"即将以 flash 方式写入设备分区：\n\n分区：{partition}\n镜像：{file}\n\n是否继续？"))
            return;

        await ExecuteAsync($"写入分区(write-flash) {partition}",
            new { op = "write_flash", part = partition, file });
    }

    private async void WritePartition_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;
        if (!RequireFile(ImageFileBox.Text, "镜像文件", mustExist: true)) return;

        var file = ImageFileBox.Text.Trim();
        if (!Confirm($"即将直接写入设备分区（不做大小校验）：\n\n分区：{partition}\n镜像：{file}\n\n" +
                     "这条路径会跳过 download 的分区大小检查，镜像过大时可能破坏分区之后的数据。\n\n是否继续？"))
            return;

        await ExecuteAsync($"写入分区(write-partition) {partition}",
            new { op = "write_partition", part = partition, file });
    }

    private async void FormatPart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;

        if (!Confirm($"即将格式化分区 {partition}，该分区内的数据会被清除且无法恢复。是否继续？"))
            return;

        await ExecuteAsync($"格式化分区 {partition}",
            new { op = "erase_partition", part = partition });
    }

    private async void ErasePart_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePartition(out var partition)) return;

        if (!Confirm($"即将擦除分区 {partition}，操作不可恢复。\n\n注意：擦除 boot / preloader 等关键分区会让设备无法开机。是否继续？"))
            return;

        await ExecuteAsync($"擦除分区 {partition}",
            new { op = "erase_flash", part = partition });
    }

    // ---------- 整机读取 ----------

    private void ReadAllBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder("选择用于存放全部镜像的目录");
        if (dir is not null) ReadAllDirBox.Text = dir;
    }

    private async void ReadAll_Click(object sender, RoutedEventArgs e)
    {
        var dir = (ReadAllDirBox.Text ?? string.Empty).Trim();
        if (dir.Length == 0)
        {
            dir = PickFolder("选择用于存放全部镜像的目录") ?? string.Empty;
            if (dir.Length == 0) return;
            ReadAllDirBox.Text = dir;
        }

        var skip     = (SkipBox.Text ?? string.Empty).Trim();
        var skipList = skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!Confirm($"即将读取设备的全部分区并保存到：\n\n{dir}\n\n过程耗时较长，请保持设备连接。是否继续？"))
            return;

        await ExecuteAsync("读取全部分区",
            new { op = "read_all", dir, skip = skipList });
    }

    // ---------- 整机写入 ----------

    private void WriteAllBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder("选择存放全部镜像的目录");
        if (dir is not null) WriteAllDirBox.Text = dir;
    }

    private async void WriteAll_Click(object sender, RoutedEventArgs e)
    {
        var dir = (WriteAllDirBox.Text ?? string.Empty).Trim();
        if (dir.Length == 0)
        {
            dir = PickFolder("选择存放全部镜像的目录") ?? string.Empty;
            if (dir.Length == 0) return;
            WriteAllDirBox.Text = dir;
        }

        if (!Directory.Exists(dir))
        {
            Warn($"镜像目录不存在：\n{dir}");
            return;
        }

        if (!VerifyWriteAllManifest(dir)) return;

        var skip     = (WriteAllSkipBox.Text ?? string.Empty).Trim();
        var skipList = skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await ExecuteAsync("写入全部分区",
            new { op = "write_all", dir, skip = skipList });
    }

    /// <summary>
    /// 「整机写入」前的清单核对。
    ///
    /// 原生层是按目录里实际存在的 &lt;分区名&gt;.bin 逐个写回，缺哪个就静默跳过哪个，
    /// 最后照样报「完成」——用户不会知道有分区根本没写进去。所以这里先把目录里的镜像
    /// 和备份集清单（backup-manifest.csv，由「备份选中」写出）对一遍，把结果摆出来让人确认。
    ///
    /// 目录里没有清单时，退回按目录中的 *.bin 现列一份：信息少一点，但至少能看清这次
    /// 到底要写哪些分区，而不是闭着眼睛刷。
    ///
    /// 返回 false 表示用户看完后决定放弃。
    /// </summary>
    private static bool VerifyWriteAllManifest(string dir)
    {
        var manifest = Path.Combine(dir, "backup-manifest.csv");

        if (File.Exists(manifest))
        {
            string[] rows;
            try
            {
                rows = File.ReadAllLines(manifest);
            }
            catch (Exception ex)
            {
                Warn($"读取备份集清单失败：\n{ex.Message}\n\n将改为按目录中的 .bin 列出待写分区。", "清单读取失败");
                rows = Array.Empty<string>();
            }

            var present = new List<string>();
            var missing = new List<string>();

            // 跳过表头：Name,Address,Size,SizeHuman,Section,File
            foreach (var row in rows.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(row)) continue;

                var fields = row.Split(',');
                var name   = fields[0].Trim();
                if (name.Length == 0) continue;

                // File 列可能缺失（手工编辑过清单），按约定回退到 <分区名>.bin
                var file = fields.Length > 5 && fields[5].Trim().Length > 0
                    ? fields[5].Trim()
                    : $"{name}.bin";

                if (File.Exists(Path.Combine(dir, file))) present.Add(name);
                else                                     missing.Add($"{name}（{file}）");
            }

            if (present.Count + missing.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"目录：{dir}");
                sb.AppendLine($"清单：backup-manifest.csv，共 {present.Count + missing.Count} 项");
                sb.AppendLine($"镜像齐全：{present.Count} 项");
                sb.AppendLine();

                if (missing.Count == 0)
                {
                    sb.AppendLine("清单核对通过，全部镜像都在。");
                }
                else
                {
                    sb.AppendLine($"缺少 {missing.Count} 项镜像，这些分区会被跳过（保持设备当前内容）：");
                    foreach (var item in missing.Take(20)) sb.AppendLine($"  ! {item}");
                    if (missing.Count > 20) sb.AppendLine($"  ……等共 {missing.Count} 项");
                }

                sb.AppendLine();
                sb.AppendLine("写入错误的分区可能导致设备无法开机，请确认镜像与设备型号匹配。");

                return Confirm(sb.ToString(),
                               missing.Count == 0 ? "整机写入确认（清单已核对）"
                                                  : "整机写入确认（有镜像缺失）");
            }
        }

        var images = Directory.EnumerateFiles(dir, "*.bin")
                              .Select(Path.GetFileNameWithoutExtension)
                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                              .ToList();

        if (images.Count == 0)
        {
            Warn($"目录里没有任何 .bin 镜像：\n{dir}\n\n" +
                 "写回是按 <分区名>.bin 找文件的，这里没有可写的内容。", "没有可写镜像");
            return false;
        }

        var text = new StringBuilder();
        text.AppendLine($"目录：{dir}");
        text.AppendLine($"未找到备份集清单，按目录中的 .bin 列出，共 {images.Count} 项：");
        foreach (var name in images.Take(20)) text.AppendLine($"  {name}");
        if (images.Count > 20) text.AppendLine($"  ……等共 {images.Count} 项");
        text.AppendLine();
        text.AppendLine("写入错误的分区可能导致设备无法开机，请确认镜像与设备型号匹配。");

        return Confirm(text.ToString(), "整机写入确认");
    }

    // ---------- 偏移读写 ----------

    private static string Hex(ulong value) => $"0x{value:X}";

    private void OffsetBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择文件");
        if (file is not null) OffsetFileBox.Text = file;
    }

    private async void ReadOffset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(OffsetAddrBox.Text, "地址", out var address)) return;
        if (!TryParseNumber(OffsetLenBox.Text, "长度", out var length)) return;

        var target = ResolveReadTarget(OffsetFileBox.Text, "offset.bin");
        if (target is null) return;
        OffsetFileBox.Text = target;

        await ExecuteAsync("读取偏移",
            new { op = "read_offset", address, length, file = target });
    }

    private async void WriteOffset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(OffsetAddrBox.Text, "地址", out var address)) return;
        if (!TryParseNumber(OffsetLenBox.Text, "长度", out var length)) return;
        if (!RequireFile(OffsetFileBox.Text, "文件", mustExist: true)) return;

        var file = OffsetFileBox.Text.Trim();
        if (!Confirm($"即将直接写入设备 flash：\n\n地址：{Hex(address)}\n长度：{Hex(length)}\n文件：{file}\n\n绕过分区表直写 flash 风险很高，是否继续？"))
            return;

        await ExecuteAsync("写入偏移",
            new { op = "write_offset", address, length, file });
    }

    private async void EraseOffset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(OffsetAddrBox.Text, "地址", out var address)) return;
        if (!TryParseNumber(OffsetLenBox.Text, "长度", out var length)) return;

        if (!Confirm($"即将擦除设备 flash：\n\n地址：{Hex(address)}\n长度：{Hex(length)}\n\n" +
                     "该范围内的数据会被清除且不可恢复。是否继续？"))
            return;

        await ExecuteAsync("擦除偏移",
            new { op = "erase_offset", address, length });
    }

    // ---------- 内存读写 ----------

    private void MemBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择文件");
        if (file is not null) MemFileBox.Text = file;
    }

    private async void Peek_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(MemAddrBox.Text, "地址", out var address)) return;
        if (!TryParseNumber(MemLenBox.Text, "长度", out var length)) return;

        var target = ResolveReadTarget(MemFileBox.Text, "memory.bin");
        if (target is null) return;
        MemFileBox.Text = target;

        await ExecuteAsync("读取内存",
            new { op = "peek", address, length, file = target });
    }

    private async void Poke_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(MemAddrBox.Text, "地址", out var address)) return;
        if (!RequireFile(MemFileBox.Text, "文件", mustExist: true)) return;

        var file = MemFileBox.Text.Trim();
        if (!Confirm($"即将直接改写设备内存：\n\n地址：{Hex(address)}\n文件：{file}\n\n写入错误地址可能立即让设备异常，且无法恢复。如不确定，请选择「否」。"))
            return;

        // 原生 poke 协议：address + length（由文件长度推导）+ file
        await ExecuteAsync("写入内存",
            new { op = "poke", address, length = (ulong)new System.IO.FileInfo(file).Length, file });
    }

    // ---------- RPMB 安全区 ----------

    private void RpmbBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择文件");
        if (file is not null) RpmbFileBox.Text = file;
    }

    private async void RpmbRead_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUInt(RpmbRegionBox.Text, "区域", out var region)) return;
        if (!TryParseUInt(RpmbStartBox.Text, "起始扇区", out var start)) return;
        uint? sectors = null;
        var countText = (RpmbCountBox.Text ?? string.Empty).Trim();
        if (countText.Length > 0)
        {
            if (!TryParseUInt(countText, "扇区数", out var count)) return;
            sectors = count;
        }

        var target = ResolveReadTarget(RpmbFileBox.Text, "rpmb.bin");
        if (target is null) return;
        RpmbFileBox.Text = target;

        await ExecuteAsync("读取 RPMB",
            new { op = "rpmb_read", region, start, sectors, file = target });
    }

    private async void RpmbWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUInt(RpmbRegionBox.Text, "区域", out var region)) return;
        if (!TryParseUInt(RpmbStartBox.Text, "起始扇区", out var start)) return;
        uint? sectors = null;
        var countText = (RpmbCountBox.Text ?? string.Empty).Trim();
        if (countText.Length > 0)
        {
            if (!TryParseUInt(countText, "扇区数", out var count)) return;
            sectors = count;
        }

        if (!RequireFile(RpmbFileBox.Text, "数据文件", mustExist: true)) return;

        var file = RpmbFileBox.Text.Trim();

        if (!Confirm($"即将写入 RPMB 安全区：\n\n文件：{file}\n起始扇区：{start}，扇区数：{sectors?.ToString() ?? "到末尾"}\n\nRPMB 写入不可撤销，写错会导致指纹 / 密钥永久丢失。请确认已完整备份。"))
            return;

        await ExecuteAsync("写入 RPMB",
            new { op = "rpmb_write", region, start, sectors, file });
    }

    private async void RpmbAuth_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUInt(RpmbRegionBox.Text, "区域", out var region)) return;

        var key = (RpmbKeyBox.Text ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            Warn("请先填写认证密钥（十六进制字符串）。");
            return;
        }

        await ExecuteAsync("RPMB 认证",
            new { op = "rpmb_auth", region, key });
    }

    private async void RpmbErase_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseUInt(RpmbRegionBox.Text, "区域", out var region)) return;
        if (!TryParseUInt(RpmbStartBox.Text, "起始扇区", out var start)) return;
        uint? sectors = null;
        var countText = (RpmbCountBox.Text ?? string.Empty).Trim();
        if (countText.Length > 0)
        {
            if (!TryParseUInt(countText, "扇区数", out var count)) return;
            sectors = count;
        }

        if (!Confirm($"即将擦除 RPMB 安全区：\n\n起始扇区：{start}，扇区数：{sectors?.ToString() ?? "到末尾"}\n\n" +
                     "擦除会永久清除该范围内的指纹 / 密钥数据，且不可恢复。是否继续？"))
            return;

        await ExecuteAsync("擦除 RPMB",
            new { op = "rpmb_erase", region, start, sectors });
    }

    // ---------- 设备控制 ----------

    private Task<int> RebootAsync(string mode)
    {
        // 原生层的重启模式：home-screen 在该层写作 homescreen
        var nativeMode = mode == "home-screen" ? "homescreen" : mode;
        return ExecuteAsync($"重启（{mode}）",
            new { op = "reboot", mode = nativeMode });
    }

    // ---------- 设备控制 ----------
    // 这一组操作原先是「一个下拉框装 15 项 + 一个执行按钮」。问题在于列表里只读查询
    // （存储信息、设备密钥）和会改动设备的操作（解锁引导校验、崩溃至 BROM）长得一模一样，
    // 用户点下去之前看不出风险。现在一项一个按钮，按用途分组，危险项单独成组。

    private async void DevRebootSystem_Click(object sender, RoutedEventArgs e)
        => await RebootAsync("normal");

    private async void DevRebootFastboot_Click(object sender, RoutedEventArgs e)
        => await RebootAsync("fastboot");

    private async void DevRebootHome_Click(object sender, RoutedEventArgs e)
        => await RebootAsync("home-screen");

    private async void DevRebootMeta_Click(object sender, RoutedEventArgs e)
        => await RebootAsync("meta");

    private async void DevRebootTest_Click(object sender, RoutedEventArgs e)
        => await RebootAsync("test");

    private async void DevShutdown_Click(object sender, RoutedEventArgs e)
        => await ExecuteAsync("关机", new { op = "shutdown" });

    private async void DevCrash_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("「崩溃至 BROM」会让设备异常复位并停在 BootROM 模式，用于进入可刷写状态。\n\n设备会立即失去响应，是否继续？"))
            await ExecuteAsync("崩溃至 BROM", new { op = "crash" });
    }

    private async void DevSlotA_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("活动槽决定下次从哪个槽（A / B）启动。\n\n是否切换到槽 A？"))
            await ExecuteAsync("切换活动槽到 A", new { op = "set_active_slot", slot = "a" });
    }

    private async void DevSlotB_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("活动槽决定下次从哪个槽（A / B）启动。\n\n是否切换到槽 B？"))
            await ExecuteAsync("切换活动槽到 B", new { op = "set_active_slot", slot = "b" });
    }

    private async void DevActiveSlot_Click(object sender, RoutedEventArgs e)
        => await ExecuteAsync("读取活动槽", new { op = "get_active_slot" });

    private async void DevStorageInfo_Click(object sender, RoutedEventArgs e)
        => await ExecuteAsync("存储信息", new { op = "storage_info" });

    private async void DevInfo_Click(object sender, RoutedEventArgs e)
        => await ExecuteAsync("芯片信息", new { op = "info" });

    private async void DevKeys_Click(object sender, RoutedEventArgs e)
        => await ExecuteAsync("读取设备密钥", new { op = "keys" });

    private async void DevSeccfgUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("解锁 seccfg 会关闭引导校验，可能导致设备无法开机（变砖），并可能清除用户数据。\n\n这是不可逆的高风险操作，是否继续？"))
            await ExecuteAsync("解锁 seccfg", new { op = "seccfg", action = "unlock" });
    }

    private async void DevSeccfgLock_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("加锁 seccfg 会恢复引导校验。请确认设备当前系统可正常启动，否则可能无法开机。\n\n是否继续？"))
            await ExecuteAsync("加锁 seccfg", new { op = "seccfg", action = "lock" });
    }

    private async void DevEfuseRead_Click(object sender, RoutedEventArgs e)
    {
        // 参数顺序是 (标题, 默认文件名, 过滤器)。原先传成了 (标题, 过滤器, 默认文件名)，
        // 结果是保存框里文件名一栏填着整串过滤规则。
        var file = PickSaveFile("保存 eFuse 数据", "efuses.bin", "eFuse 数据|*.bin|所有文件|*.*");
        if (file is null) return;

        await ExecuteAsync("读取 eFuse", new { op = "read_efuses", file });
    }

    private async void DevEfuseWrite_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择要烧入的 eFuse 数据文件", "eFuse 数据|*.bin|所有文件|*.*");
        if (file is null) return;

        // 原生层对 write_efuses 要求 confirm + aware 双确认，这里用两道弹窗对应。
        // eFuse 是单向熔丝，写错不可回退，所以刻意让人停两次。
        if (!Confirm($"即将把 eFuse 数据烧入设备：\n\n文件：{file}\n\n" +
                     "eFuse 是一次性熔丝，烧写后无法修改也无法擦除，错误的 eFuse 会让设备永久无法修复。\n\n" +
                     "请确认你确实要烧写这个文件。", "eFuse 烧写"))
            return;

        if (!Confirm("最后确认：这一步会永久改变设备的硬件级配置，且不可撤销。\n\n是否仍然写入？",
                     "不可逆操作"))
            return;

        await ExecuteAsync("写入 eFuse",
            new { op = "write_efuses", file, confirm = true, aware = true });
    }

    // ---------- XFlash 刷写 ----------

    private void XfBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择镜像文件", "镜像文件|*.img;*.bin|所有文件|*.*");
        if (file is not null) XfFileBox.Text = file;
    }

    private async void XfFlash_Click(object sender, RoutedEventArgs e)
    {
        var partition = (XfPartBox.Text ?? string.Empty).Trim();
        if (partition.Length == 0)
        {
            Warn("请先填写目标分区名。");
            return;
        }

        if (!RequireFile(XfFileBox.Text, "镜像文件", mustExist: true)) return;

        var file = XfFileBox.Text.Trim();
        if (!Confirm($"即将以 XFlash 方式刷写：\n\n分区：{partition}\n镜像：{file}\n\n是否继续？"))
            return;

        await ExecuteAsync($"XFlash 刷写 {partition}",
            new { op = "xflash_rsc_flash", part = partition, file });
    }

    // ---------- BROM payload 注入 ----------

    private void BootPlBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 payload 文件", "payload / Preloader|*.*");
        if (file is null) return;

        BootPlFileBox.Text = file;

        // 文件名里带 payload 的多半就是裸 payload，顺手勾上，省一次手动切换
        var name = Path.GetFileNameWithoutExtension(file);
        if (name.Contains("payload", StringComparison.OrdinalIgnoreCase))
            BootPlRawCheck.IsChecked = true;
    }

    /// <summary>
    /// 通过 BROM exploit 注入 payload 并跳转执行。
    ///
    /// - 普通模式：文件按 Preloader 解析，加载地址取自 GFH 头（可手动覆盖）；
    /// - 裸 payload：文件原样送到指定地址，必须填地址。
    /// 两条路径都由原生层的 boot_preloader 完成，且只接受 BROM 连接。
    /// </summary>
    private async void BootPlInject_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile(BootPlFileBox.Text, "payload 文件", mustExist: true)) return;

        var file = BootPlFileBox.Text.Trim();
        var raw  = BootPlRawCheck.IsChecked == true;

        var addrText = (BootPlAddrBox.Text ?? string.Empty).Trim();
        uint? address = null;
        if (addrText.Length > 0)
        {
            if (!TryParseUInt(addrText, "加载地址", out var parsed)) return;
            address = parsed;
        }

        if (raw && address is null)
        {
            Warn("「裸 payload」必须填写加载地址（例如 0x201000）。");
            return;
        }

        var mode = raw
            ? $"裸 payload → {Hex(address!.Value)}"
            : address is null
                ? "Preloader（地址按 GFH 头自动推导）"
                : $"Preloader → {Hex(address.Value)}";

        if (!Confirm($"即将通过 BROM exploit 注入 payload：\n\n文件：{file}\n方式：{mode}\n\n" +
                     "设备必须处于 BROM 模式；注入后会立刻跳转执行。\n" +
                     "payload 与设备型号不匹配会让设备无响应。是否继续？",
                     "BROM payload 注入"))
            return;

        await ExecuteAsync("BROM payload 注入",
            new { op = "boot_preloader", file, raw, address });
    }

    // ---------- scatter 整包刷写 ----------

    private void ScatterBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 scatter 文件", "scatter 文件|*.xml;*.txt|所有文件|*.*");
        if (file is not null) ScatterFileBox.Text = file;
    }

    private async void ScatterFlash_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile(ScatterFileBox.Text, "scatter 文件", mustExist: true)) return;

        var file = ScatterFileBox.Text.Trim();
        if (!Confirm($"即将按 scatter 整包刷写设备：\n\nscatter：{file}\n\n" +
                     "该操作会依据 scatter 覆盖多个分区，刷入不匹配的固件可能导致设备无法开机。\n" +
                     "请确认 scatter 与镜像成套、且与设备型号匹配。是否继续？",
                     "整包刷写确认"))
            return;

        await ExecuteAsync("scatter 整包刷写",
            new { op = "scatter", file });
    }

    // ---------- 32 位寄存器 ----------
    // peek / poke 是「内存 + 文件」的路子；寄存器只关心一个 32 位值，
    // 因此原生层单独提供了 peek32 / poke32，这里也不需要文件框。

    private async void RegRead_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(RegAddrBox.Text, "地址", out var address)) return;

        await ExecuteAsync("读取寄存器", new { op = "peek32", address });
    }

    private async void RegWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseNumber(RegAddrBox.Text, "地址", out var address)) return;
        if (!TryParseUInt(RegValueBox.Text, "值", out var value)) return;

        if (!Confirm($"即将直接改写设备寄存器：\n\n地址：{Hex(address)}\n值：{Hex(value)}\n\n" +
                     "写错寄存器可能立即让设备异常甚至无法开机，且难以恢复。如不确定，请选择「否」。"))
            return;

        await ExecuteAsync("写入寄存器", new { op = "poke32", address, value });
    }

    // ---------- RPMB 锁状态 ----------

    private async void RpmbLock_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("「锁定 RPMB」会把安全分区置为锁定状态，此后无法再写入 RPMB 数据。\n\n是否继续？"))
            return;

        await ExecuteAsync("锁定 RPMB", new { op = "rpmb_lock", action = "lock" });
    }

    private async void RpmbUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("「解锁 RPMB」会把 RPMB 恢复为默认（全 0）密钥状态，已写入的受保护数据可能因此不可读。\n\n" +
                     "该操作仅对 UFS 存储生效，且不可撤销。是否继续？"))
            return;

        await ExecuteAsync("解锁 RPMB", new { op = "rpmb_lock", action = "unlock" });
    }

    // ---------- SEJ AES 加解密 ----------

    private void SejInBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择要处理的文件");
        if (file is not null) SejInBox.Text = file;
    }

    private void SejOutBrowse_Click(object sender, RoutedEventArgs e)
    {
        var source = (SejInBox.Text ?? string.Empty).Trim();
        var defaultName = source.Length > 0 ? Path.GetFileName(source) + ".out" : "sej-out.bin";

        var file = PickSaveFile("选择输出文件", defaultName);
        if (file is not null) SejOutBox.Text = file;
    }

    /// <summary>
    /// 取 ComboBox 选中项的「首个词」。选项内容形如 "sw（软件密钥）"、"256"，
    /// 直接传回原生层即可（原生层同时接受短名、枚举名与数值）。
    /// </summary>
    private static string SelectedToken(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Split('（')[0].Trim() ?? string.Empty;

    private async void SejRun_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireFile(SejInBox.Text, "输入文件", mustExist: true)) return;
        if (!RequireFile(SejOutBox.Text, "输出文件", mustExist: false)) return;

        var inFile  = SejInBox.Text.Trim();
        var outFile = SejOutBox.Text.Trim();
        var encrypt = SejEncryptRadio.IsChecked == true;
        var keyId   = SelectedToken(SejKeyIdCombo);
        var keySize = int.TryParse(SelectedToken(SejKeySizeCombo), out var size) ? size : 256;

        if (!Confirm($"即将对文件执行 SEJ AES {(encrypt ? "加密" : "解密")}：\n\n" +
                     $"输入：{inFile}\n输出：{outFile}\n" +
                     $"密钥：{keyId} / {keySize} 位\n模式：{(SejCbcCheck.IsChecked == true ? "CBC" : "ECB")}\n\n" +
                     "运算全部在设备内完成，密钥不会离开芯片。是否继续？",
                     "SEJ AES"))
            return;

        await ExecuteAsync(encrypt ? "SEJ AES 加密" : "SEJ AES 解密", new
        {
            op         = "sej_aes",
            in_file    = inFile,
            out_file   = outFile,
            encrypt,
            key_id     = keyId,
            key_size   = keySize,
            cbc        = SejCbcCheck.IsChecked == true,
            anti_clone = SejAntiCloneCheck.IsChecked == true,
            legacy     = SejLegacyCheck.IsChecked == true,
            xor        = SejXorCheck.IsChecked == true,
        });
    }

    // ---------- 自定义密钥派生 ----------

    private async void KeyDerive_Click(object sender, RoutedEventArgs e)
    {
        var label = (KeyLabelBox.Text ?? string.Empty).Trim();
        var salt  = (KeySaltBox.Text ?? string.Empty).Trim();

        if (label.Length == 0)
        {
            Warn("请填写 label。");
            return;
        }

        if (salt.Length == 0)
        {
            Warn("请填写 salt。");
            return;
        }

        var size = int.TryParse(SelectedToken(KeySizeCombo), out var parsed) ? parsed : 256;

        if (!Confirm($"将从设备密钥派生新密钥：\n\nlabel：{label}\nsalt：{salt}\n长度：{size} 位\n\n" +
                     "派生结果属于敏感信息，会显示在执行日志中，请勿外传。是否继续？",
                     "密钥派生"))
            return;

        await ExecuteAsync("派生密钥", new { op = "key_derive", label, salt, size });
    }
}
