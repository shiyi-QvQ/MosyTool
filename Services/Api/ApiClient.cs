using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models.Api;

namespace Mosytool.Services.Api;

/// <summary>
/// 后端返回 <c>code != 0</c> 或网络层失败时抛出。Message 已是面向用户的中文文案，
/// 可直接显示，无需再翻译错误码。
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>业务错误码；网络层失败时为 <see cref="LocalNetworkError"/>。</summary>
    public int Code { get; }

    /// <summary>HTTP 状态码；未拿到响应时为 0。</summary>
    public int HttpStatus { get; }

    /// <summary>本地网络/超时错误（连不上服务器），区别于服务端业务错误。</summary>
    public const int LocalNetworkError = -1;

    public ApiException(int code, int httpStatus, string message) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public bool IsNetworkError => Code == LocalNetworkError;

    /// <summary>限流（默认 60 次/分钟/IP，按路由分桶）。</summary>
    public bool IsRateLimited => Code == 4029;

    /// <summary>工单已结单，不能再补充说明。</summary>
    public bool IsTicketClosed => Code == 4009;
}

/// <summary>
/// 公共 API 客户端。所有接口都在 <c>/api/public/*</c> 下，统一信封
/// <c>{ code, message, data }</c>，本类负责拆信封、错误码映射、GET 短缓存与文件下载。
/// </summary>
public static class ApiClient
{
    /// <summary>
    /// 兜底地址：仅在环境变量与设置项都没配时使用。
    /// 生产环境为 MosyTool 后台管理系统（公告 / 更新 / ROM / 致谢 / 反馈）。
    /// 该站点只提供 HTTP，未开 TLS，故此处保持 http。
    /// </summary>
    public const string DefaultBaseUrl = "http://tool.shi-yi.cn";

    /// <summary>覆盖 BaseUrl 的环境变量名，优先级最高（方便测试/内测切换）。</summary>
    public const string BaseUrlEnvVar = "MOSYTOOL_API_BASE";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 请求体不输出 null：反馈接口是「未知字段拒收」，少传字段比传 null 更安全。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>GET 接口的本进程短缓存（公告/更新/ROM/致谢都是公共数据，命中 Cache-Control）。</summary>
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private static readonly object HttpLock = new();
    private static HttpClient? _http;
    private static string? _baseUrl;

    private sealed record CacheEntry(string Body, DateTimeOffset ExpiresAt);

    /// <summary>当前 API 根地址（不含末尾斜杠）。默认 <c>MOSYTOOL_API_BASE</c> → 设置项 → 内置默认。</summary>
    public static string BaseUrl
    {
        get
        {
            if (_baseUrl is not null) return _baseUrl;

            var fromEnv = Environment.GetEnvironmentVariable(BaseUrlEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                _baseUrl = fromEnv.TrimEnd('/');
                return _baseUrl;
            }

            var fromSettings = SettingsService.ApiBaseUrl;
            _baseUrl = string.IsNullOrWhiteSpace(fromSettings) ? DefaultBaseUrl : fromSettings.TrimEnd('/');
            return _baseUrl;
        }
        set
        {
            _baseUrl = string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');
            Cache.Clear();
        }
    }

    private static HttpClient Http
    {
        get
        {
            if (_http is not null) return _http;
            lock (HttpLock)
            {
                if (_http is not null) return _http;

                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                };

                var client = new HttpClient(handler)
                {
                    // 公共接口都很快，超时给短一点，避免用户界面卡住。
                    Timeout = TimeSpan.FromSeconds(15),
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"MosyTool/{AppInfo.Version}");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                _http = client;
                return _http;
            }
        }
    }

    /// <summary>把 null / 空白串归一成 null，避免把空字符串当成有效筛选值发给后端。</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ===== 请求入口 =====

    /// <summary>GET 并拆信封。注意：最新版本查询用 404 表示「没有匹配版本」，属正常分支。</summary>
    public static async Task<T> GetAsync<T>(
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        TimeSpan? cacheTtl = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(path, query);

        if (cacheTtl is { } ttl && Cache.TryGetValue(url, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow)
        {
            var cached = JsonSerializer.Deserialize<ApiEnvelope<T>>(hit.Body, JsonOptions);
            if (cached is { Code: 0, Data: not null }) return cached.Data;
        }

        var body = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);

        var envelope = Deserialize<T>(body, 200);

        if (cacheTtl is { } storeTtl)
            Cache[url] = new CacheEntry(body, DateTimeOffset.UtcNow.Add(storeTtl));

        return envelope;
    }

    /// <summary>POST 并拆信封（反馈提交/补充说明）。</summary>
    public static async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var raw = await SendAsync(HttpMethod.Post, BuildUrl(path, null), json, ct).ConfigureAwait(false);
        return Deserialize<T>(raw, 200);
    }

    private static string BuildUrl(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var url = BaseUrl + (path.StartsWith('/') ? path : "/" + path);
        if (query is null || query.Count == 0) return url;

        var sb = new StringBuilder(url);
        var first = !url.Contains('?');
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value)) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    private static async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                                           .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 服务端在非 2xx 时也尽量返回统一信封，优先用信封里的 message。
                var message = TryReadMessage(body);
                var code = TryReadCode(body) ?? (int)response.StatusCode;
                throw new ApiException(code, (int)response.StatusCode,
                    message ?? $"服务端返回 {(int)response.StatusCode}");
            }

            return body;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new ApiException(ApiException.LocalNetworkError, 0, "无法连接服务器，请检查网络后重试");
        }
    }

    private static T Deserialize<T>(string body, int httpStatus)
    {
        ApiEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new ApiException(-2, httpStatus, "服务器返回了无法解析的数据");
        }

        if (envelope is null)
            throw new ApiException(-2, httpStatus, "服务器返回了空响应");

        if (envelope.Code != 0)
            throw new ApiException(envelope.Code, httpStatus,
                string.IsNullOrWhiteSpace(envelope.Message) ? $"请求失败（{envelope.Code}）" : envelope.Message!);

        if (envelope.Data is null)
            throw new ApiException(-2, httpStatus, "服务器返回的数据为空");

        return envelope.Data;
    }

    private static string? TryReadMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryReadCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ===== 文件下载（更新包 / ROM）=====

    /// <summary>下载进度回调：(已接收字节, 总字节)；总字节未知时为 -1。</summary>
    public delegate void DownloadProgressHandler(long received, long total);

    /// <summary>
    /// 下载专用客户端。<see cref="HttpClient.Timeout"/> 是「整个请求到响应体读完」的总时长，
    /// 公共接口 15 秒足够，但更新包 / ROM 动辄几百 MB，用同一个客户端会在 15 秒时被掐断，
    /// 用户看到的就是「下载中断，请检查网络后重试」。这里不设总时长，
    /// 改成「连续 <see cref="DownloadIdleTimeout"/> 收不到数据」才算超时。
    /// </summary>
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    /// <summary>下载期间允许的最长无数据间隔。</summary>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateDownloadClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MosyTool/{AppInfo.Version}");
        return client;
    }

    /// <summary>
    /// 下载文件到指定路径。部分 ROM（<c>need_userid_header = true</c>）必须带
    /// <c>userid: oplus-ota|</c> 请求头，否则服务器只回一个签名/跳转页，故单独暴露该参数。
    /// 失败 / 取消一律删除半成品文件，避免留下一个打不开的「安装包」。
    /// </summary>
    public static async Task DownloadToFileAsync(
        string url,
        string destinationPath,
        bool needUserIdHeader = false,
        string? sha256 = null,
        DownloadProgressHandler? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (needUserIdHeader) request.Headers.TryAddWithoutValidation("userid", "oplus-ota|");

            using var response = await DownloadHttp
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new ApiException((int)response.StatusCode, (int)response.StatusCode,
                    $"下载失败（HTTP {(int)response.StatusCode}），请稍后重试或联系管理员");

            // 下载地址失效时，服务器常常回一个「文件不存在 / 请先登录」的网页，
            // 存下来就是一堆 HTML。不拦的话会被当成安装包执行，所以这里直接判掉。
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new ApiException(-4, (int)response.StatusCode,
                    "下载地址返回的是网页而不是文件（链接可能已失效），请稍后重试或联系管理员");

            var total = response.Content.Headers.ContentLength ?? -1L;
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var buffer = new byte[81920];
            long received = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                                     81920, useAsync: true))
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                while (true)
                {
                    idle.CancelAfter(DownloadIdleTimeout);   // 每读到一块就重新计时

                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new ApiException(ApiException.LocalNetworkError, 0,
                            $"下载超时（{DownloadIdleTimeout.TotalSeconds:0} 秒没有收到数据），请检查网络后重试");
                    }

                    if (read <= 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    onProgress?.Invoke(received, total);
                }
            }

            // 有 Content-Length 时对一下字节数：网络抖动导致的截断必须报错，
            // 不能把不完整的包当成下载成功。
            if (total > 0 && received != total)
                throw new ApiException(ApiException.LocalNetworkError, 0,
                    $"下载不完整（{UpdateService.FormatSize(received)} / {UpdateService.FormatSize(total)}），请重试");

            if (!string.IsNullOrWhiteSpace(sha256))
            {
                var actual = await ComputeSha256Async(destinationPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(-3, 0, "文件校验失败（SHA256 不匹配），已删除，请重新下载");
            }
        }
        catch (ApiException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(destinationPath);
            throw new ApiException(ApiException.LocalNetworkError, 0, "下载中断，请检查网络后重试");
        }
    }

    /// <summary>算文件 SHA256（十六进制小写）。</summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                                                useAsync: true);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* 删不掉就算了，不覆盖原始错误 */ }
        catch (UnauthorizedAccessException) { }
    }
}
