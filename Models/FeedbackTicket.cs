using System;

namespace Mosytool.Models;

/// <summary>
/// 本地保存的反馈工单凭证。
/// 提交成功后后端会把带 token 的查询链接发到用户邮箱；用户把那串 token 填回客户端后，
/// 后续「查询进度 / 补充说明」就不必再依赖邮件链接。
/// </summary>
public sealed class FeedbackTicket
{
    /// <summary>工单编号，查询与补充说明都要用。</summary>
    public long Id { get; set; }

    /// <summary>查询令牌。未填邮箱或未收到邮件时为空。</summary>
    public string? Token { get; set; }

    /// <summary>提交时填写的邮箱，可作为查询凭证的替代项。</summary>
    public string? Email { get; set; }

    /// <summary>内容摘要，仅用于列表显示。</summary>
    public string? Summary { get; set; }

    /// <summary>提交时间（本地时间字符串）。</summary>
    public string? CreatedAt { get; set; }

    /// <summary>最近一次查询到的状态文案，用于列表上色。</summary>
    public string? LastStatus { get; set; }

    /// <summary>最近一次查询到的最新回复。</summary>
    public string? LastReply { get; set; }

    /// <summary>列表展示用标题。</summary>
    public string DisplayText =>
        $"{Id} · {Truncate(Summary, 24)}";

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(无摘要)";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    public override string ToString() => DisplayText;
}
