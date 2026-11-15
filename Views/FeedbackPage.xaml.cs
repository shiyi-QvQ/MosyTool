using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosytool.Models;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Views;

/// <summary>往来消息的展示行。</summary>
public sealed class ReplyRow
{
    public required string  Author     { get; init; }
    public required string  Time       { get; init; }
    public required string  Content    { get; init; }
    public required Brush   Background { get; init; }
}

public partial class FeedbackPage : Page
{
    private readonly ObservableCollection<ReplyRow> _replies = new();
    private sealed record OptionItem(string? Value, string Text);

    private bool _busy;

    public FeedbackPage()
    {
        InitializeComponent();
        ReplyList.ItemsSource = _replies;

        CategoryCombo.ItemsSource = FeedbackService.Categories
            .Select(c => new OptionItem(c.Value, c.Text)).ToList();
        CategoryCombo.SelectedIndex = 0;

        ContactTypeCombo.ItemsSource = new List<OptionItem>
        {
            new("qq",    "QQ"),
            new("wechat","微信"),
            new("other", "其他"),
        };
        ContactTypeCombo.SelectedIndex = 0;

        RefreshTicketList();
        UpdateContentCounter();
    }

    // ---------- 提交 ----------

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateContentCounter();

    private void UpdateContentCounter()
    {
        var length = ContentBox.Text?.Trim().Length ?? 0;
        ContentLabel.Text = $"反馈内容（{length}/1000，至少 {FeedbackService.MinContentLength} 字）";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var content = ContentBox.Text ?? string.Empty;
        if (!FeedbackService.IsContentLongEnough(content))
        {
            SubmitStatusText.Text = $"内容太短，至少写 {FeedbackService.MinContentLength} 个字。";
            return;
        }

        var email = FeedbackService.Normalize(EmailBox.Text);
        if (!FeedbackService.IsValidEmail(email))
        {
            SubmitStatusText.Text = "邮箱格式不正确，可以留空但填了就要填对。";
            return;
        }

        _busy = true;
        SubmitButton.IsEnabled = false;
        SubmitStatusText.Text = "正在提交…";

        try
        {
            var request = FeedbackService.BuildRequest(
                content,
                (CategoryCombo.SelectedItem as OptionItem)?.Value,
                email,
                FeedbackService.Normalize(ContactBox.Text),
                (ContactTypeCombo.SelectedItem as OptionItem)?.Value);

            var result = await FeedbackService.SubmitAsync(request);

            ContentBox.Text = string.Empty;
            UpdateContentCounter();
            RefreshTicketList();

            SubmitStatusText.Text = result.MailNotified
                ? $"提交成功，工单号 {result.Id}。受理邮件已发送，请从邮件里取回查询令牌。"
                : $"提交成功，工单号 {result.Id}。未发送受理邮件（可能没填邮箱），请记下工单号。";

            // 自动选中刚提交的工单，方便马上查进度。
            SelectTicket(result.Id);
        }
        catch (ApiException ex)
        {
            SubmitStatusText.Text = ex.IsRateLimited
                ? "提交太频繁了，请过一会儿再试。"
                : ex.Message;
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    // ---------- 工单列表 ----------

    private void RefreshTicketList()
    {
        var selectedId = (TicketList.SelectedItem as FeedbackTicket)?.Id;
        TicketList.ItemsSource = null;
        TicketList.ItemsSource = new List<FeedbackTicket>(SettingsService.Tickets);

        if (selectedId is { } id) SelectTicket(id);
    }

    private void SelectTicket(long id)
    {
        foreach (var item in TicketList.Items)
        {
            if (item is FeedbackTicket ticket && ticket.Id == id)
            {
                TicketList.SelectedItem = ticket;
                break;
            }
        }
    }

    private void TicketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TicketList.SelectedItem is not FeedbackTicket ticket) return;

        TokenBox.Text      = ticket.Token ?? string.Empty;
        QueryEmailBox.Text = ticket.Email ?? string.Empty;
        ReplyButton.IsEnabled = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (TicketList.SelectedItem is not FeedbackTicket ticket)
        {
            Dialogs.Show(Window.GetWindow(this), "请先选中一条工单。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Dialogs.Show(Window.GetWindow(this),
                $"只从本地列表移除工单 {ticket.Id} 的查询记录？\n（服务器上的反馈不会被删除）",
                "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SettingsService.RemoveTicket(ticket.Id);
        _replies.Clear();
        DetailHeader.Visibility = Visibility.Collapsed;
        DetailEmptyText.Visibility = Visibility.Visible;
        ReplyButton.IsEnabled = false;
        RefreshTicketList();
    }

    // ---------- 查询进度 ----------

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (TicketList.SelectedItem is not FeedbackTicket ticket)
        {
            Dialogs.Show(Window.GetWindow(this), "请先在列表里选中要查询的工单。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        try
        {
            var detail = await FeedbackService.QueryAsync(
                ticket.Id,
                FeedbackService.Normalize(TokenBox.Text),
                FeedbackService.Normalize(QueryEmailBox.Text));

            RenderDetail(detail);
            RefreshTicketList();
        }
        catch (ApiException ex)
        {
            DetailEmptyText.Visibility = Visibility.Visible;
            DetailEmptyText.Text = ex.HttpStatus == 401 || ex.Code == 4010
                ? "凭证不对：请填写受理邮件里的 token，或提交时用的邮箱。"
                : ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderDetail(FeedbackDetail detail)
    {
        _replies.Clear();

        DetailHeader.Visibility    = Visibility.Visible;
        DetailEmptyText.Visibility = Visibility.Collapsed;

        var color = (Color)ColorConverter.ConvertFromString(FeedbackService.StatusColor(detail.Status));
        DetailHeader.Background = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));

        DetailTitle.Text = $"{FeedbackService.CategoryText(detail.Category)}  ·  {detail.StatusText}";

        var meta = new List<string>();
        if (AnnouncementService.ParseTime(detail.CreatedAt) is { } created)
            meta.Add("提交于 " + created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (AnnouncementService.ParseTime(detail.UpdatedAt) is { } updated)
            meta.Add("最近更新 " + updated.ToLocalTime().ToString("MM-dd HH:mm"));
        meta.Add(detail.Closed ? detail.ClosedText ?? "已结单" : "可继续补充说明");
        DetailMeta.Text = string.Join("   ·   ", meta);

        // 原始内容作为第一条消息展示。
        _replies.Add(new ReplyRow
        {
            Author     = "我",
            Time       = AnnouncementService.ParseTime(detail.CreatedAt)?.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty,
            Content    = detail.Content ?? string.Empty,
            Background = (Brush)FindResource("ControlFillColorSecondaryBrush"),
        });

        foreach (var reply in detail.Replies)
            _replies.Add(ToRow(reply));

        // 旧字段兼容：replies 为空但 reply 有内容时，把它当管理员回复展示。
        if (detail.Replies.Count == 0 && !string.IsNullOrWhiteSpace(detail.Reply))
            _replies.Add(new ReplyRow
            {
                Author     = "管理员",
                Time       = string.Empty,
                Content    = detail.Reply!,
                Background = (Brush)FindResource("ControlFillColorSecondaryBrush"),
            });

        ReplyButton.IsEnabled   = detail.CanReply;
        ReplyBox.IsEnabled      = detail.CanReply;
        ReplyBox.PlaceholderText = detail.CanReply
            ? $"补充说明（至少 {FeedbackService.MinReplyLength} 个字）"
            : detail.ClosedText ?? "工单已结单，如需继续请重新提交新反馈";
    }

    private ReplyRow ToRow(FeedbackReply reply)
    {
        var fromAdmin = string.Equals(reply.Author, "admin", StringComparison.OrdinalIgnoreCase);

        return new ReplyRow
        {
            Author  = fromAdmin ? "管理员" : "我",
            Time    = AnnouncementService.ParseTime(reply.CreatedAt)?.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty,
            Content = reply.Content ?? string.Empty,
            Background = fromAdmin
                ? (Brush)FindResource("ControlFillColorSecondaryBrush")
                : new SolidColorBrush(Color.FromArgb(38, 0x3B, 0x91, 0xD8)),
        };
    }

    // ---------- 补充说明 ----------

    private async void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (TicketList.SelectedItem is not FeedbackTicket ticket) return;

        var content = ReplyBox.Text ?? string.Empty;
        if (!FeedbackService.IsReplyLongEnough(content))
        {
            Dialogs.Show(Window.GetWindow(this), $"补充内容至少 {FeedbackService.MinReplyLength} 个字。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        ReplyButton.IsEnabled = false;

        try
        {
            await FeedbackService.ReplyAsync(
                ticket.Id,
                content,
                FeedbackService.Normalize(TokenBox.Text),
                FeedbackService.Normalize(QueryEmailBox.Text));

            ReplyBox.Text = string.Empty;

            // 发完立刻回查一次，把刚发的内容显示出来。
            var detail = await FeedbackService.QueryAsync(
                ticket.Id,
                FeedbackService.Normalize(TokenBox.Text),
                FeedbackService.Normalize(QueryEmailBox.Text));

            RenderDetail(detail);
            RefreshTicketList();
        }
        catch (ApiException ex)
        {
            Dialogs.Show(Window.GetWindow(this),
                ex.IsTicketClosed
                    ? "该工单已经结单，无法再补充说明。请重新提交一条新反馈。"
                    : ex.Message,
                "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);

            // 结单态要立刻把输入框禁用掉，避免用户反复尝试；其它错误则恢复可编辑。
            if (ex.IsTicketClosed)
            {
                ReplyButton.IsEnabled = false;
                ReplyBox.IsEnabled    = false;
            }
            else
            {
                ReplyButton.IsEnabled = true;
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
