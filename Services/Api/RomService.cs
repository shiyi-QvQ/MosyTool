using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>官方 ROM 包查询。</summary>
public static class RomService
{
    // 缓存时长跟随服务端 Cache-Control；列表短一点，因为官方 CDN 直链会过期。
    private static readonly TimeSpan ListTtl    = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FacetTtl   = TimeSpan.FromSeconds(300);

    /// <summary>
    /// ROM 列表。<paramref name="device"/> 与 <paramref name="keyword"/> 是模糊匹配，
    /// 其余为精确匹配；<paramref name="brand"/> 传「未知品牌」是筛不出来的（库里存空串）。
    /// </summary>
    public static async Task<PagedList<RomPackage>> ListAsync(
        string? device = null,
        string? brand = null,
        string? region = null,
        string? romType = null,
        string? keyword = null,
        int page = 1,
        int pageSize = 20,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["device"]    = device,
            ["brand"]     = brand,
            ["region"]    = region,
            ["rom_type"]  = romType,
            ["keyword"]   = keyword,
            ["page"]      = Math.Max(1, page).ToString(),
            ["page_size"] = Math.Clamp(pageSize, 1, 100).ToString(),
        };

        return await ApiClient.GetAsync<PagedList<RomPackage>>(
            "/api/public/roms", query, ignoreCache ? null : ListTtl, ct).ConfigureAwait(false);
    }

    /// <summary>品牌聚合，直接用作厂商切换列表（按包数倒序）。</summary>
    public static async Task<List<RomBrand>> BrandsAsync(bool ignoreCache = false, CancellationToken ct = default)
    {
        var data = await ApiClient.GetAsync<AggList<RomBrand>>(
            "/api/public/roms/brands", null, ignoreCache ? null : FacetTtl, ct).ConfigureAwait(false);
        return data.List;
    }

    /// <summary>
    /// 机型聚合。同一机型的不同地区会分成多条，这里按 <c>device_name</c> 归并后再返回，
    /// 并累加包数、取最新时间，方便直接绑定到下拉框。
    /// </summary>
    public static async Task<List<RomDevice>> DevicesAsync(
        string? brand = null,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?> { ["brand"] = brand };

        var data = await ApiClient.GetAsync<AggList<RomDevice>>(
            "/api/public/roms/devices", query, ignoreCache ? null : FacetTtl, ct).ConfigureAwait(false);

        return data.List
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
            .GroupBy(d => d.DeviceName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RomDevice
            {
                DeviceName = group.First().DeviceName,
                Brand      = group.Select(g => g.Brand).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b)),
                // 归并后地区不再唯一，用出现次数最多的地区做展示提示。
                Region     = group.GroupBy(g => g.Region)
                                  .OrderByDescending(r => r.Count())
                                  .Select(r => r.Key)
                                  .FirstOrDefault(),
                Count      = group.Sum(g => g.Count),
                LatestAt   = group.Select(g => AnnouncementService.ParseTime(g.LatestAt))
                                  .Where(t => t.HasValue)
                                  .Max()?.ToString("o"),
            })
            .OrderByDescending(d => AnnouncementService.ParseTime(d.LatestAt) ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>上游仓库与加速代理；一般客户端不需要，只有自行拉整仓库清单时才用。</summary>
    public static async Task<RomSource?> SourceAsync(CancellationToken ct = default)
    {
        try
        {
            return await ApiClient.GetAsync<RomSource>(
                "/api/public/rom/source", null, FacetTtl, ct).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>取下载用的 URL：优先可直接下载的 <c>direct_url</c>，为空时退回官方页 <c>download_url</c>。</summary>
    public static string? PickDownloadUrl(RomPackage rom) =>
        !string.IsNullOrWhiteSpace(rom.DirectUrl) ? rom.DirectUrl : rom.DownloadUrl;

    /// <summary>
    /// 该 URL 下载时是否需要带 <c>userid: oplus-ota|</c>。
    /// 只有走官方 <c>downloadcheck</c> 页（<c>download_url</c>）时需要；已解析出的直链不需要。
    /// </summary>
    public static bool NeedsUserIdHeader(RomPackage rom, string url) =>
        rom.NeedUseridHeader &&
        !string.IsNullOrWhiteSpace(rom.DownloadUrl) &&
        string.Equals(url, rom.DownloadUrl, StringComparison.OrdinalIgnoreCase);

    public static string RomTypeText(string? romType) => romType?.ToLowerInvariant() switch
    {
        "full" => "完整包",
        "ota"  => "增量包",
        _      => "未知",
    };

    /// <summary>去掉前后空格并把空串归一成 null，避免界面上出现空白的筛选标签。</summary>
    public static string? Normalize(string? value) => ApiClient.Normalize(value);
}
