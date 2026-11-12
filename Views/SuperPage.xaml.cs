using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class SuperPage : Page
{
    private readonly ObservableCollection<LogicalPartition> _buildItems = new();
    private SuperService.SuperInfo? _superInfo;
    private string _superPath = string.Empty;
    private string _vbmetaPath = string.Empty;

    public SuperPage()
    {
        InitializeComponent();
        BuildList.ItemsSource = _buildItems;
        LogBox.Text = "等待操作…\n";
        Loaded += async (_, _) => await RefreshDevicePartitionsAsync(silent: true);
    }

    // ================= super 合包 =================

    private void AddBuildImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title       = "选择要合包的分区镜像",
            Filter      = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true) return;

        int added = 0;
        foreach (var file in dialog.FileNames)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (_buildItems.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"已存在同名分区 {name}，跳过：{file}");
                continue;
            }

            var item = new LogicalPartition { Name = name, ImagePath = file };

            try { item.ImageSize = SparseImage.GetRawLength(file); }
            catch { item.ImageSize = new FileInfo(file).Length; }

            _buildItems.Add(item);
            added++;
            Log($"已添加 {name}（{LogicalPartition.FormatSize(item.ImageSize)}）");
        }

        BuildHint.Text = _buildItems.Count == 0 ? "" : $"共 {_buildItems.Count} 个待打包分区";
        if (added == 0) Log("没有新增分区。");
    }

    private void RemoveBuildImage_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildList.SelectedItems.OfType<LogicalPartition>().ToList();
        foreach (var item in selected) _buildItems.Remove(item);
        BuildHint.Text = _buildItems.Count == 0 ? "" : $"共 {_buildItems.Count} 个待打包分区";
        if (selected.Count > 0) Log($"已移除 {selected.Count} 个待打包分区。");
    }

    private void ClearBuildImages_Click(object sender, RoutedEventArgs e)
    {
        _buildItems.Clear();
        BuildHint.Text = "";
        Log("已清空待打包列表。");
    }

    private void BrowseBuildOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title      = "选择 super.img 输出位置",
            FileName   = "super.img",
            Filter     = "super 镜像 (*.img)|*.img|所有文件 (*.*)|*.*",
            DefaultExt = ".img",
        };

        if (dialog.ShowDialog() == true) BuildOutputBox.Text = dialog.FileName;
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择作为模板的 super.img",
            Filter = "super 镜像 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true) TemplateBox.Text = dialog.FileName;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_buildItems.Count == 0)
        {
            MessageBox.Show("请先添加要合包的分区镜像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var output = BuildOutputBox.Text?.Trim();
        if (string.IsNullOrEmpty(output))
        {
            MessageBox.Show("请先指定输出文件（super.img）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long? deviceSize = null;
        var sizeText = DeviceSizeBox.Text?.Trim();
        if (!string.IsNullOrEmpty(sizeText))
        {
            if (!long.TryParse(sizeText, out var mb) || mb <= 0)
            {
                MessageBox.Show("总大小请填写正整数（单位 MB），或留空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            deviceSize = mb * 1024L * 1024L;
        }

        var request = new SuperService.BuildRequest { OutputPath = output, DeviceSize = deviceSize };
        foreach (var item in _buildItems) request.Partitions.Add(item);

        BuildButton.IsEnabled = false;
        ProgressBar.Value = 0;
        ProgressText.Text = "准备合包…";

        try
        {
            var templatePath = TemplateBox.Text?.Trim();
            if (!string.IsNullOrEmpty(templatePath))
            {
                Log($"读取模板 super：{templatePath}");
                request.Template = await Task.Run(() => SuperService.Load(templatePath, Log));
            }

            Log($"开始合包，共 {request.Partitions.Count} 个分区…");

            var result = await Task.Run(() => SuperService.Build(request, Log, p =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBar.Value = p * 100;
                    ProgressText.Text = $"合包中 {p * 100:0}%";
                });
            }));

            ProgressBar.Value = 100;
            ProgressText.Text = "合包完成";

            Log($"合包完成：{result.PartitionCount} 个分区，super 大小 {SuperService.FormatSize(result.DeviceSize)}，" +
                $"数据区 {SuperService.FormatSize(result.DataSize)}{(result.UsedTemplate ? "，已套用模板" : "")}");
            MessageBox.Show($"合包完成：{result.PartitionCount} 个分区，输出大小 {SuperService.FormatSize(result.DeviceSize)}。",
                            "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"合包失败：{ex.Message}");
            MessageBox.Show("合包失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BuildButton.IsEnabled = true;
        }
    }

    // ================= super 解析 / 提取 =================

    private void BrowseSuper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 super.img",
            Filter = "super 镜像 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            SuperPathBox.Text = dialog.FileName;
            Log($"已选择 super：{dialog.FileName}");
        }
    }

    private async void ParseSuper_Click(object sender, RoutedEventArgs e)
    {
        var path = SuperPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("请先选择有效的 super.img 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Log("正在解析 super…");
            _superInfo = await Task.Run(() => SuperService.Load(path, Log));
            _superPath = path;

            PartitionList.ItemsSource = _superInfo.Partitions;

            string ab = _superInfo.IsVirtualAb ? " · Virtual A/B" : "";
            SuperSummary.Text =
                $"共 {_superInfo.Partitions.Count} 个逻辑分区，总大小 {SuperService.FormatSize(_superInfo.PartitionTotalSize)}；" +
                $"组 {_superInfo.Groups.Count} 个，读取自{_superInfo.CopyUsed} metadata slot{_superInfo.SlotUsed}；" +
                $"super 容量 {SuperService.FormatSize(_superInfo.DeviceSize)}{ab}";

            Log($"解析完成：{_superInfo.Partitions.Count} 个逻辑分区。");
            foreach (var p in _superInfo.Partitions)
                Log($"  {p.Display}");

            SourcePartitionCombo.ItemsSource = _superInfo.Partitions.Select(p => p.Name).ToList();
            if (_superInfo.Partitions.Count > 0) SourcePartitionCombo.SelectedIndex = 0;

            ExtractButton.IsEnabled = _superInfo.Partitions.Count > 0;
        }
        catch (Exception ex)
        {
            Log($"解析失败：{ex.Message}");
            MessageBox.Show("解析失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectAllParts_Click(object sender, RoutedEventArgs e)
    {
        if (_superInfo == null) return;
        foreach (var p in _superInfo.Partitions) p.IsSelected = true;
    }

    private void SelectNoneParts_Click(object sender, RoutedEventArgs e)
    {
        if (_superInfo == null) return;
        foreach (var p in _superInfo.Partitions) p.IsSelected = false;
    }

    private void BrowseExtractDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "选择提取输出目录",
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ExtractDirBox.Text = dialog.SelectedPath;
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_superInfo == null || _superPath.Length == 0)
        {
            MessageBox.Show("请先解析 super.img。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputDir = ExtractDirBox.Text?.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            MessageBox.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var todo = _superInfo.Partitions.Where(p => p.IsSelected).ToList();
        if (todo.Count == 0)
        {
            MessageBox.Show("请先勾选要提取的分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outputDir);

        ExtractButton.IsEnabled = false;
        ProgressBar.Value = 0;

        try
        {
            int success = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                var part = todo[i];
                var dest = Path.Combine(outputDir, $"{part.Name}.img");
                Log($"正在提取 [{i + 1}/{todo.Count}] {part.Name} → {dest}");

                try
                {
                    await Task.Run(() => SuperService.ExtractPartition(_superPath, part, dest, p =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            ProgressBar.Value = p * 100;
                            ProgressText.Text = $"提取 {part.Name}: {p * 100:0}%";
                        });
                    }));
                    success++;
                    Log($"  {part.Name} 提取完成。");
                }
                catch (Exception ex)
                {
                    Log($"  {part.Name} 提取失败：{ex.Message}");
                }
            }

            ProgressBar.Value = 100;
            ProgressText.Text = "提取完成";
            Log($"提取结束：成功 {success}/{todo.Count}。");
            MessageBox.Show($"提取完成：成功 {success}/{todo.Count} 个分区。", "完成",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
        }
    }

    // ================= super 分区刷入指定分区 =================

    private async void RefreshDevicePartitions_Click(object sender, RoutedEventArgs e) =>
        await RefreshDevicePartitionsAsync(silent: false);

    private async Task RefreshDevicePartitionsAsync(bool silent)
    {
        List<string> names;
        try
        {
            names = await Task.Run(FastbootService.GetPartitions);
        }
        catch (Exception ex)
        {
            if (!silent) Log($"读取设备分区失败：{ex.Message}（可直接手输分区名）。");
            names = FastbootService.CommonPartitions.ToList();
        }

        TargetPartitionCombo.ItemsSource = names;
        VbmetaPartitionCombo.ItemsSource = names;

        if (!silent) Log($"已刷新设备分区：{names.Count} 个候选分区。");
    }

    private async void FlashPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_superInfo == null || _superPath.Length == 0)
        {
            MessageBox.Show("请先解析 super.img。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sourceName = SourcePartitionCombo.Text?.Trim();
        var targetName = TargetPartitionCombo.Text?.Trim();

        if (string.IsNullOrEmpty(sourceName))
        {
            MessageBox.Show("请选择 super 内的分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(targetName))
        {
            MessageBox.Show("请选择或输入目标分区名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var part = _superInfo.Find(sourceName);
        if (part == null)
        {
            MessageBox.Show($"super 中没有名为 {sourceName} 的分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"将从 super 提取分区 {sourceName}（{SuperService.FormatSize(part.Size)}）\n并刷入设备分区 {targetName}。\n\n确定继续吗？",
            "确认刷入", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        FlashPartButton.IsEnabled = false;
        ProgressBar.Value = 0;

        try
        {
            // 1) 提取到临时文件
            var tempDir = Path.Combine(Path.GetTempPath(), "mosytool_super");
            Directory.CreateDirectory(tempDir);
            var tempImg = Path.Combine(tempDir, $"{sourceName}.img");

            Log($"提取 {sourceName} → {tempImg}");
            await Task.Run(() => SuperService.ExtractPartition(_superPath, part, tempImg, p =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBar.Value = p * 100;
                    ProgressText.Text = $"提取 {sourceName}: {p * 100:0}%";
                });
            }));

            // 2) 刷入
            ProgressText.Text = $"正在刷入 {targetName}…";
            Log($"开始刷入 {targetName}…");
            bool ok = await FastbootService.FlashPartitionAsync(targetName, tempImg, Log);

            if (ok)
            {
                ProgressBar.Value = 100;
                ProgressText.Text = "刷入完成";
                Log("刷入成功！");
                MessageBox.Show("刷入完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log("刷入失败。");
                MessageBox.Show(
                    "刷入失败，请查看日志。\n\n常见原因：\n1. 目标分区名不对（A/B 设备可能是 system_a）\n2. 设备不在 fastboot 模式\n3. 分区大小不足（super 内分区比设备分区大）",
                    "刷入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log($"操作失败：{ex.Message}");
            MessageBox.Show("操作失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FlashPartButton.IsEnabled = true;
        }
    }

    // ================= 关闭 AVB 校验 =================

    private void BrowseVbmeta_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 vbmeta 镜像",
            Filter = "vbmeta 镜像 (*.img)|*.img|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        _vbmetaPath = dialog.FileName;
        VbmetaPathBox.Text = dialog.FileName;
        ShowVbmetaInfo();
    }

    private void GenerateVbmeta_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title      = "生成空白 vbmeta",
            FileName   = "vbmeta.img",
            Filter     = "vbmeta 镜像 (*.img)|*.img|所有文件 (*.*)|*.*",
            DefaultExt = ".img",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            AvbService.CreateDisabledVbmeta(dialog.FileName);
            _vbmetaPath = dialog.FileName;
            VbmetaPathBox.Text = dialog.FileName;
            ShowVbmetaInfo();
            Log($"已生成关闭校验的空白 vbmeta：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log($"生成失败：{ex.Message}");
            MessageBox.Show("生成失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowVbmetaInfo()
    {
        try
        {
            var info = AvbService.Load(_vbmetaPath);
            VbmetaInfoText.Text =
                $"版本 {info.MajorVersion}.{info.MinorVersion} · 算法 {info.AlgorithmText}\n" +
                $"flags = {info.FlagsText}\n" +
                $"签名块 {info.AuthBlockSize} B · 辅助块 {info.AuxBlockSize} B · rollback_index {info.RollbackIndex}" +
                (info.FullyDisabled ? "\n状态：校验已关闭（verity + verification）" : "\n状态：校验仍开启");
        }
        catch (Exception ex)
        {
            VbmetaInfoText.Text = $"无法读取 vbmeta：{ex.Message}";
        }
    }

    private void PatchVbmeta_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVbmetaSelected()) return;

        try
        {
            uint flags = AvbService.Patch(_vbmetaPath, true, true);
            ShowVbmetaInfo();
            Log($"已写入关闭校验位，新的 flags = {flags}。");
            MessageBox.Show("已关闭 vbmeta 的 verity 与 verification 校验。\n刷入该 vbmeta 后即可跳过 AVB 校验。",
                            "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"修改失败：{ex.Message}");
            MessageBox.Show("修改失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreVbmeta_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVbmetaSelected()) return;

        try
        {
            uint flags = AvbService.Restore(_vbmetaPath);
            ShowVbmetaInfo();
            Log($"已恢复校验位，新的 flags = {flags}。");
        }
        catch (Exception ex)
        {
            Log($"恢复失败：{ex.Message}");
            MessageBox.Show("恢复失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FlashVbmeta_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureVbmetaSelected()) return;

        var partition = VbmetaPartitionCombo.Text?.Trim();
        if (string.IsNullOrEmpty(partition)) partition = "vbmeta";

        var confirm = MessageBox.Show(
            $"将刷入 {partition} 分区：{Path.GetFileName(_vbmetaPath)}\n并附带 --disable-verity --disable-verification 参数。\n\n确定继续吗？",
            "确认刷入", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        FlashVbmetaButton.IsEnabled = false;
        try
        {
            bool ok = await AvbService.FlashDisabledVbmetaAsync(partition, _vbmetaPath, Log);
            if (ok)
            {
                Log("vbmeta 刷入成功，AVB 校验已关闭。");
                MessageBox.Show("刷入完成，AVB 校验已关闭。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log("vbmeta 刷入失败。");
                MessageBox.Show(
                    "刷入失败，请查看日志。\n\n可尝试：先用「关闭校验（改 flags）」改好 vbmeta，再用普通 fastboot flash 刷入。",
                    "刷入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            FlashVbmetaButton.IsEnabled = true;
        }
    }

    private bool EnsureVbmetaSelected()
    {
        if (_vbmetaPath.Length > 0 && File.Exists(_vbmetaPath)) return true;

        MessageBox.Show("请先选择或生成一个 vbmeta 镜像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    // ================= 日志 =================

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        LogBox.AppendText("等待操作…\n");
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
        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
