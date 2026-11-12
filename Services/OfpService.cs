using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// OPPO / realme / OnePlus 的 OFP 固件包解包。
/// 支持三类容器：
///   1) ZIP 容器（realme 加密 zip，口令固定）
///   2) 高通 OFP（文件尾部页内 0x7CEF 索引，ProFile.xml 用 AES-CFB 加密）
///   3) 联发科 OFP（文件尾部 0x6C 头 + 0x60 字节条目表，分区镜像前段 AES-CFB 加密）
/// 算法对齐开源实现 oppo_decrypt（bkerler）。
/// </summary>
public static class OfpService
{
    public enum OfpKind { Unknown, Zip, QualcommXml, MtkHeader }

    public sealed class OfpImage
    {
        public OfpKind Kind     { get; set; }
        public string  Path     { get; set; } = "";
        public long    FileSize { get; set; }

        // 高通
        public long    PageSize   { get; set; }
        public byte[]? AesKey     { get; set; }
        public byte[]? AesIv      { get; set; }
        public string  ProfileXml { get; set; } = "";
        public string  KeyHint    { get; set; } = "";

        // 联发科
        public string ProjectName { get; set; } = "";
        public string ProjectInfo { get; set; } = "";
        public string Cpu         { get; set; } = "";
        public string FlashType   { get; set; } = "";

        public List<OfpEntry> Entries { get; } = new();

        public string KindText => Kind switch
        {
            OfpKind.Zip         => "ZIP 容器（OPPO/realme 加密 zip）",
            OfpKind.QualcommXml => "高通 OFP（ProFile.xml 索引）",
            OfpKind.MtkHeader   => "联发科 OFP（尾部索引表）",
            _                   => "未知格式",
        };

        public string DetailText
        {
            get
            {
                var parts = new List<string> { KindText, $"大小 {SuperService.FormatSize(FileSize)}" };
                if (Kind == OfpKind.QualcommXml) parts.Add($"页大小 0x{PageSize:X}");
                if (Kind == OfpKind.MtkHeader)
                {
                    if (Cpu.Length > 0) parts.Add($"CPU {Cpu}");
                    if (FlashType.Length > 0) parts.Add($"闪存 {FlashType}");
                    if (ProjectName.Length > 0) parts.Add($"工程 {ProjectName}");
                }
                parts.Add($"条目 {Entries.Count}");
                return string.Join("  |  ", parts);
            }
        }
    }

    /// <summary>realme zip 容器固定口令。</summary>
    public const string ZipPassword = "flash@realme$50E7F7D847732396F1582CD62DD385ED7ABB0897";

    // 高通 OFP 密钥组：{ mc, userkey, ivec }
    private static readonly string[][] QcKeys =
    {
        new[] { "27827963787265EF89D126B69A495A21", "82C50203285A2CE7D8C3E198383CE94C", "422DD5399181E223813CD8ECDF2E4D72" },
        new[] { "E11AA7BB558A436A8375FD15DDD4651F", "77DDF6A0696841F6B74782C097835169", "A739742384A44E8BA45207AD5C3700EA" },
        new[] { "67657963787565E837D226B69A495D21", "F6C50203515A2CE7D8C3E1F938B7E94C", "42F2D5399137E2B2813CD8ECDF2F4D72" },
        new[] { "3C2D518D9BF2E4279DC758CD535147C3", "87C74A29709AC1BF2382276C4E8DF232", "598D92E967265E9BCABE2469FE4A915E" },
        new[] { "8FB8FB261930260BE945B841AEFA9FD4", "E529E82B28F5A2F8831D860AE39E425D", "8A09DA60ED36F125D64709973372C1CF" },
        new[] { "E8AE288C0192C54BF10C5707E9C4705B", "D64FC385DCD52A3C9B5FBA8650F92EDA", "79051FD8D8B6297E2E4559E997F63B7F" },
    };

    // 联发科 OFP 密钥组：{ obskey, encaeskey, encaesiv }
    private static readonly string[][] MtkKeys =
    {
        new[] { "67657963787565E837D226B69A495D21", "F6C50203515A2CE7D8C3E1F938B7E94C", "42F2D5399137E2B2813CD8ECDF2F4D72" },
        new[] { "9E4F32639D21357D37D226B69A495D21", "A3D8D358E42F5A9E931DD3917D9A3218", "386935399137416B67416BECF22F519A" },
        new[] { "892D57E92A4D8A975E3C216B7C9DE189", "D26DF2D9913785B145D18C7219B89F26", "516989E4A1BFC78B365C6BC57D944391" },
        new[] { "27827963787265EF89D126B69A495A21", "82C50203285A2CE7D8C3E198383CE94C", "422DD5399181E223813CD8ECDF2E4D72" },
        new[] { "3C4A618D9BF2E4279DC758CD535147C3", "87B13D29709AC1BF2382276C4E8DF232", "59B7A8E967265E9BCABE2469FE4A915E" },
        new[] { "1C3288822BF824259DC852C1733127D3", "E7918D22799181CF2312176C9E2DF298", "3247F889A7B6DECBCA3E28693E4AAAFE" },
        new[] { "1E4F32239D65A57D37D2266D9A775D43", "A332D3C3E42F5A3E931DD991729A321D", "3F2A35399A373377674155ECF28FD19A" },
        new[] { "122D57E92A518AFF5E3C786B7C34E189", "DD6DF2D9543785674522717219989FB0", "12698965A132C76136CC88C5DD94EE91" },
    };

    private static readonly byte[] MtkAsciiKey = Encoding.ASCII.GetBytes("ab3f76d7989207f2");
    private static readonly byte[] MtkAsciiIv  = Encoding.ASCII.GetBytes("2bf515b3a9737835");

    // ---------------- 打开 / 识别 ----------------

    public static OfpImage Open(string path, Action<string>? log = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"文件不存在：{path}");

        var image = new OfpImage { Path = path, FileSize = new FileInfo(path).Length };

        if (IsZip(path))
        {
            image.Kind = OfpKind.Zip;
            foreach (var entry in SimpleZip.ReadEntries(path))
                image.Entries.Add(new OfpEntry
                {
                    Name       = entry.Name,
                    FileName   = entry.Name,
                    Group      = "ZIP",
                    Length     = entry.UncompressedSize,
                    RawLength  = entry.UncompressedSize,
                    Encrypted  = entry.Encrypted,
                    Md5        = "",
                });

            log?.Invoke($"ZIP 容器，共 {image.Entries.Count} 个条目。");
            return image;
        }

        if (TryOpenQualcomm(image, log)) return image;
        if (TryOpenMtk(image, log)) return image;

        throw new InvalidDataException("无法识别的 OFP（既不是 ZIP，也不是已知的高通/联发科 OFP 容器）。");
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var magic = new byte[4];
            return fs.Read(magic, 0, 4) == 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 高通 OFP ----------------

    private static bool TryOpenQualcomm(OfpImage image, Action<string>? log)
    {
        using var fs = File.OpenRead(image.Path);
        long fileSize = fs.Length;

        long pageSize = 0;
        foreach (long candidate in new long[] { 0x200, 0x1000 })
        {
            if (fileSize <= candidate) continue;
            var mark = new byte[4];
            fs.Position = fileSize - candidate + 0x10;
            if (fs.Read(mark, 0, 4) != 4) continue;
            if (BitConverter.ToUInt32(mark, 0) == 0x7CEF) { pageSize = candidate; break; }
        }

        if (pageSize == 0) return false;

        foreach (var triple in QcKeys)
        {
            var key = DeriveQcKey(triple[1], triple[0]);
            var iv  = DeriveQcKey(triple[2], triple[0]);

            string? xml = TryReadProfileXml(fs, fileSize, pageSize, key, iv);
            if (xml == null) continue;

            image.Kind       = OfpKind.QualcommXml;
            image.PageSize   = pageSize;
            image.AesKey     = key;
            image.AesIv      = iv;
            image.ProfileXml = xml;
            image.KeyHint    = "已匹配内置密钥组";

            ParseProfileXml(image, xml, log);

            log?.Invoke($"高通 OFP：页大小 0x{pageSize:X}，ProFile.xml 已解密，共 {image.Entries.Count} 个条目。");
            return true;
        }

        return false;
    }

    private static string? TryReadProfileXml(FileStream fs, long fileSize, long pageSize, byte[] key, byte[] iv)
    {
        long xmlOffset = fileSize - pageSize;

        fs.Position = xmlOffset + 0x14;
        var head = new byte[8];
        if (fs.Read(head, 0, 8) != 8) return null;

        long offset = BitConverter.ToUInt32(head, 0) * pageSize;
        long length = BitConverter.ToUInt32(head, 4);

        if (length < 200) length = xmlOffset - offset - 0x57;   // A57 机型兼容
        if (offset <= 0 || length <= 0 || offset + length > fileSize) return null;

        var data = new byte[length];
        fs.Position = offset;
        if (ReadFully(fs, data, 0, (int)length) != (int)length) return null;

        using var cfb = new AesCfb128(key, iv);
        var decrypted = cfb.Decrypt(data);

        if (!Contains(decrypted, "<?xml")) return null;

        int end = decrypted.AsSpan().LastIndexOf((byte)'>');
        if (end < 0) return null;

        return Encoding.UTF8.GetString(decrypted, 0, end + 1);
    }

    private static void ParseProfileXml(OfpImage image, string xml, Action<string>? log)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("ProFile.xml 解析失败：" + ex.Message);
        }

        var root = doc.Root ?? throw new InvalidDataException("ProFile.xml 缺少根节点。");

        foreach (var group in root.Elements())
        {
            string groupName = group.Name.LocalName;

            foreach (var item in group.Elements())
            {
                if (HasFileName(item))
                {
                    AddEntry(image, item, groupName);
                    continue;
                }

                foreach (var sub in item.Elements())
                    if (HasFileName(sub))
                        AddEntry(image, sub, groupName);
            }
        }

        if (image.Entries.Count == 0) log?.Invoke("ProFile.xml 里没有可解包的条目。");
    }

    private static bool HasFileName(XElement item) =>
        item.Attribute("Path") != null || item.Attribute("filename") != null;

    private static void AddEntry(OfpImage image, XElement item, string groupName)
    {
        string fileName = (string?)item.Attribute("Path")
                          ?? (string?)item.Attribute("filename")
                          ?? "";
        if (fileName.Length == 0) return;

        long start = -1;
        if (item.Attribute("FileOffsetInSrc") != null)
            start = (long)item.Attribute("FileOffsetInSrc")! * image.PageSize;
        else if (item.Attribute("SizeInSectorInSrc") != null)
            start = (long)item.Attribute("SizeInSectorInSrc")! * image.PageSize;

        if (start < 0) return;

        long rawLength = item.Attribute("SizeInByteInSrc") != null
            ? (long)item.Attribute("SizeInByteInSrc")!
            : 0;

        long length = item.Attribute("SizeInSectorInSrc") != null
            ? (long)item.Attribute("SizeInSectorInSrc")! * image.PageSize
            : rawLength;

        // 表格/摘要类节点为明文，Config 类与 Sahara 组的长度语义与其他组不同
        bool plainCopy = groupName is "DigestsToSign" or "ChainedTableOfDigests" or "Firmware";

        if (groupName is "Config" or "Provision" or "ChainedTableOfDigests" or "DigestsToSign" or "Firmware")
            length = rawLength;

        long decryptSize = plainCopy ? 0 : 0x40000;
        if (groupName == "Sahara") decryptSize = rawLength;

        image.Entries.Add(new OfpEntry
        {
            Name        = (string?)item.Attribute("name") ?? Path.GetFileName(fileName),
            FileName    = fileName.Replace('\\', '/'),
            Group       = groupName,
            Start       = start,
            Length      = length,
            RawLength   = rawLength,
            Encrypted   = !plainCopy,
            DecryptSize = Math.Min(decryptSize, rawLength),
            Md5         = (string?)item.Attribute("md5") ?? "",
            Sha256      = (string?)item.Attribute("sha256") ?? "",
        });
    }

    // ---------------- 联发科 OFP ----------------

    private const int MtkHeaderLength = 0x6C;
    private const int MtkEntryLength  = 0x60;

    private static bool TryOpenMtk(OfpImage image, Action<string>? log)
    {
        using var fs = File.OpenRead(image.Path);
        long fileSize = fs.Length;
        if (fileSize < MtkHeaderLength + MtkEntryLength) return false;

        // 用密钥组爆破：解密文件开头 16 字节，结果以 "MMM" 开头即命中
        var probe = new byte[16];
        fs.Position = 0;
        if (fs.Read(probe, 0, 16) != 16) return false;

        byte[]? aesKey = null;
        byte[]? aesIv = null;

        foreach (var (key, iv) in MtkKeyCandidates())
        {
            if (key.Length is not (16 or 24 or 32)) continue;

            try
            {
                using var cfb = new AesCfb128(key, iv);
                var decrypted = cfb.Decrypt(probe);
                if (decrypted[0] == (byte)'M' && decrypted[1] == (byte)'M' && decrypted[2] == (byte)'M')
                {
                    aesKey = key;
                    aesIv = iv;
                    break;
                }
            }
            catch
            {
                // 密钥长度不合法，跳过
            }
        }

        if (aesKey == null || aesIv == null) return false;

        var headerKey = Encoding.ASCII.GetBytes("geyixue");

        var header = new byte[MtkHeaderLength];
        fs.Position = fileSize - MtkHeaderLength;
        if (ReadFully(fs, header, 0, MtkHeaderLength) != MtkHeaderLength) return false;
        MtkShuffle(headerKey, header);

        string prjName    = CleanCString(header, 0, 46);
        string cpu        = CleanCString(header, 58, 7);
        string flashType  = CleanCString(header, 65, 5);
        int    entryCount = BitConverter.ToUInt16(header, 70);
        string prjInfo    = CleanCString(header, 72, 32);

        int tableLength = entryCount * MtkEntryLength;
        if (entryCount <= 0 || tableLength <= 0 || fileSize < tableLength + MtkHeaderLength) return false;

        var table = new byte[tableLength];
        fs.Position = fileSize - tableLength - MtkHeaderLength;
        if (ReadFully(fs, table, 0, tableLength) != tableLength) return false;
        MtkShuffle(headerKey, table);

        for (int i = 0; i < entryCount; i++)
        {
            int at = i * MtkEntryLength;
            string name     = CleanCString(table, at, 32);
            long   start    = BitConverter.ToInt64(table, at + 32);
            long   length   = BitConverter.ToInt64(table, at + 40);
            long   encLen   = BitConverter.ToInt64(table, at + 48);
            string fileName = CleanCString(table, at + 56, 32);

            if (fileName.Length == 0) fileName = name;
            if (name.Length == 0 && fileName.Length == 0) continue;
            if (start < 0 || length <= 0) continue;

            image.Entries.Add(new OfpEntry
            {
                Name        = name,
                FileName    = fileName.Replace('\\', '/'),
                Group       = "MTK",
                Start       = start,
                Length      = length,
                RawLength   = length,
                Encrypted   = encLen > 0,
                DecryptSize = Math.Min(encLen, length),
            });
        }

        image.Kind        = OfpKind.MtkHeader;
        image.AesKey      = aesKey;
        image.AesIv       = aesIv;
        image.ProjectName = prjName;
        image.ProjectInfo = prjInfo;
        image.Cpu         = cpu;
        image.FlashType   = flashType;
        image.KeyHint     = "已爆破匹配密钥组";

        log?.Invoke($"联发科 OFP：CPU {cpu}，闪存 {flashType}，工程 {prjName}，共 {image.Entries.Count} 个条目。");
        return true;
    }

    private static IEnumerable<(byte[] Key, byte[] Iv)> MtkKeyCandidates()
    {
        foreach (var triple in MtkKeys)
        {
            var obs    = Convert.FromHexString(triple[0]);
            var encKey = Convert.FromHexString(triple[1]);
            var encIv  = Convert.FromHexString(triple[2]);

            yield return (DeriveMtkKey(obs, encKey), DeriveMtkKey(obs, encIv));
        }

        yield return (MtkAsciiKey, MtkAsciiIv);
    }

    private static byte[] DeriveMtkKey(byte[] obskey, byte[] encoded)
    {
        var buffer = new byte[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
            buffer[i] = SwapNibbles((byte)(obskey[i % obskey.Length] ^ encoded[i]));

        string hex = Convert.ToHexString(MD5.HashData(buffer)).ToLowerInvariant();
        return Encoding.ASCII.GetBytes(hex[..16]);
    }

    private static byte[] DeriveQcKey(string encodedHex, string maskHex)
    {
        var encoded = Convert.FromHexString(encodedHex);
        var mask    = Convert.FromHexString(maskHex);

        var buffer = new byte[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
            buffer[i] = SwapNibbles((byte)(encoded[i] ^ mask[i]));

        string hex = Convert.ToHexString(MD5.HashData(buffer)).ToLowerInvariant();
        return Encoding.ASCII.GetBytes(hex[..16]);
    }

    private static byte SwapNibbles(byte value) =>
        (byte)(((value & 0x0F) << 4) | ((value & 0xF0) >> 4));

    /// <summary>MTK 头部/表区的混淆还原：先交换半字节，再与密钥异或。</summary>
    private static void MtkShuffle(byte[] key, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(key[i % key.Length] ^ SwapNibbles(data[i]));
    }

    private static string CleanCString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        int limit = Math.Min(offset + length, buffer.Length);
        while (end < limit && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset).Trim();
    }

    // ---------------- 解包 ----------------

    public static void Extract(OfpImage image, IEnumerable<OfpEntry> entries, string outputDir,
                               Action<string>? log = null, Action<double>? progress = null,
                               CancellationToken ct = default)
    {
        var list = entries.ToList();
        if (list.Count == 0) throw new InvalidOperationException("没有选中的条目。");
        if (string.IsNullOrWhiteSpace(outputDir)) throw new InvalidOperationException("未指定输出目录。");

        Directory.CreateDirectory(outputDir);

        if (image.Kind == OfpKind.Zip)
        {
            SimpleZip.Extract(image.Path, outputDir, ZipPassword, list, log, progress, ct);
            return;
        }

        if (image.AesKey == null || image.AesIv == null)
            throw new InvalidOperationException("缺少解密密钥，无法解包。");

        if (image.Kind == OfpKind.QualcommXml && image.ProfileXml.Length > 0)
        {
            string xmlPath = Path.Combine(outputDir, "ProFile.xml");
            File.WriteAllText(xmlPath, image.ProfileXml, Encoding.UTF8);
            log?.Invoke($"已写出 ProFile.xml：{xmlPath}");
        }

        long total = list.Sum(e => Math.Max(e.RawLength, e.Length));
        long done  = 0;

        using var src = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

        foreach (var entry in list)
        {
            ct.ThrowIfCancellationRequested();

            string dest = Path.Combine(outputDir, entry.FileName.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            log?.Invoke($"解包 {entry.FileName}（{SuperService.FormatSize(Math.Max(entry.RawLength, entry.Length))}）");

            long written = image.Kind == OfpKind.MtkHeader
                ? ExtractMtkEntry(src, image.AesKey, image.AesIv, entry, dest, ct)
                : ExtractQcEntry(src, image.AesKey, image.AesIv, entry, dest, ct);

            long unit = Math.Max(entry.RawLength, entry.Length);
            done += unit > 0 ? unit : written;
            progress?.Invoke(total > 0 ? (double)done / total : 0);
        }

        log?.Invoke($"解包完成，共 {list.Count} 个条目 → {outputDir}");
    }

    private static long ExtractQcEntry(FileStream src, byte[] key, byte[] iv, OfpEntry entry, string dest,
                                       CancellationToken ct)
    {
        long outputSize = entry.RawLength > 0 ? entry.RawLength : entry.Length;

        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        if (!entry.Encrypted || entry.DecryptSize <= 0)
        {
            src.Position = entry.Start;
            CopyRange(src, dst, Math.Min(entry.Length, outputSize > 0 ? outputSize : entry.Length), ct);
            return outputSize;
        }

        long decryptBytes = Math.Min(entry.DecryptSize, outputSize);
        var buffer = new byte[decryptBytes];

        src.Position = entry.Start;
        if (ReadFully(src, buffer, 0, (int)decryptBytes) != (int)decryptBytes)
            throw new EndOfStreamException("OFP 数据不完整（文件被截断？）。");

        using (var cfb = new AesCfb128(key, iv))
        {
            var plain = cfb.Decrypt(buffer);
            dst.Write(plain, 0, (int)decryptBytes);
        }

        long remaining = outputSize - decryptBytes;
        if (remaining > 0)
        {
            src.Position = entry.Start + decryptBytes;
            CopyRange(src, dst, remaining, ct);
        }

        return outputSize;
    }

    private static long ExtractMtkEntry(FileStream src, byte[] key, byte[] iv, OfpEntry entry, string dest,
                                        CancellationToken ct)
    {
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        long encrypted = entry.Encrypted ? Math.Min(entry.DecryptSize, entry.Length) : 0;

        if (encrypted > 0)
        {
            long padded = (encrypted + 15) / 16 * 16;
            var buffer = new byte[padded];

            src.Position = entry.Start;
            if (ReadFully(src, buffer, 0, (int)encrypted) != (int)encrypted)
                throw new EndOfStreamException("OFP 数据不完整（文件被截断？）。");

            using var cfb = new AesCfb128(key, iv);
            var plain = cfb.Decrypt(buffer);
            dst.Write(plain, 0, (int)encrypted);
        }

        long remaining = entry.Length - encrypted;
        if (remaining > 0)
        {
            src.Position = entry.Start + encrypted;
            CopyRange(src, dst, remaining, ct);
        }

        return entry.Length;
    }

    /// <summary>把 ProFile.xml 单独保存到文件。</summary>
    public static void SaveProfileXml(OfpImage image, string path)
    {
        if (image.ProfileXml.Length == 0) throw new InvalidOperationException("当前 OFP 没有 ProFile.xml。");
        File.WriteAllText(path, image.ProfileXml, Encoding.UTF8);
    }

    // ---------------- 工具 ----------------

    private static void CopyRange(FileStream src, Stream dst, long length, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long remaining = length;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, remaining);
            int read = src.Read(buffer, 0, n);
            if (read <= 0) throw new EndOfStreamException("OFP 数据不完整（文件被截断？）。");
            dst.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool Contains(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        return data.AsSpan().IndexOf(needle) >= 0;
    }

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
}
