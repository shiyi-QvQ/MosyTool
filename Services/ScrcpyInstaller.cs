using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// scrcpy 的自动获取。
///
/// 正常情况用不到这里：scrcpy 已经内置在程序目录的 tools\scrcpy\ 下，随包分发。
/// 只有在「内置文件缺失」（例如用户只拿了单个 exe、或被杀软删掉）或想升级版本时，
/// 才走到这里自动下载安装，用户不需要去 GitHub 手点。
///
/// 下载顺序：GitHub 直连 → 加速镜像，任一成功即停；
/// 版本先问 GitHub API 要 latest，失败则回退到内置的固定版本，保证离线环境也有明确的行为。
/// </summary>
public static class ScrcpyInstaller
{
    /// <summary>程序内置的 scrcpy 版本（tools\scrcpy 里就是这一份）。</summary>
    public const string BundledVersion = "4.1";

    /// <summary>问不到 latest 时的固定回退版本。</summary>
    private const string FallbackTag = "v" + BundledVersion;

    private const string ApiLatest = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";

    /// <summary>加速镜像前缀；空串表示直连 GitHub。国内直连不通时靠后面两个兜底。</summary>
    private static readonly string[] Mirrors =
    {
        "",
        "https://ghfast.top/",
        "https://gh-proxy.com/",
    };

    /// <summary>
    /// 安装目录：和 adb / fastboot 同级。exe 旁边已有真实 tools\（离线包 -WithScrcpy、绿色目录
    /// 部署）就装那儿，否则装到单文件形态下的数据目录 %LOCALAPPDATA%\Mosytool\tools\scrcpy。
    /// </summary>
    public static string InstallDir => Path.Combine(ToolPaths.Dir, "scrcpy");

    public static string ExePath => Path.Combine(InstallDir, "scrcpy.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// 下载并安装 scrcpy 到 <see cref="InstallDir"/>，成功返回 true。
    ///
    /// onStatus 汇报阶段文字，onProgress 汇报下载百分比（0-100）；
    /// 两者都可能从后台线程回调，界面负责切回 UI 线程。
    /// </summary>
    public static async Task<bool> InstallAsync(Action<string> onStatus, Action<double> onProgress,
                                                CancellationToken ct = default)
    {
        var tags = await ResolveTagsAsync(ct).ConfigureAwait(false);
        var zip  = Path.Combine(Path.GetTempPath(), $"mosytool-scrcpy-{Guid.NewGuid():N}.zip");

        try
        {
            if (!await DownloadAnyAsync(tags, zip, onStatus, onProgress, ct).ConfigureAwait(false))
                return false;

            return await Task.Run(() => Extract(zip, onStatus), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            onStatus("已取消下载。");
            return false;
        }
        catch (Exception ex)
        {
            onStatus($"安装失败：{ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteFile(zip);
        }
    }

    // ---------- 版本 ----------

    /// <summary>候选版本列表：GitHub 上的 latest 优先，后面跟着内置的固定版本。</summary>
    private static async Task<List<string>> ResolveTagsAsync(CancellationToken ct)
    {
        var tags = new List<string>();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mosytool");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json  = await http.GetStringAsync(ApiLatest, ct).ConfigureAwait(false);
            var match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (match.Success) tags.Add(match.Groups[1].Value);
        }
        catch
        {
            // 网络不通或被限流都无所谓，下面会用固定版本
        }

        if (!tags.Contains(FallbackTag)) tags.Add(FallbackTag);
        return tags;
    }

    // ---------- 下载 ----------

    private static async Task<bool> DownloadAnyAsync(List<string> tags, string zipPath,
                                                     Action<string> onStatus, Action<double> onProgress,
                                                     CancellationToken ct)
    {
        string? lastError = null;

        foreach (var tag in tags)
        {
            // 官方 release 的资源命名约定：scrcpy-win64-vX.Y.zip
            var urlPath = $"https://github.com/Genymobile/scrcpy/releases/download/{tag}/scrcpy-win64-{tag}.zip";

            foreach (var mirror in Mirrors)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    onStatus($"正在下载 scrcpy {tag}（{MirrorName(mirror)}）…");
                    onProgress(0);
                    await DownloadAsync(mirror + urlPath, zipPath, onProgress, ct).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    onStatus($"该地址不可用（{ex.Message}），换下一个…");
                }
            }
        }

        onStatus($"所有下载地址都失败了：{lastError}");
        return false;
    }

    private static async Task DownloadAsync(string url, string savePath,
                                            Action<double> onProgress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mosytool");

        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var total = res.Content.Headers.ContentLength ?? -1;

        await using var source = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(savePath);

        var  buffer   = new byte[81920];
        long received = 0;
        int  read;

        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            if (total > 0) onProgress(received * 100.0 / total);
        }
    }

    // ---------- 解压 ----------

    private static bool Extract(string zipPath, Action<string> onStatus)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mosytool-scrcpy-" + Guid.NewGuid().ToString("N"));

        try
        {
            onStatus("下载完成，正在解压…");
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            // 官方包解开后是 scrcpy-win64-vX.Y\ 一层目录，这里递归找 scrcpy.exe 所在目录
            var payload = FindExeDir(tempDir);
            if (payload is null)
            {
                onStatus("压缩包结构异常：里面没有 scrcpy.exe。");
                return false;
            }

            onStatus("正在写入 tools\\scrcpy…");

            // 先整个删掉旧目录再拷，避免旧版本残留的 dll 和新版本混在一起
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
            Directory.CreateDirectory(InstallDir);
            CopyDirectory(payload, InstallDir);

            if (!File.Exists(ExePath))
            {
                onStatus("安装似乎没成功：目标目录里没有 scrcpy.exe。");
                return false;
            }

            onStatus($"scrcpy 已安装到 {InstallDir}");
            return true;
        }
        catch (IOException ex) when (IsFileLocked(ex))
        {
            onStatus("安装失败：tools\\scrcpy 里的文件正被占用，请先关闭正在运行的投屏窗口再试。");
            return false;
        }
        catch (Exception ex)
        {
            onStatus($"解压失败：{ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static string? FindExeDir(string root)
    {
        try
        {
            var direct = Path.Combine(root, "scrcpy.exe");
            if (File.Exists(direct)) return root;

            foreach (var file in Directory.EnumerateFiles(root, "scrcpy.exe", SearchOption.AllDirectories))
                return Path.GetDirectoryName(file);
        }
        catch
        {
            // 落回 null，交由调用方报「结构异常」
        }

        return null;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    // ---------- 小工具 ----------

    private static string MirrorName(string mirror) => mirror.Length == 0 ? "GitHub 直连" : mirror.Trim('/');

    /// <summary>文件被占用时 Windows 会报 IOException，这里只做「是不是占用」的粗判断。</summary>
    private static bool IsFileLocked(IOException ex)
        => ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件删不掉不影响功能 */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* 同上 */ }
    }
}
