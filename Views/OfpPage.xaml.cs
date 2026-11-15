using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mosytool.Services;

namespace Mosytool.Views;

public partial class OfpPage : Page
{
    private OfpService.OfpImage? _image;
    private string _ofpPath = string.Empty;

    public OfpPage()
    {
        InitializeComponent();
        LogBox.Text = "等待操作…\n";
    }

    private void BrowseOfp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "选择 OFP 固件包",
            Filter = "OFP 固件 (*.ofp;*.ozip)|*.ofp;*.ozip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            _ofpPath = dialog.FileName;
            OfpPathBox.Text = dialog.FileName;
            Log($"已选择 OFP：{dialog.FileName}");
        }
    }

    private async void ParseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_ofpPath) || !File.Exists(_ofpPath))
        {
            Dialogs.Show("请先选择有效的 OFP 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Log("正在识别并解析 OFP…");
            _image = await Task.Run(() => OfpService.Open(_ofpPath, Log));

            EntryList.ItemsSource = _image.Entries;
            OfpSummary.Text = _image.DetailText;
            EntrySummary.Text = $"共 {_image.Entries.Count} 个条目";
            if (_image.KeyHint.Length > 0) Log($"密钥：{_image.KeyHint}");

            Log($"解析完成：{_image.KindText}，共 {_image.Entries.Count} 个条目。");
            foreach (var entry in _image.Entries.Take(200))
                Log($"  {entry.Display}");

            ExtractAllButton.IsEnabled      = _image.Entries.Count > 0;
            ExtractSelectedButton.IsEnabled = _image.Entries.Count > 0;
            SaveXmlButton.IsEnabled         = _image.ProfileXml.Length > 0;
        }
        catch (Exception ex)
        {
            Log($"解析失败：{ex.Message}");
            Dialogs.Show("解析失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        foreach (var entry in _image.Entries) entry.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        foreach (var entry in _image.Entries) entry.IsSelected = false;
    }

    private void SaveXml_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null || _image.ProfileXml.Length == 0) return;

        var dialog = new SaveFileDialog
        {
            Title    = "导出 ProFile.xml",
            FileName = "ProFile.xml",
            Filter   = "XML 文件 (*.xml)|*.xml",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            OfpService.SaveProfileXml(_image, dialog.FileName);
            Log($"已导出 ProFile.xml：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log($"导出失败：{ex.Message}");
        }
    }

    private async void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        await ExtractAsync(_image.Entries.ToList());
    }

    private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;

        var selected = _image.Entries.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Dialogs.Show("请先勾选要解包的条目。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ExtractAsync(selected);
    }

    private async Task ExtractAsync(List<Models.OfpEntry> entries)
    {
        if (_image == null)
        {
            Dialogs.Show("请先解析 OFP 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputDir = OutputDirBox.Text?.Trim();
        if (string.IsNullOrEmpty(outputDir))
        {
            Dialogs.Show("请先选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractAllButton.IsEnabled      = false;
        ExtractSelectedButton.IsEnabled = false;
        ProgressBar.Value = 0;

        try
        {
            Log($"开始解包 {entries.Count} 个条目 → {outputDir}");
            await Task.Run(() => OfpService.Extract(_image, entries, outputDir, Log, p =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBar.Value = p * 100;
                    ProgressText.Text = $"解包中 {p * 100:0}%";
                });
            }));

            ProgressBar.Value = 100;
            ProgressText.Text = "解包完成";
            Dialogs.Show($"解包完成，共 {entries.Count} 个条目。", "完成",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"解包失败：{ex.Message}");
            Dialogs.Show("解包失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
