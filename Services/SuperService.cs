using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// super 分区（Android 动态分区 / liblp）解析、合包与逻辑分区提取。
///
/// 磁盘布局（字节）：
///   [0, 4096)                                  保留区
///   [4096, 8192)                               主 geometry
///   [8192, 12288)                              备份 geometry
///   12288 + metadata_max_size * slot           主 metadata 各槽
///   12288 + metadata_max_size * slot_count ... 备份 metadata 各槽
///   alignUp(以上总和, alignment)               逻辑分区数据区
/// </summary>
public static class SuperService
{
    // ---------------- liblp 常量 ----------------
    public const uint GeometryMagic = 0x616c4467;
    public const uint HeaderMagic   = 0x414C5030;

    public const int ReservedBytes = 4096;
    public const int GeometrySize  = 4096;
    public const int HeaderSize    = 256;   // LpMetadataHeader（minor_version >= 2）
    public const int HeaderSizeV1  = 128;   // LpMetadataHeaderV1_0（minor_version < 2）
    public const int SectorSize    = 512;

    public const int PartitionEntrySize   = 52;
    public const int ExtentEntrySize      = 24;
    public const int GroupEntrySize       = 48;
    public const int BlockDeviceEntrySize = 64;

    public const int MajorVersion           = 10;
    public const int MinorVersion           = 2;
    public const int MinorVerExpandedHeader = 2;

    public const int AttrReadOnly     = 1;
    public const int AttrSlotSuffixed = 2;
    public const int AttrUpdated      = 4;
    public const int AttrDisabled     = 8;

    public const int TargetLinear = 0;
    public const int TargetZero   = 1;

    public const uint HeaderFlagVirtualAb      = 0x1;
    public const uint HeaderFlagOverlaysActive = 0x2;

    public const int  DefaultMetadataMaxSize   = 65536;
    public const int  DefaultMetadataSlotCount = 2;
    public const int  DefaultLogicalBlockSize  = 4096;
    public const long DefaultAlignment         = 1048576;

    public const string SuperPartitionName = "super";
    public const string DefaultGroupName   = "default";

    // ---------------- 数据结构 ----------------

    public sealed class SuperGeometry
    {
        public uint MetadataMaxSize   { get; set; } = DefaultMetadataMaxSize;
        public uint MetadataSlotCount { get; set; } = DefaultMetadataSlotCount;
        public uint LogicalBlockSize  { get; set; } = DefaultLogicalBlockSize;

        public long TotalMetadataSize =>
            ReservedBytes + (GeometrySize + (long)MetadataMaxSize * MetadataSlotCount) * 2;

        public static long PrimaryMetadataOffset(SuperGeometry g, int slot) =>
            ReservedBytes + GeometrySize * 2L + (long)g.MetadataMaxSize * slot;

        public static long BackupMetadataOffset(SuperGeometry g, int slot) =>
            ReservedBytes + GeometrySize * 2L + (long)g.MetadataMaxSize * g.MetadataSlotCount +
            (long)g.MetadataMaxSize * slot;
    }

    public sealed class SuperInfo
    {
        public SuperGeometry Geometry { get; set; } = new();

        public List<LogicalPartition> Partitions { get; } = new();
        public List<string>           Groups     { get; } = new();

        public uint HeaderFlags        { get; set; }
        public int  MinorVersion       { get; set; } = SuperService.MinorVersion;
        public long FirstLogicalSector { get; set; }
        public long Alignment          { get; set; } = DefaultAlignment;
        public long AlignmentOffset    { get; set; }
        public long DeviceSize         { get; set; }
        public long FileLength         { get; set; }

        public string BlockDeviceName  { get; set; } = SuperPartitionName;
        public uint   BlockDeviceFlags { get; set; }

        public int    SlotUsed { get; set; }
        public string CopyUsed { get; set; } = "主";

        public long PartitionTotalSize => Partitions.Sum(p => p.Size);

        public LogicalPartition? Find(string name) =>
            Partitions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public bool IsVirtualAb => (HeaderFlags & HeaderFlagVirtualAb) != 0;
    }

    public sealed class BuildRequest
    {
        public string OutputPath { get; set; } = "";

        /// <summary>待打包分区（Name + ImagePath，ImagePath 支持 raw 与 sparse）。</summary>
        public List<LogicalPartition> Partitions { get; } = new();

        /// <summary>可选模板 super.img：继承 geometry、组、对齐、设备大小等。</summary>
        public SuperInfo? Template { get; set; }

        /// <summary>可选：手工指定 super 总大小（字节）。</summary>
        public long? DeviceSize { get; set; }
    }

    public sealed class BuildResult
    {
        public long DeviceSize     { get; set; }
        public long DataSize       { get; set; }
        public int  PartitionCount { get; set; }
        public bool UsedTemplate   { get; set; }
    }

    // ---------------- 加载 ----------------

    /// <summary>判断文件是否是 super 镜像（检查 geometry magic）。</summary>
    public static bool IsSuperImage(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return TryReadGeometry(fs, ReservedBytes, null) != null
                || TryReadGeometry(fs, ReservedBytes + GeometrySize, null) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 super 镜像，返回分区表等信息。</summary>
    public static SuperInfo Load(string path, Action<string>? log = null)
    {
        using var fs = File.OpenRead(path);

        var geometry = TryReadGeometry(fs, ReservedBytes, log)
                       ?? TryReadGeometry(fs, ReservedBytes + GeometrySize, log);

        if (geometry == null)
            throw new InvalidDataException("未找到合法的 super 几何信息（geometry magic 不匹配）。");

        // 依次尝试：主 metadata → 备用槽 → 备份副本
        var candidates = new List<(bool Primary, int Slot)>();
        for (int slot = 0; slot < geometry.MetadataSlotCount; slot++) candidates.Add((true, slot));
        for (int slot = 0; slot < geometry.MetadataSlotCount; slot++) candidates.Add((false, slot));

        SuperInfo? parsed = null;
        foreach (var (primary, slot) in candidates)
        {
            string text = $"{(primary ? "主" : "备份")} metadata slot{slot}";
            try
            {
                var info = ParseMetadata(fs, geometry, primary, slot);
                if (info == null) continue;

                info.Geometry = geometry;
                info.FileLength = fs.Length;
                info.SlotUsed = slot;
                info.CopyUsed = primary ? "主" : "备份";
                parsed = info;
                log?.Invoke($"已读取 {text}。");
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  {text} 不可用：{ex.Message}");
            }
        }

        if (parsed == null)
            throw new InvalidDataException("所有 metadata 副本都不可读或已损坏。");

        return parsed;
    }

    private static SuperGeometry? TryReadGeometry(FileStream fs, long offset, Action<string>? log)
    {
        if (offset + GeometrySize > fs.Length) return null;

        var buf = new byte[GeometrySize];
        fs.Position = offset;
        if (ReadFully(fs, buf, 0, 16) != 16) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != GeometryMagic) return null;

        uint structSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        if (structSize < 48 || structSize > GeometrySize) return null;

        if (ReadFully(fs, buf, 16, (int)structSize - 16) != (int)structSize - 16) return null;

        uint maxSize    = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40));
        uint slotCount  = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(44));
        uint blockSize  = structSize >= 52 ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(48)) : SectorSize;

        if (maxSize == 0 || slotCount == 0 || slotCount > 8 || maxSize % SectorSize != 0) return null;

        // 校验 geometry 的 SHA256（不致命，仅提示）
        var hashInput = new byte[structSize];
        Array.Copy(buf, hashInput, (int)structSize);
        hashInput.AsSpan(8, 32).Clear();
        var hash = SHA256.HashData(hashInput);
        if (!hash.AsSpan().SequenceEqual(buf.AsSpan(8, 32)))
            log?.Invoke("geometry 校验和不匹配，仍按读到的参数继续。");

        return new SuperGeometry
        {
            MetadataMaxSize   = maxSize,
            MetadataSlotCount = slotCount,
            LogicalBlockSize  = blockSize == 0 ? (uint)SectorSize : blockSize,
        };
    }

    // ---------------- metadata 解析 ----------------

    private static SuperInfo? ParseMetadata(FileStream fs, SuperGeometry geometry, bool primary, int slot)
    {
        long offset = primary
            ? SuperGeometry.PrimaryMetadataOffset(geometry, slot)
            : SuperGeometry.BackupMetadataOffset(geometry, slot);

        if (offset + geometry.MetadataMaxSize > fs.Length) return null;

        // 先读 V1.0 头部，拿到 header_size / tables_size
        var head = new byte[HeaderSizeV1];
        fs.Position = offset;
        if (ReadFully(fs, head, 0, HeaderSizeV1) != HeaderSizeV1) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != HeaderMagic) return null;

        int  minorVersion = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
        uint headerSize   = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        uint tablesSize   = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));

        int expectedHeader = minorVersion >= MinorVerExpandedHeader ? HeaderSize : HeaderSizeV1;
        if (headerSize != expectedHeader)
            throw new InvalidDataException($"header_size={headerSize}，与版本 {MajorVersion}.{minorVersion} 期望的 {expectedHeader} 不符。");
        if (tablesSize == 0 || (long)headerSize + tablesSize > geometry.MetadataMaxSize)
            throw new InvalidDataException($"元数据表大小非法（tables_size={tablesSize}）。");

        var blob = new byte[headerSize + tablesSize];
        Array.Copy(head, blob, HeaderSizeV1);
        if (headerSize > HeaderSizeV1)
        {
            fs.Position = offset + HeaderSizeV1;
            ReadFully(fs, blob, HeaderSizeV1, (int)headerSize - HeaderSizeV1);
        }

        fs.Position = offset + headerSize;
        ReadFully(fs, blob, (int)headerSize, (int)tablesSize);

        // header 校验和
        var headerCopy = new byte[headerSize];
        Array.Copy(blob, headerCopy, (int)headerSize);
        headerCopy.AsSpan(12, 32).Clear();
        if (!SHA256.HashData(headerCopy).AsSpan().SequenceEqual(blob.AsSpan(12, 32)))
            throw new InvalidDataException("metadata header 校验和不匹配（该副本已损坏）。");

        var tables = blob.AsSpan((int)headerSize, (int)tablesSize);

        var info = new SuperInfo
        {
            MinorVersion = minorVersion,
            HeaderFlags  = headerSize >= 132 ? BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(128)) : 0,
        };

        var (pOff, pNum, pStride) = Descriptor(blob, 80);
        var (xOff, xNum, xStride) = Descriptor(blob, 92);
        var (gOff, gNum, gStride) = Descriptor(blob, 104);
        var (bOff, bNum, bStride) = Descriptor(blob, 116);

        // 组表
        for (uint i = 0; i < gNum; i++)
        {
            long entry = (long)gOff + (long)i * gStride;
            if (entry + GroupEntrySize > tablesSize) throw new InvalidDataException("groups 表越界。");
            info.Groups.Add(ReadCString(tables.Slice((int)entry, 36)));
        }

        // 分区表 + extent 表
        for (uint i = 0; i < pNum; i++)
        {
            long entry = (long)pOff + (long)i * pStride;
            if (entry + PartitionEntrySize > tablesSize) throw new InvalidDataException("partitions 表越界。");

            var e = tables.Slice((int)entry, PartitionEntrySize);
            int attributes      = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[36..]);
            uint firstExtent    = BinaryPrimitives.ReadUInt32LittleEndian(e[40..]);
            uint numExtents     = BinaryPrimitives.ReadUInt32LittleEndian(e[44..]);
            uint groupIndex     = BinaryPrimitives.ReadUInt32LittleEndian(e[48..]);

            var part = new LogicalPartition
            {
                Name         = ReadCString(e[..36]),
                Group        = groupIndex < info.Groups.Count ? info.Groups[(int)groupIndex] : DefaultGroupName,
                ReadOnly     = (attributes & AttrReadOnly) != 0,
                SlotSuffixed = (attributes & AttrSlotSuffixed) != 0,
                Updated      = (attributes & AttrUpdated) != 0,
                Disabled     = (attributes & AttrDisabled) != 0,
            };

            long firstByte = long.MaxValue;
            for (uint k = 0; k < numExtents; k++)
            {
                long xEntry = (long)xOff + (long)(firstExtent + k) * xStride;
                if (xEntry + ExtentEntrySize > tablesSize) throw new InvalidDataException("extents 表越界。");

                var x = tables.Slice((int)xEntry, ExtentEntrySize);
                var extent = new ExtentInfo
                {
                    Sectors      = (long)BinaryPrimitives.ReadUInt64LittleEndian(x),
                    TargetType   = (int)BinaryPrimitives.ReadUInt32LittleEndian(x[8..]),
                    TargetData   = (long)BinaryPrimitives.ReadUInt64LittleEndian(x[12..]),
                    TargetSource = (int)BinaryPrimitives.ReadUInt32LittleEndian(x[20..]),
                };

                part.Extents.Add(extent);
                part.Size += extent.ByteLength;
                if (extent.IsLinear && extent.ByteOffset < firstByte) firstByte = extent.ByteOffset;
            }

            part.DataOffset = firstByte == long.MaxValue ? 0 : firstByte;
            info.Partitions.Add(part);
        }

        // super 块设备（第一项）
        if (bNum > 0 && (long)bOff + BlockDeviceEntrySize <= tablesSize)
        {
            var b = tables.Slice((int)bOff, BlockDeviceEntrySize);
            info.FirstLogicalSector = (long)BinaryPrimitives.ReadUInt64LittleEndian(b);
            uint alignment          = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]);
            info.AlignmentOffset    = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
            info.DeviceSize         = (long)BinaryPrimitives.ReadUInt64LittleEndian(b[16..]);
            info.BlockDeviceName    = ReadCString(b.Slice(24, 36));
            info.BlockDeviceFlags   = BinaryPrimitives.ReadUInt32LittleEndian(b[60..]);
            info.Alignment          = alignment == 0 ? DefaultAlignment : alignment;
        }

        _ = bStride;
        return info;
    }

    private static (uint Offset, uint NumEntries, uint EntrySize) Descriptor(byte[] blob, int at)
    {
        if (at + 12 > blob.Length) return (0, 0, 0);
        return (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at)),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at + 8)));
    }

    private static string ReadCString(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        if (end < 0) end = raw.Length;
        return Encoding.ASCII.GetString(raw[..end]);
    }

    // ---------------- 通用工具 ----------------

    internal static long AlignUp(long value, long alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    internal static string FormatSize(long bytes) => LogicalPartition.FormatSize(bytes);

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static void WriteAt(Stream stream, long offset, byte[] data)
    {
        stream.Position = offset;
        stream.Write(data, 0, data.Length);
    }

    private static void WriteZeros(Stream stream, long count)
    {
        if (count <= 0) return;

        var zeros = new byte[Math.Min(count, 1 << 20)];
        long remaining = count;
        while (remaining > 0)
        {
            int n = (int)Math.Min(zeros.Length, remaining);
            stream.Write(zeros, 0, n);
            remaining -= n;
        }
    }

    // ---------------- 提取逻辑分区 ----------------

    /// <summary>把 super 里的某个逻辑分区提取成 raw 镜像。</summary>
    public static long ExtractPartition(string superPath, SuperInfo info, string partitionName, string outputPath,
                                        Action<double>? progress = null, CancellationToken ct = default)
    {
        var part = info.Find(partitionName)
                   ?? throw new InvalidDataException($"super 中没有名为 {partitionName} 的分区。");
        return ExtractPartition(superPath, part, outputPath, progress, ct);
    }

    /// <summary>把 super 里的某个逻辑分区提取成 raw 镜像，返回写入字节数。</summary>
    public static long ExtractPartition(string superPath, LogicalPartition part, string outputPath,
                                        Action<double>? progress = null, CancellationToken ct = default)
    {
        long total = part.Size;
        long written = 0;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var src = new FileStream(superPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        var buffer = new byte[1 << 20];

        foreach (var extent in part.Extents)
        {
            ct.ThrowIfCancellationRequested();

            if (extent.IsZero)
            {
                WriteZeros(dst, extent.ByteLength);
                written += extent.ByteLength;
                progress?.Invoke(total > 0 ? (double)written / total : 0);
                continue;
            }

            if (!extent.IsLinear)
                throw new InvalidDataException($"不支持的 extent 类型 {extent.TargetType}。");

            src.Position = extent.ByteOffset;
            long remaining = extent.ByteLength;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, remaining);
                int read = ReadFully(src, buffer, 0, n);
                if (read <= 0) throw new EndOfStreamException("super 文件比分区表声明的更短。");
                dst.Write(buffer, 0, read);
                remaining -= read;
                written   += read;
                progress?.Invoke(total > 0 ? (double)written / total : 0);
            }
        }

        dst.Flush();
        return written;
    }

    // ---------------- 合包 ----------------

    /// <summary>
    /// 把若干分区镜像打包成一个 super.img（liblp 格式）。
    /// 提供模板时会继承模板的 geometry / 组 / 对齐 / 总大小，兼容性最好。
    /// </summary>
    public static BuildResult Build(BuildRequest request, Action<string>? log = null,
                                    Action<double>? progress = null, CancellationToken ct = default)
    {
        var items = request.Partitions
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.ImagePath))
            .ToList();

        if (items.Count == 0) throw new InvalidOperationException("没有可合包的分区镜像。");
        if (string.IsNullOrWhiteSpace(request.OutputPath)) throw new InvalidOperationException("未指定输出文件。");

        foreach (var item in items)
            if (!File.Exists(item.ImagePath))
                throw new FileNotFoundException($"分区 {item.Name} 的镜像不存在：{item.ImagePath}");

        var template = request.Template;

        var geometry = new SuperGeometry
        {
            MetadataMaxSize   = template?.Geometry.MetadataMaxSize   ?? DefaultMetadataMaxSize,
            MetadataSlotCount = template?.Geometry.MetadataSlotCount ?? DefaultMetadataSlotCount,
            LogicalBlockSize  = template?.Geometry.LogicalBlockSize  ?? DefaultLogicalBlockSize,
        };

        long alignment = template?.Alignment is > 0 ? template!.Alignment : DefaultAlignment;
        long dataStart = AlignUp(geometry.TotalMetadataSize, alignment);
        long firstLogicalSector = dataStart / SectorSize;

        // ---- 组表：default 恒为 0 号 ----
        var groupNames = new List<string> { DefaultGroupName };
        var groupMax   = new Dictionary<string, long>(StringComparer.Ordinal) { [DefaultGroupName] = 0 };

        if (template != null)
        {
            foreach (var group in template.Groups)
            {
                if (string.IsNullOrEmpty(group) || groupNames.Contains(group)) continue;
                groupNames.Add(group);
                groupMax[group] = 0;
            }
        }

        // ---- 依次排布每个分区 ----
        long cursor = dataStart;
        var extents = new List<ExtentInfo>();
        var built   = new List<LogicalPartition>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(item.Name))
                throw new InvalidOperationException($"分区名重复：{item.Name}。");

            long rawSize = SparseImage.GetRawLength(item.ImagePath);
            if (rawSize <= 0) throw new InvalidDataException($"{item.Name}：镜像内容为空。");

            long padded = AlignUp(rawSize, alignment);
            long sectorAligned = AlignUp(rawSize, SectorSize);
            if (padded < sectorAligned) padded = sectorAligned;

            cursor = AlignUp(cursor, alignment);

            var clone = new LogicalPartition
            {
                Name         = item.Name,
                ImagePath    = item.ImagePath,
                ImageSize    = rawSize,
                Size         = padded,
                DataOffset   = cursor,
                Group        = groupNames.Contains(item.Group) ? item.Group : DefaultGroupName,
                ReadOnly     = item.ReadOnly,
                SlotSuffixed = item.SlotSuffixed,
                Updated      = item.Updated,
                Disabled     = item.Disabled,
            };

            clone.Extents.Add(new ExtentInfo
            {
                Sectors      = padded / SectorSize,
                TargetType   = TargetLinear,
                TargetData   = cursor / SectorSize,
                TargetSource = 0,
            });

            extents.Add(clone.Extents[0]);
            built.Add(clone);
            cursor += padded;

            log?.Invoke($"  排布 {clone.Name,-16} {FormatSize(rawSize),10} → {FormatSize(padded),10}  @ {FormatSize(clone.DataOffset)}");
        }

        long needSize = AlignUp(cursor, SectorSize);

        long deviceSize;
        if (request.DeviceSize is > 0)
        {
            deviceSize = request.DeviceSize.Value;
            if (deviceSize < needSize)
                throw new InvalidOperationException(
                    $"指定的 super 大小 {FormatSize(deviceSize)} 小于所需 {FormatSize(needSize)}。");
        }
        else if (template != null && template.DeviceSize >= needSize)
        {
            deviceSize = template.DeviceSize;
        }
        else
        {
            if (template != null)
                log?.Invoke($"模板总大小 {FormatSize(template.DeviceSize)} 小于所需 {FormatSize(needSize)}，按所需大小输出。");
            deviceSize = needSize;
        }

        deviceSize = AlignUp(deviceSize, SectorSize);

        var metadataBlob = SerializeMetadata(
            geometry, built, groupNames, groupMax, extents,
            template?.HeaderFlags ?? 0,
            firstLogicalSector, alignment, template?.AlignmentOffset ?? 0,
            deviceSize,
            template?.BlockDeviceName ?? SuperPartitionName,
            template?.BlockDeviceFlags ?? 0);

        if (metadataBlob.Length > geometry.MetadataMaxSize)
            throw new InvalidOperationException(
                $"元数据 {metadataBlob.Length} 字节超过 metadata_max_size（{geometry.MetadataMaxSize}），分区数量过多。");

        log?.Invoke($"geometry: metadata_max_size={geometry.MetadataMaxSize}, slot_count={geometry.MetadataSlotCount}, " +
                    $"logical_block_size={geometry.LogicalBlockSize}");
        log?.Invoke($"对齐 {FormatSize(alignment)}，数据区起始 {FormatSize(dataStart)}，super 总大小 {FormatSize(deviceSize)}");

        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        long totalBytes = built.Sum(p => p.Size);
        long writtenTotal = 0;

        using (var fs = new FileStream(request.OutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
        {
            // 整体预留大小（Windows 上扩展出来的部分是 0）
            fs.SetLength(deviceSize);

            var geometryBlob = SerializeGeometry(geometry);
            WriteAt(fs, ReservedBytes, geometryBlob);
            WriteAt(fs, ReservedBytes + GeometrySize, geometryBlob);

            for (int slot = 0; slot < geometry.MetadataSlotCount; slot++)
            {
                WriteAt(fs, SuperGeometry.PrimaryMetadataOffset(geometry, slot), metadataBlob);
                WriteAt(fs, SuperGeometry.BackupMetadataOffset(geometry, slot), metadataBlob);
            }

            log?.Invoke($"已写入 geometry 与 {geometry.MetadataSlotCount * 2} 份 metadata。");

            foreach (var part in built)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"写入 {part.Name} …");

                fs.Position = part.DataOffset;
                long captured = writtenTotal;

                long copied = CopyImage(part.ImagePath, fs, part.Size,
                    done => progress?.Invoke(totalBytes > 0 ? (double)(captured + done) / totalBytes : 0), ct);

                writtenTotal = captured + copied;
                progress?.Invoke(totalBytes > 0 ? (double)writtenTotal / totalBytes : 0);
            }

            fs.Flush();
        }

        log?.Invoke($"合包完成：{built.Count} 个分区，super 大小 {FormatSize(deviceSize)} → {request.OutputPath}");

        return new BuildResult
        {
            DeviceSize     = deviceSize,
            DataSize       = totalBytes,
            PartitionCount = built.Count,
            UsedTemplate   = template != null,
        };
    }

    /// <summary>把镜像写入目标流（自动展开 sparse），并补齐到 extent 大小。</summary>
    private static long CopyImage(string imagePath, Stream dest, long paddedSize,
                                  Action<long>? onProgress, CancellationToken ct)
    {
        long raw;

        if (SparseImage.IsSparse(imagePath))
        {
            raw = SparseImage.Decompress(imagePath, dest,
                p => onProgress?.Invoke((long)(p * paddedSize)), ct);
        }
        else
        {
            using var src = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var buffer = new byte[1 << 20];
            raw = 0;

            while (raw < src.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, src.Length - raw);
                int read = src.Read(buffer, 0, n);
                if (read <= 0) break;
                dest.Write(buffer, 0, read);
                raw += read;
                onProgress?.Invoke(raw);
            }
        }

        if (raw > paddedSize)
            throw new InvalidDataException($"镜像展开后 {FormatSize(raw)} 超过为其分配的空间 {FormatSize(paddedSize)}。");

        WriteZeros(dest, paddedSize - raw);
        onProgress?.Invoke(paddedSize);
        return raw;
    }

    // ---------------- 序列化 ----------------

    private static byte[] SerializeGeometry(SuperGeometry geometry)
    {
        var buf = new byte[GeometrySize];

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 52);            // struct_size
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), geometry.MetadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), geometry.MetadataSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(48), geometry.LogicalBlockSize);

        var payload = new byte[52];
        Array.Copy(buf, payload, 52);
        SHA256.HashData(payload).CopyTo(buf.AsSpan(8, 32));

        return buf;
    }

    private static byte[] SerializeMetadata(
        SuperGeometry geometry,
        List<LogicalPartition> partitions,
        List<string> groups,
        Dictionary<string, long> groupMax,
        List<ExtentInfo> extents,
        uint headerFlags,
        long firstLogicalSector,
        long alignment,
        long alignmentOffset,
        long deviceSize,
        string deviceName,
        uint deviceFlags)
    {
        int pBytes = partitions.Count * PartitionEntrySize;
        int xBytes = extents.Count * ExtentEntrySize;
        int gBytes = groups.Count * GroupEntrySize;

        var tables = new byte[pBytes + xBytes + gBytes + BlockDeviceEntrySize];
        int pOff = 0;
        int xOff = pBytes;
        int gOff = pBytes + xBytes;
        int bOff = pBytes + xBytes + gBytes;

        // 分区表
        for (int i = 0; i < partitions.Count; i++)
        {
            var part = partitions[i];
            var span = tables.AsSpan(pOff + i * PartitionEntrySize, PartitionEntrySize);

            WriteCString(span[..36], part.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)Attributes(part));
            BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)i);                    // first_extent_index
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)part.Extents.Count);   // num_extents
            BinaryPrimitives.WriteUInt32LittleEndian(span[48..], (uint)GroupIndex(groups, part.Group));
        }

        // extent 表
        for (int i = 0; i < extents.Count; i++)
        {
            var extent = extents[i];
            var span = tables.AsSpan(xOff + i * ExtentEntrySize, ExtentEntrySize);

            BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)extent.Sectors);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)extent.TargetType);
            BinaryPrimitives.WriteUInt64LittleEndian(span[12..], (ulong)extent.TargetData);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)extent.TargetSource);
        }

        // 组表
        for (int i = 0; i < groups.Count; i++)
        {
            var span = tables.AsSpan(gOff + i * GroupEntrySize, GroupEntrySize);
            WriteCString(span[..36], groups[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[36..], 0);                          // flags
            BinaryPrimitives.WriteUInt64LittleEndian(span[40..],
                (ulong)(groupMax.TryGetValue(groups[i], out var max) ? max : 0));             // maximum_size
        }

        // super 块设备
        {
            var span = tables.AsSpan(bOff, BlockDeviceEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)firstLogicalSector);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)alignmentOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)deviceSize);
            WriteCString(span.Slice(24, 36), deviceName);
            BinaryPrimitives.WriteUInt32LittleEndian(span[60..], deviceFlags);
        }

        // 头部
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)tables.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), headerFlags);

        // 表描述符：offset 相对于头部之后
        WriteDescriptor(header, 80, 0, partitions.Count, PartitionEntrySize);
        WriteDescriptor(header, 92, xOff, extents.Count, ExtentEntrySize);
        WriteDescriptor(header, 104, gOff, groups.Count, GroupEntrySize);
        WriteDescriptor(header, 116, bOff, 1, BlockDeviceEntrySize);

        SHA256.HashData(tables).CopyTo(header.AsSpan(48, 32));

        var headerForHash = new byte[HeaderSize];
        Array.Copy(header, headerForHash, HeaderSize);
        headerForHash.AsSpan(12, 32).Clear();
        SHA256.HashData(headerForHash).CopyTo(header.AsSpan(12, 32));

        var blob = new byte[HeaderSize + tables.Length];
        Array.Copy(header, blob, HeaderSize);
        Array.Copy(tables, 0, blob, HeaderSize, tables.Length);
        return blob;
    }

    private static void WriteDescriptor(byte[] header, int at, int offset, int numEntries, int entrySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 4), (uint)numEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 8), (uint)entrySize);
    }

    private static int Attributes(LogicalPartition part)
    {
        int attributes = 0;
        if (part.ReadOnly)     attributes |= AttrReadOnly;
        if (part.SlotSuffixed) attributes |= AttrSlotSuffixed;
        if (part.Updated)      attributes |= AttrUpdated;
        if (part.Disabled)     attributes |= AttrDisabled;
        return attributes;
    }

    private static int GroupIndex(List<string> groups, string name)
    {
        int index = groups.IndexOf(name);
        return index < 0 ? 0 : index;
    }

    private static void WriteCString(Span<byte> destination, string value)
    {
        destination.Clear();

        var bytes = Encoding.ASCII.GetBytes(value);
        int count = Math.Min(bytes.Length, destination.Length - 1);
        bytes.AsSpan(0, count).CopyTo(destination);
    }
}
