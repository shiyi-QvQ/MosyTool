using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mosytool.Models.Api;
using Mosytool.Services.Api;

namespace Mosytool.Views;

/// <summary>列表行：把 RomPackage 拍平成适合直接绑定的展示字段。</summary>
public sealed class RomRow
{
    public required RomPackage Source { get; init; }

    public string Title =>
        !string.IsNullOrWhiteSpace(Source.DeviceName) ? Source.DeviceName!
        : !string.IsNullOrWhiteSpace(Source.ModelCode) ? Source.ModelCode!
        : "未知机型";

    public string Tags
    {
        get
        {
            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(Source.Region)) tags.Add(Source.Region!);
            tags.Add(RomService.RomTypeText(Source.RomType));
            if (!string.IsNullOrWhiteSpace(Source.AndroidVersion)) tags.Add("Android " + Source.AndroidVersion);
            return string.Join(" · ", tags);
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Source.VersionName)) parts.Add("版本 " + Source.VersionName);
            if (!string.IsNullOrWhiteSpace(Source.OtaVersion)) parts.Add("OTA " + Source.OtaVersion);
            if (!string.IsNullOrWhiteSpace(Source.ColorOsVersion)) parts.Add("ColorOS " + Source.ColorOsVersion);
            if (!string.IsNullOrWhiteSpace(Source.SecurityPatch)) parts.Add("补丁 " + Source.SecurityPatch);
            if (!string.IsNullOrWhiteSpace(Source.ModelCode)) parts.Add("机型码 " + Source.ModelCode);
            return parts.Count > 0 ? string.Join("   ", parts) : "未提供版本信息";
        }
    }

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Source.FileName)) parts.Add(Source.FileName!);
            parts.Add(UpdateService.FormatSize(Source.FileSize));

            if (AnnouncementService.ParseTime(Source.PublishedAt) is { } published)
                parts.Add("发布于 " + published.ToLocalTime().ToString("yyyy-MM-dd"));

            return string.Join("   ·   ", parts);
        }
    }

    public bool CanDownload => RomService.PickDownloadUrl(Source) is not null;

    public bool HasPage => !string.IsNullOrWhiteSpace(Source.DownloadUrl);
}

public partial class RomPage : Page
{
    private const int PageSize = 20;

    private readonly ObservableCollection<RomRow> _rows = new();

    private int  _page       = 1;
    private int  _totalPages = 1;
    private bool _brandsLoaded;
    private bool _isLoading;
    private CancellationTokenSource? _downloadCts;

    private sealed record OptionItem(string? Value, string Text)
    {
        // 双保险：XAML 里已设 DisplayMemberPath，但 ToolTip / 无障碍朗读等位置
        // 仍可能取 ToString()，默认的 record 输出会把字段名一起带出来。
        public override string ToString() => Text;
    }

    public RomPage()
    {
        InitializeComponent();
        RomList.ItemsSource = _rows;

        TypeCombo.ItemsSource = new List<OptionItem>
        {
            new(null,    "全部类型"),
            new("full",  "完整包"),
            new("ota",   "增量包"),
        };
        TypeCombo.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            if (_brandsLoaded) return;
            _brandsLoaded = true;
            await LoadBrandsAsync();
            await SearchAsync(1);
        };

        Unloaded += (_, _) => _downloadCts?.Cancel();
    }

    // ---------- 筛选数据 ----------

    private async Task LoadBrandsAsync()
    {
        try
        {
            var brands = await RomService.BrandsAsync();
            var items = new List<OptionItem> { new(null, "全部品牌") };
            items.AddRange(brands
                .Where(b => !string.IsNullOrWhiteSpace(b.Brand))
                .Select(b => new OptionItem(b.Brand, $"{b.Brand}（{b.DeviceCount} 机型 / {b.Count} 个包）")));

            BrandCombo.ItemsSource = items;
            BrandCombo.SelectedIndex = 0;
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task LoadDevicesAsync(string? brand)
    {
        try
        {
            var devices = await RomService.DevicesAsync(brand);

            var items = new List<OptionItem> { new(null, "全部机型") };
            items.AddRange(devices
                .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
                .Select(d => new OptionItem(d.DeviceName, $"{d.DeviceName}（{d.Count}）")));

            DeviceCombo.ItemsSource = items;
            DeviceCombo.SelectedIndex = 0;
        }
        catch (ApiException)
        {
            // 机型列表拉不到不影响按关键字搜索，静默降级。
            DeviceCombo.ItemsSource = new List<OptionItem> { new(null, "全部机型") };
            DeviceCombo.SelectedIndex = 0;
        }
    }

    private async void BrandCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await LoadDevicesAsync((BrandCombo.SelectedItem as OptionItem)?.Value);

    // ---------- 查询 ----------

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(1);

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        BrandCombo.SelectedIndex  = 0;
        DeviceCombo.SelectedIndex = 0;
        TypeCombo.SelectedIndex   = 0;
        KeywordBox.Text           = string.Empty;
        await SearchAsync(1);
    }

    private async void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync(1);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1) await SearchAsync(_page - 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < _totalPages) await SearchAsync(_page + 1);
    }

    private async Task SearchAsync(int page)
    {
        if (_isLoading) return;
        _isLoading = true;
        SetPageButtons(false);

        StatusText.Text = "正在查询…";

        try
        {
            var result = await RomService.ListAsync(
                device:  (DeviceCombo.SelectedItem as OptionItem)?.Value,
                brand:   (BrandCombo.SelectedItem as OptionItem)?.Value,
                romType: (TypeCombo.SelectedItem as OptionItem)?.Value,
                keyword: RomService.Normalize(KeywordBox.Text),
                page:    page,
                pageSize: PageSize);

            _rows.Clear();
            foreach (var rom in result.List)
                _rows.Add(new RomRow { Source = rom });

            _page       = Math.Max(1, result.Page);
            _totalPages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)Math.Max(1, result.PageSize)));

            PageText.Text  = $"{_page} / {_totalPages}";
            StatusText.Text = result.Total == 0
                ? "没有匹配的 ROM，换个关键字或放宽筛选条件试试。"
                : $"共 {result.Total} 个结果，当前第 {_page} 页（每页 {result.PageSize} 条）。";
        }
        catch (ApiException ex)
        {
            _rows.Clear();
            StatusText.Text = ex.Message;
        }
        finally
        {
            _isLoading = false;
            SetPageButtons(true);
        }
    }

    private void SetPageButtons(bool enabled)
    {
        PrevButton.IsEnabled = enabled && _page > 1;
        NextButton.IsEnabled = enabled && _page < _totalPages;
    }

    // ---------- 下载 ----------

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RomRow row }) return;

        var url = RomService.PickDownloadUrl(row.Source);
        if (url is null) return;

        if (_downloadCts is not null)
        {
            MessageBox.Show(Window.GetWindow(this), "已有下载在进行中，请先等待或取消。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "选择保存位置",
            FileName         = row.Source.FileName ?? $"{row.Title}-{row.Source.VersionName}.zip",
            Filter           = "所有文件|*.*",
            OverwritePrompt  = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _downloadCts = new CancellationTokenSource();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        StatusText.Text = "正在下载…";

        try
        {
            await ApiClient.DownloadToFileAsync(
                url,
                dialog.FileName,
                needUserIdHeader: RomService.NeedsUserIdHeader(row.Source, url),
                sha256: null,
                onProgress: (received, total) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (total > 0)
                    {
                        DownloadProgress.IsIndeterminate = false;
                        DownloadProgress.Value = received * 100.0 / total;
                        StatusText.Text = $"正在下载… {received * 100.0 / total:0.0}%" +
                                          $"（{UpdateService.FormatSize(received)} / {UpdateService.FormatSize(total)}）";
                    }
                    else
                    {
                        DownloadProgress.IsIndeterminate = true;
                        StatusText.Text = $"正在下载… 已接收 {UpdateService.FormatSize(received)}";
                    }
                })),
                ct: _downloadCts.Token);

            StatusText.Text = $"下载完成：{dialog.FileName}";
            OpenInExplorer(dialog.FileName);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载。";
            TryDelete(dialog.FileName);
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;

            // 直链过期时给一条官方页兜底路径。
            if (row.Source.DownloadUrl is { Length: > 0 } page && ex.Message.Contains("下载失败"))
                if (MessageBox.Show(Window.GetWindow(this),
                        $"{ex.Message}\n\n是否改用浏览器打开官方下载页？", "下载失败",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    OpenUrl(page);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RomRow row } &&
            row.Source.DownloadUrl is { Length: > 0 } url)
            OpenUrl(url);
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法打开链接：{ex.Message}";
        }
    }

    private void OpenInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响下载结果。
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
