using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Microsoft.Win32;

using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

/// <summary>
/// 设备解锁：先选机型，再按机型加载资源、步骤与方案。
///
/// 流程对照教程：
///   槽位检查 → 写 ocdt（B.7）→ 重启并格式化数据分区（B.8）
///   → 重开调试（B.9）→ FastBoot 解锁（B.10~B.12）→ 询问是否还原 ocdt（C.1）
/// </summary>
public partial class UnlockPage : Page
{
    private CancellationTokenSource? _cts;
    private UnlockDevice? _device;
    private UnlockService.DeviceInfo? _info;

    /// <summary>当前槽位："A" / "B" / null（没读到）。只支持 A 槽。</summary>
    private string? _slot;

    public UnlockPage()
    {
        InitializeComponent();

        LogBox.Text = "等待操作…\n";
        DeviceCombo.ItemsSource = UnlockService.Devices;
    }

    // ==================== 机型选择 ====================

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not UnlockDevice device) return;
        LoadDevice(device);
    }

    /// <summary>按机型加载资源、步骤与方案标签。</summary>
    private void LoadDevice(UnlockDevice device)
    {
        _device = device;
        _info = null;
        _slot = null;

        DeviceMeta.Visibility = Visibility.Visible;
        ChipText.Text   = device.Chip;
        ModelText.Text  = device.Model;
        MethodText.Text = device.Method;

        EmptyState.Visibility  = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        OcdtUnlockBox.Text = FindAsset(Path.Combine(device.Folder, "ocdt-unlock.img")) ?? string.Empty;
        OcdtStockBox.Text  = FindAsset(Path.Combine(device.Folder, "ocdt-stock.img"))  ?? string.Empty;
        DaDirBox.Text      = FindDir(Path.Combine(device.Folder, "da")) ?? string.Empty;

        StepsList.ItemsSource = device.Steps
            .Select((step, index) => new StepItem(index + 1, step))
            .ToList();

        DeviceInfoText.Text = "尚未读取设备";
        UpdateSlotUi();

        LogBox.Clear();
        Log($"已选择 {device.Name} · {device.Method}");

        if (string.IsNullOrWhiteSpace(OcdtUnlockBox.Text))
            Log("未找到该机型的 ocdt 镜像，请手动选择。");
    }

    /// <summary>步骤列表的显示项。</summary>
    private sealed record StepItem(int Index, string Text);

    // ==================== 资源定位 ====================

    private static string? FindAsset(string relative)
    {
        foreach (var dir in ToolPaths.Dirs)
        {
            try
            {
                var path = Path.Combine(dir, relative);
                if (File.Exists(path)) return path;
            }
            catch
            {
                // 非法路径直接跳过，继续找下一个候选目录
            }
        }

        return null;
    }

    private static string? FindDir(string relative)
    {
        foreach (var dir in ToolPaths.Dirs)
        {
            try
            {
                var path = Path.Combine(dir, relative);
                if (Directory.Exists(path)) return path;
            }
            catch
            {
            }
        }

        return null;
    }

    // ==================== 选择文件 ====================

    private void BrowseOcdtUnlock_Click(object sender, RoutedEventArgs e)
        => OcdtUnlockBox.Text = PickImage() ?? OcdtUnlockBox.Text;

    private void BrowseOcdtStock_Click(object sender, RoutedEventArgs e)
        => OcdtStockBox.Text = PickImage() ?? OcdtStockBox.Text;

    private string? PickImage()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 ocdt 镜像",
            Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void BrowseDaDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "选择 DA 所在目录",
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DaDirBox.Text = dialog.SelectedPath;
    }

    // ==================== 槽位检查 ====================

    private async void CheckSlot_Click(object sender, RoutedEventArgs e)
        => await CheckSlotAsync(silent: false);

    /// <summary>通过 adb 读当前槽位；读不到时返回 null，由调用方决定怎么提示。</summary>
    private async Task<string?> CheckSlotAsync(bool silent)
    {
        SetBusy(true, "读取槽位…");

        try
        {
            var (ok, slot, message) = await UnlockService.ReadSlotAsync(Log);

            _slot = ok ? slot : null;
            UpdateSlotUi();
            Log(message);

            if (!ok && !silent)
            {
                Dialogs.Show(
                    message + "\n\n请确认手机已开机、已开启 USB 调试，并在手机上允许本机调试。",
                    "读取槽位失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return _slot;
        }
        catch (Exception ex)
        {
            Log("读取槽位异常：" + ex.Message);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateSlotUi()
    {
        var (brush, text) = _slot switch
        {
            "A" => ("Mt.Status.OkBrush",    "A 槽位（目标）"),
            "B" => ("Mt.Status.ErrorBrush", "B 槽位（需先切槽）"),
            _   => ("Mt.Status.IdleBrush",  "未检测"),
        };

        SlotDot.Background = (Brush)FindResource(brush);
        SlotText.Text      = text;
    }

    /// <summary>
    /// 解锁前的槽位校验。B 槽位按教程提示切槽，读不到槽位则要求用户自己确认。
    /// 返回 true 表示可以继续写入。
    /// </summary>
    private async Task<bool> EnsureTargetSlotAsync()
    {
        if (_slot is null) await CheckSlotAsync(silent: true);

        if (_slot == UnlockService.TargetSlot) return true;

        if (_slot == "B")
        {
            var answer = Dialogs.Show(
                "当前是 B 槽位。\n\n" +
                "已知问题：B 槽位刷入 ocdt 后，极大概率卡在开机无限重启。\n\n" +
                "请先切到 A 槽位：\n" +
                "1. 用官方降级工具把系统降级到 ColorOS 12（此时就是 A 槽位）\n" +
                "2. 再升级回 ColorOS 13\n" +
                "3. 升级时已经下载好的完整卡刷包，直接发到手机里再刷一次，就会固定在 A 槽位\n\n" +
                "切好之后再回来解锁。现在仍然继续吗？",
                "不是 A 槽位", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes;
        }

        var proceed = Dialogs.Show(
            "没能自动读到当前槽位。\n\n" +
            "请先确认手机是 A 槽位（开机后开启 USB 调试，点「检查槽位」；或在手机上运行「查看槽位.bat」）。\n\n" +
            "B 槽位刷 ocdt 可能无限重启。已经确认是 A 槽位了吗？",
            "无法确认槽位", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return proceed == MessageBoxResult.Yes;
    }

    // ==================== 读取设备信息 ====================

    private async void ReadInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null) return;

        SetBusy(true, "读取设备信息…");
        _cts = new CancellationTokenSource();

        try
        {
            var (ok, info, message) = await UnlockService.ReadDeviceInfoAsync(CreateOptions(), Log, _cts.Token);

            if (ok && info is not null)
            {
                _info = info;
                DeviceInfoText.Text = info.Describe();
                WarnIfChipMismatch(info);
            }
            else
            {
                DeviceInfoText.Text = message;
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("读取异常：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void WarnIfChipMismatch(UnlockService.DeviceInfo info)
    {
        if (_device is null || info.HwCode == _device.HwCode) return;

        Log($"注意：读到的芯片是 {info.Chip}，与所选机型（{_device.Chip}）不一致。");
    }

    // ==================== 解锁 / 还原 ====================

    private async void Unlock_Click(object sender, RoutedEventArgs e)
        => await RunFlashAsync(OcdtUnlockBox.Text, "开始解锁", isUnlock: true);

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null) return;

        var image = OcdtStockBox.Text;
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            Dialogs.Show("没有找到原厂 ocdt 镜像，请先选择。", "缺少镜像",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 已经解锁的设备在 FastBoot 下直接刷；手机开机的话先自动重启过去
        if (FastbootService.DetectDevice() is not null || await IsAdbOnlineAsync())
        {
            if (await RestoreViaFastbootAsync(image)) return;

            var fallback = Dialogs.Show(
                "FastBoot 方式没还原成功。\n\n" +
                "要改用 MTK 下载模式吗？需要先把手机关机，再按住音量键插线。",
                "改用下载模式", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (fallback != MessageBoxResult.Yes) return;
        }

        await RunFlashAsync(image, "还原 ocdt", isUnlock: false);
    }

    /// <summary>解锁与还原共用同一条写入流程，只是镜像不同。</summary>
    private async Task RunFlashAsync(string image, string action, bool isUnlock)
    {
        if (_device is null) return;

        // 解锁前必须过槽位这一关：B 槽位按教程提示切槽，读不到槽位就要求用户确认
        if (isUnlock && !await EnsureTargetSlotAsync()) return;

        if (ChkSafety.IsChecked != true)
        {
            Dialogs.Show("请先勾选「已完成上述准备」。", "还差一步",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            Dialogs.Show("没有找到对应的 ocdt 镜像。", "缺少镜像",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DaDirBox.Text) || !Directory.Exists(DaDirBox.Text))
        {
            Dialogs.Show("请先选择 DA 目录。", "缺少 DA",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = Dialogs.Show(
            $"{_device.Name} · {action}\n\n" +
            "· 会清空手机全部数据\n" +
            "· 可能失去保修与 OTA\n" +
            "· 过程中不要拔线\n\n" +
            "确认继续？",
            "确认" + action, MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "准备中…");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // ---- 1. 读芯片号（决定用哪个 DA 条目）----
            if (_info is null)
            {
                var (ok, info, message) = await UnlockService.ReadDeviceInfoAsync(CreateOptions(), Log, token);
                if (!ok || info is null)
                {
                    DeviceInfoText.Text = message;
                    Dialogs.Show(message, "读取设备失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _info = info;
                DeviceInfoText.Text = info.Describe();
                WarnIfChipMismatch(info);

                // 换 DA 就是换会话，设备必须重新从 BROM 开始，否则第二次握手会超时
                Dialogs.Show(
                    "请拔下数据线，重新按住音量键插线，然后点「确定」继续写入。",
                    "请重新插拔数据线", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // ---- 2. 按芯片号准备 DA ----
            ProgressText.Text = "准备 DA…";
            var daPath = Path.Combine(Path.GetTempPath(), $"mosytool-da-{_info.HwCode:X4}-{_info.HwSubCode:X4}.bin");

            if (!UnlockService.PrepareDa(DaDirBox.Text, _info.HwCode, _info.HwSubCode, daPath, Log, out var daError))
            {
                Log(daError);
                Dialogs.Show(daError, "DA 准备失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var options = CreateOptions(daPath);

            // ---- 3. 写入 ----
            ProgressText.Text = "写入中…";
            var (flashOk, flashMessage) = await UnlockService.FlashOcdtAsync(options, image, Log, token);

            if (!flashOk)
            {
                Log(flashMessage);
                Dialogs.Show(flashMessage, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // ---- 4. 重启 ----
            ProgressText.Text = "重启设备…";
            await UnlockService.RebootAsync(options, Log, token);

            Log($"{action}完成。");

            if (isUnlock)
            {
                ShowPostUnlockGuide();
                await AskRestoreOcdtAsync();
            }
            else
            {
                Dialogs.Show("原厂 ocdt 已写入，设备正在重启。", "完成",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("异常：" + ex.Message);
            Dialogs.Show("操作异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ==================== 教程 B.9 ~ B.12 / C.1 ====================

    /// <summary>教程 B.9 ~ B.12 必须让用户看到，缺一步都可能导致解锁失败。</summary>
    private void ShowPostUnlockGuide()
    {
        Dialogs.Show(
            "ocdt 已写入，设备正在重启。\n\n" +
            "接下来这四步必须完成：\n\n" +
            "9. 开机后进 设置 → 关于本机 → 连点版本号 打开开发者选项，勾上 OEM 解锁 与 USB 调试；数据线重插一次，选「文件传输」\n\n" +
            "10. 让手机进入 FastBoot（点「进入 FastBoot 并解锁」，程序会自动重启过去）\n\n" +
            "11. 执行 fastboot flashing unlock；手机出现英文后，按一下音量上键，再按一下电源键（5 秒内完成）\n\n" +
            "12. 重启后开机出现三行橙色英文，才算解锁成功\n\n" +
            "如果开机停在简体中文界面：先选简体中文 → 格式化数据分区 → 输入验证码。",
            "第 9~12 步（缺一不可）", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>教程 C.1：不打算升 C15 以上就要还原 ocdt，否则可能影响扬声器与摄像头。</summary>
    private async Task AskRestoreOcdtAsync()
    {
        var answer = Dialogs.Show(
            "是否还原原厂 ocdt？\n\n" +
            "· 继续用官方 ColorOS 13、不打算升 C15 以上 → 建议还原\n" +
            "· 打算刷橘子 / 澎湃等 C15+ 系统 → 不用还原\n\n" +
            "长期不还原，可能出现扬声器、摄像头异常。\n\n" +
            "还没做完第 9~12 步的话先点「否」，解锁成功后再点「还原 ocdt」。\n\n" +
            "现在还原吗？",
            "是否还原 ocdt", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            await RestoreViaFastbootAsync(OcdtStockBox.Text);
    }

    /// <summary>adb 是否有已授权的在线设备（手机开机且开了 USB 调试）。</summary>
    private static async Task<bool> IsAdbOnlineAsync()
    {
        if (AdbService.AdbPath is null) return false;

        var output = await Task.Run(() => AdbService.Run(AdbService.AdbPath!, "devices"));
        return output.Split('\n').Any(line => line.Contains("\tdevice", StringComparison.Ordinal));
    }

    // ==================== 通用 ====================

    /// <summary>在 FastBoot 下直接刷回原厂 ocdt，必要时先把手机重启过去。</summary>
    private async Task<bool> RestoreViaFastbootAsync(string image)
    {
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            Dialogs.Show("没有找到原厂 ocdt 镜像。", "缺少镜像",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        SetBusy(true, "还原 ocdt…");

        // 独立的取消源：本方法可能被写 ocdt 的流程串行调用，不能共用外层那个
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        try
        {
            if (FastbootService.DetectDevice() is null)
            {
                ProgressText.Text = "重启到 FastBoot…";

                if (!await UnlockService.RebootToBootloaderAsync(Log, token))
                {
                    Dialogs.Show(
                        "没能自动重启到 FastBoot。\n\n" +
                        "请手动运行 EnterFASTBOOT.bat，等手机进入 FastBoot 后再点「还原 ocdt」。",
                        "需要进入 FastBoot", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                ProgressText.Text = "等待 FastBoot…";
                if (!await UnlockService.WaitForFastbootAsync(Log, 45000, token))
                {
                    Dialogs.Show(
                        "没有等到 FastBoot 设备。\n\n请确认手机屏幕出现 FASTBOOT 字样后再试一次。",
                        "没进入 FastBoot", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            ProgressText.Text = "写入原厂 ocdt…";
            if (!await FastbootService.FlashPartitionAsync("ocdt", image, Log))
            {
                Dialogs.Show(
                    "fastboot 写入 ocdt 失败。\n\n" +
                    "可以让手机关机，按住音量键插线进下载模式，再点「还原 ocdt」走 MTK 方式。",
                    "还原失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            await FastbootService.RunRawAsync("reboot", Log);
            Log("原厂 ocdt 已还原。");
            Dialogs.Show("原厂 ocdt 已还原，设备正在重启。", "完成",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
            return false;
        }
        catch (Exception ex)
        {
            Log("还原异常：" + ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
            _cts = null;
        }
    }

    /// <summary>教程 B.10 ~ B.12：进入 FastBoot 并下发解锁指令。</summary>
    private async void EnterFastbootUnlock_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "进入 FastBoot…");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            if (FastbootService.DetectDevice() is null)
            {
                if (!await UnlockService.RebootToBootloaderAsync(Log, token) ||
                    !await UnlockService.WaitForFastbootAsync(Log, 45000, token))
                {
                    Dialogs.Show(
                        "没能自动进入 FastBoot。\n\n" +
                        "请手动运行 EnterFASTBOOT.bat，等手机屏幕出现 FASTBOOT 字样后，再点一次本按钮。",
                        "需要手动进入 FastBoot", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ProgressText.Text = "下发解锁指令…";
            var (ok, message) = await FastbootService.SetLockStateAsync(true, Log);

            if (!ok)
            {
                Dialogs.Show(message, "解锁指令失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Dialogs.Show(
                "解锁指令已下发。\n\n" +
                "手机出现英文提示后，请按一下音量上键，再按一下电源键（5 秒内完成）。\n\n" +
                "按完点确定，我来重启手机。",
                "请在手机上确认", MessageBoxButton.OK, MessageBoxImage.Information);

            await FastbootService.RunRawAsync("reboot", Log);

            Dialogs.Show(
                "已发送重启。\n\n" +
                "开机后出现三行橙色英文，说明 bootloader 解锁成功；\n没有出现就重新做第 10、11 步。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("解锁异常：" + ex.Message);
            Dialogs.Show("解锁异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>读信息时不要带 DA：带 DA 会直接进 DA 模式，而这一步只需要 devinfo。</summary>
    private static MtkOptions CreateOptions(string? daFile = null)
        => new() { DaFile = daFile, PreloaderFile = null, AuthFile = null, UsbLog = true };

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("已请求取消…");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Log(string message)
    {
        // MTK 原生层在后台线程回调日志，直接碰控件会抛「调用线程无法访问此对象」
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        DeviceCombo.IsEnabled          = !busy;
        ReadInfoButton.IsEnabled       = !busy;
        UnlockButton.IsEnabled         = !busy;
        FastbootUnlockButton.IsEnabled = !busy;
        RestoreButton.IsEnabled        = !busy;
        CancelButton.IsEnabled         = busy;
        ProgressBar.IsIndeterminate    = busy;

        if (!busy) ProgressBar.Value = 0;

        ProgressText.Text = busy ? (status ?? "处理中…") : "等待操作";
    }
}
