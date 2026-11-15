using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Controls;

/// <summary>
/// 公告卡片：既做启动自动弹出的 popup 公告，也做首页点击查看的公告详情。
///
/// 不是独立窗口——由 <see cref="Services.Dialogs.ShowCard"/> 挂到当前窗口的遮罩层里，
/// 所以弹出来时背景会模糊、点空白处即可关闭。
/// </summary>
public partial class AnnouncementCard : UserControl
{
    /// <summary>卡片结束（点关闭 / 我知道了 / 不再提醒）。</summary>
    public event Action? Finished;

    /// <summary>启动自动弹出的 popup 公告（单条）。</summary>
    public AnnouncementCard(Announcement announcement) : this(new[] { announcement }, isPopup: true)
    {
    }

    /// <summary>
    /// <paramref name="isPopup"/> 为 true 时是启动弹窗；为 false 时作为「公告详情」展示，
    /// 隐藏「不再提醒」并把主按钮改成「关闭」。
    /// </summary>
    public AnnouncementCard(Announcement announcement, bool isPopup) : this(new[] { announcement }, isPopup)
    {
    }

    /// <summary>
    /// 一次展示多条公告。多条时会合成一篇 Markdown（每条带小标题与时间）在一张卡片里滚动查看。
    /// </summary>
    public AnnouncementCard(IReadOnlyList<Announcement> announcements, bool isPopup = true)
    {
        InitializeComponent();

        if (announcements.Count == 0) return;

        var first    = announcements[0];
        var multiple = announcements.Count > 1;

        TitleText.Text = multiple
            ? $"有 {announcements.Count} 条公告"
            : string.IsNullOrWhiteSpace(first.Title) ? "公告" : first.Title!.Trim();

        // 单条时显示这条的等级徽章 / 置顶 / 时间；多条时各条的元信息写进正文，头部只留标题。
        if (multiple)
        {
            LevelBadge.Visibility  = Visibility.Collapsed;
            PinnedBadge.Visibility = Visibility.Collapsed;
            DateText.Text          = string.Empty;
        }
        else
        {
            LevelText.Text         = AnnouncementService.LevelText(first.Level);
            LevelBadge.Background  = new SolidColorBrush(ToColor(AnnouncementService.LevelColor(first.Level)));
            PinnedBadge.Visibility = first.Pinned ? Visibility.Visible : Visibility.Collapsed;

            var published = AnnouncementService.ParseTime(first.PublishAt)
                            ?? AnnouncementService.ParseTime(first.CreatedAt);
            DateText.Text = published?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
        }

        // 正文是 Markdown：标题、列表、引用、代码块、链接、表格都能正常排版
        ContentMarkdown.Document = MarkdownRenderer.ToFlowDocument(BuildMarkdown(announcements));

        if (isPopup)
        {
            // 展示即视为「已提醒」，避免用户直接点关闭后下次启动又弹一遍。
            foreach (var announcement in announcements)
            {
                SettingsService.MarkAnnouncementPopped(announcement.Id);
                SettingsService.MarkAnnouncementsRead(new[] { announcement.Id });
            }
        }
        else
        {
            MuteButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "关闭";
        }

        ConfirmButton.IsDefault = true;

        Loaded += (_, _) => ConfirmButton.Focus();
    }

    /// <summary>把多条公告拼成一篇 Markdown；单条时直接用原文，不改动排版。</summary>
    private static string BuildMarkdown(IReadOnlyList<Announcement> announcements)
    {
        if (announcements.Count == 1) return announcements[0].Content ?? string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < announcements.Count; i++)
        {
            var announcement = announcements[i];

            if (i > 0) sb.Append("\n\n---\n\n");

            var title = string.IsNullOrWhiteSpace(announcement.Title)
                ? $"公告 {i + 1}"
                : announcement.Title!.Trim();

            sb.Append("## ").Append(title).Append("\n\n");

            var time = AnnouncementService.ParseTime(announcement.PublishAt)
                       ?? AnnouncementService.ParseTime(announcement.CreatedAt);

            sb.Append('*').Append(AnnouncementService.LevelText(announcement.Level));
            if (time is { } published) sb.Append(" · ").Append(published.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (announcement.Pinned) sb.Append(" · 置顶");
            sb.Append("*\n\n");

            sb.Append(announcement.Content ?? string.Empty);
        }

        return sb.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Finished?.Invoke();

    private void Mute_Click(object sender, RoutedEventArgs e) => Finished?.Invoke();

    private void Confirm_Click(object sender, RoutedEventArgs e) => Finished?.Invoke();

    private static Color ToColor(string hex) =>
        ColorConverter.ConvertFromString(hex) is Color color ? color : Colors.Gray;
}
