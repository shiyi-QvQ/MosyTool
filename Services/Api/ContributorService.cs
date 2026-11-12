using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>致谢人员名单（只返回后台标记为可见的记录，已按 sort_order 排好序）。</summary>
public static class ContributorService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

    public static async Task<List<Contributor>> ListAsync(
        string? keyword = null,
        int pageSize = 50,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["keyword"]   = keyword,
            ["page"]      = "1",
            ["page_size"] = Math.Clamp(pageSize, 1, 200).ToString(),
        };

        var data = await ApiClient.GetAsync<PagedList<Contributor>>(
            "/api/public/contributors", query, ignoreCache ? null : Ttl, ct).ConfigureAwait(false);

        return data.List;
    }
}
