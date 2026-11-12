using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// 通过 adb 往设备里推送文件 / 文件夹（adb push）。
/// </summary>
public static class AdbFileService
{
    /// <summary>设备上的目标目录；/sdcard 即内部存储根，普通用户可写。</summary>
    public const string DefaultRemoteDir = "/sdcard/Download/";

    /// <summary>推送结果汇总。</summary>
    public sealed class PushSummary
    {
        public int  Total     { get; init; }
        public int  Ok        { get; init; }
        public int  Failed    { get; init; }
        public bool Cancelled { get; init; }

        public string Message => Cancelled
            ? $"已取消：成功 {Ok} / 共 {Total} 项。"
            : Failed > 0
                ? $"推送结束：成功 {Ok} 项，失败 {Failed} 项，共 {Total} 项。"
                : $"推送完成：{Ok} 项全部成功。";
    }

    /// <summary>
    /// 逐项推送，返回汇总结果。
    ///
    /// onItem(索引, 单项百分比, 状态文本) 在每项开始 / 进度 / 结束时回调（可能在后台线程）；
    /// onLine 收 adb 的原始输出行，错误信息就靠它透出来。
    /// </summary>
    public static async Task<PushSummary> PushAsync(
        string serial,
        IReadOnlyList<PushItem> items,
        string remoteDir,
        Action<int, int, string> onItem,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
        {
            onLine("未找到 adb.exe，无法推送。");
            return new PushSummary { Total = items.Count, Failed = items.Count };
        }

        var dir = NormalizeRemoteDir(remoteDir);
        if (dir is null)
        {
            onLine($"目标目录不合法：{remoteDir}");
            onLine("需要填设备内路径，且以 / 开头，例如 /sdcard/Download/");
            return new PushSummary { Total = items.Count, Failed = items.Count };
        }

        // /sdcard/Download 这类目录一般已存在，mkdir -p 失败也不影响后续 push，仅提示。
        await EnsureRemoteDirAsync(serial, dir, onLine, ct).ConfigureAwait(false);

        int ok = 0, failed = 0;
        var cancelled = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var item = items[i];

            if (!File.Exists(item.LocalPath) && !Directory.Exists(item.LocalPath))
            {
                onLine($"跳过（本地不存在）：{item.LocalPath}");
                onItem(i, 100, "本地不存在");
                failed++;
                continue;
            }

            onItem(i, 0, "推送中…");
            var lastPercent = -1;

            // 目标必须写成「目录 + 文件名」的完整路径，不能只给一个以 / 结尾的目录。
            // adb（实测 1.0.35）在「目标是目录」时要自己去本地路径里推导 basename，
            // 遇到含中文的文件名会推错，目标变成 /sdcard/Download/. ，
            // 设备端报 "couldn't create file: Is a directory"；中文目录更阴——不报错，
            // 但目录名被换成 . ，里面所有文件被平铺进上层目录。
            // 名字由我们自己拼好，adb 不需要推导，中文名就能正常创建。
            var remotePath = dir + item.Name;
            var args = $"-s {serial} push {WinArg(item.LocalPath)} {WinArg(remotePath)}";
            onLine($"> adb {args}");

            var code = await AdbService.RunStreamingAsync(
                AdbService.AdbPath!,
                args,
                line =>
                {
                    // adb 的进度条是 "[ 42%] /sdcard/xxx" 这种，单独抽出来更新进度，
                    // 其余行（含报错）原样交给界面。
                    var percent = ParsePercent(line);
                    if (percent >= 0)
                    {
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            onItem(i, percent, $"推送中 {percent}%");
                        }
                        return;
                    }

                    if (line.Trim().Length > 0) onLine(line);
                },
                ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                onItem(i, lastPercent < 0 ? 0 : lastPercent, "已取消");
                break;
            }

            if (code == 0)
            {
                onItem(i, 100, "完成");
                onLine($"已推送：{item.Name}（{item.SizeHuman}）");
                ok++;
            }
            else
            {
                onItem(i, 100, "失败");
                onLine($"推送失败（退出码 {code}）：{item.Name}");
                failed++;
            }
        }

        return new PushSummary { Total = items.Count, Ok = ok, Failed = failed, Cancelled = cancelled };
    }

    /// <summary>在设备上建目录（mkdir -p），返回是否成功。</summary>
    public static async Task<bool> MakeDirectoryAsync(string serial, string dir, CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null) return false;

        var (ok, _) = await MakeDirectoryCoreAsync(serial, dir, ct).ConfigureAwait(false);
        return ok;
    }

    private static async Task<(bool Ok, string Error)> MakeDirectoryCoreAsync(string serial, string dir,
                                                                             CancellationToken ct)
    {
        var errors = new List<string>();

        var code = await AdbService.RunStreamingAsync(
            AdbService.AdbPath!,
            BuildShellArgs(serial, $"mkdir -p {ShArg(dir)}"),
            line =>
            {
                var text = line.Trim();
                if (text.Length > 0) lock (errors) errors.Add(text);
            },
            ct).ConfigureAwait(false);

        return (code == 0, string.Join(" / ", errors));
    }

    /// <summary>推送前先把目标目录建出来。</summary>
    private static async Task EnsureRemoteDirAsync(string serial, string dir,
                                                   Action<string> onLine, CancellationToken ct)
    {
        var (ok, error) = await MakeDirectoryCoreAsync(serial, dir, ct).ConfigureAwait(false);

        // 目录已存在时 mkdir -p 本来就不报错；只有真的建不出来才提示。
        if (!ok && error.Length > 0)
            onLine($"提示：创建目录 {dir} 失败（{error}），仍会尝试推送。");
    }

    // ---------- 设备目录浏览 ----------

    /// <summary>设备上的一个目录项。</summary>
    public sealed class RemoteEntry
    {
        public required string Name        { get; init; }
        public required bool   IsDirectory { get; init; }

        /// <summary>文件字节数；目录固定为 0（各机型对目录 size 的含义不一致）。</summary>
        public long Size { get; init; }
    }

    /// <summary>列目录的结果；Ok 为 false 时 Error 是设备给出的原因（多半是权限不足）。</summary>
    public sealed class ListResult
    {
        public bool              Ok      { get; init; }
        public string            Error   { get; init; } = string.Empty;
        public List<RemoteEntry> Entries { get; init; } = new();
    }

    /// <summary>
    /// 列出设备上的某个目录，用于让用户直接点选推送目标，而不是手打路径。
    ///
    /// ls -p 会给目录名补一个 /，靠它区分文件夹和文件最省事；
    /// 个别老设备的 ls 不认 -p，就退回 -F（同样给目录补 /，另外给可执行文件补 *）。
    /// </summary>
    public static async Task<ListResult> ListAsync(string serial, string dir, CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
            return new ListResult { Ok = false, Error = "未找到 adb.exe" };

        var result = await RunListAsync(serial, $"-a -p {ShArg(dir)}", ct).ConfigureAwait(false);
        if (!result.Ok)
            result = await RunListAsync(serial, $"-a -F {ShArg(dir)}", ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// 带类型与大小的目录读取（ls -al），用于「文件推送」页的列表 / 宫格。
    ///
    /// ls -l 每行固定是 8 个字段 + 末尾的文件名：
    ///   -rw-rw---- 1 root sdcard_rw 12345 2024-01-01 12:00 文件名（可能含空格）
    /// 因此按前 8 个字段切分、剩下的整段当名字，中文名与带空格的名字都不会被切坏。
    /// </summary>
    public static async Task<ListResult> ListDetailedAsync(string serial, string dir, CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
            return new ListResult { Ok = false, Error = "未找到 adb.exe" };

        var lines = new List<string>();

        var code = await AdbService.RunStreamingAsync(
            AdbService.AdbPath!,
            BuildShellArgs(serial, $"ls -al {ShArg(dir)}"),
            line => { lock (lines) lines.Add(line); },
            ct).ConfigureAwait(false);

        var entries = new List<RemoteEntry>();
        var errors  = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("total", StringComparison.Ordinal)) continue;

            if (IsErrorLine(line))
            {
                errors.Add(line.Trim());
                continue;
            }

            var fields = line.Split(new[] { ' ', '\t' }, 8, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8) continue;   // 不是 -l 的常规行（个别机型会混入其它输出）

            var permissions = fields[0];
            if (permissions.Length == 0) continue;

            var isDirectory = permissions[0] == 'd';
            _ = long.TryParse(fields[4], out var size);

            var name = fields[7].Trim();

            // 符号链接会带 " -> 目标"，只保留链接本身的名字
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0) name = name[..arrow].Trim();

            if (name.Length == 0 || name == "." || name == "..") continue;

            entries.Add(new RemoteEntry
            {
                Name        = name,
                IsDirectory = isDirectory,
                Size        = isDirectory ? 0 : size,
            });
        }

        if (code != 0 || (entries.Count == 0 && errors.Count > 0))
        {
            return new ListResult
            {
                Ok    = false,
                Error = errors.Count > 0 ? string.Join(" / ", errors) : "设备没有返回可用的目录内容",
            };
        }

        return new ListResult { Ok = true, Entries = entries };
    }

    /// <summary>
    /// 把设备上的文件 / 目录导出到本机（adb pull）。
    /// onProgress 收到的是 adb 进度行里的百分比，onLine 收原始输出。
    /// </summary>
    public static async Task<(bool Ok, string Error)> PullAsync(
        string serial,
        string remotePath,
        string localDir,
        Action<string> onLine,
        Action<int> onProgress,
        CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
        {
            onLine("未找到 adb.exe，无法导出。");
            return (false, "未找到 adb.exe");
        }

        try { Directory.CreateDirectory(localDir); }
        catch (Exception ex) { onLine($"提示：无法创建本机目录 {localDir}（{ex.Message}），仍会尝试导出。"); }

        var args = $"-s {serial} pull {WinArg(remotePath)} {WinArg(localDir)}";
        onLine($"> adb {args}");

        var lastPercent = -1;
        var errors      = new List<string>();

        var code = await AdbService.RunStreamingAsync(
            AdbService.AdbPath!,
            args,
            line =>
            {
                var percent = ParsePercent(line);
                if (percent >= 0)
                {
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        onProgress(percent);
                    }
                    return;
                }

                var text = line.Trim();
                if (text.Length == 0) return;

                if (IsErrorLine(text)) lock (errors) errors.Add(text);
                onLine(text);
            },
            ct).ConfigureAwait(false);

        return (code == 0, string.Join(" / ", errors));
    }

    /// <summary>ls / pull 输出里的报错行（权限不足、路径不存在等）。</summary>
    private static bool IsErrorLine(string line)
        => line.StartsWith("ls:", StringComparison.Ordinal) ||
           line.Contains("Permission denied", StringComparison.Ordinal) ||
           line.Contains("No such file", StringComparison.Ordinal) ||
           line.Contains("does not exist", StringComparison.Ordinal);

    private static async Task<ListResult> RunListAsync(string serial, string lsArgs, CancellationToken ct)
    {
        var lines = new List<string>();

        var code = await AdbService.RunStreamingAsync(
            AdbService.AdbPath!,
            BuildShellArgs(serial, $"ls {lsArgs}"),
            line => { lock (lines) lines.Add(line); },
            ct).ConfigureAwait(false);

        var entries = new List<RemoteEntry>();
        var errors  = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 报错行（ls: xxx: Permission denied 之类）不是目录项，留作失败原因
            if (line.StartsWith("ls:", StringComparison.Ordinal) ||
                line.Contains("Permission denied", StringComparison.Ordinal) ||
                line.Contains("No such file", StringComparison.Ordinal))
            {
                errors.Add(line);
                continue;
            }

            var isDirectory = line.EndsWith("/", StringComparison.Ordinal);

            // -F 还会给可执行文件补 *、符号链接补 @，这里把所有类型标记都削掉只留名字
            var name = line.TrimEnd('/', '*', '@', '=', '|', '>');
            if (name.Length == 0 || name == "." || name == "..") continue;

            entries.Add(new RemoteEntry { Name = name, IsDirectory = isDirectory });
        }

        if (code != 0 || (entries.Count == 0 && errors.Count > 0))
        {
            return new ListResult
            {
                Ok    = false,
                Error = errors.Count > 0 ? string.Join(" / ", errors) : "设备没有返回可用的目录内容",
            };
        }

        return new ListResult { Ok = true, Entries = entries };
    }

    /// <summary>把用户输入的目标目录规整成 /xxx/ 形式；不合法返回 null。</summary>
    private static string? NormalizeRemoteDir(string remoteDir)
    {
        var text = remoteDir?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.StartsWith("/", StringComparison.Ordinal)) return null;

        return text.EndsWith("/", StringComparison.Ordinal) ? text : text + "/";
    }

    /// <summary>从 adb 的进度行里取百分比；不是进度行返回 -1。</summary>
    private static int ParsePercent(string line)
    {
        var match = Regex.Match(line, @"\[\s*(\d{1,3})%\]");
        if (!match.Success) return -1;

        return int.TryParse(match.Groups[1].Value, out var percent) ? percent : -1;
    }

    /// <summary>
    /// 拼出 `-s SERIAL shell "…"`：整条设备命令作为一个 Windows 参数传进去，
    /// 否则 adb 会把参数用空格重新拼起来，里面的单引号就白加了。
    /// </summary>
    private static string BuildShellArgs(string serial, string command)
        => $"-s {serial} shell {WinArg(command)}";

    /// <summary>
    /// 把一段文本包成单个 Windows 命令行参数（只在需要时才加双引号）。
    ///
    /// 结尾反斜杠要翻倍，否则会把用于收尾的引号转义掉，例如 "C:\dir\" 会变成 C:\dir"。
    /// </summary>
    private static string WinArg(string text)
    {
        if (text.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return text;

        var body    = text.Replace("\"", "\\\"");
        var trailing = body.Length - body.TrimEnd('\\').Length;

        return "\"" + body + new string('\\', trailing) + "\"";
    }

    /// <summary>包成设备 shell 的单引号参数（POSIX 规则：内部的单引号写成 '\''）。</summary>
    private static string ShArg(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
