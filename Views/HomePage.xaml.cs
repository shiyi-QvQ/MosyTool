using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mosytool.Controls;
using Mosytool.Models;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Views;

public partial class HomePage : Page
{
    private static readonly string[] Mottos =
    {
        "刷机一时爽，一直刷机一直爽。",
        "备份做得好，刷机没烦恼。",
        "变砖不可怕，有 EDL 就能救。",
        "刷机千万条，备份第一条。",
        "Fastboot 在手，天下我有。",
        "刷机有风险，操作需谨慎。",
        "记得先解锁 BL 再刷机哦。",
        "记得备份哦",
        "稳住，还能蒸。",
        "今天也要元气满满地刷机。",
    };

    private static readonly Random Rng = new();

    private readonly DispatcherTimer _timer;
    private string _serial    = string.Empty;
    private string _stateKind = "disconnected";
    private bool   _isDetecting;

    /// <summary>首页被缓存复用，用静态标志保证 popup 公告每次运行只检查一次。</summary>
    private static bool _startupPopupChecked;

    private bool _loadingAnnouncements;

    /// <summary>整页卡片的入场动效只在本次运行首次进首页时播一次，避免来回切页反复闪。</summary>
    private static bool _entrancePlayed;

    /// <summary>内容区窄于这个宽度时，快捷入口从 3 列收成 2 列。</summary>
    private const double NarrowContentWidth = 900;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = e.NewSize.Width < NarrowContentWidth ? 2 : 3;
        if (QuickToolsGrid.Columns != columns) QuickToolsGrid.Columns = columns;
    }

    private void RefreshDevice_Click(object sender, RoutedEventArgs e) => _ = DetectAsync();

    /// <summary>快捷入口：点哪张卡就导航到哪个工具页。</summary>
    private void QuickTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag) return;

        Type? pageType = tag switch
        {
            "fastboot" => typeof(FastbootPage),
            "adb"      => typeof(AdbPage),
            "mtk"      => typeof(MtkPage),
            "edl"      => typeof(EdlPage),
            "super"    => typeof(SuperPage),
            "driver"   => typeof(DriverPage),
            _          => null,
        };

        if (pageType is null) return;
        (Window.GetWindow(this) as MainWindow)?.NavigateTo(pageType);
    }

    public HomePage()
    {
        InitializeComponent();
        AnnouncementBox.Text = "欢迎使用手机刷机工具。刷机前请先备份好数据，并确认设备型号与固件匹配。";
        VersionText.Text    = $"v{AppInfo.DisplayVersion}";
        SubtitleText.Text   = Mottos[Rng.Next(Mottos.Length)];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await DetectAsync();

        SizeChanged += OnPageSizeChanged;

        Loaded += async (_, _) =>
        {
            PlayEntrance();

            _ = DetectAsync();

            // 启动弹窗只在本次运行第一次进首页时检查一次；
            // 标志先置位再 await，避免快速来回切页时 Loaded 重入弹两次。
            var checkPopups = !_startupPopupChecked;
            _startupPopupChecked = true;
            await LoadAnnouncementsAsync(showPopups: checkPopups);
        };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _timer.Start();
                _ = DetectAsync();
            }
            else
            {
                _timer.Stop();
            }
        };

        _ = RefreshDriverStatusAsync();
    }

    /// <summary>卡片依次淡入上浮，标题行轻微跟随——整页"铺开"的入场。</summary>
    private void PlayEntrance()
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;

        Anim.Stagger(new FrameworkElement[]
        {
            DeviceCard, MeterCard, ModeCard, SideToolsRow, AnnouncementCard, DriverCard,
        }, stepMs: 55, offsetY: 14);
    }

    private async Task DetectAsync()
    {
        if (_isDetecting) return;
        if (!AdbService.IsAdbAvailable && !AdbService.IsFastbootAvailable
            && !PenumbraService.IsAvailable) return;

        _isDetecting = true;
        try
        {
            var info = await Task.Run(DeviceService.Detect);
            _serial    = info.Serial;
            _stateKind = info.StateKind;
            ApplyDeviceInfo(info);
        }
        finally
        {
            _isDetecting = false;
        }
    }

    private void ApplyDeviceInfo(DeviceInfo info)
    {
        DeviceName.Text           = info.Name;
        DeviceCodename.Text       = info.Codename;
        DeviceSerial.Text         = info.Serial;
        DeviceAndroidVersion.Text = info.AndroidVersion;
        DeviceHardware.Text       = info.Hardware;
        DeviceCpu.Text            = info.Cpu;
        DeviceResolution.Text     = info.Resolution;
        DeviceRam.Text            = info.AvailableRam == "-" ? info.TotalRam : $"{info.AvailableRam} / {info.TotalRam}";
        DeviceState.Text          = info.State;

        DeviceRomVersion.Text = info.RomVersion;
        DeviceKernel.Text     = info.KernelVersion;
        DeviceBuild.Text      = info.BuildNumber;

        ApplyBattery(info);
        ApplyStorage(info);
        ApplyRam(info);

        StateDot.Background = new SolidColorBrush(GetStateColor(info.StateKind));

        _adbUnlocked = info.BootloaderUnlocked;

        ApplyRebootButtons(info.StateKind);
        UpdateLockUi(info.StateKind);
    }

    // ---------- 图形化 ----------

    private void ApplyBattery(DeviceInfo info)
    {
        if (info.BatteryPercent < 0)
        {
            Anim.To(BatteryRing, Wpf.Ui.Controls.ProgressRing.ProgressProperty, 0);
            BatteryRingText.Text = "--";
            BatteryDetail.Text   = "未获取到电量";
            BatteryRing.ClearValue(Control.ForegroundProperty);
            return;
        }

        // 推值而不是赋值：轮询到新电量时环是"滑"过去的
        Anim.To(BatteryRing, Wpf.Ui.Controls.ProgressRing.ProgressProperty, info.BatteryPercent);
        BatteryRingText.Text = $"{info.BatteryPercent}%";
        BatteryDetail.Text   = info.BatteryCharging ? "充电中" : "放电中";

        if (info.BatteryCharging)
            BatteryRing.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6B));
        else if (info.BatteryPercent <= 20)
            BatteryRing.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x50, 0x4E));
        else
            BatteryRing.ClearValue(Control.ForegroundProperty);
    }

    private void ApplyStorage(DeviceInfo info)
    {
        if (info.StorageTotalGb <= 0)
        {
            Anim.To(StorageBar, RangeBase.ValueProperty, 0);
            DeviceStorage.Text       = "--";
            StorageFreeText.Text     = string.Empty;
            StorageNominalText.Text  = string.Empty;
            StorageBar.ClearValue(Control.ForegroundProperty);
            return;
        }

        Anim.To(StorageBar, RangeBase.ValueProperty, Math.Clamp(info.StoragePercent, 0, 100));
        DeviceStorage.Text       = $"已用 {info.StorageUsedGb:0.#} GB";
        StorageFreeText.Text     = $"可用 {info.StorageFreeGb:0.#} GB / 共 {info.StorageTotalGb:0.#} GB";
        StorageNominalText.Text  = info.StorageNominal;

        if (info.StoragePercent >= 90)
            StorageBar.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x50, 0x4E));
        else
            StorageBar.ClearValue(Control.ForegroundProperty);
    }

    private void ApplyRam(DeviceInfo info)
    {
        if (info.RamTotalGb <= 0)
        {
            Anim.To(RamBar, RangeBase.ValueProperty, 0);
            RamUsedText.Text    = "--";
            RamFreeText.Text    = string.Empty;
            RamNominalText.Text = string.Empty;
            RamBar.ClearValue(Control.ForegroundProperty);
            return;
        }

        Anim.To(RamBar, RangeBase.ValueProperty, Math.Clamp(info.RamPercent, 0, 100));
        RamUsedText.Text    = $"已用 {info.RamUsedGb:0.#} GB";
        RamFreeText.Text    = $"可用 {info.RamAvailGb:0.#} GB / 共 {info.RamTotalGb:0.#} GB";
        RamNominalText.Text = info.RamNominal;

        if (info.RamPercent >= 90)
            RamBar.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x50, 0x4E));
        else
            RamBar.ClearValue(Control.ForegroundProperty);
    }

    private static Color GetStateColor(string stateKind) => stateKind switch
    {
        "online"       => Color.FromRgb(0x2E, 0x9E, 0x6B), // 绿色：已连接
        "fastboot"     => Color.FromRgb(0xCC, 0x8A, 0x2F), // 橙色：fastboot
        "edl"          => Color.FromRgb(0xCC, 0x8A, 0x2F), // 橙色：9008
        "mtk"          => Color.FromRgb(0x3B, 0x91, 0xD8), // 蓝色：MTK BROM / Preloader
        "unauthorized" => Color.FromRgb(0xD1, 0x50, 0x4E), // 红色：未授权
        "offline"      => Color.FromRgb(0xD1, 0x50, 0x4E), // 红色：离线
        _              => Color.FromRgb(0x98, 0xA1, 0xAB), // 灰色：未连接
    };

    // ---------- 模式切换 ----------

    /// <summary>按当前连接通道决定哪些目标模式可达。</summary>
    private void ApplyRebootButtons(string stateKind)
    {
        bool online   = stateKind == "online";
        bool fastboot = stateKind == "fastboot";
        bool edl      = stateKind == "edl";

        RebootSystemButton.IsEnabled    = online || fastboot || edl;  // EDL 下走复位 / 手动提示
        RebootFastbootButton.IsEnabled  = online || edl;              // Fastboot 下已在 fastboot 里
        RebootFastbootdButton.IsEnabled = online || fastboot || edl;
        RebootRecoveryButton.IsEnabled  = online || fastboot;
        RebootEdlButton.IsEnabled       = online || fastboot;

        RebootHintText.Text = stateKind switch
        {
            "online"   => "当前是 adb 通道，用 adb 指令重启到目标模式。",
            "fastboot" => "当前是 Fastboot 通道，用 fastboot 指令切换；EDL 部分机型未开放。",
            "edl"      => "当前是 EDL(9008)：adb / fastboot 都不可用。可到「高通 EDL 工具」页连接设备并复位回系统。",
            "mtk"      => "当前是 MTK BROM / Preloader：adb / fastboot 都不可用，请到「MTK 工具」页操作。",
            _          => "未连接设备，请先连接手机。",
        };
    }

    private void RebootSystem_Click(object sender, RoutedEventArgs e)    => RebootTo("system");
    private void RebootFastboot_Click(object sender, RoutedEventArgs e)  => RebootTo("fastboot");
    private void RebootFastbootd_Click(object sender, RoutedEventArgs e) => RebootTo("fastbootd");
    private void RebootRecovery_Click(object sender, RoutedEventArgs e)  => RebootTo("recovery");
    private void RebootEdl_Click(object sender, RoutedEventArgs e)       => RebootTo("edl");

    private void RebootTo(string target)
    {
        var (ok, message) = DeviceService.RebootTo(_serial, _stateKind, target);

        Dialogs.Show(message, ok ? "已下发指令" : "未能完成",
                        MessageBoxButton.OK,
                        ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        if (ok) _ = DetectAsync();
    }

    // ---------- Bootloader 解 / 回锁 ----------

    private bool?   _lockedState;      // true=已解锁 false=已锁定 null=查不到
    private bool?   _adbUnlocked;      // 系统内用 getprop 读到的锁状态
    private string? _lockProbedState;  // 已查询过锁状态的通道（online / fastboot）
    private bool    _probingLock;

    /// <summary>两个通道都能查锁状态：系统内用 getprop，Fastboot 里用 getvar。</summary>
    private void UpdateLockUi(string stateKind)
    {
        if (stateKind == "online")
        {
            _lockedState     = _adbUnlocked;
            _lockProbedState = "online";
        }
        else if (stateKind == "fastboot")
        {
            if (_lockProbedState != "fastboot" && !_probingLock)
                _ = ProbeLockStateAsync();
        }
        else
        {
            _lockedState     = null;
            _lockProbedState = null;
        }

        RenderLock();
    }

    private void RenderLock()
    {
        // adb 在线时点按钮会先引导进入 Fastboot，所以两种通道都允许点击
        bool clickable = _stateKind is "fastboot" or "online";

        switch (_lockedState)
        {
            case true:
                LockDot.Background     = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6B));
                LockStateText.Text     = "已解锁" + LockSource();
                UnlockButton.IsEnabled = false;
                LockButton.IsEnabled   = clickable;
                break;

            case false:
                LockDot.Background     = new SolidColorBrush(Color.FromRgb(0xCC, 0x8A, 0x2F));
                LockStateText.Text     = "已锁定" + LockSource();
                UnlockButton.IsEnabled = clickable;
                LockButton.IsEnabled   = false;
                break;

            default:
                LockDot.Background     = new SolidColorBrush(Color.FromRgb(0x98, 0xA1, 0xAB));
                LockStateText.Text     = _stateKind switch
                {
                    "fastboot" => "查询中…",
                    "online"   => "未知（该机型未暴露锁状态，可进 Fastboot 复查）",
                    _          => "未知（连接设备后自动检测）",
                };
                UnlockButton.IsEnabled = clickable;
                LockButton.IsEnabled   = clickable;
                break;
        }
    }

    private string LockSource() => _lockProbedState switch
    {
        "online"   => "（系统内读取）",
        "fastboot" => "（Fastboot 读取）",
        _          => string.Empty,
    };

    private async Task ProbeLockStateAsync()
    {
        _probingLock     = true;
        _lockProbedState = "fastboot";
        try
        {
            var state = await Task.Run(FastbootService.GetUnlockedState);
            _lockedState = state;
        }
        finally
        {
            _probingLock = false;
        }

        RenderLock();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => _ = SetLockAsync(unlock: true);
    private void Lock_Click(object sender, RoutedEventArgs e)   => _ = SetLockAsync(unlock: false);

    private async Task SetLockAsync(bool unlock)
    {
        // 系统内也能点到按钮：先问一句，切到 Fastboot 后自动继续
        if (_stateKind == "online")
        {
            var ask = Dialogs.Show(
                "解 / 回锁必须在 Fastboot 模式下执行。\n\n是否现在把设备切换到 Fastboot？切换完成后本工具会自动继续。",
                "需要先进入 Fastboot", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;

            if (!await SwitchToFastbootAsync()) return;
        }

        if (_stateKind != "fastboot")
        {
            Dialogs.Show("解 / 回锁只能在 Fastboot 模式下操作。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var action  = unlock ? "解锁" : "回锁";
        var warning = unlock
            ? "解锁 bootloader 会清空手机全部数据（照片、联系人、应用等），且部分品牌会失去保修，部分银行 / 支付类应用可能无法使用。\n\n" +
              "指令下发后，手机屏幕上还会弹出确认界面，需要用音量键选择、电源键确认。\n\n确定继续吗？"
            : "回锁 bootloader 同样会清空数据。\n\n" +
              "如果当前系统或分区不是官方原版，回锁后可能无法开机（变砖），请务必确认已刷回官方完整包。\n\n确定继续吗？";

        if (Dialogs.Show(warning, $"确认{action} BL",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        UnlockButton.IsEnabled = false;
        LockButton.IsEnabled   = false;

        var lines = new ConcurrentQueue<string>();
        var (ok, message) = await FastbootService.SetLockStateAsync(unlock, line => lines.Enqueue(line));

        if (!ok && lines.Count > 0)
            message += "\n\n--- 设备返回 ---\n" + string.Join("\n", lines.ToArray().TakeLast(8));

        Dialogs.Show(message, ok ? $"{action}指令已下发" : $"{action}失败",
                        MessageBoxButton.OK,
                        ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        // 锁状态变了，让下一次轮询重新查询
        _lockProbedState = null;
        _lockedState     = null;
        _adbUnlocked     = null;
        UpdateLockUi(_stateKind);
        _ = DetectAsync();
    }

    /// <summary>切到 Fastboot 并等待设备出现，最多 45 秒。</summary>
    private async Task<bool> SwitchToFastbootAsync()
    {
        var (ok, message) = DeviceService.RebootTo(_serial, _stateKind, "fastboot");
        if (!ok)
        {
            Dialogs.Show(message, "切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _lockProbedState       = null;
        _lockedState           = null;
        UnlockButton.IsEnabled = false;
        LockButton.IsEnabled   = false;
        LockStateText.Text     = "正在进入 Fastboot，请稍候…";

        for (var i = 0; i < 45; i++)
        {
            await Task.Delay(1000);

            var found = await Task.Run(FastbootService.DetectDevice);
            if (found is null) continue;

            _serial    = found;
            _stateKind = "fastboot";
            return true;
        }

        Dialogs.Show("等待设备进入 Fastboot 超时。\n\n请确认数据线连接正常，或手动重启到 bootloader 后重试。",
                        "超时", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = DetectAsync();
        return false;
    }

    // ---------- 驱动检测 ----------

    private void CheckDriver_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshDriverStatusAsync();
    }

    private async Task RefreshDriverStatusAsync()
    {
        var has = await Task.Run(DeviceService.HasAndroidDevice);
        DeviceManagerStatus.Text = has ? "已检测到 Android 设备" : "未检测到 Android 设备";
        DriverDot.Background = new SolidColorBrush(
            has ? Color.FromRgb(0x2E, 0x9E, 0x6B) : Color.FromRgb(0xD1, 0x50, 0x4E));
    }

    // ---------- 端口驱动 ----------

    private void DriverPageButton_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo(typeof(DriverPage));

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        => DriverService.OpenDriversDir();

    // ---------- 远程公告 ----------

    private void RefreshAnnouncement_Click(object sender, RoutedEventArgs e)
        => _ = LoadAnnouncementsAsync(showPopups: false, ignoreCache: true);

    /// <summary>
    /// 拉取远程公告：首页公告栏展示最新几条，popup 类型单独弹窗。
    /// 失败不打断首页其它功能，只把提示文案换掉。
    /// </summary>
    private async Task LoadAnnouncementsAsync(bool showPopups, bool ignoreCache = false)
    {
        if (_loadingAnnouncements) return;
        _loadingAnnouncements = true;

        try
        {
            AnnouncementBox.Text       = "正在加载公告…";
            AnnouncementBox.Visibility = Visibility.Visible;

            // 不带 type 参数 = 不筛选，首页直接把最新几条都列出来
            var list = await AnnouncementService.ListAsync(limit: 5, ignoreCache: ignoreCache);

            RenderAnnouncements(list);

            if (showPopups)
                await ShowPendingPopupAsync();
        }
        catch (ApiException ex)
        {
            AnnouncementList.Children.Clear();
            AnnouncementBox.Visibility = Visibility.Visible;
            AnnouncementBox.Text = ex.IsNetworkError
                ? "公告获取失败：无法连接服务器。"
                : $"公告获取失败：{ex.Message}";
        }
        finally
        {
            _loadingAnnouncements = false;
        }
    }

    private void RenderAnnouncements(IReadOnlyList<Announcement> list)
    {
        AnnouncementList.Children.Clear();

        if (list.Count == 0)
        {
            AnnouncementBox.Text = "暂时没有新公告。";
            AnnouncementBox.Visibility = Visibility.Visible;
            return;
        }

        // 有内容时隐藏兜底文案，交给列表展示
        AnnouncementBox.Visibility = Visibility.Collapsed;

        foreach (var item in list)
            AnnouncementList.Children.Add(BuildAnnouncementCard(item));

        SettingsService.MarkAnnouncementsRead(list.Select(a => a.Id));
    }

    /// <summary>
    /// 单条公告卡片：左侧等级色条 + 等级徽章 / 置顶标记 + 标题 + 相对时间 + 正文摘要。
    /// 整卡可点击，打开详情窗口看完整内容。
    /// </summary>
    private UIElement BuildAnnouncementCard(Announcement item)
    {
        var accent = new SolidColorBrush(ToColor(AnnouncementService.LevelColor(item.Level)));
        var published = AnnouncementService.ParseTime(item.PublishAt)
                        ?? AnnouncementService.ParseTime(item.CreatedAt);

        // ── 标题行：徽章 + 置顶 + 标题 ──
        // 用 Grid 分列：横向 StackPanel 会给标题无限宽，窄窗口下省略号不生效、标题直接顶出卡片
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        titleRow.Children.Add(new SquircleBorder
        {
            CornerRadius      = 6,
            Padding           = new Thickness(6, 1, 6, 1),
            Background        = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = AnnouncementService.LevelText(item.Level),
                FontSize   = 11,
                Foreground = Brushes.White,
            },
        });

        if (item.Pinned)
        {
            var pinBadge = new SquircleBorder
            {
                CornerRadius      = 6,
                Padding           = new Thickness(6, 1, 6, 1),
                Margin            = new Thickness(6, 0, 0, 0),
                Background        = (Brush)FindResource("AccentFillColorDefaultBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "置顶", FontSize = 11, Foreground = Brushes.White },
            };
            Grid.SetColumn(pinBadge, 1);
            titleRow.Children.Add(pinBadge);
        }

        // 标题里也可能夹着 **、` 之类的语法字符，走一遍 Markdown 取文本，别把星号显示出来
        var title = MarkdownRenderer.ToPlainText(item.Title);
        if (string.IsNullOrWhiteSpace(title)) title = item.Title?.Trim() ?? string.Empty;

        var titleBlock = new TextBlock
        {
            Text              = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title,
            FontSize          = 13.5,
            FontWeight        = FontWeights.SemiBold,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (Brush)FindResource("TextFillColorPrimaryBrush"),
        };
        Grid.SetColumn(titleBlock, 2);
        titleRow.Children.Add(titleBlock);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titleRow);

        if (published is not null)
        {
            var local = published.Value.ToLocalTime();
            var date = new TextBlock
            {
                Text              = FormatRelative(local),
                FontSize          = 11.5,
                Margin            = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = local.ToString("yyyy-MM-dd HH:mm"),
                Foreground        = (Brush)FindResource("TextFillColorTertiaryBrush"),
            };
            Grid.SetColumn(date, 1);
            header.Children.Add(date);
        }

        // 摘要按 Markdown 排版：标题加粗、列表带符号、行内代码有底纹。
        // 只显示前三行，完整内容点开详情窗口看（纯文本摘要把排版全抹平了，写着难受）。
        var body = MarkdownRenderer.BuildSummary(item.Content);
        body.Margin = new Thickness(0, 6, 0, 0);

        var content = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        content.Children.Add(header);
        content.Children.Add(body);

        var stripe = new Border
        {
            Width        = 3,
            CornerRadius = new CornerRadius(2),
            Background   = accent,
            Margin       = new Thickness(0, 2, 0, 2),
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(stripe);
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);

        var normalFill   = (Brush)FindResource("ControlFillColorSecondaryBrush");
        var hoverFill    = (Brush)FindResource("Mt.Button.HoverOverlayBrush");
        var normalStroke = (Brush)FindResource("CardStrokeColorDefaultBrush");

        // 悬停用一层不透明度渐变的叠加层，而不是直接换 Background，避免底色硬切。
        // 这一层必须铺满整张卡片：之前挂在 Padding 里侧，只有内容区被盖住，
        // 四周留出一圈原底色，看上去像悬停时凭空多出一块白色圆角区域。
        // 颜色也换成按钮/列表项同款的一点点黑，悬停只是轻微压深，不会突然变白。
        var hoverLayer = new SquircleBorder
        {
            CornerRadius     = 8,
            Background       = hoverFill,
            Opacity          = 0,
            IsHitTestVisible = false,
        };

        layout.Margin = new Thickness(12, 10, 12, 10);

        var inner = new Grid();
        inner.Children.Add(hoverLayer);
        inner.Children.Add(layout);

        var card = new SquircleBorder
        {
            Margin          = new Thickness(0, 10, 0, 0),
            CornerRadius    = 8,
            Background      = normalFill,
            BorderThickness = 1,
            BorderBrush     = normalStroke,
            Cursor          = Cursors.Hand,
            ToolTip         = "点击查看完整公告",
            Child           = inner,
        };

        void FadeTo(double target, int durationMs)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            hoverLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease });
        }

        card.MouseEnter += (_, _) => FadeTo(1, 120);
        card.MouseLeave += (_, _) => FadeTo(0, 180);
        card.MouseLeftButtonUp += (_, _) => OpenAnnouncementDetail(item);

        return card;
    }

    private void OpenAnnouncementDetail(Announcement item)
        => Dialogs.ShowAnnouncement(Window.GetWindow(this), item, isPopup: false);

    /// <summary>把发布时间转成「刚刚 / N 分钟前 / N 小时前 / N 天前 / 日期」。</summary>
    private static string FormatRelative(DateTimeOffset local)
    {
        var diff = DateTimeOffset.Now - local;
        if (diff.TotalSeconds < 60) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours   < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays} 天前";
        return local.ToString("yyyy-MM-dd");
    }

    private static Color ToColor(string hex) =>
        ColorConverter.ConvertFromString(hex) is Color color ? color : Colors.Gray;

    /// <summary>弹出一条尚未弹过的 popup 公告。</summary>
    private async Task ShowPendingPopupAsync()
    {
        var pending = await AnnouncementService.PendingPopupsAsync(SettingsService.PoppedAnnouncementIds);
        if (pending.Count == 0) return;

        // 以前一次只弹最新的那条，其余要等下次启动，用户会以为「公告只显示一条」。
        // 现在把待提醒的公告合并到一个遮罩卡片里，一次看完；弹窗打开即记为已弹过。
        Dialogs.ShowAnnouncement(Window.GetWindow(this), pending, isPopup: true);
    }
}
