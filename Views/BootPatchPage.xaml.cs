using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mosytool.Services;

namespace Mosytool.Views;

/// <summary>
/// 脱机修补：给 boot / init_boot 镜像离线注入 Magisk，或配合 AnyKernel3 制作 GKI。
/// </summary>
public partial class BootPatchPage : Page
{
    private CancellationTokenSource? _cts;

    /// <summary>当前镜像的探测结果（有没有内核 → 决定 GKI / LKM）。</summary>
    private BootImageInfo? _image;

    public BootPatchPage()
    {
        InitializeComponent();
        LogBox.Text = "等待操作…\n";
        RefreshKmiList();
        UpdateSchemeHint();
    }

    // ==================== 修补方案 ====================

    private BootPatchService.PatchScheme SelectedScheme => SchemeCombo.SelectedIndex switch
    {
        1 => BootPatchService.PatchScheme.Magisk,
        2 => BootPatchService.PatchScheme.KernelSuGki,
        3 => BootPatchService.PatchScheme.Lkm,
        _ => BootPatchService.PatchScheme.Auto,
    };

    private string SelectedFamily =>
        (FamilyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? LkmPatchService.FamilySukiSU;

    /// <summary>镜像里确定没有内核（init_boot / vendor_boot）。</summary>
    private bool ImageHasNoKernel => _image is { Kind: BootImageKind.InitBoot or BootImageKind.VendorBoot };

    private void SchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 期间就会触发一次，那时后面的控件还没建好
        if (SchemeHint is null) return;

        UpdateSchemeHint();
    }

    /// <summary>
    /// 方案提示 + 依赖项的联动文案。KernelSU 系的路线由镜像决定：
    /// 有内核才能走 GKI（要安装包里的 kpimg），没有内核只能走 LKM（用预编译 .ko，不要安装包）。
    /// </summary>
    private void UpdateSchemeHint()
    {
        if (SchemeHint is null) return;

        SchemeHint.Text = SelectedScheme switch
        {
            BootPatchService.PatchScheme.Magisk =>
                "固定走 Magisk 的 ramdisk 注入（boot_patch.sh 那套），组件从安装包里取。",

            BootPatchService.PatchScheme.KernelSuGki when ImageHasNoKernel =>
                "当前镜像是 init_boot / vendor_boot，里面没有内核，GKI 这条路走不了；"
                + "开始修补时会自动改用 LKM（注入 .ko，不需要安装包）。",

            BootPatchService.PatchScheme.KernelSuGki =>
                "把安装包里的 kpimg 嵌进内核，只改内核，需要安装包。适用 boot.img —— "
                + "init_boot.img 没有内核，会改用 LKM。",

            BootPatchService.PatchScheme.Lkm =>
                "把预编译的 kernelsu.ko 打进 ramdisk，不改内核，所以 init_boot / vendor_boot 也能用，"
                + "不需要安装包；代价是 KMI 必须和手机内核版本严格一致，打错了开不了机。",

            _ => ImageHasNoKernel
                ? "镜像是 init_boot / vendor_boot（没有内核）：KernelSU 系会自动走 LKM；选 Magisk 包则仍按 ramdisk 注入。"
                : "按安装包和镜像一起判断：Magisk 包注入 ramdisk；KernelSU / SukiSU 包有内核走 GKI 补丁，"
                + "没有内核（init_boot / vendor_boot）自动改用 LKM。",
        };

        // 会走 LKM 的组合把「安装包」标成可留空，别让人以为非选不可
        var needsApk = EffectiveScheme(_image, out _) != BootPatchService.PatchScheme.Lkm;

        ApkLabel.Text = needsApk
            ? "安装包（Magisk / KernelSU 系 APK，组件从它里面取；LKM 模式不需要）"
            : "安装包（当前走 LKM，不需要，可留空）";

        KmiCombo.ToolTip = needsApk ? null : "走 LKM 必须选 KMI（可点「读取设备」自动识别）";
    }

    /// <summary>
    /// 这次点击实际会走哪条路。返回 Lkm 时不需要安装包。
    /// 只在界面预算一次，真正的分流在 BootPatchService 里还有一道，两边结论保持一致。
    /// </summary>
    private BootPatchService.PatchScheme EffectiveScheme(BootImageInfo? image, out bool lkmForced)
    {
        lkmForced = false;

        var scheme = SelectedScheme;
        var noKernel = image is { Kind: BootImageKind.InitBoot or BootImageKind.VendorBoot };

        if (scheme == BootPatchService.PatchScheme.Lkm) return BootPatchService.PatchScheme.Lkm;

        if (scheme == BootPatchService.PatchScheme.KernelSuGki)
        {
            if (noKernel)
            {
                lkmForced = true;
                return BootPatchService.PatchScheme.Lkm;
            }

            return scheme;
        }

        if (scheme == BootPatchService.PatchScheme.Auto && noKernel)
        {
            // 没有内核的镜像上，只有「明确的 Magisk 包」才走 Magisk，其余（含没选包）都会落到 LKM
            var apk = MagiskApkBox.Text;
            var hasMagiskPackage = !string.IsNullOrWhiteSpace(apk)
                                && File.Exists(apk)
                                && !BootPatchService.IsKernelSuPackage(apk);

            if (!hasMagiskPackage)
            {
                lkmForced = true;
                return BootPatchService.PatchScheme.Lkm;
            }
        }

        return scheme;
    }

    /// <summary>重新探测镜像类型，并把结论写进界面（无内核时顺手把方案切到 LKM）。</summary>
    private void RefreshImageInfo()
    {
        if (ImageInfoText is null) return;

        var path = BootImageBox.Text;
        _image = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? BootImageInspector.Probe(path)
            : null;

        if (_image is null)
        {
            ImageInfoText.Text = "选择镜像后自动判断类型：boot.img 有内核可走 GKI；init_boot / vendor_boot 没有内核，只能走 LKM。";
            UpdateSchemeHint();
            return;
        }

        var info = _image.Value;

        ImageInfoText.Text = info.Kind switch
        {
            BootImageKind.Boot =>
                $"{info.Describe()}：有内核，KernelSU 系可走 GKI（嵌入 kpimg，需要安装包），也可选 LKM。",
            BootImageKind.InitBoot =>
                $"{info.Describe()}：没有内核，KernelSU 系只能走 LKM（注入 .ko，不需要安装包）。",
            BootImageKind.VendorBoot =>
                $"{info.Describe()}：这里既没有内核，也不是 init 挂载点，一般不需要刷这个分区 —— "
                + "Android 13+ 请选 init_boot.img。确有需要可手动选 LKM 尝试（ksud 官方只声明支持 boot / init_boot）。",
            _ => info.Describe(),
        };

        // init_boot 上没有内核，自动 / GKI 两个方案都收敛到 LKM，省得用户点下去才被拦；
        // vendor_boot 不自动切 —— 那个分区本就不是 KernelSU 的落点，先让人看清楚提示。
        if (info.Kind == BootImageKind.InitBoot
            && SelectedScheme is BootPatchService.PatchScheme.Auto or BootPatchService.PatchScheme.KernelSuGki)
        {
            SchemeCombo.SelectedIndex = 3;
            Log("镜像里没有内核，修补方案已自动切换为「KernelSU 系 LKM」。");
        }
        else
        {
            UpdateSchemeHint();
        }
    }

    /// <summary>从已连接设备读 uname -r 填进 KMI 下拉，省得用户自己去翻内核版本。</summary>
    private async void DetectKmiButton_Click(object sender, RoutedEventArgs e)
    {
        DetectKmiButton.IsEnabled = false;
        try
        {
            var kmi = await Task.Run(KmiResolver.FromDevice);

            if (kmi is null)
            {
                MessageBox.Show("没读到设备内核版本。\n请确认手机已连接且开启 USB 调试，或在下方手动选择 KMI。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (KmiCombo.Items.Contains(kmi))
            {
                KmiCombo.SelectedItem = kmi;
            }
            else
            {
                MessageBox.Show($"设备内核是 {kmi}，但 {SelectedFamily} 没有这个 KMI 的模块。\n"
                              + "请换一个管理器，或在「LKM KMI」里改选。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            DetectKmiButton.IsEnabled = true;
        }
    }

    private void FamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KmiCombo is null) return;   // 初始化的同一帧里 KmiCombo 还没建好
        RefreshKmiList();
    }

    /// <summary>
    /// 按家族把目录里实际存在的 KMI 列出来（各家带的版本不一样）。
    /// 刻意不默认选中第一项：KMI 选错会打错 .ko、刷完起不来，必须让用户自己确认过手机内核版本。
    /// </summary>
    private void RefreshKmiList()
    {
        var family   = SelectedFamily;
        var previous = KmiCombo.SelectedItem as string;
        var list     = LkmPatchService.GetKmis(family);

        KmiCombo.ItemsSource = list;

        if (previous is not null && list.Contains(previous))
            KmiCombo.SelectedItem = previous;
        else
            KmiCombo.SelectedItem = null;
    }

    /// <summary>
    /// 日志回调来自两处：UI 线程（点击、结果）和工具进程的输出线程（magiskboot / kptools 逐行回显）。
    /// 后者直接碰 TextBox 会抛「调用线程无法访问此对象」，必须先切回 UI 线程。
    /// </summary>
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    // ==================== 选择文件 ====================

    private void BrowseBoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 boot / init_boot 镜像",
            Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            BootImageBox.Text = dialog.FileName;
            Log($"已选择镜像：{dialog.FileName}");

            // 输出目录还没选就顺着镜像所在位置给个默认值
            if (string.IsNullOrWhiteSpace(OutputDirBox.Text))
                OutputDirBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;

            // 立刻判断「有没有内核」，据此决定 GKI / LKM
            RefreshImageInfo();
        }
    }

    private void BrowseMagisk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 Magisk 安装包",
            Filter = "Magisk 安装包 (*.apk)|*.apk|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            MagiskApkBox.Text = dialog.FileName;
            Log($"已选择 Magisk 安装包：{Path.GetFileName(dialog.FileName)}");
            UpdateSchemeHint();
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "选择输出目录",
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirBox.Text = dialog.SelectedPath;
    }

    private void BrowseAk3_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 AnyKernel3 压缩包",
            Filter = "AnyKernel3 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            Ak3Box.Text = dialog.FileName;
            Log($"已选择 AnyKernel3：{Path.GetFileName(dialog.FileName)}");
        }
    }

    // ==================== 脱机修补 ====================

    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        var bootImage = BootImageBox.Text;
        var magiskApk = MagiskApkBox.Text;
        var outDir    = OutputDirBox.Text;

        if (string.IsNullOrWhiteSpace(bootImage) || !File.Exists(bootImage))
        {
            MessageBox.Show("请先选择有效的 boot / init_boot 镜像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 点下去之前再确认一次镜像类型：镜像是 init_boot / vendor_boot 时，
        // 自动 / GKI 都会被收敛到 LKM（不需要安装包），别让用户卡在「选包」上。
        RefreshImageInfo();

        var scheme = EffectiveScheme(_image, out var lkmForced);
        var kmi    = KmiCombo.SelectedItem as string ?? string.Empty;

        if (lkmForced)
        {
            Log($"按镜像分流：{_image?.Describe() ?? "镜像"} 里没有内核，本次改走 KernelSU 系 LKM。");
            Log($"LKM 用的是预编译 .ko（当前家族：{SelectedFamily}），必须和你装的安装包是同一家。");
        }

        // LKM 用的是随程序发布的 .ko，不需要安装包；KMI 没选就交给服务层从设备读，读不到再报错
        if (scheme == BootPatchService.PatchScheme.Lkm)
        {
            if (string.IsNullOrWhiteSpace(kmi))
                Log("未指定 KMI，修补时将从已连接设备读取内核版本。");
        }
        else if (string.IsNullOrWhiteSpace(magiskApk) || !File.Exists(magiskApk))
        {
            MessageBox.Show("请先选择有效的安装包（Magisk 或 KernelSU 系 APK）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new BootPatchService.PatchOptions
        {
            Scheme             = scheme,
            LkmFamily          = SelectedFamily,
            LkmKmi             = kmi,
            Architecture       = (ArchCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "arm64-v8a",
            KeepVerity         = ChkKeepVerity.IsChecked == true,
            KeepForceEncrypt   = ChkKeepForceEncrypt.IsChecked == true,
            PatchVbmetaFlag    = ChkPatchVbmeta.IsChecked == true,
            RecoveryMode       = ChkRecoveryMode.IsChecked == true,
            LegacySar          = ChkLegacySar.IsChecked == true,
            PatchKernelBinaries= ChkPatchBinaries.IsChecked == true,
        };

        // 输出名跟着镜像类型走：刷错分区比刷错包更常见，文件名至少提醒一句
        var (outputName, partition) = _image?.Kind switch
        {
            BootImageKind.InitBoot   => ("patched-init_boot.img",   "init_boot"),
            BootImageKind.VendorBoot => ("patched-vendor_boot.img", "vendor_boot"),
            _                        => ("patched-boot.img",        "boot"),
        };

        var outputImage = Path.Combine(outDir, outputName);
        var workDir     = Path.Combine(outDir, "patch-work");

        Log("========== 开始脱机修补 ==========");
        SetBusy(true);
        _cts = new CancellationTokenSource();

        try
        {
            var result = await BootPatchService.PatchAsync(bootImage, magiskApk, outputImage,
                                                           options, workDir, Log, _cts.Token);

            if (result.Success)
            {
                Log($"修补成功：{result.OutputImage}");
                MessageBox.Show($"修补完成。\n\n输出文件：\n{result.OutputImage}\n\n把它刷入 {partition} 分区即可。",
                                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"修补失败：{result.Message}");
            }
        }
        catch (Exception ex)
        {
            Log($"修补异常：{ex.Message}");
            MessageBox.Show("修补异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ==================== 制作 GKI ====================

    private async void GkiButton_Click(object sender, RoutedEventArgs e)
    {
        var bootImage = BootImageBox.Text;
        var ak3Zip    = Ak3Box.Text;
        var outDir    = OutputDirBox.Text;

        if (string.IsNullOrWhiteSpace(bootImage) || !File.Exists(bootImage))
        {
            MessageBox.Show("请先选择有效的 boot 镜像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ak3Zip) || !File.Exists(ak3Zip))
        {
            MessageBox.Show("请先选择有效的 AnyKernel3 压缩包。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new BootPatchService.GkiOptions
        {
            PatchDtb      = ChkGkiDtb.IsChecked == true,
            WantInitramfs = ChkGkiInitramfs.IsChecked == true,
        };

        var outputImage = Path.Combine(outDir, "boot-new.img");
        var workDir     = Path.Combine(outDir, "gki-work");

        Log("========== 开始制作 GKI ==========");
        SetBusy(true);
        _cts = new CancellationTokenSource();

        try
        {
            var result = await BootPatchService.BuildGkiAsync(bootImage, ak3Zip, outputImage,
                                                              options, workDir, Log, _cts.Token);

            if (result.Success)
            {
                Log($"制作完成：{result.OutputImage}");
                MessageBox.Show($"已生成 GKI 镜像。\n\n输出文件：\n{result.OutputImage}",
                                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"制作失败：{result.Message}");
            }
        }
        catch (Exception ex)
        {
            Log($"制作异常：{ex.Message}");
            MessageBox.Show("制作异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ==================== 通用 ====================

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("已请求取消…");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void SetBusy(bool busy)
    {
        PatchButton.IsEnabled  = !busy;
        GkiButton.IsEnabled    = !busy;
        CancelButton.IsEnabled = busy;
        ProgressBar.IsIndeterminate = busy;

        if (!busy) ProgressBar.Value = 0;

        ProgressText.Text = busy ? "处理中…" : "等待操作…";
    }
}
