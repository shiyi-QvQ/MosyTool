using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Mosytool.Controls;
using Mosytool.Services;
using Wpf.Ui.Controls;

namespace Mosytool.Views;

/// <summary>
/// 选择手机上的目录：直接读设备的目录结构，让用户点进去挑，而不是手打 /sdcard/... 这种路径。
///
/// 只列文件夹——推送目标本来就只能是目录；文件只在状态行里报个数，免得列表被塞满。
/// </summary>
public partial class RemoteDirWindow : MtWindow
{
    /// <summary>常用目录快捷入口，和推送页面上的保持一致。</summary>
    private static readonly (string Label, string Path)[] Shortcuts =
    {
        ("内部存储", "/sdcard"),
        ("下载",     "/sdcard/Download"),
        ("相册",     "/sdcard/Pictures"),
        ("相机",     "/sdcard/DCIM"),
        ("音乐",     "/sdcard/Music"),
        ("文档",     "/sdcard/Documents"),
        ("临时目录", "/data/local/tmp"),
        ("根目录",   "/"),
    };

    private readonly string _serial;
    private readonly ObservableCollection<AdbFileService.RemoteEntry> _entries = new();

    private CancellationTokenSource? _listCts;
    private string _current = "/sdcard";

    /// <summary>用户确认的目录，始终以 / 结尾。</summary>
    public string SelectedPath { get; private set; } = "/sdcard/";

    public RemoteDirWindow(string serial, string? initialPath)
    {
        _serial = serial;

        InitializeComponent();

        FolderList.ItemsSource = _entries;

        foreach (var (label, path) in Shortcuts)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content    = label,
                Appearance = ControlAppearance.Secondary,
                Margin     = new Thickness(0, 0, 8, 8),
                Padding    = new Thickness(10, 4, 10, 4),
                FontSize   = 12,
                Tag        = path,
            };

            button.Click += (_, _) => _ = NavigateAsync((string)button.Tag);
            QuickPanel.Children.Add(button);
        }

        Loaded += async (_, _) => await NavigateAsync(initialPath);

        // 关窗时停掉还在跑的 ls，别让它回来往已经没了的列表里塞东西
        Closed += (_, _) => _listCts?.Cancel();
    }

    // ---------- 浏览 ----------

    private async Task NavigateAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(_serial)) return;

        var target = Normalize(path);

        // 连续点几个目录时，只认最后一次的结果
        _listCts?.Cancel();
        _listCts = new CancellationTokenSource();
        var ct = _listCts.Token;

        PathBox.Text            = target;
        StatusText.Text         = "正在读取…";
        TargetText.Text         = string.Empty;
        ConfirmButton.IsEnabled = false;
        _entries.Clear();

        var result = await AdbFileService.ListAsync(_serial, target, ct);

        if (ct.IsCancellationRequested) return;

        _current = target;
        ConfirmButton.IsEnabled = true;
        TargetText.Text         = $"推送目标：{WithSlash(target)}";

        if (!result.Ok)
        {
            // 读不到不代表不能写（典型是根目录下没权限的路径），所以照样允许选它
            StatusText.Text = $"读不到这个目录：{result.Error}\n可以直接点「用这个目录」，推送时会尝试创建并写入。";
            return;
        }

        var folders = 0;
        var files   = 0;

        // ls 本身就按名字排好了，这里直接照单添加即可，不用再排一次
        foreach (var entry in result.Entries)
        {
            if (!entry.IsDirectory)
            {
                files++;
                continue;
            }

            folders++;
            _entries.Add(entry);
        }

        StatusText.Text = folders > 0
            ? $"里面有 {folders} 个文件夹" + (files > 0 ? $"，另有 {files} 个文件未列出" : string.Empty)
            : files > 0
                ? $"这个目录下没有子文件夹（有 {files} 个文件），可以直接选它。"
                : "这个目录是空的，可以直接选它。";
    }

    private async Task OpenSelectedAsync()
    {
        if (FolderList.SelectedItem is not AdbFileService.RemoteEntry entry) return;

        await NavigateAsync(WithSlash(_current) + entry.Name);
    }

    // ---------- 界面事件 ----------

    private async void Up_Click(object sender, RoutedEventArgs e) => await NavigateAsync(ParentOf(_current));

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateAsync(PathBox.Text);

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await NavigateAsync(PathBox.Text);
    }

    private async void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await OpenSelectedAsync();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        // 只取一层名字：带 / 的话很容易糊出一个谁也没注意到的多级路径
        var name = NewNameBox.Text.Trim().Replace("/", string.Empty);
        if (name.Length == 0)
        {
            StatusText.Text = "先在输入框里填一个文件夹名。";
            return;
        }

        var target = WithSlash(_current) + name;
        StatusText.Text = $"正在创建 {target} …";

        if (!await AdbFileService.MakeDirectoryAsync(_serial, target, CancellationToken.None))
        {
            StatusText.Text = $"创建失败：{target}（多半是权限不够，换 /sdcard 下面试试）";
            return;
        }

        NewNameBox.Text = string.Empty;
        await NavigateAsync(target);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = WithSlash(_current);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------- 路径处理 ----------

    /// <summary>规整成「以 / 开头、不以 / 结尾」的形式，方便拼接。</summary>
    private static string Normalize(string? path)
    {
        var text = path?.Trim().Replace('\\', '/') ?? string.Empty;
        if (!text.StartsWith("/", StringComparison.Ordinal)) text = "/" + text;

        var trimmed = text.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>补上结尾的 /；根目录保持单个 /。</summary>
    private static string WithSlash(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed + "/";
    }

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index   = trimmed.LastIndexOf('/');

        return index <= 0 ? "/" : trimmed[..index];
    }
}
