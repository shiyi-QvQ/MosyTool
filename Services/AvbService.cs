using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Mosytool.Services;

/// <summary>
/// Android Verified Boot（AVB）辅助：识别 / 修补 vbmeta 镜像，关闭 verity 与 verification 校验。
///
/// 原理：vbmeta 头部（AvbVBMetaHeader，256 字节）偏移 120 处是一个 u32 的 flags：
///   bit0 (1) = HASHTREE_DISABLED            关闭 dm-verity 树校验
///   bit1 (2) = VERIFICATION_DISABLED        关闭 vbmeta 签名校验
/// 把这两位都置 1（flags |= 3）并重新写回，即等价于
///   fastboot --disable-verity --disable-verification flash vbmeta vbmeta.img
/// 好处是不依赖 fastboot 版本、也不需要在刷写时额外传参。
/// </summary>
public static class AvbService
{
    /// <summary>vbmeta magic："AVB0" 小端读出的 u32。</summary>
    public const uint VbmetaMagic = 0x00425641;

    public const int HeaderSize = 256;

    /// <summary>flags 在头部中的偏移。</summary>
    public const int FlagsOffset = 120;

    /// <summary>rollback_index_location 偏移。</summary>
    public const int RollbackLocationOffset = 124;

    public const uint FlagHashtreeDisabled     = 0x1;
    public const uint FlagVerificationDisabled = 0x2;

    public sealed class VbmetaInfo
    {
        public string Path          { get; set; } = "";
        public uint   MajorVersion  { get; set; }
        public uint   MinorVersion  { get; set; }
        public uint   AlgorithmType { get; set; }
        public uint   Flags         { get; set; }
        public ulong  RollbackIndex { get; set; }
        public ulong  AuthBlockSize { get; set; }
        public ulong  AuxBlockSize  { get; set; }

        public bool VerityDisabled         => (Flags & FlagHashtreeDisabled) != 0;
        public bool VerificationDisabled   => (Flags & FlagVerificationDisabled) != 0;
        public bool FullyDisabled          => (Flags & (FlagHashtreeDisabled | FlagVerificationDisabled)) == 3;

        public string AlgorithmText => AlgorithmType switch
        {
            0 => "NONE（未签名）",
            1 => "SHA256_RSA2048",
            2 => "SHA256_RSA4096",
            3 => "SHA256_RSA8192",
            4 => "SHA512_RSA2048",
            5 => "SHA512_RSA4096",
            6 => "SHA512_RSA8192",
            _ => $"未知({AlgorithmType})",
        };

        public string FlagsText
        {
            get
            {
                if (Flags == 0) return "0（校验已开启）";
                var parts = new System.Collections.Generic.List<string>();
                if (VerityDisabled) parts.Add("关闭 verity");
                if (VerificationDisabled) parts.Add("关闭 verification");
                return $"{Flags}（{string.Join(" / ", parts)}）";
            }
        }
    }

    /// <summary>判断文件是否是 vbmeta 镜像。</summary>
    public static bool IsVbmeta(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == VbmetaMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 vbmeta 头部信息。</summary>
    public static VbmetaInfo Load(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < HeaderSize) throw new InvalidDataException("文件太小，不是合法的 vbmeta 镜像。");

        var head = new byte[HeaderSize];
        fs.Position = 0;
        if (!ReadFully(fs, head, 0, HeaderSize) || BinaryPrimitives.ReadUInt32LittleEndian(head) != VbmetaMagic)
            throw new InvalidDataException("vbmeta magic 不匹配（应为 AVB0）。");

        return new VbmetaInfo
        {
            Path          = path,
            MajorVersion  = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)),
            MinorVersion  = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8)),
            AuthBlockSize = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(12)),
            AuxBlockSize  = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20)),
            AlgorithmType = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(28)),
            RollbackIndex = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(112)),
            Flags         = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(FlagsOffset)),
        };
    }

    /// <summary>
    /// 就地把 vbmeta 的 verity / verification 校验位写进 flags。
    /// 返回新的 flags。原文件不会被备份，调用方需要时自行处理。
    /// </summary>
    public static uint Patch(string path, bool disableVerity = true, bool disableVerification = true)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (fs.Length < HeaderSize) throw new InvalidDataException("文件太小，不是合法的 vbmeta 镜像。");

        Span<byte> magic = stackalloc byte[4];
        fs.Position = 0;
        if (fs.Read(magic) != 4 || BinaryPrimitives.ReadUInt32LittleEndian(magic) != VbmetaMagic)
            throw new InvalidDataException("vbmeta magic 不匹配（应为 AVB0）。");

        fs.Position = FlagsOffset;
        Span<byte> raw = stackalloc byte[4];
        if (fs.Read(raw) != 4) throw new EndOfStreamException("读取 vbmeta flags 失败。");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw);

        if (disableVerity)         flags |= FlagHashtreeDisabled;
        if (disableVerification)   flags |= FlagVerificationDisabled;

        BinaryPrimitives.WriteUInt32LittleEndian(raw, flags);
        fs.Position = FlagsOffset;
        fs.Write(raw);
        fs.Flush();

        return flags;
    }

    /// <summary>恢复校验：把 verity / verification 位清 0。</summary>
    public static uint Restore(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        if (fs.Length < HeaderSize) throw new InvalidDataException("文件太小，不是合法的 vbmeta 镜像。");

        fs.Position = FlagsOffset;
        Span<byte> raw = stackalloc byte[4];
        if (fs.Read(raw) != 4) throw new EndOfStreamException("读取 vbmeta flags 失败。");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw) & ~(FlagHashtreeDisabled | FlagVerificationDisabled);
        BinaryPrimitives.WriteUInt32LittleEndian(raw, flags);
        fs.Position = FlagsOffset;
        fs.Write(raw);
        fs.Flush();

        return flags;
    }

    /// <summary>
    /// 生成一个「未签名 + 关闭校验」的最小 vbmeta 镜像（256 字节头部，无描述符、无签名）。
    /// 适用于设备本来就没有真正校验 vbmeta、只要求存在该分区的场景。
    /// </summary>
    public static void CreateDisabledVbmeta(string outputPath)
    {
        var header = new byte[HeaderSize];

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), VbmetaMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);   // required_libavb_version_major
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);   // required_libavb_version_minor
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), 0);  // authentication_data_block_size
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20), 0);  // auxiliary_data_block_size
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 0);  // algorithm_type = NONE
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(112), 0); // rollback_index
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(FlagsOffset),
            FlagHashtreeDisabled | FlagVerificationDisabled);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RollbackLocationOffset), 0);

        var release = Encoding.ASCII.GetBytes("avbtool 1.2.0");
        release.AsSpan(0, Math.Min(release.Length, 47)).CopyTo(header.AsSpan(128));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(outputPath, header);
    }

    /// <summary>
    /// 用 fastboot 刷 vbmeta 并顺带下发 --disable-verity / --disable-verification。
    /// 部分机型（尤其 bootloader 未解锁）会拒绝该参数，此时请改用 Patch 后的镜像。
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> FlashDisabledVbmetaAsync(
        string partition, string imagePath, Action<string> log)
    {
        var args = $"flash --disable-verity --disable-verification {partition} \"{imagePath}\"";
        log($"> fastboot {args}");
        return await FastbootService.RunRawAsync(args, log);
    }

    private static bool ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total == count;
    }
}
