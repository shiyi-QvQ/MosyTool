using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class AndroidGeneralPage : Page
{
    private string? _serial;
    private bool _adbOnline;
    private bool _busy;

    public AndroidGeneralPage()
    {
        InitializeComponent();

        LogBox.Reset("等待操作…");
        Loaded += async (_, _) => await DetectAsync();
    }

    // ==================== 设备检测 ====================

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            var info = await Task.Run(DeviceService.Detect);

            _serial    = info.StateKind == "disconnected" ? null : info.Serial;
            _adbOnline = info.StateKind == "online";

            var (text, kind) = info.StateKind switch
            {
                "online"       => ("已连接（ADB）", "ok"),
                "fastboot"     => ("Fastboot 模式", "warn"),
                "unauthorized" => ("未授权 USB 调试", "warn"),
                _              => ("未连接", "idle"),
            };

            StateText.Text  = text;
            DeviceText.Text = _serial is null ? "-" : $"{info.Name} · {info.Serial}";
            SetDot(kind);

            if (info.StateKind == "unauthorized")
                Log("手机已连接但未授权，请在手机上点「允许 USB 调试」。");
            else if (_serial is null)
                Log("未检测到设备。Fastboot 功能需要设备进入 Fastboot / FastbootD 模式。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ==================== Fastboot 侧操作 ====================

    private async void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("格式化设备（双清）",
                "将执行 fastboot erase userdata / erase metadata / -w。\n\n" +
                "手机上的全部数据（照片、聊天记录、账号）会被清空，且无法恢复。\n\n是否继续？"))
            return;

        await RunFastbootAsync(log => AndroidGeneralService.FactoryResetAsync(log));
    }

    private async void EraseFrp_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("擦除谷歌锁（FRP）",
                "将执行 fastboot erase frp，擦掉开机时的谷歌账号校验分区。\n\n" +
                "仅推荐用于自己回收/重置的机器。是否继续？"))
            return;

        await RunFastbootAsync(log => AndroidGeneralService.EraseFrpAsync(log));
    }

    private async void SlotA_Click(object sender, RoutedEventArgs e)
        => await RunFastbootAsync(log => AndroidGeneralService.SetActiveSlotAsync(AndroidGeneralService.SlotA, log));

    private async void SlotB_Click(object sender, RoutedEventArgs e)
        => await RunFastbootAsync(log => AndroidGeneralService.SetActiveSlotAsync(AndroidGeneralService.SlotB, log));

    // ==================== ADB 侧操作 ====================

    private async void FixAdb_Click(object sender, RoutedEventArgs e)
        => await RunDeviceAsync(serial => AndroidGeneralService.FixAdbAsync(serial, Log));

    private async void Ddr_Click(object sender, RoutedEventArgs e)
        => await RunDeviceAsync(serial => AndroidGeneralService.ReadDdrAsync(serial));

    private async void DiagOn_Click(object sender, RoutedEventArgs e)
        => await RunDeviceAsync(serial => AndroidGeneralService.SetDiagPortAsync(serial, enable: true));

    private async void DiagOff_Click(object sender, RoutedEventArgs e)
        => await RunDeviceAsync(serial => AndroidGeneralService.SetDiagPortAsync(serial, enable: false));

    private async void Mtp_Click(object sender, RoutedEventArgs e)
        => await RunDeviceAsync(serial => AndroidGeneralService.SetUsbFunctionAsync(serial, "mtp"));

    private async void MiuiUsb_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("强开小米 USB 安全设置",
                "将开启 USB 安装与 Fastboot 常驻，并把安全中心的安装拦截关掉。\n\n" +
                "需要设备已授予 Shell root 权限（su）。是否继续？"))
            return;

        await RunDeviceAsync(serial => AndroidGeneralService.EnableMiuiUsbAsync(serial, Log));
    }

    // ==================== 执行封装 ====================

    private async Task RunFastbootAsync(Func<Action<string>, Task<(bool Ok, string Message)>> action)
    {
        if (!FastbootService.IsAvailable)
        {
            Log("未检测到 fastboot 设备，请先让手机进入 Fastboot / FastbootD 模式。");
            return;
        }

        SetBusy(true);
        try
        {
            var (ok, message) = await action(Log);
            Log(ok ? $"成功：{message}" : $"失败：{message}");
        }
        catch (Exception ex)
        {
            Log($"异常：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunDeviceAsync(Func<string, Task<(bool Ok, string Message)>> action)
    {
        if (_serial is null || !_adbOnline)
        {
            Log("没有在线且已授权的 ADB 设备，先点「重新检测」。");
            return;
        }

        SetBusy(true);
        try
        {
            var (ok, message) = await action(_serial);
            Log(ok ? $"成功：{message}" : $"失败：{message}");
        }
        catch (Exception ex)
        {
            Log($"异常：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool Confirm(string title, string text)
        => MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    // ==================== 界面状态 ====================

    private Button[] AllActionButtons() =>
    [
        FactoryResetButton, EraseFrpButton, SlotAButton, SlotBButton,
        FixAdbButton, DdrButton, DiagOnButton, DiagOffButton, MtpButton, MiuiUsbButton,
    ];

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;

        if (busy)
        {
            foreach (var button in AllActionButtons()) button.IsEnabled = false;
            return;
        }

        // Fastboot 侧和 ADB 侧各按自己的可用条件放开
        var fastboot = FastbootService.IsAvailable;
        FactoryResetButton.IsEnabled = fastboot;
        EraseFrpButton.IsEnabled     = fastboot;
        SlotAButton.IsEnabled        = fastboot;
        SlotBButton.IsEnabled        = fastboot;

        var adb = _adbOnline && _serial is not null;
        FixAdbButton.IsEnabled  = adb;
        DdrButton.IsEnabled     = adb;
        DiagOnButton.IsEnabled  = adb;
        DiagOffButton.IsEnabled = adb;
        MtpButton.IsEnabled     = adb;
        MiuiUsbButton.IsEnabled = adb;
    }

    private void SetDot(string kind)
    {
        var key = kind switch
        {
            "ok"   => "Mt.Status.OkBrush",
            "warn" => "Mt.Status.WarnBrush",
            _      => "Mt.Status.IdleBrush",
        };

        if (Application.Current?.TryFindResource(key) is Brush brush)
            StateDot.Background = brush;
    }

    private void Log(string message)
    {
        if (Dispatcher.CheckAccess())
            LogBox.Append(message);
        else
            Dispatcher.BeginInvoke(() => LogBox.Append(message));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Reset("日志已清空。");
}
