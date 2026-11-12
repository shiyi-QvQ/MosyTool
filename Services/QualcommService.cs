using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// 高通 EDL（9008）设备通信封装。全部能力由内置原生层 qualcomm_ffi.dll 提供（进程内 P/Invoke）。
///
/// 能力分两类：读（读 GPT 分区表 / 备份分区）与写（写回分区 / 擦除分区），外加复位。
/// 写操作会真的改动设备，调用方必须在界面上做好二次确认。
///
/// 与 MTK 最大的不同：sahara 握手只发生一次。firehose 装载器一跑起来，设备就不再处于
/// 「等装载器」的状态，重复走 sahara 必然失败。所以这里把会话缓存起来，读表与备份
/// 复用同一个句柄，直到用户主动断开或复位。
/// </summary>
public static class QualcommService
{
    private const string NativeMissingText =
        "未找到 qualcomm_ffi.dll，请确认 tools 目录完整。";

    /// <summary>内置原生层（qualcomm_ffi.dll）是否可用。</summary>
    public static bool IsAvailable => QualcommNative.IsAvailable;

    /// <summary>原生层的实时日志出口，由界面订阅。</summary>
    public static event Action<string>? NativeLog;

    public static void Initialize()
    {
        if (!QualcommNative.IsAvailable) return;

        QualcommNative.AttachLogger();
        QualcommNative.Log += OnNativeLog;
    }

    private static void OnNativeLog(int level, string message)
    {
        // 与原生层 emit 的数值对齐：0=Info 1=Warn 2=Error
        var tag = level switch
        {
            2 => "ERROR",
            1 => "WARN ",
            _ => "INFO ",
        };

        NativeLog?.Invoke($"[{tag}] {message}");
    }

    // ---------- 会话（跨操作复用） ----------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static QualcommNative.QcSession? _session;
    private static string? _programmer;
    private static string? _memory;

    public static bool IsConnected   => _session is not null;
    public static string? DeviceName => _session?.Device;
    public static string? MemoryName => _session?.MemoryName;
    public static ulong SectorSize   => _session?.SectorSize ?? 0;
    public static ulong MaxPayload   => _session?.MaxPayload ?? 0;

    /// <summary>
    /// 所有设备操作的统一外壳：串行化 → 确保会话就绪 → 执行 body。
    ///
    /// 会话的建立（sahara 握手 + 推装载器 + firehose configure）很贵，而且一旦失败，
    /// 设备状态就回不去了。这里把「确保会话」放进同一段临界区，避免两个操作同时握手。
    /// </summary>
    private static async Task<T> WithSessionAsync<T>(
        string programmer, string? memoryName,
        Func<QualcommNative.QcSession, T> body,
        Action<string> onOutput, CancellationToken ct)
    {
        if (!QualcommNative.IsAvailable)
            throw new QualcommNativeException(NativeMissingText);

        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureSessionCore(programmer, memoryName, onOutput);
            return body(_session!);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>确保有一个与当前参数匹配的会话；不匹配或不存在则重开。调用方必须持有 <see cref="Gate"/>。</summary>
    private static void EnsureSessionCore(string programmer, string? memoryName, Action<string> onOutput)
    {
        if (string.IsNullOrWhiteSpace(programmer))
            throw new QualcommNativeException("请先选择 firehose 装载器（prog_firehose_*.elf）文件。");

        if (_session is not null
            && string.Equals(_programmer, programmer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_memory, memoryName, StringComparison.OrdinalIgnoreCase))
            return;

        CloseSessionCore();

        onOutput($"正在连接 9008 设备（装载器：{Path.GetFileName(programmer)}），最长等待 15 秒…");

        var session = QualcommNative.QcSession.Open(new QcOpenConfig
        {
            ProgrammerPath = programmer,
            MemoryName     = string.IsNullOrWhiteSpace(memoryName) ? null : memoryName,
        });

        try
        {
            // 打开后立刻问一次 info，把存储类型 / 扇区大小回填到会话上
            var infoJson = session.Execute(new Dictionary<string, object?> { ["op"] = "info" });
            using var doc = JsonDocument.Parse(infoJson);
            var root = doc.RootElement;

            session.MemoryName = GetString(root, "memory_name");
            session.SectorSize = GetU64(root, "sector_size");
            session.MaxPayload = GetU64(root, "max_payload");
        }
        catch
        {
            session.Dispose();
            throw;
        }

        _session    = session;
        _programmer = programmer;
        _memory     = memoryName;

        onOutput($"已连接：{session.Device}（{session.MemoryName}，扇区 {session.SectorSize} 字节）");
    }

    private static void CloseSessionCore()
    {
        var session = _session;
        _session    = null;
        _programmer = null;
        _memory     = null;

        try { session?.Dispose(); }
        catch { /* 释放失败无碍 */ }
    }

    /// <summary>主动断开：释放 firehose 会话。设备仍停在 EDL，下次操作会重新握手。</summary>
    public static async Task DisconnectAsync()
    {
        await Gate.WaitAsync();
        try { CloseSessionCore(); }
        finally { Gate.Release(); }
    }

    // ---------- 连接 / 读表 / 备份 / 复位 ----------

    /// <summary>建立会话并返回设备摘要。失败返回 (false, 原因)。</summary>
    public static async Task<(bool Ok, string Message)> ConnectAsync(
        string programmer, string? memoryName, Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable) return (false, NativeMissingText);

        try
        {
            var message = await WithSessionAsync(programmer, memoryName,
                s => $"已连接：{s.Device}（{s.MemoryName}，扇区 {s.SectorSize} 字节，单次上限 {s.MaxPayload} 字节）",
                onOutput, ct);

            return (true, message);
        }
        catch (OperationCanceledException)
        {
            onOutput("已取消。");
            return (false, "已取消。");
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"失败：{ex.Message}");
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>读取主 GPT 分区表。会话不存在时会自动建立。</summary>
    public static async Task<(List<EdlPartition> Partitions, int Code)> ReadGptAsync(
        string programmer, string? memoryName, Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable)
        {
            onOutput(NativeMissingText);
            return (new List<EdlPartition>(), 1);
        }

        try
        {
            var partitions = await WithSessionAsync(programmer, memoryName, s =>
            {
                onOutput("正在读取 GPT 分区表…");
                var json = s.Execute(new Dictionary<string, object?>
                {
                    ["op"]   = "read_gpt",
                    ["phys"] = 0,
                });

                var list = ParsePartitions(json);
                onOutput($"共解析出 {list.Count} 个分区。");
                return list;
            }, onOutput, ct);

            return (partitions, 0);
        }
        catch (OperationCanceledException)
        {
            onOutput("已取消。");
            return (new List<EdlPartition>(), -1);
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"失败：{ex.Message}");
            return (new List<EdlPartition>(), 1);
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            return (new List<EdlPartition>(), 1);
        }
    }

    /// <summary>批量里的一项：按扇区区间把分区读到文件。</summary>
    public sealed record EdlBackupItem(string Label, string Output, ulong StartSector, ulong NumSectors);

    /// <summary>
    /// 在一个会话里连续备份多个分区。会话只握手一次，逐项复用。
    /// 单项失败不中断整批。返回 (成功数, 失败数)。
    /// </summary>
    public static async Task<(int Succeeded, int Failed)> BackupAsync(
        string programmer, string? memoryName, IReadOnlyList<EdlBackupItem> items,
        Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable)
        {
            onOutput(NativeMissingText);
            return (0, items.Count);
        }

        var succeeded = 0;
        var failed    = 0;

        try
        {
            await WithSessionAsync(programmer, memoryName, s =>
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = items[i];
                    try
                    {
                        s.Execute(new Dictionary<string, object?>
                        {
                            ["op"]           = "backup_partition",
                            ["phys"]         = 0,
                            ["start_sector"] = item.StartSector,
                            ["num_sectors"]  = item.NumSectors,
                            ["output"]       = item.Output,
                        });

                        succeeded++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 完成");
                    }
                    catch (QualcommNativeException ex)
                    {
                        failed++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 失败：{ex.Message}");
                    }
                }

                return 0;
            }, onOutput, ct);
        }
        catch (OperationCanceledException)
        {
            onOutput($"已取消（成功 {succeeded} 项，失败 {failed} 项）。");
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"会话失败：{ex.Message}");
            failed++;
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            failed++;
        }

        return (succeeded, failed);
    }

    /// <summary>批量写入里的一项：把镜像写进分区的扇区区间。</summary>
    public sealed record EdlWriteItem(string Label, string Image, ulong StartSector, ulong Sectors);

    /// <summary>
    /// 在一个会话里连续写入多个分区。单项失败不中断整批。
    ///
    /// 写多少扇区由镜像的实际长度决定（原生层按文件大小计算），不会把整个分区铺满；
    /// 分区尾部保留原内容。
    /// </summary>
    public static async Task<(int Succeeded, int Failed)> WriteAsync(
        string programmer, string? memoryName, IReadOnlyList<EdlWriteItem> items,
        Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable)
        {
            onOutput(NativeMissingText);
            return (0, items.Count);
        }

        var succeeded = 0;
        var failed    = 0;
        var temps     = new List<string>();

        try
        {
            await WithSessionAsync(programmer, memoryName, s =>
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = items[i];
                    try
                    {
                        var source = PrepareImage(item.Image, temps, onOutput);

                        s.Execute(new Dictionary<string, object?>
                        {
                            ["op"]           = "program_partition",
                            ["phys"]         = 0,
                            ["start_sector"] = item.StartSector,
                            ["num_sectors"]  = item.Sectors,
                            ["label"]        = item.Label,
                            ["image"]        = source,
                        });

                        succeeded++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 写入完成");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 写入失败：{ex.Message}");
                    }
                }

                return 0;
            }, onOutput, ct);
        }
        catch (OperationCanceledException)
        {
            onOutput($"已取消（成功 {succeeded} 项，失败 {failed} 项）。");
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"会话失败：{ex.Message}");
            failed++;
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            failed++;
        }
        finally
        {
            foreach (var temp in temps)
            {
                try { File.Delete(temp); } catch { /* 临时文件删不掉无碍 */ }
            }
        }

        return (succeeded, failed);
    }

    /// <summary>批量擦除里的一项。</summary>
    public sealed record EdlEraseItem(string Label, ulong StartSector, ulong NumSectors);

    /// <summary>在一个会话里连续擦除多个分区。单项失败不中断整批。</summary>
    public static async Task<(int Succeeded, int Failed)> EraseAsync(
        string programmer, string? memoryName, IReadOnlyList<EdlEraseItem> items,
        Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable)
        {
            onOutput(NativeMissingText);
            return (0, items.Count);
        }

        var succeeded = 0;
        var failed    = 0;

        try
        {
            await WithSessionAsync(programmer, memoryName, s =>
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = items[i];
                    try
                    {
                        s.Execute(new Dictionary<string, object?>
                        {
                            ["op"]           = "erase_partition",
                            ["phys"]         = 0,
                            ["start_sector"] = item.StartSector,
                            ["num_sectors"]  = item.NumSectors,
                            ["label"]        = item.Label,
                        });

                        succeeded++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 擦除完成");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        onOutput($"[{i + 1}/{items.Count}] {item.Label} 擦除失败：{ex.Message}");
                    }
                }

                return 0;
            }, onOutput, ct);
        }
        catch (OperationCanceledException)
        {
            onOutput($"已取消（成功 {succeeded} 项，失败 {failed} 项）。");
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"会话失败：{ex.Message}");
            failed++;
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            failed++;
        }

        return (succeeded, failed);
    }

    /// <summary>
    /// 写入前的镜像准备：sparse 镜像必须先展开成 raw。
    ///
    /// 直接把 sparse 文件当裸数据写进去，设备只会拿到 28 字节的 sparse 头部加一堆块索引，
    /// 分区必然废掉——所以这一步不能省，展开件放在临时目录，批次结束后由调用方清理。
    /// </summary>
    private static string PrepareImage(string image, List<string> temps, Action<string> onOutput)
    {
        if (!File.Exists(image))
            throw new QualcommNativeException($"找不到镜像文件：{image}");

        if (!SparseImage.IsSparse(image)) return image;

        var raw = SparseImage.GetRawLength(image);
        onOutput($"检测到 sparse 镜像，正在展开为 raw（{HumanSize((ulong)Math.Max(raw, 0))}）…");

        var temp = Path.Combine(Path.GetTempPath(), $"mosytool_edl_{Guid.NewGuid():N}.raw");

        try
        {
            using var output = File.Create(temp);
            SparseImage.Decompress(image, output);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* 清不掉就留给系统 */ }
            throw;
        }

        temps.Add(temp);
        onOutput($"展开完成：{Path.GetFileName(temp)}");

        return temp;
    }

    /// <summary>让设备从 EDL 复位重启。成功后会话作废（设备已离开 EDL）。</summary>
    public static async Task<(bool Ok, string Message)> ResetAsync(
        string programmer, string? memoryName, Action<string> onOutput, CancellationToken ct = default)
    {
        if (!QualcommNative.IsAvailable) return (false, NativeMissingText);

        try
        {
            var message = await WithSessionAsync(programmer, memoryName, s =>
            {
                var json = s.Execute(new Dictionary<string, object?> { ["op"] = "reset" });
                using var doc = JsonDocument.Parse(json);
                return GetString(doc.RootElement, "message");
            }, onOutput, ct);

            // 复位后设备会退出 EDL，会话随之作废
            await DisconnectAsync();

            return (true, string.IsNullOrWhiteSpace(message) ? "复位指令已发出，设备正在重启。" : message);
        }
        catch (OperationCanceledException)
        {
            onOutput("已取消。");
            return (false, "已取消。");
        }
        catch (QualcommNativeException ex)
        {
            onOutput($"失败：{ex.Message}");
            await DisconnectAsync();
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            onOutput($"错误：{ex.Message}");
            return (false, ex.Message);
        }
    }

    // ---------- 设备检测 ----------

    /// <summary>设备管理器里的一个 9008 相关 USB 端口。</summary>
    public sealed record EdlUsbPort(string InstanceId, ushort Vid, ushort Pid, string Name,
                                    string? Driver, bool Online)
    {
        /// <summary>形如 "05C6:9008"。</summary>
        public string Id => $"{Vid:X4}:{Pid:X4}";

        /// <summary>该驱动能否被 nusb 的后端直接打开。</summary>
        public bool DriverUsable => Driver is not null
            && (Driver.Equals("WinUSB",  StringComparison.OrdinalIgnoreCase)
             || Driver.Equals("libusbK", StringComparison.OrdinalIgnoreCase)
             || Driver.Equals("libusb0", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 枚举设备管理器里所有 9008 相关端口（含已拔掉的残留记录）。
    /// 目的是让界面能说清楚「有没有设备、挂在什么驱动上」，而不是直接起会话干等 15 秒。
    /// </summary>
    public static List<EdlUsbPort> EnumerateEdlPorts()
    {
        var result = new List<EdlUsbPort>();

        try
        {
            const string query =
                "SELECT DeviceID, Name, Status FROM Win32_PnPEntity WHERE " +
                "DeviceID LIKE '%VID_05C6&PID_9008%' OR " +
                "DeviceID LIKE '%VID_05C6&PID_900E%' OR " +
                "DeviceID LIKE '%QDLOADER 9008%'";

            using var searcher = new ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get())
            {
                var instanceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                if (!TryParseVidPid(instanceId, out var vid, out var pid)) continue;

                var status = obj["Status"]?.ToString() ?? string.Empty;

                result.Add(new EdlUsbPort(
                    instanceId, vid, pid,
                    obj["Name"]?.ToString() ?? "Qualcomm 9008",
                    DriverOf(instanceId),
                    status.Equals("OK", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { /* WMI 不可用时忽略 */ }

        return result;
    }

    /// <summary>面向界面的设备体检。返回 (是否可直接执行, 说明文本, 建议文本)。</summary>
    public static (bool Ready, string Text, string Hint) InspectDevice()
    {
        if (!QualcommNative.IsAvailable)
            return (false, "当前设备端口：无", string.Empty);

        var ports = EnumerateEdlPorts();

        if (ports.Count == 0)
            return (false, "当前设备端口：无", string.Empty);

        var online = ports.FirstOrDefault(p => p.Online);
        if (online is null)
        {
            var last = ports[0];
            return (false, $"当前设备端口：{last.Id}（离线）", string.Empty);
        }

        return (true, $"当前设备端口：{online.Id}（{online.Name}）", string.Empty);
    }

    private static bool TryParseVidPid(string instanceId, out ushort vid, out ushort pid)
    {
        vid = pid = 0;

        var m = Regex.Match(instanceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        if (!m.Success) return false;

        return ushort.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out pid);
    }

    /// <summary>读设备绑定的驱动服务名。WinUSB / libusbK / libusb0 才能被 nusb 直接打开。</summary>
    private static string? DriverOf(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return key?.GetValue("Service") as string;
        }
        catch { return null; }
    }

    // ---------- 分区表衍生物（纯计算，不碰设备） ----------

    /// <summary>一个 A/B 分区对。缺哪一侧就是 null——有些机型的 A/B 分区数量并不对称。</summary>
    public sealed record EdlSlotPair(string BaseName, EdlPartition? SlotA, EdlPartition? SlotB)
    {
        /// <summary>形如 "boot（A/B 齐全）"。</summary>
        public string Describe()
        {
            var state = (SlotA is not null, SlotB is not null) switch
            {
                (true, true)   => "A/B 齐全",
                (true, false)  => "只有 A",
                (false, true)  => "只有 B",
                _              => "缺失",
            };

            return $"{BaseName}（{state}）";
        }
    }

    /// <summary>
    /// 从分区表里找出 A/B 槽位。判定依据只有分区名后缀 <c>_a</c> / <c>_b</c>——
    /// 这是 A/B 机型的固定命名约定，不需要额外读 misc 之类的元数据。
    /// </summary>
    public static List<EdlSlotPair> DetectSlots(IEnumerable<EdlPartition> partitions)
    {
        var slots = new Dictionary<string, (EdlPartition? A, EdlPartition? B)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var p in partitions)
        {
            if (p.Name.Length < 3) continue;

            var suffix = p.Name[^2..];
            bool isA;

            if (suffix.Equals("_a", StringComparison.OrdinalIgnoreCase)) isA = true;
            else if (suffix.Equals("_b", StringComparison.OrdinalIgnoreCase)) isA = false;
            else continue;

            var baseName = p.Name[..^2];

            if (!slots.TryGetValue(baseName, out var pair))
            {
                pair = (null, null);
                order.Add(baseName);
            }

            if (isA) pair.A = p; else pair.B = p;
            slots[baseName] = pair;
        }

        return order.Select(k =>
        {
            var (a, b) = slots[k];
            return new EdlSlotPair(k, a, b);
        }).ToList();
    }

    /// <summary>
    /// 按分区表生成 rawprogram XML（QFIL 约定），filename 一律取「分区名.img」，
    /// 便于和从固件包里解出来的镜像目录对齐。
    ///
    /// 只产出 program 结点：patch 结点是给 bootloader 打二进制补丁用的，属于机型专属知识，
    /// 由工具凭空生成只会帮倒忙。
    /// </summary>
    public static string BuildRawprogramXml(IEnumerable<EdlPartition> partitions,
                                            ulong sectorSize, int phys = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" ?>");
        sb.AppendLine("<!-- 由 Mosytool 依据设备上的 GPT 分区表生成 -->");
        sb.AppendLine("<data>");

        foreach (var p in partitions.OrderBy(p => p.FirstLba))
        {
            sb.AppendLine(
                "  <program " +
                $"SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" " +
                "file_sector_offset=\"0\" " +
                $"filename=\"{p.Name}.img\" " +
                $"label=\"{p.Name}\" " +
                $"num_partition_sectors=\"{p.Sectors}\" " +
                $"physical_partition_number=\"{phys}\" " +
                $"start_sector=\"{p.FirstLba}\" />");
        }

        sb.AppendLine("</data>");

        return sb.ToString();
    }

    // ---------- 解析 ----------

    private static List<EdlPartition> ParsePartitions(string json)
    {
        var result = new List<EdlPartition>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("partitions", out var array)) return result;

        foreach (var item in array.EnumerateArray())
        {
            var size = GetU64(item, "size");

            result.Add(new EdlPartition
            {
                Index         = (int)GetU64(item, "index"),
                Name          = GetString(item, "name"),
                TypeGuid      = GetString(item, "type_guid"),
                PartitionGuid = GetString(item, "partition_guid"),
                FirstLba      = GetU64(item, "first_lba"),
                LastLba       = GetU64(item, "last_lba"),
                Sectors       = GetU64(item, "sectors"),
                Size          = size,
                SizeHuman     = HumanSize(size),
            });
        }

        return result;
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static ulong GetU64(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetUInt64()
            : 0;

    /// <summary>把字节数格式化成易读大小。</summary>
    public static string HumanSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
