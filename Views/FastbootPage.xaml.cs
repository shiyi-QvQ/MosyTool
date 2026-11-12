using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class FastbootPage : Page
{
    /// <summary>分区表内容。绑定在 DataGrid 上，「只看双槽位」筛选走它的默认视图。</summary>
    private readonly ObservableCollection<FastbootPartition> _partitions = new();

    public FastbootPage()
    {
        InitializeComponent();
        PartitionGrid.ItemsSource = _partitions;
        LogBox.Reset("等待操作…");
        Loaded += async (_, _) => await DetectAsync();
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectAsync();
    }

    private async Task DetectAsync()
    {
        if (!FastbootService.IsAvailable)
        {
            UpdateState("未检测到 fastboot", "none");
            Log("未检测到 fastboot 工具，请将 platform-tools 放入程序目录的 tools 文件夹。");
            return;
        }

        DetectButton.IsEnabled = false;
        try
        {
            Log("> 正在检测 Fastboot 设备…");
            var serial = await Task.Run(FastbootService.DetectDevice);

            if (serial is null)
            {
                UpdateState("未处于 Fastboot 模式", "none");
                Log("未检测到 Fastboot 设备。");
                FlashButton.IsEnabled = false;
                ResetPartitionTable();
                PromptEnterFastboot();
                return;
            }

            UpdateState("Fastboot 模式", "fastboot");
            Log($"已检测到 Fastboot 设备: {serial}");

            // 分区表里已经带了 current-slot，省一次 getvar；设备不支持时才单独再问一遍。
            Log("> 正在读取分区表（fastboot getvar all）…");
            var table = await Task.Run(FastbootService.GetPartitionTable);

            var slot = table.CurrentSlot?.ToUpperInvariant()
                       ?? await Task.Run(FastbootService.GetCurrentSlot);
            SlotText.Text = slot is null ? "无 A/B 分区" : slot;
            Log($"当前槽位: {slot ?? "无"}");

            LoadPartitionTable(table);

            bool userspace = await Task.Run(FastbootService.IsUserspace);
            if (userspace)
            {
                Log("提示：当前处于 FastbootD（用户空间）模式，静态分区（logo/boot/recovery 等）不可见。");
                Log("      若要刷 logo 等静态分区，请先回到 bootloader 模式（重启到 bootloader）。");
            }

            FlashButton.IsEnabled = true;
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    // ---------- 分区表 ----------

    /// <summary>「读取分区」按钮：重新读一次设备分区表。</summary>
    private async void LoadPartition_Click(object sender, RoutedEventArgs e)
    {
        if (!FastbootService.IsAvailable)
        {
            MessageBox.Show("未检测到 fastboot 工具，请将 platform-tools 放入程序目录的 tools 文件夹。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadPartitionButton.IsEnabled = false;
        try
        {
            Log("> 正在读取分区表（fastboot getvar all）…");
            LoadPartitionTable(await Task.Run(FastbootService.GetPartitionTable));
        }
        finally
        {
            LoadPartitionButton.IsEnabled = true;
        }
    }

    /// <summary>把分区表灌进界面：表格、下拉框、槽位统计一次更新。</summary>
    private void LoadPartitionTable(FastbootService.FastbootPartitionTable table)
    {
        _partitions.Clear();
        foreach (var partition in table.Partitions)
            _partitions.Add(partition);

        // 下拉框照旧给「实际分区名 + 常见分区兜底」，方便直接选或手输。
        var names = table.Partitions.Select(p => p.Name).ToList();
        if (names.Count == 0) names.AddRange(FastbootService.CommonPartitions);

        PartitionCombo.ItemsSource = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool has = table.Partitions.Count > 0;

        // 产品名和分区表同源（都来自 getvar all），读到就直接显示，省一次 fastboot 调用。
        if (!string.IsNullOrWhiteSpace(table.Product))
            ModelText.Text = table.Product;

        SlotSchemeText.Text = !has
            ? "-"
            : table.IsAbDevice
                ? $"A/B 双槽位（slot-count = {table.SlotCount}）"
                : "单槽位（无 A/B 分区）";

        SlotStatsText.Text = !has
            ? "-"
            : $"双槽位分区 {table.SlottedGroupCount} 组 · 公共分区 {table.CommonCount} 个 · 共 {table.Partitions.Count} 条记录";

        RefreshSlotFilter();

        ExportPartitionButton.IsEnabled = has;
        CopyPartitionButton.IsEnabled   = has;

        if (!has)
        {
            Log("未能读到分区。请确认设备处于 fastboot / fastbootd 模式后重试。");
            return;
        }

        Log($"已读取 {table.Partitions.Count} 条分区记录：双槽位 {table.SlottedGroupCount} 组，公共分区 {table.CommonCount} 个。");
        if (!table.IsAbDevice)
            Log("提示：该设备没有 A/B 槽位，分区名不带 _a / _b 后缀。");
        else if (table.CurrentSlot is null)
            Log("提示：设备是 A/B 双槽位，但没报出当前槽位。");
    }

    /// <summary>设备不在 fastboot 模式时把分区表清空，避免留着上一次的旧数据。</summary>
    private void ResetPartitionTable()
    {
        _partitions.Clear();
        ModelText.Text      = "-";
        SlotText.Text       = "-";
        SlotSchemeText.Text = "-";
        SlotStatsText.Text  = "-";

        RefreshSlotFilter();

        ExportPartitionButton.IsEnabled = false;
        CopyPartitionButton.IsEnabled   = false;
    }

    /// <summary>「只看 A/B 双槽位分区」开关：直接过滤 DataGrid 的默认视图。</summary>
    private void SlotFilter_Changed(object sender, RoutedEventArgs e) => RefreshSlotFilter();

    private void RefreshSlotFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_partitions);
        view.Filter = SlotFilterCheck.IsChecked == true
            ? o => o is FastbootPartition p && p.IsSlotted
            : null;
        view.Refresh();

        UpdatePartitionSummary();
    }

    /// <summary>刷新表格上方的统计文案（带筛选状态）。</summary>
    private void UpdatePartitionSummary()
    {
        if (_partitions.Count == 0)
        {
            PartitionCountText.Text = "尚未读取分区。点击「读取分区」获取设备上的实际分区（A/B 双槽位会分别列出）。";
            return;
        }

        if (SlotFilterCheck.IsChecked == true)
        {
            int shown = CollectionViewSource.GetDefaultView(_partitions).Cast<object>().Count();
            PartitionCountText.Text = $"已筛选：仅 A/B 双槽位分区，命中 {shown} / {_partitions.Count} 条记录。";
            return;
        }

        PartitionCountText.Text = $"共 {_partitions.Count} 条分区记录。";
    }

    /// <summary>选中行时把分区名填进「目标分区」，省得再去下拉框里找。</summary>
    private void PartitionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = PartitionGrid.SelectedItems.OfType<FastbootPartition>().ToList();
        if (selected.Count == 0) return;

        PartitionCombo.Text = selected[0].Name;

        var preview = string.Join(", ", selected.Take(6).Select(p => p.Name));
        Log(selected.Count <= 6
            ? $"已选中 {selected.Count} 个分区：{preview}"
            : $"已选中 {selected.Count} 个分区：{preview} …");
    }

    private void ExportPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title    = "导出分区表",
            FileName = "fastboot_partition_table.csv",
            Filter   = "CSV 文件 (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,BaseName,Slot,SlotInfo,IsCurrentSlot,Size,SizeHuman,Type");
            foreach (var p in _partitions)
                sb.AppendLine($"{p.Name},{p.BaseName},{p.Slot},{p.SlotInfo},{p.IsActive},{p.Size},{p.SizeHuman},{p.Type}");

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            Log($"已导出 {_partitions.Count} 条分区记录到：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log($"导出失败：{ex.Message}");
        }
    }

    private void CopyPartition_Click(object sender, RoutedEventArgs e)
    {
        var selected = PartitionGrid.SelectedItems.OfType<FastbootPartition>().ToList();
        var names = (selected.Count > 0 ? selected : _partitions.ToList()).Select(p => p.Name).ToList();
        if (names.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
            Log($"已复制 {names.Count} 个分区名到剪贴板。");
        }
        catch (Exception ex)
        {
            Log($"复制失败：{ex.Message}");
        }
    }

    private void PromptEnterFastboot()
    {
        MessageBox.Show(
            "未检测到 Fastboot 设备。\n\n进入 Fastboot 模式的方法：\n1. 关机后同时按住「音量下键 + 电源键」\n2. 或在已连接 adb 时，到首页点击「进入 Fastboot」\n\n进入后点击「刷新检测」。",
            "请进入 Fastboot 模式", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择要刷入的镜像文件",
            Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            ImagePathBox.Text = dialog.FileName;
            Log($"已选择镜像: {dialog.FileName}");
        }
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        var partition = PartitionCombo.Text?.Trim();
        var imagePath = ImagePathBox.Text?.Trim();

        if (string.IsNullOrEmpty(partition))
        {
            MessageBox.Show("请选择或输入目标分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            MessageBox.Show("请先选择要刷入的镜像文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FastbootService.IsUserspace())
        {
            var r = MessageBox.Show(
                "当前设备处于 FastbootD（用户空间）模式，静态分区（logo 等）不可见，刷入会报「partition does not exist」。\n\n是否先回到 bootloader 模式？",
                "检测到 FastbootD 模式", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                FastbootService.RebootToBootloader(Log);
                return;
            }
        }

        var confirm = MessageBox.Show(
            $"确定刷入 {partition} 分区吗？\n镜像: {Path.GetFileName(imagePath)}",
            "确认刷入", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        FlashButton.IsEnabled = false;
        try
        {
            Log($"开始刷入 {partition} 分区…");
            bool flashOk = await FastbootService.FlashPartitionAsync(partition, imagePath, Log);

            if (flashOk)
            {
                Log("刷入成功！");
                MessageBox.Show("刷入完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log("刷入失败。");
                MessageBox.Show(
                    "刷入失败，请查看日志。\n\n常见原因：\n1. 分区名不对（A/B 设备可能是 logo_a / logo_b）\n2. 设备处于 FastbootD 模式，需先回到 bootloader",
                    "刷入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            FlashButton.IsEnabled = true;
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Reset("等待操作…");
    }

    private void UpdateState(string text, string kind)
    {
        StateText.Text   = text;
        StateDot.Background = new SolidColorBrush(kind == "fastboot"
            ? Color.FromRgb(0xFF, 0xB8, 0x5C)   // 橙色
            : Color.FromRgb(0x6B, 0x72, 0x80));  // 灰色
    }

    private void Log(string message)
    {
        if (Dispatcher.CheckAccess())
            AppendLog(message);
        else
            Dispatcher.BeginInvoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        // 等级由 LogView 按行文本推断（[错误] / 刷入失败。/ > 阶段标题 …），这里不需要操心
        LogBox.Append(message);
    }
}
