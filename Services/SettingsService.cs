using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Mosytool.Models;

namespace Mosytool.Services;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MosyTool", "settings.json");

    public static Color AccentColor { get; private set; } = Color.FromRgb(0x3B, 0x91, 0xD8);

    /// <summary>界面主题：system（跟随系统）/ light / dark。</summary>
    public static string ThemeMode { get; private set; } = "system";

    // ---------- MTK 工具的连接选项（跨会话记忆） ----------

    public static string? MtkDaFile { get; private set; }
    public static string? MtkPreloaderFile { get; private set; }
    public static string? MtkAuthFile { get; private set; }
    public static bool MtkVerbose { get; private set; }
    public static bool MtkUsbLog { get; private set; }

    // ---------- 高通 EDL 工具选项 ----------

    /// <summary>firehose 装载器（prog_firehose_*.elf）路径。</summary>
    public static string? EdlProgrammerFile { get; private set; }

    /// <summary>存储类型：null / 空 = 自动探测，否则为 eMMC / UFS / NAND。</summary>
    public static string? EdlMemoryName { get; private set; }

    // ---------- 公共 API ----------

    /// <summary>API 根地址；为空则用 <see cref="Api.ApiClient"/> 的内置默认值。</summary>
    public static string? ApiBaseUrl { get; private set; }

    /// <summary>更新渠道，对应后端 channel 字段，默认 stable。</summary>
    public static string ApiChannel { get; private set; } = "stable";

    /// <summary>用户点了「跳过此版本」的版本号，匹配到该版本时不再提示。</summary>
    public static string? SkippedVersion { get; private set; }

    /// <summary>上次成功检查更新的 UTC 时间。</summary>
    public static DateTime LastUpdateCheckUtc { get; private set; }

    /// <summary>已读公告 ID，用于首页红点与弹窗只弹一次。</summary>
    public static HashSet<long> ReadAnnouncementIds { get; private set; } = new();

    /// <summary>最近一次弹过窗的公告 ID，避免同一条弹窗重复弹出。</summary>
    public static HashSet<long> PoppedAnnouncementIds { get; private set; } = new();

    /// <summary>本地保存的反馈工单。</summary>
    public static List<FeedbackTicket> Tickets { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            var json = File.ReadAllText(SettingsPath);
            var obj  = JsonSerializer.Deserialize<Settings>(json);
            if (obj is null) return;

            if (!string.IsNullOrEmpty(obj.AccentColor))
            {
                var parsed = ColorConverter.ConvertFromString(obj.AccentColor);
                if (parsed is Color c) AccentColor = c;
            }

            ThemeMode = string.IsNullOrWhiteSpace(obj.ThemeMode) ? "system" : obj.ThemeMode!;

            MtkDaFile        = obj.MtkDaFile;
            MtkPreloaderFile = obj.MtkPreloaderFile;
            MtkAuthFile      = obj.MtkAuthFile;
            MtkVerbose       = obj.MtkVerbose;
            MtkUsbLog        = obj.MtkUsbLog;

            EdlProgrammerFile = obj.EdlProgrammerFile;
            EdlMemoryName     = obj.EdlMemoryName;

            // 早期默认值是本地调试地址（127.0.0.1:8090），若配置里存的是它，
            // 视为「未配置」，让它回落到 ApiClient.DefaultBaseUrl（线上后台）。
            var savedBaseUrl = obj.ApiBaseUrl;
            ApiBaseUrl = string.IsNullOrWhiteSpace(savedBaseUrl) || IsLegacyLocalBaseUrl(savedBaseUrl)
                ? null
                : savedBaseUrl;

            ApiChannel        = string.IsNullOrWhiteSpace(obj.ApiChannel) ? "stable" : obj.ApiChannel;
            SkippedVersion    = obj.SkippedVersion;
            LastUpdateCheckUtc = obj.LastUpdateCheckUtc;

            ReadAnnouncementIds   = new HashSet<long>(obj.ReadAnnouncementIds ?? new List<long>());
            PoppedAnnouncementIds = new HashSet<long>(obj.PoppedAnnouncementIds ?? new List<long>());
            Tickets               = obj.Tickets ?? new List<FeedbackTicket>();
        }
        catch { /* 忽略损坏的配置文件 */ }
    }

    public static void SaveAccentColor(Color color)
    {
        AccentColor = color;
        Persist();
    }

    public static void SaveThemeMode(string mode)
    {
        ThemeMode = string.IsNullOrWhiteSpace(mode) ? "system" : mode.Trim().ToLowerInvariant();
        Persist();
    }

    public static void SaveMtkOptions(string? daFile, string? preloaderFile, string? authFile,
                                      bool verbose, bool usbLog)
    {
        MtkDaFile        = daFile;
        MtkPreloaderFile = preloaderFile;
        MtkAuthFile      = authFile;
        MtkVerbose       = verbose;
        MtkUsbLog        = usbLog;
        Persist();
    }

    public static void SaveEdlOptions(string? programmerFile, string? memoryName)
    {
        EdlProgrammerFile = programmerFile;
        EdlMemoryName     = memoryName;
        Persist();
    }

    public static void SaveApiOptions(string? baseUrl, string channel)
    {
        ApiBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        ApiChannel = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim();
        Api.ApiClient.BaseUrl = ApiBaseUrl ?? string.Empty;
        Persist();
    }

    public static void MarkAnnouncementsRead(IEnumerable<long> ids)
    {
        var changed = false;
        foreach (var id in ids)
            if (ReadAnnouncementIds.Add(id)) changed = true;

        if (changed) Persist();
    }

    public static void MarkAnnouncementPopped(long id)
    {
        if (PoppedAnnouncementIds.Add(id)) Persist();
    }

    public static void SaveUpdateState(DateTime checkedUtc, string? skippedVersion)
    {
        LastUpdateCheckUtc = checkedUtc;
        SkippedVersion     = skippedVersion;
        Persist();
    }

    /// <summary>新增或按 Id 覆盖保存一条工单。</summary>
    public static void SaveTicket(FeedbackTicket ticket)
    {
        var index = Tickets.FindIndex(t => t.Id == ticket.Id);
        if (index >= 0) Tickets[index] = ticket;
        else Tickets.Insert(0, ticket);
        Persist();
    }

    public static void RemoveTicket(long id)
    {
        if (Tickets.RemoveAll(t => t.Id == id) > 0) Persist();
    }

    public static FeedbackTicket? FindTicket(long id) => Tickets.FirstOrDefault(t => t.Id == id);

    /// <summary>历史遗留的本地调试地址，需要被忽略以回落到线上后台。</summary>
    private static bool IsLegacyLocalBaseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!uri.IsLoopback) return false;
        return uri.Port == 8090 || uri.Port == 5000 || uri.Port == 8080;
    }

    private static void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings
            {
                AccentColor       = AccentColor.ToString(),
                ThemeMode         = ThemeMode,
                MtkDaFile         = MtkDaFile,
                MtkPreloaderFile  = MtkPreloaderFile,
                MtkAuthFile       = MtkAuthFile,
                MtkVerbose        = MtkVerbose,
                MtkUsbLog         = MtkUsbLog,
                EdlProgrammerFile = EdlProgrammerFile,
                EdlMemoryName     = EdlMemoryName,

                ApiBaseUrl         = ApiBaseUrl,
                ApiChannel         = ApiChannel,
                SkippedVersion     = SkippedVersion,
                LastUpdateCheckUtc = LastUpdateCheckUtc,
                ReadAnnouncementIds   = ReadAnnouncementIds.ToList(),
                PoppedAnnouncementIds = PoppedAnnouncementIds.ToList(),
                Tickets               = Tickets,
            }));
        }
        catch { /* 忽略写入失败 */ }
    }

    private sealed class Settings
    {
        public string? AccentColor { get; set; }
        public string? ThemeMode { get; set; }
        public string? MtkDaFile { get; set; }
        public string? MtkPreloaderFile { get; set; }
        public string? MtkAuthFile { get; set; }
        public bool MtkVerbose { get; set; }
        public bool MtkUsbLog { get; set; }
        public string? EdlProgrammerFile { get; set; }
        public string? EdlMemoryName { get; set; }

        public string? ApiBaseUrl { get; set; }
        public string? ApiChannel { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTime LastUpdateCheckUtc { get; set; }
        public List<long>? ReadAnnouncementIds { get; set; }
        public List<long>? PoppedAnnouncementIds { get; set; }
        public List<FeedbackTicket>? Tickets { get; set; }
    }
}
