using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Mosytool.Controls;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Views;

/// <summary>更新提示与下载窗口。</summary>
public partial class UpdateWindow : MtWindow
{
    private readonly AppRelease _release;
    private readonly bool _forced;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateCheck check)
    {
        _release = check.Latest!;
        _forced  = UpdateService.IsForced(check);

        InitializeComponent();

        VersionText.Text = $"当前版本 v{AppInfo.DisplayVersion}  →  最新版本 v{_release.VersionName}";

        var parts = new System.Collections.Generic.List<string> { UpdateService.FormatSize(_release.FileSize) };
        if (AnnouncementService.ParseTime(_release.PublishedAt) is { } published)
            parts.Add("发布于 " + published.ToLocalTime().ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(_release.Sha256)) parts.Add("已提供校验值");
        MetaText.Text = string.Join("  ·  ", parts);

        ChangelogViewer.Document = MarkdownRenderer.ToFlowDocument(
            string.IsNullOrWhiteSpace(_release.Changelog) ? "本次更新未提供说明。" : _release.Changelog!.Trim());

        if (_forced)
        {
            // 强制更新：不给「跳过 / 稍后」，只能现在装或直接关窗口。
            ForceBadge.Visibility = Visibility.Visible;
            SkipButton.IsEnabled  = false;
            LaterButton.Content   = "退出程序";
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.SaveUpdateState(DateTime.UtcNow, _release.VersionName);
        DialogResult = false;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        // 强制更新时「稍后」= 直接退出，不留下可继续使用的旧版本。
        DialogResult = false;
        if (_forced)
        {
            SettingsService.SaveUpdateState(DateTime.UtcNow, SettingsService.SkippedVersion);
            App.ExitImmediately();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true);

        var candidates = UpdateService.DownloadCandidates(_release).ToList();
        if (candidates.Count == 0)
        {
            Fail("该版本没有提供下载地址。");
            return;
        }

        var target = Path.Combine(Path.GetTempPath(), "MosyTool-update",
                                  FileNameFrom(_release, candidates[0]));

        try
        {
            // 每个地址先原地重试一次（网络抖动导致断流很常见），
            // 仍失败再换镜像，全部失败才报错。
            ApiException? lastError = null;

            foreach (var url in candidates)
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        StatusText.Text = attempt == 1 ? "正在下载…" : "下载中断，正在重试…";
                        await ApiClient.DownloadToFileAsync(url, target, false, _release.Sha256,
                            (received, total) => ReportProgress(received, total), cts.Token)
                            .ConfigureAwait(true);

                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (ApiException ex)
                    {
                        lastError = ex;

                        // SHA256 对不上说明这个地址给的文件本身不对，重试没意义，直接换下一个。
                        if (ex.Code == -3) break;
                    }
                }

                if (lastError is null) break;
            }

            if (lastError is not null) throw lastError;

            Progress.Value = 100;
            StatusText.Text = "下载完成，正在启动安装…";
            OnDownloaded(target);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载。";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
    }

    private void OnDownloaded(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

            Dialogs.Show(this,
                "安装程序已启动。\n\n请按提示完成安装，安装完成后重新打开本工具即可。",
                "下载完成", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            App.ExitImmediately();
        }
        catch (Exception)
        {
            // 不是可执行文件（例如压缩包）时退化为打开所在目录，交给用户手动处理。
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                Dialogs.Show(this,
                    $"已下载到：\n{path}\n\n请手动解压或运行，完成后重新打开本工具。",
                    "下载完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex2)
            {
                Fail($"已下载到 {path}，但无法自动打开：{ex2.Message}");
            }
        }
    }

    private void ReportProgress(long received, long total)
    {
        // 进度回调来自下载线程，直接改控件会抛
        // 「调用线程无法访问此对象，因为另一个线程拥有该对象」。统一切回 UI 线程。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ReportProgress(received, total)));
            return;
        }

        if (total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = received * 100.0 / total;
            StatusText.Text = $"正在下载… {received * 100.0 / total:0.0}%  " +
                              $"({UpdateService.FormatSize(received)} / {UpdateService.FormatSize(total)})";
        }
        else
        {
            Progress.IsIndeterminate = true;
            StatusText.Text = $"正在下载… {UpdateService.FormatSize(received)}";
        }
    }

    /// <summary>下载途中直接关窗口时，把下载也一起取消，别留个后台还在传文件。</summary>
    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        base.OnClosed(e);
    }

    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy;
        SkipButton.IsEnabled   = !busy && !_forced;
        Progress.Visibility    = Visibility.Visible;
        StatusText.Visibility  = Visibility.Visible;
        if (busy) StatusText.Text = "正在准备下载…";
    }

    private void Fail(string message)
    {
        // 先解除忙碌再隐藏进度条：SetBusy 会把进度条显示出来，
        // 顺序反了失败后还会留一条停在 100% 的进度条，看着像下载成功了。
        SetBusy(false);
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Text     = message;

        Dialogs.Show(this, message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 从 download_url 猜文件名；没有可用名字时按版本号生成。
    /// 必须把 Windows 不允许的字符换掉：带 ? / : / * 的地址直接拼成路径，
    /// 创建文件时就会抛异常，表现成「一点下载就失败」。
    /// </summary>
    private static string FileNameFrom(AppRelease release, string url)
    {
        string? name = null;
        try { name = Path.GetFileName(new Uri(url).AbsolutePath); }
        catch (UriFormatException) { }

        if (string.IsNullOrWhiteSpace(name)) name = url.Split('?', '#')[0];
        name = Path.GetFileName(name ?? string.Empty);

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var clean = sb.ToString().Trim().TrimEnd('.');
        if (clean.Length == 0) clean = $"MosyTool-{release.VersionName ?? "update"}";
        if (!clean.Contains('.')) clean += ".exe";

        return clean;
    }
}
