using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosytool.Controls;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Views;

/// <summary>
/// 独立公告页：左侧列表 + 右侧 Markdown 正文。
/// 列表项只展示「等级 / 标题 / 一行预览 / 时间」，正文一律在右侧渲染，避免列表里出现长段落。
/// </summary>
public partial class AnnouncementPage : Page
{
    private const int FetchLimit = 50;

    private readonly List<Row> _all = new();
    private readonly ObservableCollection<Row> _view = new();
    private CancellationTokenSource? _cts;
    private bool _loadedOnce;

    public AnnouncementPage()
    {
        InitializeComponent();

        NoticeList.ItemsSource = _view;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _cts?.Cancel();
    }

    // ===================================================================== 列表行

    /// <summary>
    /// 列表行。等级色、时间、预览都在构造时算好，DataTemplate 里只做绑定，避免在模板里塞逻辑。
    /// </summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _unread;

        public Row(Announcement source, bool unread)
        {
            Source = source;
            _unread = unread;

            Title = string.IsNullOrWhiteSpace(source.Title) ? "(无标题公告)" : source.Title!.Trim();

            var plain = MarkdownRenderer.ToPlainText(source.Content);
            Preview = plain.Length > 72 ? plain[..72] + "…" : plain;

            var time = AnnouncementService.ParseTime(source.PublishAt)
                       ?? AnnouncementService.ParseTime(source.CreatedAt);
            DateText = time?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;

            LevelText = AnnouncementService.LevelText(source.Level);
            LevelBrush = Frozen(AnnouncementService.LevelColor(source.Level));
            PinnedVisibility = source.Pinned ? Visibility.Visible : Visibility.Collapsed;
        }

        public Announcement Source { get; }
        public string Title { get; }
        public string Preview { get; }
        public string DateText { get; }
        public string LevelText { get; }
        public Brush LevelBrush { get; }
        public Visibility PinnedVisibility { get; }

        public Visibility UnreadDotVisibility => _unread ? Visibility.Visible : Visibility.Collapsed;

        public void MarkRead()
        {
            if (!_unread) return;
            _unread = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadDotVisibility)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    // ===================================================================== 加载

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 页面实例被 NavigationCache 缓存，切回来也会触发 Loaded。
        // 首次进入才拉数据，之后交给「刷新」按钮，避免每次切 tab 都打一次接口。
        if (_loadedOnce) return;
        await ReloadAsync(false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync(true);

    private async Task ReloadAsync(bool ignoreCache)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        ShowState(loading: true);

        try
        {
            // 不按 type 筛选：公告页要能看到后台发布的全部公告。
            // 之前只拉 type=notice，后台把公告建成 home / popup 时这一页就空了或只剩一条。
            var list = await AnnouncementService.ListAsync(
                announceType: null, keyword: null, limit: FetchLimit,
                ignoreCache: ignoreCache, ct: cts.Token);

            _all.Clear();
            foreach (var item in list)
                _all.Add(new Row(item, !SettingsService.ReadAnnouncementIds.Contains(item.Id)));

            _loadedOnce = true;
            ApplyFilter();
        }
        catch (OperationCanceledException) { /* 页面切走或再次刷新，忽略 */ }
        catch (Exception ex)
        {
            ShowState(error: ex.Message);
        }
    }

    // ===================================================================== 筛选

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchBox?.Text?.Trim() ?? string.Empty;

        var matched = keyword.Length == 0
            ? _all
            : _all.Where(r =>
                  r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                  r.Preview.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        var previous = NoticeList.SelectedItem as Row;

        _view.Clear();
        foreach (var row in matched) _view.Add(row);

        if (_view.Count == 0)
        {
            ShowState(empty: keyword.Length > 0 ? $"没有匹配「{keyword}」的公告" : "暂无公告");
            ShowDetail(null);
            return;
        }

        ShowState();

        // 尽量保留原选中项；筛掉了就退回首行，避免右侧停在一条已经不在列表里的公告上。
        NoticeList.SelectedItem = previous is not null && _view.Contains(previous) ? previous : _view[0];
    }

    // ===================================================================== 详情

    private void NoticeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoticeList.SelectedItem is Row row) ShowDetail(row);
    }

    private void ShowDetail(Row? row)
    {
        if (row is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        DetailTitle.Text = row.Title;
        DetailLevelText.Text = row.LevelText;
        DetailLevelText.Foreground = row.LevelBrush;
        DetailPinnedBadge.Visibility = row.PinnedVisibility;

        var meta = new List<string>();
        if (row.DateText.Length > 0) meta.Add(row.DateText);
        if (!string.IsNullOrWhiteSpace(row.Source.Author)) meta.Add(row.Source.Author!.Trim());
        DetailMetaText.Text = string.Join(" · ", meta);

        DetailMarkdown.Document = MarkdownRenderer.ToFlowDocument(row.Source.Content);

        // 打开即已读：只写一次配置，首页的红点由同一个集合驱动。
        if (row.UnreadDotVisibility == Visibility.Visible)
        {
            row.MarkRead();
            SettingsService.MarkAnnouncementsRead(new[] { row.Source.Id });
        }
    }

    // ===================================================================== 状态切换

    private void ShowState(bool loading = false, string? error = null, string? empty = null)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = error is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = empty is not null ? Visibility.Visible : Visibility.Collapsed;

        if (error is not null) ErrorText.Text = error;
        if (empty is not null) EmptyText.Text = empty;
    }
}
