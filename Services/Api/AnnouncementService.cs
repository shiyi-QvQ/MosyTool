using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>
/// 远程公告。接口本身已过滤「已发布且在有效期」，客户端只需按类型分流展示：
/// <c>popup</c> 启动弹窗、<c>home</c> 首页公告栏、<c>notice</c> 公告列表。
/// </summary>
public static class AnnouncementService
{
    /// <summary>服务端该接口的 max-age 是 60 秒，客户端按同样节奏缓存即可。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 拉取公告列表，按「置顶优先 + 发布时间倒序」排好序返回。
    /// 接口没有 level 筛选参数，只有 keyword 与 type；<paramref name="announceType"/> 传 null/空/非法值
    /// 都表示不按类型筛选。
    /// </summary>
    public static async Task<List<Announcement>> ListAsync(
        string? announceType = null,
        string? keyword = null,
        int limit = 5,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["type"]    = announceType,
            ["keyword"] = keyword,
            ["limit"]   = Math.Clamp(limit, 1, 50).ToString(),
        };

        var data = await ApiClient.GetAsync<AggList<Announcement>>(
            "/api/public/announcements", query, ignoreCache ? null : Ttl, ct).ConfigureAwait(false);

        return Sort(data.List);
    }

    /// <summary>取启动弹窗公告（type=popup），已弹过的会被过滤掉。</summary>
    public static async Task<List<Announcement>> PendingPopupsAsync(
        IReadOnlySet<long> poppedIds, CancellationToken ct = default)
    {
        var list = await ListAsync("popup", null, 10, false, ct).ConfigureAwait(false);
        return list.Where(a => !poppedIds.Contains(a.Id)).ToList();
    }

    /// <summary>首页公告栏（type=home）。</summary>
    public static Task<List<Announcement>> HomeAsync(int limit = 5, bool ignoreCache = false,
                                                     CancellationToken ct = default)
        => ListAsync("home", null, limit, ignoreCache, ct);

    /// <summary>普通公告列表（type=notice）。</summary>
    public static Task<List<Announcement>> NoticesAsync(int limit = 20, bool ignoreCache = false,
                                                        CancellationToken ct = default)
        => ListAsync("notice", null, limit, ignoreCache, ct);

    /// <summary>等级对应的强调色（十六进制），供 UI 直接绑定。</summary>
    public static string LevelColor(string? level) => level?.ToLowerInvariant() switch
    {
        "urgent"    => "#D13438",
        "important" => "#F7630C",
        _           => "#3B91D8",
    };

    public static string LevelText(string? level) => level?.ToLowerInvariant() switch
    {
        "urgent"    => "紧急",
        "important" => "重要",
        _           => "公告",
    };

    private static List<Announcement> Sort(IEnumerable<Announcement> source) => source
        .OrderByDescending(a => a.Pinned)
        .ThenByDescending(a => ParseTime(a.PublishAt) ?? ParseTime(a.CreatedAt) ?? DateTimeOffset.MinValue)
        .ToList();

    /// <summary>解析 RFC3339 时间；后端字段可能为 null 或带时区。</summary>
    internal static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, out var t) ? t : null;
    }
}
