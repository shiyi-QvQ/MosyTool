using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mosytool.Services;

/// <summary>
/// MTK 镜像的 part_hdr_t 解析与 CERT2 哈希覆盖签名。
/// 签名采用 CERT2 哈希覆盖方式（与 sign_mtk_cert.py 一致），无需 RSA 私钥。
/// 目前只服务 LK 修补：不涉及开机 Logo 图片的编解码。
/// </summary>
public static class LogoService
{
    private const uint PartMagic       = 0x58881688;
    private const int  PartHeaderSize  = 512;
    private const uint ImgTypeGroupCert = 0x02u << 24;
    private const uint ImgTypeCert2    = (0x02u << 24) | 0x02;
    private const string OidImageHash        = "2.16.886.2454.2.1";
    private const string OidImageHeaderHash  = "2.16.886.2454.2.4";

    // ============ MTK CERT2 哈希覆盖签名 ============

    /// <summary>
    /// MTK CERT2 哈希覆盖签名，等价 <c>sign_mtk_cert.py -w --legacy</c>：把当前内容的
    /// Header / Image 摘要写进 CERT2 前置覆盖块（原始 DER 原样保留），并按新长度更新 dsize。
    /// </summary>
    public static byte[] SignCert2(byte[] data, Action<string>? log)
    {
        var parts = ParseAllPartHeaders(data);
        var cert2Entry = parts.FirstOrDefault(p => p.Hdr.ImgType == ImgTypeCert2);
        if (cert2Entry is null) throw new InvalidDataException("未找到 CERT2 分区头");

        var (cOff, cBlobOff, _, cHdr) = cert2Entry;
        var der = data[(cBlobOff)..(cBlobOff + (int)cHdr.Dsize)];
        log?.Invoke($"CERT2 偏移 0x{cOff:x}, blob 偏移 0x{cBlobOff:x}, dsize={cHdr.Dsize}");

        // 从原 CERT2 DER 里读出 Image Header Hash 和 Image Hash 的 BIT STRING，确定算法
        byte[]? oldHeaderDigest = FindOidDigest(der, OidImageHeaderHash);
        byte[]? oldImageDigest  = FindOidDigest(der, OidImageHash);
        log?.Invoke($"原 Image Header Hash ({oldHeaderDigest?.Length ?? 0} 字节): {Hex(oldHeaderDigest)}");
        log?.Invoke($"原 Image Hash ({oldImageDigest?.Length ?? 0} 字节): {Hex(oldImageDigest)}");

        // 定位目标镜像分区（CERT2 之前、非 cert 组的分区，即 lk）
        var target = parts.LastOrDefault(p => p.Off < cOff && (p.Hdr.ImgType & 0xff000000) != ImgTypeGroupCert);
        if (target is null) throw new InvalidDataException("未找到目标镜像分区");

        int tOff = target.Off;
        int tDataOff = target.DataOff;
        int hdrSz = (int)(target.Hdr.HdrSz != 0 ? target.Hdr.HdrSz : PartHeaderSize);
        int paddedSize = RoundUp((int)target.Hdr.Dsize, (int)(target.Hdr.AlignSz != 0 ? target.Hdr.AlignSz : 1));

        var headerBytes = data[(tOff)..(tOff + hdrSz)];
        var dataBytes   = data[(tDataOff)..(tDataOff + paddedSize)];

        byte[]? newHeaderDigest = oldHeaderDigest is null ? null : Hash(headerBytes, oldHeaderDigest.Length);
        byte[]? newImageDigest  = oldImageDigest  is null ? null : Hash(dataBytes, oldImageDigest.Length);
        log?.Invoke($"新 Image Header Hash: {Hex(newHeaderDigest)}");
        log?.Invoke($"新 Image Hash: {Hex(newImageDigest)}");

        var insertBlock = BuildHashOverrideBlock(newHeaderDigest, newImageDigest);
        if (insertBlock.Length == 0) throw new InvalidDataException("无可更新的哈希");

        // legacy 模式：额外前置 BIT STRING(原始 CERT2 SEQUENCE)
        var originalSeq = FindOriginalCert2Sequence(der);
        var legacyBlock = BuildBitStringTlv(originalSeq);
        log?.Invoke($"原始 CERT2 SEQUENCE {originalSeq.Length} 字节, legacy 块 {legacyBlock.Length} 字节, insert 块 {insertBlock.Length} 字节");

        var newBlob = Concat(new[] { legacyBlock, insertBlock, der });

        // 替换 CERT2 blob + 更新 dsize
        int oldPadded = RoundUp((int)cHdr.Dsize, (int)(cHdr.AlignSz != 0 ? cHdr.AlignSz : 1));
        int newPadded = RoundUp(newBlob.Length, (int)(cHdr.AlignSz != 0 ? cHdr.AlignSz : 1));

        var outBytes = new byte[data.Length + (newPadded - oldPadded)];
        Array.Copy(data, 0, outBytes, 0, cBlobOff);
        Array.Copy(newBlob, 0, outBytes, cBlobOff, newBlob.Length);
        int tailStart = cBlobOff + oldPadded;
        Array.Copy(data, tailStart, outBytes, cBlobOff + newPadded, data.Length - tailStart);

        WriteInt32(outBytes, cOff + 4, newBlob.Length);  // 更新 dsize
        return outBytes;
    }

    /// <summary>
    /// 校验 CERT2 哈希覆盖块是否与镜像当前内容一致：重算目标分区的 Header / Image 摘要，
    /// 回查覆盖块里写入的 BIT STRING（等价 verify_mtk_image.py 的校验目的）。
    /// 修补完镜像后跑一遍，能确认「改完确实重新签名成功」。
    /// </summary>
    public static bool VerifyCert2Override(byte[] image, Action<string>? log)
    {
        List<PartEntry> parts;
        try
        {
            parts = ParseAllPartHeaders(image);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[校验] 解析分区头失败：{ex.Message}");
            return false;
        }

        var cert2 = parts.FirstOrDefault(p => p.Hdr.ImgType == ImgTypeCert2);
        if (cert2 is null)
        {
            log?.Invoke("[校验] 未找到 CERT2 分区头，镜像可能未签名");
            return false;
        }

        var target = parts.LastOrDefault(p => p.Off < cert2.Off && (p.Hdr.ImgType & 0xff000000) != ImgTypeGroupCert);
        if (target is null)
        {
            log?.Invoke("[校验] 未找到 CERT2 之前的目标镜像分区");
            return false;
        }

        int blobEnd = Math.Min(cert2.DataOff + (int)cert2.Hdr.Dsize, image.Length);
        if (blobEnd <= cert2.DataOff) { log?.Invoke("[校验] CERT2 数据区越界"); return false; }
        var der = image[cert2.DataOff..blobEnd];

        int hdrSz = Math.Min(target.Hdr.HeaderSize, image.Length - target.Off);
        int paddedSize = Math.Min(target.Hdr.PaddedSize, image.Length - target.DataOff);
        if (hdrSz <= 0 || paddedSize <= 0) { log?.Invoke("[校验] 目标分区数据越界"); return false; }
        var headerBytes = image[target.Off..(target.Off + hdrSz)];
        var dataBytes = image[target.DataOff..(target.DataOff + paddedSize)];

        // 摘要长度即算法（32=SHA-256 / 48=SHA-384），两种都试，命中说明覆盖块与当前内容一致
        foreach (int digestLen in new[] { 32, 48 })
        {
            var pattern = Concat(new[]
            {
                BuildOidTlv(OidImageHeaderHash), BuildBitStringTlv(Hash(headerBytes, digestLen)),
                BuildOidTlv(OidImageHash),       BuildBitStringTlv(Hash(dataBytes, digestLen)),
            });
            if (der.AsSpan().IndexOf(pattern) >= 0)
            {
                log?.Invoke($"[校验] CERT2 覆盖块与当前内容一致（{(digestLen == 32 ? "SHA-256" : "SHA-384")}），签名有效。");
                return true;
            }
        }

        log?.Invoke("[校验] CERT2 里没有与当前内容匹配的哈希覆盖块，签名可能无效");
        return false;
    }

    private static byte[] Hash(byte[] data, int digestLen)
    {
        using var sha = digestLen == 48 ? (HashAlgorithm)SHA384.Create() : SHA256.Create();
        return sha.ComputeHash(data);
    }

    // ============ DER 编码 / 解析 ============

    private static byte[] BuildHashOverrideBlock(byte[]? headerDigest, byte[]? imageDigest)
    {
        var parts = new List<byte[]>();
        if (headerDigest is not null) { parts.Add(BuildOidTlv(OidImageHeaderHash)); parts.Add(BuildBitStringTlv(headerDigest)); }
        if (imageDigest  is not null) { parts.Add(BuildOidTlv(OidImageHash));       parts.Add(BuildBitStringTlv(imageDigest)); }
        if (parts.Count == 0) return Array.Empty<byte>();
        var content = Concat(parts);
        return Concat(new[] { new byte[] { 0xa0 }, EncodeLength(content.Length), content });
    }

    private static byte[] BuildOidTlv(string oid)
    {
        var b = EncodeOid(oid);
        return Concat(new[] { new byte[] { 0x06 }, EncodeLength(b.Length), b });
    }

    private static byte[] BuildBitStringTlv(byte[] payload)
    {
        var val = new byte[payload.Length + 1];
        Array.Copy(payload, 0, val, 1, payload.Length);
        return Concat(new[] { new byte[] { 0x03 }, EncodeLength(val.Length), val });
    }

    private static byte[] EncodeLength(int n)
    {
        if (n < 0x80) return new[] { (byte)n };
        var s = new List<byte>();
        int v = n;
        while (v > 0) { s.Insert(0, (byte)(v & 0xff)); v >>= 8; }
        var result = new byte[s.Count + 1];
        result[0] = (byte)(0x80 | s.Count);
        for (int i = 0; i < s.Count; i++) result[i + 1] = s[i];
        return result;
    }

    private static byte[] EncodeOid(string oid)
    {
        var parts = oid.Split('.').Select(int.Parse).ToArray();
        var outList = new List<byte> { (byte)(40 * parts[0] + parts[1]) };
        for (int i = 2; i < parts.Length; i++)
        {
            int p = parts[i];
            if (p == 0) { outList.Add(0); continue; }
            var partsB = new List<int>();
            while (p > 0) { partsB.Insert(0, p & 0x7f); p >>= 7; }
            for (int j = 0; j < partsB.Count; j++)
                outList.Add(j != partsB.Count - 1 ? (byte)(0x80 | partsB[j]) : (byte)partsB[j]);
        }
        return outList.ToArray();
    }

    private static byte[]? FindOidDigest(byte[] der, string oidStr)
    {
        return WalkFindOid(ParseTlvTree(der), oidStr);
    }

    private static byte[]? WalkFindOid(List<TlvNode> nodes, string oidStr)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            if (node.Val is not null && node.TagClass == 0 && node.TagNum == 0x06 && DecodeOid(node.Val) == oidStr)
            {
                // 找同一层 OID 后面的兄弟节点里的 BIT STRING
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    var bit = FindBitString(nodes[j]);
                    if (bit is not null && bit.Length > 1) return bit[1..];
                }
                return null;
            }

            if (node.Children is not null)
            {
                var res = WalkFindOid(node.Children, oidStr);
                if (res is not null) return res;
            }
        }
        return null;
    }

    private static byte[] FindOriginalCert2Sequence(byte[] der)
    {
        foreach (var node in ParseTopLevelTlvs(der))
            if (node.TagClass == 0 && node.Constructed && node.TagNum == 0x10)
                return der[(node.Off)..(node.Off + node.HeaderLen + node.Length)];
        throw new InvalidDataException("未找到原始 CERT2 SEQUENCE");
    }

    private sealed class TlvNode
    {
        public int TagClass;
        public bool Constructed;
        public int TagNum;
        public int HeaderLen;
        public int Length;
        public int Off;         // 相对偏移
        public int ValOff;
        public byte[]? Val;
        public List<TlvNode>? Children;
    }

    private static List<TlvNode> ParseTopLevelTlvs(byte[] data)
    {
        var nodes = new List<TlvNode>();
        int off = 0;
        while (off < data.Length)
        {
            var node = ReadTlv(data, off);
            if (node is null) break;
            nodes.Add(node);
            off = node.ValOff + node.Length;
        }
        return nodes;
    }

    private static List<TlvNode> ParseTlvTree(byte[] data)
    {
        var root = new List<TlvNode>();
        ParseLevel(data, root, 0);
        return root;
    }

    private static void ParseLevel(byte[] data, List<TlvNode> nodes, int baseOff)
    {
        int off = 0;
        while (off < data.Length)
        {
            var node = ReadTlv(data, off);
            if (node is null) break;
            node.Off = baseOff + off;
            node.ValOff = baseOff + off + node.HeaderLen;
            if (node.Constructed)
            {
                var content = data[(off + node.HeaderLen)..(off + node.HeaderLen + node.Length)];
                node.Children = new List<TlvNode>();
                ParseLevel(content, node.Children, baseOff + off + node.HeaderLen);
            }
            nodes.Add(node);
            off += node.HeaderLen + node.Length;
        }
    }

    private static TlvNode? ReadTlv(byte[] data, int off)
    {
        if (off >= data.Length) return null;
        byte b0 = data[off];
        int tagClass = (b0 & 0xc0) >> 6;
        bool constructed = (b0 & 0x20) != 0;
        int tagNum = b0 & 0x1f;
        int i = off + 1;
        if (tagNum == 0x1f)
        {
            tagNum = 0;
            while (i < data.Length)
            {
                byte b = data[i++];
                tagNum = (tagNum << 7) | (b & 0x7f);
                if ((b & 0x80) == 0) break;
            }
        }
        if (i >= data.Length) return null;
        byte lb = data[i++];
        int length;
        if ((lb & 0x80) == 0) { length = lb; }
        else
        {
            int num = lb & 0x7f;
            if (i + num > data.Length) return null;
            length = 0;
            for (int k = 0; k < num; k++) length = (length << 8) | data[i++];
        }
        int hdrLen = i - off;
        int valOff = off + hdrLen;
        if (valOff + length > data.Length) return null;

        return new TlvNode
        {
            TagClass = tagClass,
            Constructed = constructed,
            TagNum = tagNum,
            HeaderLen = hdrLen,
            Length = length,
            Off = off,
            ValOff = valOff,
            Val = constructed ? null : data[valOff..(valOff + length)],
        };
    }

    private static byte[]? FindBitString(TlvNode node)
    {
        if (node.Val is not null && node.TagClass == 0 && node.TagNum == 0x03) return node.Val;
        if (node.Children is not null)
            foreach (var ch in node.Children)
            {
                var r = FindBitString(ch);
                if (r is not null) return r;
            }
        return null;
    }

    private static string DecodeOid(byte[] oidBytes)
    {
        if (oidBytes.Length == 0) return "";
        int first = oidBytes[0];
        var parts = new List<string> { (first / 40).ToString(), (first % 40).ToString() };
        int val = 0;
        for (int i = 1; i < oidBytes.Length; i++)
        {
            val = (val << 7) | (oidBytes[i] & 0x7f);
            if ((oidBytes[i] & 0x80) == 0) { parts.Add(val.ToString()); val = 0; }
        }
        return string.Join(".", parts);
    }

    // ============ 工具函数 ============

    private static int RoundUp(int value, int align)
    {
        if (align <= 0) return value;
        return (value + align - 1) / align * align;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var p in parts) { Array.Copy(p, 0, result, pos, p.Length); pos += p.Length; }
        return result;
    }

    private static string Hex(byte[]? data)
    {
        if (data is null || data.Length == 0) return "(空)";
        const int preview = 8;
        int n = Math.Min(data.Length, preview);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
        if (data.Length > preview) sb.Append("…");
        return sb.ToString();
    }

    private static void WriteInt32(byte[] data, int off, int value)
    {
        data[off]     = (byte)value;
        data[off + 1] = (byte)(value >> 8);
        data[off + 2] = (byte)(value >> 16);
        data[off + 3] = (byte)(value >> 24);
    }

    /// <summary>MTK part_hdr_t：512 字节分区头。</summary>
    public sealed class PartHeader
    {
        public uint Magic;
        public uint Dsize;
        public string Name = "";
        public byte[] RawName = new byte[32];
        public uint Maddr;
        public uint Mode;
        public uint ExtMagic;
        public uint HdrSz;
        public uint HdrVer;
        public uint ImgType;
        public uint ImgListEnd;
        public uint AlignSz;
        public uint DsizeExtend;
        public uint MaddrExtend;

        /// <summary>分区头实际长度（hdr_sz 为 0 时按 512 处理）。</summary>
        public int HeaderSize => (int)(HdrSz != 0 ? HdrSz : PartHeaderSize);

        /// <summary>数据区按 align_sz 对齐后的长度（对齐值为 0 时视为不填充）。</summary>
        public int PaddedSize => RoundUp((int)Dsize, (int)(AlignSz != 0 ? AlignSz : 1));
    }

    /// <summary>
    /// 镜像里的一个分区：Off=分区头起点，DataOff=数据起点，NextOff=下一个分区头起点。
    /// LK 修补要按分区切分/回填，所以对同程序集开放。
    /// </summary>
    public sealed record PartEntry(int Off, int DataOff, int NextOff, PartHeader Hdr);

    private static (PartHeader hdr, int dataOff, int nextOff, int padded) ParsePartHeader(byte[] data, int off)
    {
        if (off + PartHeaderSize > data.Length) throw new InvalidDataException("文件过小");
        var hdr = new PartHeader
        {
            Magic       = BitConverter.ToUInt32(data, off),
            Dsize       = BitConverter.ToUInt32(data, off + 4),
            Name        = Encoding.Latin1.GetString(data, off + 8, 32).Split('\0')[0],
            Maddr       = BitConverter.ToUInt32(data, off + 40),
            Mode        = BitConverter.ToUInt32(data, off + 44),
            ExtMagic    = BitConverter.ToUInt32(data, off + 48),
            HdrSz       = BitConverter.ToUInt32(data, off + 52),
            HdrVer      = BitConverter.ToUInt32(data, off + 56),
            ImgType     = BitConverter.ToUInt32(data, off + 60),
            ImgListEnd  = BitConverter.ToUInt32(data, off + 64),
            AlignSz     = BitConverter.ToUInt32(data, off + 68),
            DsizeExtend = BitConverter.ToUInt32(data, off + 72),
            MaddrExtend = BitConverter.ToUInt32(data, off + 76),
        };
        Array.Copy(data, off + 8, hdr.RawName, 0, 32);
        if (hdr.Magic != PartMagic) throw new InvalidDataException($"在偏移 0x{off:x} 处缺少 MTK 分区头");
        int hdrSz = (int)(hdr.HdrSz != 0 ? hdr.HdrSz : PartHeaderSize);
        int alignSz = (int)(hdr.AlignSz != 0 ? hdr.AlignSz : 1);
        int padded = RoundUp((int)hdr.Dsize, alignSz);
        return (hdr, off + hdrSz, off + hdrSz + padded, padded);
    }

    /// <summary>顺序解析镜像里的所有 part_hdr_t（lk / bl2_ext / aee / lk_main_dtb / CERT2 …）。</summary>
    public static List<PartEntry> ParseAllPartHeaders(byte[] data)
    {
        var entries = new List<PartEntry>();
        int off = 0;
        while (off + PartHeaderSize <= data.Length)
        {
            var (hdr, dataOff, nextOff, _) = ParsePartHeader(data, off);
            entries.Add(new PartEntry(off, dataOff, nextOff, hdr));
            off = nextOff;
            if (hdr.ImgListEnd != 0) break;
        }
        return entries;
    }
}
