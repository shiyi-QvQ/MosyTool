using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mosytool.Models;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class PayloadPage : Page
{
    private List<PayloadPartition> _partitions  = new();
    private string                _payloadPath = string.Empty;

    public PayloadPage()
    {
        InitializeComponent();
        LogBox.Text = "等待操作…\n";
    }

    private void BrowsePayload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 payload 文件或压缩包",
            Filter = "payload 及压缩包 (*.bin;*.zip)|*.bin;*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            _payloadPath     = dialog.FileName;
            PayloadPathBox.Text = dialog.FileName;
            Log($"已选择 payload: {dialog.FileName}");
        }
    }

    private void ParseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath))
        {
            MessageBox.Show("请先选择有效的 payload.bin 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Log("正在解析文件…");
            var actualPayload = PayloadService.ResolvePayload(_payloadPath);
            if (!string.Equals(actualPayload, _payloadPath, StringComparison.OrdinalIgnoreCase))
            {
                Log("检测到压缩包，已自动解压提取 payload.bin。");
                _payloadPath = actualPayload;
            }

            _partitions = PayloadService.Parse(_payloadPath);
            PartitionList.ItemsSource = _partitions;
            PartitionSummary.Text = $"共 {_partitions.Count} 个分区";

            int extractable = _partitions.Count(p => p.IsExtractable);
            Log($"解析完成：共 {_partitions.Count} 个分区，其中 {extractable} 个可直接解包。");
            foreach (var p in _partitions)
                Log($"  {p.Display}");

            ExtractAllButton.IsEnabled      = _partitions.Count > 0;
            ExtractSelectedButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log($"解析失败: {ex.Message}");
            MessageBox.Show("解析失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "选择解包输出目录",
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirBox.Text = dialog.SelectedPath;
    }

    private async void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        await ExtractAsync(_partitions);
    }

    private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _partitions.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先勾选要解包的分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ExtractAsync(selected);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _partitions) p.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _partitions) p.IsSelected = false;
    }

    private async Task ExtractAsync(List<PayloadPartition> partitions)
    {
        if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath))
        {
            MessageBox.Show("请先选择并解析 payload.bin。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputDir = OutputDirBox.Text?.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            MessageBox.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(outputDir);

        var todo    = partitions.Where(p => p.IsExtractable).ToList();
        int skipped = partitions.Count - todo.Count;

        if (todo.Count == 0)
        {
            MessageBox.Show("所选分区都不支持直接解包（需要增量源镜像）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractAllButton.IsEnabled      = false;
        ExtractSelectedButton.IsEnabled = false;
        ProgressBar.Value = 0;

        try
        {
            int success = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                var p = todo[i];
                var outputPath = Path.Combine(outputDir, $"{p.Name}.img");
                Log($"正在解包 [{i + 1}/{todo.Count}] {p.Name} → {outputPath}");

                double lastProgress = -1;
                try
                {
                    await Task.Run(() => PayloadService.ExtractPartition(_payloadPath, p, outputPath, progress =>
                    {
                        if (progress - lastProgress < 0.01 && progress < 1.0) return;
                        lastProgress = progress;
                        double val = progress;
                        Dispatcher.BeginInvoke(() =>
                        {
                            ProgressBar.Value = val * 100;
                            ProgressText.Text = $"解包 {p.Name}: {val * 100:0}%";
                        });
                    }));
                    success++;
                    Log($"  {p.Name} 解包完成。");
                }
                catch (Exception ex)
                {
                    Log($"  {p.Name} 解包失败: {ex.Message}");
                }
            }

            Log($"解包结束：成功 {success}/{todo.Count}" + (skipped > 0 ? $"，跳过 {skipped} 个不支持的分区。" : "。"));
            ProgressBar.Value = 100;
            ProgressText.Text = "解包完成";

            MessageBox.Show($"解包完成：成功 {success}/{todo.Count} 个分区。", "完成",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            ExtractAllButton.IsEnabled      = true;
            ExtractSelectedButton.IsEnabled = true;
        }
    }

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
