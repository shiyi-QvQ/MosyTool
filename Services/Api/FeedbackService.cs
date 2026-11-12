using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>
/// 意见反馈（工单）。要点：
/// 1. 请求体开了「未知字段拒收」，所以只用文档里定义的字段，<b>绝不能传</b> ip / user_agent；
/// 2. 查询用 query string（token / email 任填其一，token 优先），补充说明的凭证放在 body；
/// 3. 工单结单后补充会返回 4009，必须单独处理并引导用户新开单。
/// </summary>
public static class FeedbackService
{
    /// <summary>提交成功的工单编号会持久化到这里，供后续查询。</summary>
    public static IReadOnlyList<FeedbackTicket> LocalTickets => SettingsService.Tickets;

    // ===== 校验 =====

    public const int MinContentLength  = 5;    // 提交：至少 5 字
    public const int MaxContentLength  = 1000;
    public const int MinReplyLength    = 2;    // 补充：至少 2 字

    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || EmailRegex.IsMatch(email.Trim());

    public static bool IsContentLongEnough(string? content) =>
        (content?.Trim().Length ?? 0) >= MinContentLength;

    public static bool IsReplyLongEnough(string? content) =>
        (content?.Trim().Length ?? 0) >= MinReplyLength;

    // ===== 接口调用 =====

    /// <summary>提交反馈。成功后会尝试把工单编号写进本地设置。</summary>
    public static async Task<FeedbackSubmitResult> SubmitAsync(
        FeedbackSubmitRequest request, CancellationToken ct = default)
    {
        var result = await ApiClient.PostAsync<FeedbackSubmitResult>(
            "/api/public/feedback", request, ct).ConfigureAwait(false);

        SettingsService.SaveTicket(new FeedbackTicket
        {
            Id         = result.Id,
            Email      = request.Email,
            Token      = null, // 令牌只会出现在受理邮件里，接口不返回，需用户自行回填。
            Summary    = request.Content,
            CreatedAt  = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            LastStatus = "待处理",
        });

        return result;
    }

    /// <summary>按内容与联系方式组装请求体，自动补上客户端版本 / 系统 / 设备信息。</summary>
    public static FeedbackSubmitRequest BuildRequest(
        string content,
        string? category,
        string? email,
        string? contact,
        string? contactType)
    {
        return new FeedbackSubmitRequest
        {
            Content     = content.Trim(),
            Category    = category,
            Email       = Trim(email),
            Contact     = Trim(contact),
            ContactType = contactType,
            AppVersion  = AppInfo.ShortVersion,
            Platform    = OsDisplayName(),
            DeviceInfo  = $"{Environment.MachineName} ({RuntimeInformation.OSArchitecture})",
        };
    }

    /// <summary>查询工单进度。token 与 email 任填其一，两者都传时后端以 token 为准。</summary>
    public static async Task<FeedbackDetail> QueryAsync(
        long id, string? token = null, string? email = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["token"] = Trim(token),
            ["email"] = Trim(email),
        };

        var detail = await ApiClient.GetAsync<FeedbackDetail>(
            $"/api/public/feedback/{id}", query, null, ct).ConfigureAwait(false);

        // 顺手刷新本地记录的最近状态，供工单列表展示。
        var ticket = SettingsService.FindTicket(id);
        if (ticket is not null)
        {
            ticket.LastStatus = detail.StatusText;
            ticket.LastReply  = detail.Replies.Count > 0 ? detail.Replies[^1].Content : detail.Reply;
            ticket.Token      = Trim(token) ?? ticket.Token;
            ticket.Email      = Trim(email) ?? ticket.Email;
            SettingsService.SaveTicket(ticket);
        }

        return detail;
    }

    /// <summary>
    /// 补充说明。凭证在 body 里。工单已结单时抛出 <see cref="ApiException.IsTicketClosed"/>（4009）。
    /// </summary>
    public static Task<FeedbackReplyResult> ReplyAsync(
        long id, string content, string? token = null, string? email = null, CancellationToken ct = default)
    {
        var payload = new FeedbackReplyRequest
        {
            Content = content.Trim(),
            Token   = Trim(token),
            Email   = Trim(email),
        };

        return ApiClient.PostAsync<FeedbackReplyResult>(
            $"/api/public/feedback/{id}/reply", payload, ct);
    }

    // ===== 展示辅助 =====

    /// <summary>分类下拉项：值, 显示名。</summary>
    public static readonly IReadOnlyList<(string Value, string Text)> Categories = new List<(string, string)>
    {
        ("bug",        "问题反馈"),
        ("suggestion", "功能建议"),
        ("rom",        "ROM 资源"),
        ("ui",         "界面体验"),
        ("other",      "其他"),
    };

    public static string CategoryText(string? category) => category switch
    {
        "bug"        => "问题反馈",
        "suggestion" => "功能建议",
        "rom"        => "ROM 资源",
        "ui"         => "界面体验",
        _            => "其他",
    };

    /// <summary>状态色：待处理灰、处理中蓝、已解决绿、已忽略灰。</summary>
    public static string StatusColor(string? status) => status switch
    {
        "processing" => "#3B91D8",
        "resolved"   => "#107C10",
        "ignored"    => "#8A8886",
        _            => "#F7630C",
    };

    /// <summary>邮件查询页地址，可直接用系统浏览器打开。</summary>
    public static string BuildWebQueryUrl(long id, string? token = null, string? email = null)
    {
        var t = Trim(token);
        var e = Trim(email);
        var suffix = t is not null
            ? $"token={Uri.EscapeDataString(t)}"
            : e is not null ? $"email={Uri.EscapeDataString(e)}" : string.Empty;

        return string.IsNullOrEmpty(suffix)
            ? $"{ApiClient.BaseUrl}/feedback?id={id}"
            : $"{ApiClient.BaseUrl}/feedback?id={id}&{suffix}";
    }

    private static string? Trim(string? value) => ApiClient.Normalize(value);

    /// <summary>供界面层统一做「空白串归一成 null」，避免把空串当凭证发出去。</summary>
    public static string? Normalize(string? value) => ApiClient.Normalize(value);

    private static string OsDisplayName()
    {
        // 后端 platform 字段是「系统版本」的自由文本（上限 32 字符），这里给出可读版本号。
        var version = Environment.OSVersion.Version;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"Windows {version.Major}.{version.Minor} (Build {version.Build})"
            : RuntimeInformation.OSDescription;
    }
}
