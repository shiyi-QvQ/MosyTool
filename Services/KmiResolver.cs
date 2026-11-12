using System;
using System.Text.RegularExpressions;

namespace Mosytool.Services;

/// <summary>
/// LKM 的 KMI（Kernel Module Interface，形如 android15-6.6）识别。
/// 模块是预编译的，KMI 差一点都加载不上，所以这一项不能猜。
/// </summary>
public static class KmiResolver
{
    /// <summary>
    /// 从内核版本号拼出 KMI。
    /// 例：6.6.118-android15-8-ge58033dc8ea6-abogki… → android15-6.6
    /// </summary>
    public static string? Parse(string? kernelVersion)
    {
        if (string.IsNullOrWhiteSpace(kernelVersion)) return null;

        var android = Regex.Match(kernelVersion, @"android(?<v>\d+)", RegexOptions.IgnoreCase);
        var version = Regex.Match(kernelVersion, @"(?<major>\d+)\.(?<minor>\d+)");

        if (!android.Success || !version.Success) return null;

        return $"android{android.Groups["v"].Value}-"
             + $"{version.Groups["major"].Value}.{version.Groups["minor"].Value}";
    }

    /// <summary>
    /// 从已连接的设备读 uname -r 再解析。设备没连、没开调试、或版本号里没有 android 字样都会返回 null。
    /// </summary>
    public static string? FromDevice()
    {
        if (!AdbService.IsAdbAvailable) return null;

        try
        {
            var raw = AdbService.Run(AdbService.AdbPath!, "shell uname -r", 8000);

            // 多设备时 adb 会先抱怨一句，真正的版本号在最后一行
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kmi = Parse(line);
                if (kmi is not null) return kmi;
            }
        }
        catch
        {
            // 读不到就让调用方提示手选
        }

        return null;
    }
}
