using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Mosytool.Models;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace Mosytool.Services;

public static class PayloadService
{
    private const uint MagicValue = 0x43724155; // "CrAU"

    /// <summary>
    /// 若输入是压缩包（zip），自动解压并提取其中的 payload.bin，返回实际 payload 路径；否则原样返回。
    /// </summary>
    public static string ResolvePayload(string inputPath)
    {
        if (!IsZip(inputPath)) return inputPath;

        var tempDir = Path.Combine(Path.GetTempPath(), "MosyTool_payload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(inputPath, tempDir);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("解压压缩包失败: " + ex.Message);
        }

        var payloadPath = Directory.GetFiles(tempDir, "payload.bin", SearchOption.AllDirectories).FirstOrDefault();
        if (payloadPath is null)
            throw new InvalidDataException("压缩包中未找到 payload.bin。");

        return payloadPath;
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var magic = new byte[4];
            int read = fs.Read(magic, 0, 4);
            return read >= 4 && magic[0] == 0x50 && magic[1] == 0x4B; // "PK"
        }
        catch { return false; }
    }

    /// <summary>解析 payload.bin，返回所有分区信息。</summary>
    public static List<PayloadPartition> Parse(string payloadPath)
    {
        using var fs = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        var magic = ReadBigEndianUInt32(reader);
        if (magic != MagicValue)
            throw new InvalidDataException("不是有效的 payload.bin 文件（magic 不匹配）。");

        var version       = ReadBigEndianUInt64(reader);
        var manifestSize  = ReadBigEndianUInt64(reader);
        uint metaSigSize  = version > 1 ? ReadBigEndianUInt32(reader) : 0;

        var manifest = reader.ReadBytes((int)manifestSize);
        var dataStart = fs.Position + metaSigSize; // 跳过 metadata signature 后是数据区起点

        var partitions = ParseManifest(manifest);
        foreach (var p in partitions)
            p.DataOffset += dataStart;

        return partitions;
    }

    /// <summary>提取单个分区到目标文件。progress: 0.0~1.0。</summary>
    public static void ExtractPartition(string payloadPath, PayloadPartition partition, string outputPath, Action<double>? progress)
    {
        using var src = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        src.Seek(partition.DataOffset, SeekOrigin.Begin);

        switch (partition.OperationType)
        {
            case 0: // REPLACE —— 未压缩，直接复制
                CopyBytes(src, dst, partition.DataLength, progress);
                break;

            case 1: // REPLACE_BZ —— bzip2 压缩
                using (var bz = BZip2Stream.Create(src, SharpCompress.Compressors.CompressionMode.Decompress, false, false, false))
                    bz.CopyTo(dst);
                progress?.Invoke(1.0);
                break;

            case 8: // REPLACE_XZ —— xz 压缩
                using (var xz = new XZStream(src))
                    xz.CopyTo(dst);
                progress?.Invoke(1.0);
                break;

            default:
                throw new NotSupportedException($"分区 {partition.Name} 的类型 [{partition.TypeName}] 需要增量源镜像，无法单独解包。");
        }
    }

    // ---------- 数据提取 ----------

    private static void CopyBytes(FileStream src, FileStream dst, long length, Action<double>? progress)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        long total     = length;

        while (remaining > 0)
        {
            int read = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            dst.Write(buffer, 0, read);
            remaining -= read;

            if (total > 0)
                progress?.Invoke((total - remaining) / (double)total);
        }
        progress?.Invoke(1.0);
    }

    // ---------- header 大端序读取 ----------

    private static uint ReadBigEndianUInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static ulong ReadBigEndianUInt64(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt64(bytes, 0);
    }

    // ---------- protobuf 解析 ----------

    private static List<PayloadPartition> ParseManifest(byte[] data)
    {
        var partitions = new List<PayloadPartition>();
        int pos = 0;

        while (pos < data.Length)
        {
            (ulong tag, pos) = ReadVarint(data, pos);
            int field    = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            if (field == 13 && wireType == 2) // partitions (repeated PartitionUpdate)
            {
                (ulong len, pos) = ReadVarint(data, pos);
                var part = ParsePartitionUpdate(data, pos, (int)len);
                pos += (int)len;
                if (part is not null) partitions.Add(part);
            }
            else
            {
                pos = SkipField(data, pos, wireType);
            }
        }

        return partitions;
    }

    private static PayloadPartition? ParsePartitionUpdate(byte[] data, int start, int length)
    {
        var part = new PayloadPartition();
        bool hasName = false;

        int pos = start;
        int end = start + length;

        while (pos < end)
        {
            (ulong tag, pos) = ReadVarint(data, pos);
            int field    = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            switch (field)
            {
                case 1 when wireType == 2: // partition_name (string)
                    (ulong nlen, pos) = ReadVarint(data, pos);
                    part.Name = Encoding.UTF8.GetString(data, pos, (int)nlen);
                    pos += (int)nlen;
                    hasName = true;
                    break;

                case 7 when wireType == 2: // new_partition_info (PartitionInfo)
                    (ulong ilen, pos) = ReadVarint(data, pos);
                    part.Size = ParsePartitionInfoSize(data, pos, (int)ilen);
                    pos += (int)ilen;
                    break;

                case 8 when wireType == 2: // operations (repeated InstallOperation)
                    (ulong olen, pos) = ReadVarint(data, pos);
                    if (part.OperationType == 0 && part.DataLength == 0)
                        (part.OperationType, part.DataOffset, part.DataLength) = ParseInstallOperation(data, pos, (int)olen);
                    pos += (int)olen;
                    break;

                default:
                    pos = SkipField(data, pos, wireType);
                    break;
            }
        }

        if (!hasName) return null;
        part.TypeName = GetTypeName(part.OperationType);
        return part;
    }

    private static long ParsePartitionInfoSize(byte[] data, int start, int length)
    {
        int pos = start;
        int end = start + length;

        while (pos < end)
        {
            (ulong tag, pos) = ReadVarint(data, pos);
            int field    = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            if (field == 1 && wireType == 0)
            {
                (ulong v, _) = ReadVarint(data, pos);
                return (long)v;
            }
            pos = SkipField(data, pos, wireType);
        }
        return 0;
    }

    private static (int type, long offset, long length) ParseInstallOperation(byte[] data, int start, int length)
    {
        int type = -1;
        long offset = 0;
        long len = 0;

        int pos = start;
        int end = start + length;

        while (pos < end)
        {
            (ulong tag, pos) = ReadVarint(data, pos);
            int field    = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);

            switch (field)
            {
                case 1 when wireType == 0: // type
                    (ulong t, pos) = ReadVarint(data, pos);
                    type = (int)t;
                    break;

                case 2 when wireType == 0: // data_offset
                    (ulong off, pos) = ReadVarint(data, pos);
                    offset = (long)off;
                    break;

                case 3 when wireType == 0: // data_length
                    (ulong dl, pos) = ReadVarint(data, pos);
                    len = (long)dl;
                    break;

                default:
                    pos = SkipField(data, pos, wireType);
                    break;
            }
        }

        return (type, offset, len);
    }

    private static (ulong value, int pos) ReadVarint(byte[] data, int pos)
    {
        ulong result = 0;
        int shift = 0;

        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 64) break;
        }

        return (result, pos);
    }

    private static int SkipField(byte[] data, int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                (_, pos) = ReadVarint(data, pos);
                return pos;
            case 1: // 64-bit
                return pos + 8;
            case 2: // length-delimited
                (ulong len, pos) = ReadVarint(data, pos);
                return pos + (int)len;
            case 5: // 32-bit
                return pos + 4;
            default:
                throw new InvalidDataException($"未知 protobuf wire type: {wireType}");
        }
    }

    private static string GetTypeName(int type) => type switch
    {
        0  => "REPLACE",
        1  => "REPLACE_BZ",
        2  => "MOVE",
        3  => "BSDIFF",
        4  => "SOURCE_COPY",
        5  => "SOURCE_BSDIFF",
        6  => "ZERO",
        7  => "DISCARD",
        8  => "REPLACE_XZ",
        9  => "PUFFDIFF",
        10 => "BROTLI_BSDIFF",
        11 => "ZUCCHINI",
        _  => $"UNKNOWN({type})",
    };
}
