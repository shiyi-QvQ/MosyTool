using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Mosytool.Services;

/// <summary>
/// adb / fastboot / 两个原生 *_ffi.dll 的定位与首次解压。
///
/// 这些文件必须以真实文件存在：adb、fastboot 是用 Process.Start 拉起来的独立进程，
/// 原生层是 P/Invoke 加载的 dll。单文件发布时 exe 旁边没有 tools\ 目录，所以它们在
/// 编译期以 EmbeddedResource 内嵌进程序集（逻辑名 tools/xxx），第一次用到时解压到
/// %LOCALAPPDATA%\Mosytool\tools\。
///
/// 查找顺序（<see cref="Dirs"/>）：exe 同级的真实 tools\ → 解压目录 → exe 同级目录。
/// 开发时 bin\...\tools\ 是真实目录，直接命中、不会多解压一份；单文件发布时落到解压目录；
/// 用户手动把文件放到 exe 同级 tools\（例如 scrcpy）也照旧认。
/// </summary>
internal static class ToolPaths
{
    /// <summary>内嵌资源的逻辑名前缀，与 csproj 里的 LogicalName 对应。</summary>
    private const string ResourcePrefix = "tools/";

    /// <summary>记录已解压的程序集版本，版本变了就整份重写一遍。</summary>
    private const string StampName = ".extracted";

    private static readonly Lazy<string[]> LazyDirs =
        new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>单文件形态下的解压目录（也是用户手放 scrcpy 之类文件的地方）。</summary>
    public static string ExtractDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mosytool", "tools");

    /// <summary>按优先级排列的 tools 目录，最后一项是 exe 同级目录。</summary>
    public static string[] Dirs => LazyDirs.Value;

    /// <summary>主 tools 目录：优先 exe 同级的真实目录，没有才是解压目录（写文件时用它）。</summary>
    public static string Dir => Dirs[0];

    /// <summary>按 <see cref="Dirs"/> 顺序找文件，都找不到返回 null。</summary>
    public static string? Find(string fileName)
    {
        foreach (var dir in Dirs)
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 路径非法（空目录名之类）就当这个候选不存在
            }
        }

        return null;
    }

    private static string[] Resolve()
    {
        var dirs = new List<string>();

        // 1) exe 同级的真实 tools\：开发时（bin\...\tools）和绿色目录部署都走这里
        var side = Path.Combine(AppContext.BaseDirectory, "tools");
        try
        {
            if (Directory.Exists(side)) dirs.Add(side);
        }
        catch { }

        // 2) 内嵌资源解压出来的那份。解压失败不抛，界面会如实提示找不到 adb / 原生层
        try { ExtractEmbedded(); } catch { }
        try
        {
            if (Directory.Exists(ExtractDir)) dirs.Add(ExtractDir);
        }
        catch { }

        // 3) exe 同级目录本身（历史上允许把 adb.exe 直接摆在 exe 旁边）
        dirs.Add(AppContext.BaseDirectory);

        return dirs.ToArray();
    }

    private static void ExtractEmbedded()
    {
        var assembly = typeof(ToolPaths).Assembly;
        var names    = assembly.GetManifestResourceNames();
        if (names.Length == 0) return;

        var version = assembly.GetName().Version?.ToString() ?? "0";
        var stamp   = Path.Combine(ExtractDir, StampName);

        var refresh = true;
        try { refresh = !string.Equals(File.ReadAllText(stamp).Trim(), version, StringComparison.Ordinal); }
        catch { }

        Directory.CreateDirectory(ExtractDir);

        foreach (var name in names)
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            // tools/adb.exe -> adb.exe；支持 tools\da\xxx、tools/lkm/KernelSU/xxx 这类子目录。
            // 逻辑名里的分隔符取决于构建机：手写的 LogicalName 是 '/'，用 %(RecursiveDir) 展开的
            // 在 Windows 上是 '\'，两种都归一化。
            var relative = name[ResourcePrefix.Length..]
                           .Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar);
            if (relative.Length == 0) continue;

            var target = Path.Combine(ExtractDir, relative);

            try
            {
                using var source = assembly.GetManifestResourceStream(name);
                if (source is null) continue;

                // 版本没变、文件又在、大小还对得上，就不用重写（避免每次启动都读盘 5 MB）
                if (!refresh && SameSize(target, source.Length)) continue;

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                WriteFile(source, target);
            }
            catch
            {
                // 单个文件失败不影响其它文件，缺失的交给上层如实报错
            }
        }

        try { File.WriteAllText(stamp, version); } catch { }
    }

    private static bool SameSize(string path, long length)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == length; }
        catch { return false; }
    }

    /// <summary>
    /// 先写 .tmp 再改名，避免半个文件被当成可用文件；目标被占用（adb.exe 正在跑）时重试几次。
    /// </summary>
    private static bool WriteFile(Stream source, string target)
    {
        var temp = target + ".tmp";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                source.Position = 0;
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(output);

                File.Move(temp, target, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) break;
                Thread.Sleep(120 * attempt);
            }
        }

        try { File.Delete(temp); } catch { }
        return false;
    }
}
