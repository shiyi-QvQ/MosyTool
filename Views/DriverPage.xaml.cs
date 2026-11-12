using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Mosytool.Services;

namespace Mosytool.Views;

/// <summary>
/// 端口驱动转换。
///
/// 这一页存在的理由很具体：本工具的 EDL / MTK 原生层要直接读写 USB 端点，Windows 上
/// 只有 WinUSB / libusbK / libusb0 三种驱动允许用户态程序这么做。设备挂在厂商驱动
/// （qcusbser / usbser 之类）上时，原生层只会报「打开失败」，过去只能让用户自己去
/// 装 Zadig 换驱动。这里把这件事收进工具自己。
/// </summary>
public partial class DriverPage : Page
{
    private readonly ObservableCollection<UsbPort> _ports = new();

    /// <summary>同一时刻只允许一个操作在跑：驱动操作会重新枚举设备，并发没有意义。</summary>
    private bool _busy;

    public DriverPage()
    {
        InitializeComponent();

        PortList.ItemsSource = _ports;
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ---------- 刷新 ----------

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;

        RefreshButton.IsEnabled = false;
        StatusText.Text         = "正在读取端口…";

        try
        {
            var ports = await Task.Run(DriverService.EnumeratePorts);

            _ports.Clear();
            foreach (var port in ports) _ports.Add(port);

            EmptyText.Visibility = _ports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var needWork = 0;
            foreach (var port in _ports)
            {
                if (port.NeedsWinUsb) needWork++;
            }

            StatusText.Text = _ports.Count == 0
                ? "没有发现 USB 端口"
                : needWork == 0
                    ? $"共 {_ports.Count} 个 USB 端口，驱动都没问题"
                    : $"共 {_ports.Count} 个 USB 端口，其中 {needWork} 个建议切换为 WinUSB";

            DriverDirText.Text = DriverService.DriversDir;
            LoadInfButton.Content = $"装载本地驱动（{DriverService.CountLocalInfs()}）";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ---------- 逐端口的操作 ----------

    private async void SwitchWinUsb_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is not { } port) return;

        await RunAsync(DriverOpKind.SwitchWinUsb, port,
            $"把「{port.Name}」({port.Id}) 的驱动切换为系统自带的 WinUSB？\n\n" +
            "切换后本工具的 EDL / MTK 功能才能直接读写这个端口。\n" +
            "设备会重新枚举，正在进行的操作请先停掉。");
    }

    private async void DisableEnable_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is not { } port) return;

        // 已经是禁用状态的端口，这个按钮就是"启用"
        var disable = port.Issue != DriverIssue.Disabled;

        if (disable && !Confirm($"禁用「{port.Name}」({port.Id})？\n\n禁用后设备在设备管理器里会显示为不可用。"))
            return;

        await RunAsync(disable ? DriverOpKind.DisableDevice : DriverOpKind.EnableDevice, port, null);
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (PortOf(sender) is not { } port) return;

        if (!Confirm($"卸载「{port.Name}」({port.Id}) 的设备节点并删除它的驱动软件？\n\n" +
                     "用于驱动装乱之后重来一遍：卸载后拔插一次数据线，系统会重新识别并要求安装驱动。"))
            return;

        await RunAsync(DriverOpKind.RemoveDevice, port, null);
    }

    // ---------- 全局操作 ----------

    private async void Scan_Click(object sender, RoutedEventArgs e)
        => await RunAsync(DriverOpKind.ScanDevices, null, null);

    private async void LoadLocalInfs_Click(object sender, RoutedEventArgs e)
    {
        var count = DriverService.CountLocalInfs();
        if (count == 0)
        {
            Inform($"驱动目录里还没有 .inf 文件：\n{DriverService.DriversDir}\n\n" +
                   "把该机型的驱动（.inf / .sys / .cat）解压进去，再点这个按钮。",
                   "驱动目录是空的", MessageBoxImage.Information);

            DriverService.OpenDriversDir();
            return;
        }

        await RunAsync(DriverOpKind.LoadLocalInfs, null,
            $"把驱动目录里的 {count} 个 .inf 装进系统驱动仓库？\n\n" +
            "装好后回到列表里，对目标端口点「切换为 WinUSB」。");
    }

    private void OpenDriverDir_Click(object sender, RoutedEventArgs e) => DriverService.OpenDriversDir();

    // ---------- 公共流程 ----------

    /// <summary>
    /// 一次驱动操作：先确认，再交给提权进程执行，最后刷新列表。
    /// 平时是普通权限启动的，所以这里会弹 UAC；用户点了"否"会被如实报成"已取消"。
    /// </summary>
    private async Task RunAsync(DriverOpKind kind, UsbPort? port, string? confirm)
    {
        if (_busy) return;

        if (confirm is not null && !Confirm(confirm)) return;

        _busy = true;
        StatusText.Text = "正在执行，等待管理员确认…";

        try
        {
            var result = await DriverService.RunElevatedAsync(kind, port);

            StatusText.Text = result.Message;

            Inform(result.Message,
                   result.Success ? "完成" : "没能完成",
                   result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync();
    }

    private static UsbPort? PortOf(object sender)
        => (sender as FrameworkElement)?.DataContext as UsbPort;

    private bool Confirm(string text)
    {
        var owner = Application.Current?.MainWindow;

        return (owner is null
            ? MessageBox.Show(text, "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(owner, text, "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            == MessageBoxResult.OK;
    }

    private static void Inform(string text, string title, MessageBoxImage icon)
    {
        var owner = Application.Current?.MainWindow;

        if (owner is null) MessageBox.Show(text, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
    }
}
