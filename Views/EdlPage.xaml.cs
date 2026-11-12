using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
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
/// 高通 EDL（9008）工具页。全部能力通过界面表单驱动内置的 qualcomm_ffi 原生层。
/// 本期只做只读：读 GPT / 备份分区 / 从 EDL 复位，不写任何存储。
/// </summary>
public partial class EdlPage : Page
{
    private readonly ObservableCollection<EdlPartition> _partitions = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    public EdlPage()
    {
        InitializeComponent();
        PartitionGrid.ItemsSource = _partitions;
        // 原生层日志实时流进执行日志（回调来自 Rust 线程，Append 自己会切回 UI 线程）
        QualcommService.NativeLog += OnNativeLog;
        Loaded += (_, _) =>
        {
            LoadOptions();
            _ = RefreshStatusAsync();
        };
    }

    private void OnNativeLog(string line) => Append(line);

    // ---------- 连接选项 ----------

    private string ProgrammerPath => (ProgrammerBox.Text ?? string.Empty).Trim();

    private string? MemoryName
    {
        get
        {
            var tag = (MemoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }
    }

    private void LoadOptions()
    {
        ProgrammerBox.Text = SettingsService.EdlProgrammerFile ?? string.Empty;

        // 还没配装载器的用户一进来就该看见这一栏，配好的就收起
        LoaderExpander.IsExpanded = string.IsNullOrEmpty(SettingsService.EdlProgrammerFile);

        SelectMemory(SettingsService.EdlMemoryName);
    }

    /// <summary>按存储类型字符串选中下拉项；未知或为空则回到「自动」。</summary>
    private void SelectMemory(string? memory)
    {
        for (var i = 0; i < MemoryCombo.Items.Count; i++)
        {
            var item = (ComboBoxItem)MemoryCombo.Items[i];
            if (string.Equals(item.Tag as string, memory, StringComparison.OrdinalIgnoreCase))
            {
                MemoryCombo.SelectedIndex = i;
                return;
            }
        }

        MemoryCombo.SelectedIndex = 0;
    }

    private void SaveOptions() => SettingsService.SaveEdlOptions(
        string.IsNullOrWhiteSpace(ProgrammerPath) ? null : ProgrammerPath,
        MemoryName);

    private void MemoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SaveOptions();
    }

    private void ProgrammerBrowse_Click(object sender, RoutedEventArgs e)
    {
        var file = PickOpenFile("选择 firehose 装载器文件",
            "firehose 装载器|*.elf;*.mbn;*.bin|所有文件|*.*");
        if (file is null) return;

        ProgrammerBox.Text = file;
        SaveOptions();
    }

    // ---------- 状态 ----------

    private async Task RefreshStatusAsync()
    {
        if (!QualcommService.IsAvailable)
        {
            ToolStatusText.Text   = "未就绪";
            DeviceStatusText.Text = "当前设备端口：无";
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            ToolStatusText.Text = "已就绪";

            var (ready, text, _) = await Task.Run(QualcommService.InspectDevice);
            DeviceStatusText.Text = text;

            // 设备未就绪时用警示色，避免用户对着不动的日志干等超时。
            if (ready)
                DeviceStatusText.ClearValue(TextBlock.ForegroundProperty);
            else if (TryFindResource("SystemFillColorCautionBrush") is Brush caution)
                DeviceStatusText.Foreground = caution;

            UpdateSessionUi();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    /// <summary>按当前会话状态刷新按钮可用性。读写设备都要有会话，导出类只要读到过表就能用。</summary>
    private void UpdateSessionUi()
    {
        var connected = QualcommService.IsConnected;
        var hasList   = _partitions.Count > 0;
        var hasSelect = PartitionGrid.SelectedItems.Count > 0;

        DisconnectButton.IsEnabled = connected;
        ResetButton.IsEnabled      = connected;

        ExportPartitionButton.IsEnabled  = hasList;
        ExportRawprogramButton.IsEnabled = hasList;
        DetectSlotButton.IsEnabled       = hasList;

        BackupSelectedButton.IsEnabled = connected && hasSelect;
        WriteBackButton.IsEnabled      = connected && hasSelect;
        EraseButton.IsEnabled          = connected && hasSelect;
    }

    // ---------- 执行 ----------

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// 所有设备操作的统一外壳：忙检查 → 设备体检 → 置运行标志 → 执行体 → 复位。
    /// </summary>
    private async Task<int> RunGuardedAsync(string title,
                                            Func<CancellationToken, Action<string>, Task<int>> run)
    {
        if (_running)
        {
            MessageBox.Show("已有操作正在执行，请等待完成或先点击「停止」。", "忙",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return -1;
        }

        if (string.IsNullOrWhiteSpace(ProgrammerPath))
        {
            Warn("请先在上方「装载器文件」里选择 firehose programmer（prog_firehose_*.elf）。",
                 "缺少装载器");
            LoaderExpander.IsExpanded = true;
            return -1;
        }

        if (!File.Exists(ProgrammerPath))
        {
            Warn($"装载器文件不存在：\n{ProgrammerPath}", "文件缺失");
            return -1;
        }

        // 前置检查：原生层对不在它端口表里的设备会一路静默到超时，先拦一道。
        var (deviceReady, deviceText, deviceHint) = await Task.Run(QualcommService.InspectDevice);
        if (!deviceReady &&
            !Confirm($"{deviceText}\n\n{deviceHint}\n\n" +
                     "直接执行的话，会等满 15 秒超时后才报错。\n\n是否仍然尝试？", "设备未就绪"))
        {
            Append(string.Empty);
            Append($"[已取消] {deviceText}");
            Append($"[建议] {deviceHint}");
            return -1;
        }

        SaveOptions();
        _running             = true;
        StopButton.IsEnabled = true;

        Append(string.Empty);
        Append($"> {title}");

        if (!deviceReady)
            Append($"[警告] {deviceText} {deviceHint}");

        _cts = new CancellationTokenSource();
        try
        {
            return await run(_cts.Token, Append);
        }
        finally
        {
            _cts?.Dispose();
            _cts                 = null;
            _running             = false;
            StopButton.IsEnabled = false;
            UpdateSessionUi();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => OutputBox.Clear();

    private void Append(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Append(line));
            return;
        }

        // 等级由 LogView 按行文本推断（[错误] / [警告] / [成功] / [提示] / "> 阶段标题" …）
        OutputBox.Append(line);
    }

    // ---------- 连接 / 断开 ----------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!QualcommService.IsAvailable) { ReportNativeMissing(); return; }

        await RunGuardedAsync("连接 EDL 设备", async (ct, sink) =>
        {
            var (ok, message) = await QualcommService.ConnectAsync(ProgrammerPath, MemoryName, sink, ct);
            sink($"[{(ok ? "连接成功" : "连接失败")}] {message}");
            return ok ? 0 : 1;
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await QualcommService.DisconnectAsync();
        Append(string.Empty);
        Append("[已断开] firehose 会话已释放。设备仍停在 EDL，下次操作会重新握手。");
        UpdateSessionUi();
    }

    // ---------- 分区表 ----------

    private async void LoadPartition_Click(object sender, RoutedEventArgs e)
    {
        if (!QualcommService.IsAvailable) { ReportNativeMissing(); return; }

        var loaded = new List<EdlPartition>();

        var code = await RunGuardedAsync("读取 GPT 分区表", async (ct, sink) =>
        {
            var (partitions, exit) = await QualcommService.ReadGptAsync(ProgrammerPath, MemoryName, sink, ct);
            loaded = partitions;
            sink(exit == 0 ? "[读取分区表 完成]" : "[读取分区表 失败，原因见上方日志]");
            return exit;
        });

        _partitions.Clear();
        if (code == 0)
        {
            foreach (var partition in loaded)
                _partitions.Add(partition);
        }

        PartitionCountText.Text = _partitions.Count > 0
            ? $"共 {_partitions.Count} 个分区。"
            : "未读到分区。请查看下方日志，确认设备是否已成功进入 firehose。";

        // 表换了，上一次的槽位结论就不再成立
        SlotInfoText.Visibility = Visibility.Collapsed;

        UpdateSessionUi();
    }

    private void ExportPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count == 0) return;

        var path = PickSaveFile("导出分区表", "gpt_partitions.csv", "CSV 文件|*.csv");
        if (path is null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Index,FirstLba,LastLba,Sectors,Size,SizeHuman,TypeGuid,PartitionGuid");
            foreach (var p in _partitions)
                sb.AppendLine($"{p.Name},{p.Index},{p.FirstLba},{p.LastLba},{p.Sectors},{p.Size}," +
                              $"{p.SizeHuman},{p.TypeGuid},{p.PartitionGuid}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Warn($"已导出 {_partitions.Count} 条记录到：\n{path}", "导出完成");
        }
        catch (Exception ex)
        {
            Warn($"导出失败：{ex.Message}", "错误");
        }
    }

    private void PartitionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSessionUi();

    /// <summary>取分区表里当前选中的分区（支持 Ctrl / Shift 多选）。</summary>
    private List<EdlPartition> SelectedPartitions()
        => PartitionGrid.SelectedItems.OfType<EdlPartition>().ToList();

    /// <summary>
    /// 把选中分区备份成一个「备份集」：镜像统一落在 backup_&lt;时间戳&gt;\ 下，
    /// 同时写一份清单，记录每个镜像对应的扇区区间与大小，便于日后核对。
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
        var dir   = Path.Combine(root, $"edl_backup_{stamp}");

        if (!Confirm($"即将备份以下分区：\n\n{preview}\n\n保存到：\n{dir}\n\n" +
                     "备份为只读操作，不会修改设备。是否继续？", "备份备份集"))
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
            .Select(p => new QualcommService.EdlBackupItem(
                $"备份 {p.Name}", Path.Combine(dir, $"{p.Name}.img"), p.FirstLba, p.Sectors))
            .ToList();

        var code = await RunGuardedAsync($"备份选中分区（{selected.Count} 个）", async (ct, sink) =>
        {
            var (ok, bad) = await QualcommService.BackupAsync(ProgrammerPath, MemoryName, items, sink, ct);
            sink($"[备份结束：成功 {ok} 项，失败 {bad} 项]");
            return bad == 0 ? 0 : 1;
        });

        WriteBackupManifest(dir, selected, stamp, code == 0);
    }

    /// <summary>写备份集清单。清单写不出来不影响已落盘的镜像，所以只静默忽略。</summary>
    private static void WriteBackupManifest(string dir, IEnumerable<EdlPartition> partitions,
                                            string stamp, bool completed)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,FirstLba,Sectors,Size,SizeHuman,File");
            foreach (var p in partitions)
                sb.AppendLine($"{p.Name},{p.FirstLba},{p.Sectors},{p.Size},{p.SizeHuman},{p.Name}.img");

            File.WriteAllText(Path.Combine(dir, "backup-manifest.csv"), sb.ToString(), Encoding.UTF8);

            File.WriteAllText(Path.Combine(dir, "backup-readme.txt"),
                $"EDL 备份集：{stamp}\r\n状态：{(completed ? "全部成功" : "部分失败，请对照日志确认")}\r\n\r\n" +
                "本备份集为分区镜像的只读转储，请勿重命名 .img 文件——文件名与分区名的对应关系就是还原依据。\r\n",
                Encoding.UTF8);
        }
        catch { /* 清单非关键，忽略 */ }
    }

    // ---------- rawprogram XML / 槽位 ----------

    private void ExportRawprogram_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count == 0) return;

        var path = PickSaveFile("导出 rawprogram XML", "rawprogram0.xml", "XML 文件|*.xml");
        if (path is null) return;

        try
        {
            // 没连过设备时拿不到真实扇区大小，按最常见的 512 兜底
            var sector = QualcommService.SectorSize;
            if (sector == 0) sector = 512;

            File.WriteAllText(path,
                QualcommService.BuildRawprogramXml(_partitions, sector),
                new UTF8Encoding(false));

            Warn($"已生成 rawprogram XML（{_partitions.Count} 条 program 记录，扇区 {sector} 字节）：\n{path}\n\n" +
                 "文件里的 filename 统一写作「分区名.img」，请把镜像按同名放进同一目录后再用。", "导出完成");
        }
        catch (Exception ex)
        {
            Warn($"导出失败：{ex.Message}", "错误");
        }
    }

    /// <summary>
    /// A/B 槽位检测：判定依据只有分区名的 <c>_a</c> / <c>_b</c> 后缀。
    ///
    /// 真正的「当前活动槽位」记在设备的元数据里，本工具不去猜——只把成对关系摊开，
    /// 让用户自己判断要刷哪个槽。
    /// </summary>
    private void DetectSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count == 0) return;

        var slots = QualcommService.DetectSlots(_partitions);

        Append(string.Empty);

        if (slots.Count == 0)
        {
            SlotInfoText.Text = "未发现 A/B 槽位分区：这张分区表里没有「_a / _b」成对命名的分区。";
            SlotInfoText.Visibility = Visibility.Visible;

            Append("[槽位检测] 未发现 A/B 槽位分区。");
            return;
        }

        var paired = slots.Count(s => s.SlotA is not null && s.SlotB is not null);

        var names = string.Join("、", slots.Take(10).Select(s => s.Describe()));
        if (slots.Count > 10) names += $"……等共 {slots.Count} 组";

        SlotInfoText.Text = $"A/B 槽位：共 {slots.Count} 组，其中 {paired} 组 A/B 齐全。\n{names}";
        SlotInfoText.Visibility = Visibility.Visible;

        Append($"[槽位检测] 共 {slots.Count} 组 A/B 分区，{paired} 组齐全。");

        foreach (var slot in slots)
        {
            var a = slot.SlotA is null ? "—" : QualcommService.HumanSize(slot.SlotA.Size);
            var b = slot.SlotB is null ? "—" : QualcommService.HumanSize(slot.SlotB.Size);
            Append($"  {slot.BaseName}：A {a} / B {b}");
        }
    }

    // ---------- 写回 / 擦除 ----------

    /// <summary>
    /// 写回：以「分区名.img」为约定，从用户指定的镜像目录里取每个选中分区的镜像。
    ///
    /// 用目录约定而不是逐个弹文件对话框，是因为刷一次往往要连着写好几个分区；
    /// 对不上的分区会被明确列出来并跳过，不做静默替换。
    /// </summary>
    private async void WriteBack_Click(object sender, RoutedEventArgs e)
    {
        if (!QualcommService.IsAvailable) { ReportNativeMissing(); return; }

        var selected = SelectedPartitions();
        if (selected.Count == 0)
        {
            Warn("请先在分区表中选中要写回的分区（按住 Ctrl / Shift 可多选）。");
            return;
        }

        var dir = PickFolder("选择存放镜像的目录（约定：分区名.img）");
        if (dir is null) return;

        var items   = new List<QualcommService.EdlWriteItem>();
        var missing = new List<string>();

        foreach (var p in selected)
        {
            var image = FindImage(dir, p.Name);

            if (image is null)
            {
                missing.Add(p.Name);
                continue;
            }

            items.Add(new QualcommService.EdlWriteItem($"写入 {p.Name}", image, p.FirstLba, p.Sectors));
        }

        if (items.Count == 0)
        {
            Warn($"在目录里没找到任何匹配的镜像：\n{dir}\n\n" +
                 "镜像文件名必须与分区名一致（例如 boot.img）。", "无可写入的镜像");
            return;
        }

        var preview = string.Join('\n', items.Take(8).Select(i =>
            $"  {Path.GetFileName(i.Image)}  →  {i.Label[3..]}"));

        if (items.Count > 8) preview += $"\n  ……等共 {items.Count} 个分区";

        if (missing.Count > 0)
            preview += $"\n\n以下 {missing.Count} 个分区在目录里没有对应镜像，将被跳过：\n  " +
                       string.Join("、", missing);

        if (!Confirm($"即将把镜像写入设备，会覆盖分区原有内容且不可撤销：\n\n{preview}\n\n" +
                     "写入过程中请勿断开数据线或让设备断电。是否继续？", "确认写回"))
            return;

        await RunGuardedAsync($"写回分区（{items.Count} 个）", async (ct, sink) =>
        {
            var (ok, bad) = await QualcommService.WriteAsync(ProgrammerPath, MemoryName, items, sink, ct);
            sink($"[写回结束：成功 {ok} 项，失败 {bad} 项]");
            return bad == 0 ? 0 : 1;
        });
    }

    /// <summary>在镜像目录里按分区名找镜像：先试 .img / .bin / 同名无扩展名，再兜底做一次忽略大小写的匹配。</summary>
    private static string? FindImage(string dir, string partition)
    {
        foreach (var candidate in new[] { partition + ".img", partition + ".bin", partition })
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path)) return path;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileNameWithoutExtension(file)
                        .Equals(partition, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch { /* 目录列不出来就当作没找到 */ }

        return null;
    }

    private async void EraseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!QualcommService.IsAvailable) { ReportNativeMissing(); return; }

        var selected = SelectedPartitions();
        if (selected.Count == 0)
        {
            Warn("请先在分区表中选中要擦除的分区（按住 Ctrl / Shift 可多选）。");
            return;
        }

        var preview = string.Join('\n', selected.Take(8).Select(p => $"  {p.Name}（{p.SizeHuman}）"));
        if (selected.Count > 8) preview += $"\n  ……等共 {selected.Count} 个分区";

        var total = selected.Aggregate(0UL,
            (sum, p) => sum + p.Sectors * Math.Max(QualcommService.SectorSize, 1));

        if (!Confirm($"即将擦除以下分区，内容会被整段清零且不可恢复：\n\n{preview}\n\n" +
                     $"合计约 {QualcommService.HumanSize(total)}。\n\n确认是这些分区？", "确认擦除"))
            return;

        var items = selected
            .Select(p => new QualcommService.EdlEraseItem($"擦除 {p.Name}", p.FirstLba, p.Sectors))
            .ToList();

        await RunGuardedAsync($"擦除分区（{selected.Count} 个）", async (ct, sink) =>
        {
            var (ok, bad) = await QualcommService.EraseAsync(ProgrammerPath, MemoryName, items, sink, ct);
            sink($"[擦除结束：成功 {ok} 项，失败 {bad} 项]");
            return bad == 0 ? 0 : 1;
        });
    }

    // ---------- 设备控制 ----------

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!QualcommService.IsAvailable) { ReportNativeMissing(); return; }

        if (!Confirm("即将让设备从 EDL 复位重启。\n\n" +
                     "复位会释放 firehose 会话，设备随后退出 9008 并正常开机。是否继续？", "复位设备"))
            return;

        await RunGuardedAsync("复位设备（退出 EDL）", async (ct, sink) =>
        {
            var (ok, message) = await QualcommService.ResetAsync(ProgrammerPath, MemoryName, sink, ct);
            sink($"[{(ok ? "复位成功" : "复位失败")}] {message}");
            return ok ? 0 : 1;
        });

        UpdateSessionUi();
    }

    private void ReportNativeMissing()
    {
        Append(string.Empty);
        Append("[无法执行] 未找到原生层 qualcomm_ffi.dll，请确认 tools 目录完整。");

        Warn("未找到原生层 qualcomm_ffi.dll。\n\n请确认安装包的 tools 目录完整后重试。",
             "原生层不可用");
    }

    // ---------- 交互辅助 ----------

    private static bool Confirm(string message, string title = "操作确认")
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    private static void Warn(string message, string title = "提示")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

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
            Description            = description,
            UseDescriptionForTitle = true,
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }
}
