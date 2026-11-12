using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

/// <summary>
/// ADB 工具页：顶部「功能」下拉切换「文件推送 / 投屏」，左侧功能面板，右侧常驻日志。
///
/// 两个约定：
/// 1) 进入页面就自动读一次当前连接设备的文件，用户不用先点刷新；
/// 2) 导出 / 推送 / 准备投屏组件这些耗时动作的进度一律进右侧日志与进度条，
///    不弹对话框打断操作。
/// </summary>
public partial class AdbPage : Page
{
    /// <summary>
    /// 当前页实例。侧栏把「ADB 工具」拆成了「文件推送 / 投屏」两个子项，两者共用本页，
    /// 主窗口点完子项后靠这个引用把页面切到对应功能。
    /// </summary>
    public static AdbPage? Current { get; private set; }

    /// <summary>
    /// 侧栏点「文件推送 / 投屏」时主窗口先写这里，页面加载完成后再消费掉。
    /// 页面这次才被创建出来时，主窗口那次 SelectFeature 调用会因为 Current 还是 null 而落空。
    /// </summary>
    public static bool? PendingFeature { get; set; }

    /// <summary>列表与宫格共用的数据源。</summary>
    private readonly ObservableCollection<AdbFileItem> _files = new();

    private DeviceInfo? _device;
    private string      _remoteDir = AdbFileService.DefaultRemoteDir;

    private bool _ready;      // InitializeComponent 完成后才允许写日志 / 切面板
    private bool _busy;       // 有传输任务在跑，期间锁住按钮
    private bool _gridView;   // 当前是不是宫格视图
    private bool _detecting;

    private CancellationTokenSource? _cts;

    public AdbPage()
    {
        InitializeComponent();

        Current = this;

        FileGrid.ItemsSource  = _files;
        FileTiles.ItemsSource = _files;

        InitMirrorOptions();

        Loaded   += OnLoaded;
        _ready    = true;

        OutputBox.Reset("等待操作…");
    }

    /// <summary>切到某个功能面板。侧栏选中「文件推送 / 投屏」时调用。</summary>
    public void SelectFeature(bool mirror)
    {
        if (FeatureCombo.SelectedIndex != (mirror ? 1 : 0))
        {
            // 赋值会走 FeatureCombo_SelectionChanged，面板与日志都在那里处理
            FeatureCombo.SelectedIndex = mirror ? 1 : 0;
            return;
        }

        ApplyFeature(mirror);   // 索引没变化时下拉框不会发通知，这里补一次
    }

    // ==================================================================
    // 生命周期
    // ==================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScrcpyService.Initialize();

        if (ScrcpyService.Running is not null)
        {
            StartMirrorButton.IsEnabled = false;
            StopMirrorButton.IsEnabled  = true;
        }

        await DetectAndLoadAsync();

        // 从侧栏「文件推送 / 投屏」进来时，落到用户点的那一项
        if (PendingFeature is bool pending)
        {
            PendingFeature = null;
            SelectFeature(pending);
        }
    }

    /// <summary>检测设备；设备处于「已连接」时顺手把文件列表读出来。</summary>
    private async Task DetectAndLoadAsync()
    {
        if (_detecting) return;
        _detecting = true;

        try
        {
            SetDeviceState("正在检测…", "idle");
            DetectButton.IsEnabled = false;

            var info = await Task.Run(DeviceService.Detect);
            _device = info;

            var online = info.StateKind == "online" && !string.IsNullOrWhiteSpace(info.Serial);

            SerialText.Text      = string.IsNullOrWhiteSpace(info.Serial) ? "-" : $"序列号 {info.Serial}";
            ModelText.Text       = string.IsNullOrWhiteSpace(info.Name) ? "机型 -" : $"机型 {info.Name}";
            MirrorStateText.Text = info.State;
            MirrorModelText.Text = string.IsNullOrWhiteSpace(info.Name) ? "-" : info.Name;

            SetDeviceState(info.State, info.StateKind);
            ShowHint(info.StateKind);

            if (online)
            {
                await LoadFilesAsync(_remoteDir, auto: true);
            }
            else
            {
                _files.Clear();
                FileCountText.Text = "未连接";
                FileStateText.Text = "没有处于「已连接」状态的设备，文件推送不可用。";
            }
        }
        finally
        {
            _detecting             = false;
            DetectButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private void SetDeviceState(string text, string kind)
    {
        StateText.Text = text;
        StateDot.Background = new SolidColorBrush(kind switch
        {
            "online"                    => Color.FromRgb(0x2E, 0x9E, 0x6B),
            "fastboot" or "edl"         => Color.FromRgb(0xCC, 0x8A, 0x2F),
            "mtk"                       => Color.FromRgb(0x3B, 0x91, 0xD8),
            "unauthorized" or "offline" => Color.FromRgb(0xD1, 0x50, 0x4E),
            _                           => Color.FromRgb(0x98, 0xA1, 0xAB),
        });
    }

    /// <summary>只在需要用户动手时给一句提示，其余状态保持干净。</summary>
    private void ShowHint(string stateKind)
    {
        var hint = stateKind switch
        {
            "unauthorized" => "请在手机弹窗里勾选「始终允许」并点确定，然后重新检测。",
            "offline"      => "设备处于离线状态，重新插拔数据线或重启 adb 服务后再试。",
            _              => string.Empty,
        };

        DeviceHintText.Text       = hint;
        DeviceHintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitMirrorOptions()
    {
        SizeCombo.ItemsSource      = new[] { "原始分辨率", "1920", "1600", "1280", "1024", "800" };
        SizeCombo.SelectedIndex    = 0;
        BitRateCombo.ItemsSource   = new[] { "16M", "12M", "8M", "4M", "2M", "1M" };
        BitRateCombo.SelectedIndex = 2;
        FpsCombo.ItemsSource       = new[] { "60", "30", "24", "15" };
        FpsCombo.SelectedIndex     = 0;
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e) => await DetectAndLoadAsync();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => OutputBox.Reset("等待操作…");

    // ==================================================================
    // 功能切换
    // ==================================================================

    private void FeatureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;   // XAML 里 SelectedIndex 的初始赋值会先触发一次

        ApplyFeature(FeatureCombo.SelectedIndex == 1);
    }

    /// <summary>真正切面板：日志只在切换时写一次，侧栏选中和下拉框选择走同一条路径。</summary>
    private void ApplyFeature(bool mirror)
    {
        FilePanel.Visibility   = mirror ? Visibility.Collapsed : Visibility.Visible;
        MirrorPanel.Visibility = mirror ? Visibility.Visible : Visibility.Collapsed;

        Log(mirror ? "已切换到「投屏」。" : "已切换到「文件推送」。");
    }

    // ==================================================================
    // 文件推送
    // ==================================================================

    /// <summary>读取设备目录；auto 为 true 表示这是进入页面时的自动读取。</summary>
    private async Task LoadFilesAsync(string dir, bool auto = false)
    {
        dir = NormalizeDir(dir);

        if (_device is null || _device.StateKind != "online" || string.IsNullOrWhiteSpace(_device.Serial))
        {
            _files.Clear();
            FileCountText.Text = "未连接";
            FileStateText.Text = auto
                ? "进入页面时没有读到可用的设备；连接并授权后点「刷新」。"
                : "请先连接设备。";
            UpdateButtons();
            return;
        }

        FileCountText.Text = "正在读取…";
        FileStateText.Text = $"正在读取 {dir} …";

        var result = await AdbFileService.ListDetailedAsync(_device.Serial, dir);

        _files.Clear();

        if (!result.Ok)
        {
            FileCountText.Text = "读取失败";
            FileStateText.Text = $"读取失败：{result.Error}";
            Log($"读取设备目录失败：{result.Error}");
            UpdateButtons();
            return;
        }

        // 文件夹排前面，同类按名称排序，和资源管理器的习惯一致
        foreach (var entry in result.Entries
                     .OrderByDescending(entry => entry.IsDirectory)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            _files.Add(AdbFileItem.FromDevice(entry.Name, entry.IsDirectory, entry.Size));
        }

        _remoteDir         = dir;
        RemoteDirBox.Text  = dir;
        FileCountText.Text = $"{_files.Count} 项";
        FileStateText.Text = $"已读取 {dir}（{_files.Count} 项）；双击文件夹可进入。";
        Log($"已读取 {dir}：{_files.Count} 项。");

        UpdateButtons();
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e) => await LoadFilesAsync(_remoteDir);

    private async void OpenDir_Click(object sender, RoutedEventArgs e) => await LoadFilesAsync(RemoteDirBox.Text);

    private async void RemoteDirBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await LoadFilesAsync(RemoteDirBox.Text);
    }

    private async void GoUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = ParentDir(_remoteDir);
        if (parent == _remoteDir)
        {
            Log("已经在设备存储的根目录。");
            return;
        }

        await LoadFilesAsync(parent);
    }

    private void FileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _gridView ? FileTiles : FileGrid)) return;   // 只认当前可见视图
        UpdateButtons();
    }

    /// <summary>双击文件夹进入下一层；双击文件则提示它需要导出。</summary>
    private async void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is AdbFileItem item) await EnterItemAsync(item);
    }

    private async void FileTiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTiles.SelectedItem is AdbFileItem item) await EnterItemAsync(item);
    }

    private async Task EnterItemAsync(AdbFileItem item)
    {
        if (item.IsLocal) return;

        if (!item.IsDirectory)
        {
            Log($"「{item.Name}」是文件，用「导出文件」取到本机。");
            return;
        }

        await LoadFilesAsync(_remoteDir + item.Name + "/");
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        _gridView = !_gridView;

        FileGrid.Visibility  = _gridView ? Visibility.Collapsed : Visibility.Visible;
        FileTiles.Visibility = _gridView ? Visibility.Visible : Visibility.Collapsed;

        ViewToggleButton.Content = _gridView ? "切换列表显示" : "切换宫格显示";
        UpdateButtons();
    }

    /// <summary>导出选中项到本机（adb pull，文件夹会递归导出）。</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryGetOnlineSerial(out var serial)) return;

        var selected = SelectedItems().Where(item => !item.IsLocal).ToList();
        if (selected.Count == 0)
        {
            Log("请先在列表里选择要导出的文件或文件夹。");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择本机保存位置", Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        var target = dialog.FolderName;
        var cts    = new CancellationTokenSource();
        _cts = cts;
        BeginTransfer($"导出 {selected.Count} 项到 {target}");

        try
        {
            var done = 0;

            foreach (var item in selected)
            {
                cts.Token.ThrowIfCancellationRequested();

                var row  = item;
                var name = item.Name;

                row.Status = "导出中…";

                var (ok, error) = await AdbFileService.PullAsync(
                    serial,
                    _remoteDir + name,
                    target,
                    Log,
                    value => Post(() =>
                    {
                        row.Status = $"导出中 {value}%";
                        SetProgress((done + value / 100.0) / selected.Count * 100);
                    }),
                    cts.Token);

                row.Status = ok ? "已完成" : "失败";
                Log(ok
                    ? $"已导出：{name} → {target}"
                    : $"导出失败：{name}（{error}）");

                done++;
                SetProgress(done * 100.0 / selected.Count);
            }

            Log($"导出结束：共 {selected.Count} 项，成功 {selected.Count(item => item.Status == "已完成")} 项。");
        }
        catch (OperationCanceledException)
        {
            foreach (var item in selected.Where(item => item.Status.StartsWith("导出中", StringComparison.Ordinal)))
                item.Status = "已取消";

            Log("导出已取消。");
        }
        finally
        {
            EndTransfer();
        }
    }

    /// <summary>把本机选中的文件推送到当前设备目录。</summary>
    private async void PushFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryGetOnlineSerial(out var serial)) return;

        var dialog = new OpenFileDialog
        {
            Title      = "选择要推送的文件",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true) return;

        var paths = dialog.FileNames;
        if (paths.Length == 0) return;

        var items = paths.Select(path => new PushItem(path)).ToList();
        var rows  = paths.Select(AdbFileItem.FromLocal).ToList();

        // 先把要推的项插到列表最前面，状态列里就能直接看到每一项的进度
        foreach (var row in rows)
        {
            row.Status = "待推送";
            _files.Insert(0, row);
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        BeginTransfer($"推送 {items.Count} 项到 {_remoteDir}");

        try
        {
            var summary = await AdbFileService.PushAsync(
                serial,
                items,
                _remoteDir,
                (index, percent, status) => Post(() =>
                {
                    if (index < 0 || index >= rows.Count) return;

                    rows[index].Status = status;
                    SetProgress((index + Math.Max(0, percent) / 100.0) / rows.Count * 100);
                }),
                Log,
                cts.Token);

            Log(summary.Message);
        }
        catch (OperationCanceledException)
        {
            Log("推送已取消。");
        }
        finally
        {
            EndTransfer();
            await LoadFilesAsync(_remoteDir);   // 推送完刷新一次，本机临时行被真实设备项替换
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("正在取消当前任务…");
    }

    // ==================================================================
    // 投屏
    // ==================================================================

    private void BrowseRecord_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title      = "选择录制文件的保存位置",
            Filter     = "MP4 视频|*.mp4",
            FileName   = $"投屏录制_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
            DefaultExt = ".mp4",
        };

        if (dialog.ShowDialog() == true) RecordBox.Text = dialog.FileName;
    }

    private async void StartMirror_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !TryGetOnlineSerial(out var serial)) return;

        // scrcpy 是随包分发的，正常情况这里直接就是已就绪；缺失时才自动补齐，
        // 全程只写日志，不弹「未找到 xxx」之类的提示框。
        await EnsureScrcpyAsync();
        if (!ScrcpyService.IsAvailable)
        {
            Log("投屏组件不可用，已结束。");
            return;
        }

        var options = BuildOptions();
        StartMirrorButton.IsEnabled = false;

        Log($"开始投屏：设备 {serial}，分辨率 {SizeCombo.SelectedItem}，" +
            $"码率 {BitRateCombo.SelectedItem}，帧率 {FpsCombo.SelectedItem}。");

        var process = ScrcpyService.Start(serial, options, Log, () => Post(() =>
        {
            StartMirrorButton.IsEnabled = true;
            StopMirrorButton.IsEnabled  = false;
            Log("投屏已结束。");
        }));

        if (process is null)
        {
            StartMirrorButton.IsEnabled = true;
            Log("投屏启动失败。");
            return;
        }

        StopMirrorButton.IsEnabled = true;
        Log("投屏已启动。");
    }

    private void StopMirror_Click(object sender, RoutedEventArgs e)
    {
        ScrcpyService.StopRunning();

        StartMirrorButton.IsEnabled = true;
        StopMirrorButton.IsEnabled  = false;

        Log("已结束投屏。");
    }

    /// <summary>确保 scrcpy 可用；缺失时自动补齐所需文件。</summary>
    private async Task EnsureScrcpyAsync()
    {
        ScrcpyService.Refresh();
        if (ScrcpyService.IsAvailable) return;

        Log("正在准备投屏组件…");
        Post(() =>
        {
            TransferProgress.Value      = 0;
            TransferProgress.Visibility = Visibility.Visible;
        });

        var installed = await ScrcpyInstaller.InstallAsync(
            Log,
            percent => Post(() => TransferProgress.Value = Math.Clamp(percent, 0, 100)));

        ScrcpyService.Refresh();

        Post(() => TransferProgress.Visibility = Visibility.Collapsed);

        Log(installed && ScrcpyService.IsAvailable
            ? "投屏组件已就绪。"
            : "投屏组件准备未完成，请检查网络后重试。");
    }

    private ScrcpyOptions BuildOptions()
    {
        var maxSize = SizeCombo.SelectedIndex <= 0
            ? 0
            : int.TryParse(SizeCombo.SelectedItem as string, out var size) ? size : 0;

        var maxFps = int.TryParse(FpsCombo.SelectedItem as string, out var fps) ? fps : 0;

        return new ScrcpyOptions
        {
            MaxSize       = maxSize,
            VideoBitRate  = BitRateCombo.SelectedItem as string ?? "8M",
            MaxFps        = maxFps,
            NoAudio       = NoAudioCheck.IsChecked   == true,
            StayAwake     = StayAwakeCheck.IsChecked == true,
            TurnScreenOff = ScreenOffCheck.IsChecked  == true,
            Fullscreen    = FullscreenCheck.IsChecked == true,
            RecordPath    = string.IsNullOrWhiteSpace(RecordBox.Text) ? null : RecordBox.Text.Trim(),
            WindowTitle   = "MosyTool 投屏",
        };
    }

    // ==================================================================
    // 公共小工具
    // ==================================================================

    private bool TryGetOnlineSerial(out string serial)
    {
        serial = _device?.Serial ?? string.Empty;

        if (_device is null || _device.StateKind != "online" || string.IsNullOrWhiteSpace(serial))
        {
            Log("当前没有处于「已连接」状态的设备。");
            serial = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>当前可见视图里选中的项。</summary>
    private List<AdbFileItem> SelectedItems()
        => (_gridView ? FileTiles.SelectedItems : FileGrid.SelectedItems)
            .OfType<AdbFileItem>()
            .ToList();

    private void BeginTransfer(string title)
    {
        _busy = true;

        Log($"=== {title} ===");
        TransferProgress.Value      = 0;
        TransferProgress.Visibility = Visibility.Visible;
        CancelTransferButton.Visibility = Visibility.Visible;

        UpdateButtons();
    }

    private void EndTransfer()
    {
        _busy = false;

        _cts?.Dispose();
        _cts = null;

        TransferProgress.Visibility     = Visibility.Collapsed;
        CancelTransferButton.Visibility = Visibility.Collapsed;

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var online = _device is not null
                     && _device.StateKind == "online"
                     && !string.IsNullOrWhiteSpace(_device.Serial);

        ExportButton.IsEnabled       = online && !_busy && SelectedItems().Count > 0;
        PushFilesButton.IsEnabled    = online && !_busy;
        RefreshFilesButton.IsEnabled = !_busy;
        GoUpButton.IsEnabled         = !_busy;
    }

    private void SetProgress(double percent)
        => Post(() => TransferProgress.Value = Math.Clamp(percent, 0, 100));

    /// <summary>adb 的回调都在后台线程，界面更新统一回到 UI 线程。</summary>
    private void Post(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    private void Log(string message) => Post(() => OutputBox.Append(message));

    private static string NormalizeDir(string? text)
    {
        var dir = (text ?? string.Empty).Trim().Replace('\\', '/');

        if (dir.Length == 0) return AdbFileService.DefaultRemoteDir;
        if (!dir.StartsWith('/')) dir = "/" + dir;
        if (!dir.EndsWith('/'))   dir += "/";

        return dir;
    }

    private static string ParentDir(string dir)
    {
        var trimmed = dir.TrimEnd('/');
        var index   = trimmed.LastIndexOf('/');

        if (index <= 0) return "/";

        var parent = trimmed[..index];
        return parent.Length == 0 ? "/" : parent + "/";
    }
}
