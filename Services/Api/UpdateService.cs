using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>
/// 在线更新。
/// ⚠️ 关键点：<c>/api/public/update/latest</c> 在「没有匹配版本」时<b>不会返回 404</b>，
/// 而是 HTTP 200 + <c>code 0</c> + <c>latest = null</c>，且 <c>data</c> 里<b>没有 force 字段</b>。
/// 因此必须判空 <c>latest</c>，不能直接假设 force 存在。
/// </summary>
public static class UpdateService
{
    /// <summary>update/latest 的 max-age 是 30 秒。</summary>
    private static readonly TimeSpan LatestTtl = TimeSpan.FromSeconds(30);

    /// <summary>updates 列表的 max-age 是 60 秒。</summary>
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(60);

    /// <summary>当前渠道，跟随设置项。注意只有 beta 会命中 beta 通道，其余值一律按 stable 处理。</summary>
    public static string Channel =>
        string.IsNullOrWhiteSpace(SettingsService.ApiChannel) ? "stable" : SettingsService.ApiChannel;

    /// <summary>
    /// 上报给服务端 / 参与比较的当前版本。
    /// 用三段式的 <see cref="AppInfo.DisplayVersion"/> 而不是程序集的四段版本：
    /// 后台是拿 version_name（通常写 1.1.0）做字符串比较的，
    /// 上报 "1.1.0.0" 会被判成「低于 1.1.0 或不同版本」，从而误报可更新。
    /// </summary>
    public static string CurrentVersion => AppInfo.DisplayVersion;

    /// <summary>
    /// 检查更新。参数名是 <c>current</c>（不是 version）；<c>current</c> 留空会被服务端直接判定为可更新。
    /// </summary>
    public static async Task<UpdateCheck> CheckAsync(
        string? channel = null,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["platform"] = AppInfo.Platform,
            ["channel"]  = string.IsNullOrWhiteSpace(channel) ? Channel : channel,
            ["current"]  = CurrentVersion,
        };

        return await ApiClient.GetAsync<UpdateCheck>(
            "/api/public/update/latest", query, ignoreCache ? null : LatestTtl, ct).ConfigureAwait(false);
    }

    /// <summary>版本历史（标准分页，按 version_code 倒序）。</summary>
    public static async Task<PagedList<AppRelease>> HistoryAsync(
        string? channel = null,
        string? keyword = null,
        int page = 1,
        int pageSize = 20,
        bool ignoreCache = false,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["platform"]  = AppInfo.Platform,
            ["channel"]   = string.IsNullOrWhiteSpace(channel) ? Channel : channel,
            ["keyword"]   = keyword,
            ["page"]      = Math.Max(1, page).ToString(),
            ["page_size"] = Math.Clamp(pageSize, 1, 100).ToString(),
        };

        return await ApiClient.GetAsync<PagedList<AppRelease>>(
            "/api/public/updates", query, ignoreCache ? null : ListTtl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 是否应该提示更新。
    ///
    /// 不再直接相信后端的 <c>has_update</c>：那是后台按字符串比较算出来的，
    /// 一旦 version_name 与当前版本段数不同（1.1.0 / 1.1.0.0），就会出现
    /// 「明明没新版却一直提示」或者「装了新版仍提示」两种错判。
    /// 这里统一以本地版本比较为准（后端漏判时也能兜住），
    /// 只把 <c>latest</c> 是否存在当作前提。
    ///
    /// 「跳过此版本」只对普通更新生效：强制更新不能被它压掉，否则 UpdateWindow 里
    /// 「强更不给跳过」那套逻辑就成了摆设。
    /// </summary>
    public static bool ShouldPrompt(UpdateCheck check, string? skippedVersion)
    {
        if (check.Latest is null) return false;

        var latest = check.Latest;

        // 版本号都没有（或压根不是数字版本）时无从比较，交给后端的 has_update。
        if (ParseVersion(latest.VersionName) is null)
            return check.HasUpdate;

        if (!IsNewer(latest.VersionName, CurrentVersion)) return false;

        if (!IsForced(check) && SameVersion(skippedVersion, latest.VersionName)) return false;

        return true;
    }

    /// <summary>比较两个版本号是否相同（补齐四段后再比，1.1.0 与 1.1.0.0 视为同一版）。</summary>
    private static bool SameVersion(string? left, string? right)
    {
        var a = ParseVersion(left);
        var b = ParseVersion(right);
        if (a is not null && b is not null) return a == b;

        return string.Equals(left?.Trim().TrimStart('v', 'V'),
                             right?.Trim().TrimStart('v', 'V'),
                             StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 本地兜底比较：后台的 version_name 可能写成三段（1.1.0），客户端上报的是四段
    /// （1.1.0.0），按 System.Version 语义 1.1.0 &lt; 1.1.0.0，服务端就会一直说「可更新」。
    /// 两边都补齐成四段再比，只有确实比当前高才提示，避免装完之后陷入关不掉的强制更新。
    /// 任一侧解析不出来（旧记录、写成「最新版」之类）时不做拦截，交给服务端的 has_update。
    /// </summary>
    private static bool IsNewer(string? latest, string? current)
    {
        var latestVersion  = ParseVersion(latest);
        var currentVersion = ParseVersion(current);

        if (latestVersion is null || currentVersion is null) return true;
        return latestVersion > currentVersion;
    }

    /// <summary>解析三段 / 四段版本号（可带 v 前缀）；不是纯数字版本时返回 null。</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length is < 2 or > 4) return null;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return null;

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>强制更新：后端只在 force &amp;&amp; has_update 时返回 force=true，且该字段可能缺省。</summary>
    public static bool IsForced(UpdateCheck check) =>
        check.HasUpdate && (check.Force == true || check.Latest?.ForceUpdate == true);

    /// <summary>下载候选地址：先直链，再镜像。</summary>
    public static IEnumerable<string> DownloadCandidates(AppRelease release)
    {
        if (!string.IsNullOrWhiteSpace(release.DownloadUrl)) yield return release.DownloadUrl!;
        foreach (var mirror in release.MirrorUrls)
            if (!string.IsNullOrWhiteSpace(mirror)) yield return mirror;
    }

    /// <summary>格式化文件大小。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
