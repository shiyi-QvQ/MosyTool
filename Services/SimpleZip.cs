using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>ZIP 条目（含 Zip64 与 ZipCrypto 标记）。</summary>
public sealed class SimpleZipEntry
{
    public string Name { get; set; } = "";
    public ushort Method { get; set; }
    public ushort Flags { get; set; }
    public uint   Crc { get; set; }
    public uint   DosTime { get; set; }
    public long   CompressedSize { get; set; }
    public long   UncompressedSize { get; set; }
    public long   LocalHeaderOffset { get; set; }
    public bool   IsAes { get; set; }

    public bool Encrypted => (Flags & 0x1) != 0;
}

/// <summary>
/// 轻量 ZIP 读取器：.NET 自带的 ZipArchive 不支持口令，这里补上传统 ZipCrypto(PKWARE) 解密，
/// 用于解包 realme/OPPO 的加密 zip 型 OFP。
/// </summary>
public static class SimpleZip
{
    private const uint EocdSignature       = 0x06054B50;
    private const uint Zip64EocdSignature  = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint CentralSignature    = 0x02014B50;

    public static List<SimpleZipEntry> ReadEntries(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadEntries(fs);
    }

    public static List<SimpleZipEntry> ReadEntries(Stream stream)
    {
        var result = new List<SimpleZipEntry>();

        long eocdPosition = FindEocd(stream);
        if (eocdPosition < 0) throw new InvalidDataException("不是合法的 ZIP（找不到 EOCD）。");

        stream.Position = eocdPosition + 10;
        var buffer = new byte[12];
        ReadFully(stream, buffer, 0, 12);

        long totalEntries = BitConverter.ToUInt16(buffer, 0);
        long cdSize       = BitConverter.ToUInt32(buffer, 2);
        long cdOffset     = BitConverter.ToUInt32(buffer, 6);

        // Zip64：EOCD 里放不下时去读 Zip64 EOCD
        if (totalEntries == 0xFFFF || cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
        {
            long locator = eocdPosition - 20;
            if (locator >= 0)
            {
                stream.Position = locator;
                var loc = new byte[20];
                if (ReadFully(stream, loc, 0, 20) == 20 && BitConverter.ToUInt32(loc, 0) == Zip64LocatorSignature)
                {
                    long zip64 = (long)BitConverter.ToUInt64(loc, 8);
                    stream.Position = zip64;
                    var z = new byte[56];
                    if (ReadFully(stream, z, 0, 56) == 56 && BitConverter.ToUInt32(z, 0) == Zip64EocdSignature)
                    {
                        totalEntries = (long)BitConverter.ToUInt64(z, 32);
                        cdSize       = (long)BitConverter.ToUInt64(z, 40);
                        cdOffset     = (long)BitConverter.ToUInt64(z, 48);
                    }
                }
            }
        }

        stream.Position = cdOffset;

        for (long i = 0; i < totalEntries; i++)
        {
            var head = new byte[46];
            if (ReadFully(stream, head, 0, 46) != 46) break;
            if (BitConverter.ToUInt32(head, 0) != CentralSignature)
                throw new InvalidDataException("中央目录结构损坏。");

            var entry = new SimpleZipEntry
            {
                Flags             = BitConverter.ToUInt16(head, 8),
                Method            = BitConverter.ToUInt16(head, 10),
                DosTime           = BitConverter.ToUInt16(head, 12),
                Crc               = BitConverter.ToUInt32(head, 16),
                CompressedSize    = BitConverter.ToUInt32(head, 20),
                UncompressedSize  = BitConverter.ToUInt32(head, 24),
                LocalHeaderOffset = BitConverter.ToUInt32(head, 42),
            };

            int nameLength  = BitConverter.ToUInt16(head, 28);
            int extraLength = BitConverter.ToUInt16(head, 30);
            int commentLen  = BitConverter.ToUInt16(head, 32);

            var name = new byte[nameLength];
            ReadFully(stream, name, 0, nameLength);
            entry.Name = DecodeName(name, entry.Flags);

            var extra = new byte[extraLength];
            if (extraLength > 0) ReadFully(stream, extra, 0, extraLength);
            ParseExtra(entry, extra);

            if (commentLen > 0) Skip(stream, commentLen);

            result.Add(entry);
        }

        return result;
    }

    private static void ParseExtra(SimpleZipEntry entry, byte[] extra)
    {
        int position = 0;
        while (position + 4 <= extra.Length)
        {
            ushort id   = BitConverter.ToUInt16(extra, position);
            ushort size = BitConverter.ToUInt16(extra, position + 2);
            int data    = position + 4;
            if (data + size > extra.Length) break;

            if (id == 0x0001)   // Zip64 扩展
            {
                int at = data;
                if (entry.UncompressedSize == 0xFFFFFFFF && at + 8 <= data + size)
                {
                    entry.UncompressedSize = (long)BitConverter.ToUInt64(extra, at);
                    at += 8;
                }
                if (entry.CompressedSize == 0xFFFFFFFF && at + 8 <= data + size)
                {
                    entry.CompressedSize = (long)BitConverter.ToUInt64(extra, at);
                    at += 8;
                }
                if (entry.LocalHeaderOffset == 0xFFFFFFFF && at + 8 <= data + size)
                {
                    entry.LocalHeaderOffset = (long)BitConverter.ToUInt64(extra, at);
                    at += 8;
                }
            }
            else if (id == 0x9901)   // WinZip AES
            {
                entry.IsAes = true;
            }

            position = data + size;
        }
    }

    private static string DecodeName(byte[] raw, ushort flags)
    {
        bool utf8 = (flags & 0x800) != 0;
        try
        {
            return utf8 ? Encoding.UTF8.GetString(raw) : Encoding.GetEncoding(936).GetString(raw);
        }
        catch
        {
            return Encoding.UTF8.GetString(raw);
        }
    }

    private static long FindEocd(Stream stream)
    {
        long length = stream.Length;
        int window = (int)Math.Min(length, 22 + 65535);
        var buffer = new byte[window];

        stream.Position = length - window;
        if (ReadFully(stream, buffer, 0, window) != window) return -1;

        for (int i = window - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(buffer, i) == EocdSignature)
                return length - window + i;

        return -1;
    }

    /// <summary>解包指定条目（支持 ZipCrypto 口令）。</summary>
    public static void Extract(string zipPath, string outputDir, string password, List<OfpEntry> entries,
                               Action<string>? log = null, Action<double>? progress = null,
                               CancellationToken ct = default)
    {
        if (entries.Count == 0) throw new InvalidOperationException("没有选中的条目。");
        Directory.CreateDirectory(outputDir);

        using var fs = File.OpenRead(zipPath);
        var byName = new Dictionary<string, SimpleZipEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ReadEntries(fs)) byName[item.Name] = item;

        long total = 0;
        foreach (var entry in entries)
            total += byName.TryGetValue(entry.FileName, out var z) ? Math.Max(1, z.UncompressedSize) : 1;

        long done = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!byName.TryGetValue(entry.FileName, out var zipEntry))
            {
                log?.Invoke($"zip 中不存在 {entry.FileName}，已跳过。");
                done += 1;
                continue;
            }

            if (zipEntry.IsAes)
                throw new NotSupportedException($"{entry.FileName} 使用 WinZip AES 加密，当前版本不支持该变体。");

            string dest = Path.Combine(outputDir, entry.FileName.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            log?.Invoke($"解包 {entry.FileName}（{SuperService.FormatSize(zipEntry.UncompressedSize)}）");

            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var source = OpenEntryStream(fs, zipEntry, password, log))
            {
                if (zipEntry.Method == 8)
                {
                    using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                    CopyStream(inflate, dst, ct);
                }
                else if (zipEntry.Method == 0)
                {
                    CopyStream(source, dst, ct);
                }
                else
                {
                    throw new NotSupportedException($"不支持的压缩方式 {zipEntry.Method}。");
                }
            }

            VerifyCrc(dest, zipEntry, log);

            done += Math.Max(1, zipEntry.UncompressedSize);
            progress?.Invoke(total > 0 ? (double)done / total : 0);
        }

        log?.Invoke($"解包完成，共 {entries.Count} 个条目 → {outputDir}");
    }

    private static Stream OpenEntryStream(FileStream fs, SimpleZipEntry entry, string password, Action<string>? log)
    {
        fs.Position = entry.LocalHeaderOffset;

        var head = new byte[30];
        if (ReadFully(fs, head, 0, 30) != 30) throw new InvalidDataException("本地文件头不完整。");
        if (BitConverter.ToUInt32(head, 0) != 0x04034B50)
            throw new InvalidDataException("本地文件头签名不匹配。");

        int nameLength  = BitConverter.ToUInt16(head, 26);
        int extraLength = BitConverter.ToUInt16(head, 28);
        Skip(fs, nameLength + extraLength);

        if (entry.Encrypted)
            return new ZipCryptoStream(fs, entry.CompressedSize, Encoding.UTF8.GetBytes(password), entry, log);

        return new SubStream(fs, entry.CompressedSize);
    }

    private static void VerifyCrc(string path, SimpleZipEntry entry, Action<string>? log)
    {
        if ((entry.Flags & 0x8) != 0 || entry.Crc == 0) return;

        try
        {
            using var fs = File.OpenRead(path);
            uint crc = Crc32.Compute(fs);
            if (crc != entry.Crc)
                log?.Invoke($"  ⚠ {entry.Name} 校验不一致（文件可能损坏）。");
        }
        catch
        {
            // 校验失败不影响解包结果
        }
    }

    /// <summary>ZipCrypto（PKWARE 传统加密）流式解密，构造时自动吞掉 12 字节加密头。</summary>
    private sealed class ZipCryptoStream : Stream
    {
        private readonly Stream _inner;
        private readonly uint[] _keys = { 0x12345678u, 0x23456789u, 0x34567890u };
        private readonly long _length;
        private long _position;

        public ZipCryptoStream(Stream inner, long compressedSize, byte[] password,
                               SimpleZipEntry entry, Action<string>? log)
        {
            _inner  = inner;
            _length = Math.Max(0, compressedSize - 12);

            foreach (byte value in password) UpdateKeys(value);

            var header = new byte[12];
            if (ReadFully(inner, header, 0, 12) != 12)
                throw new InvalidDataException("加密头不完整。");

            for (int i = 0; i < 12; i++)
            {
                byte cipher = header[i];
                header[i] = (byte)(cipher ^ DecryptByte());
                UpdateKeys(cipher);
            }

            byte check = (entry.Flags & 0x8) != 0
                ? (byte)(entry.DosTime >> 8)
                : (byte)(entry.Crc >> 24);

            if (header[11] != check)
                log?.Invoke($"  ⚠ {entry.Name} 口令校验未通过（若文件内容正常可忽略）。");
        }

        private void UpdateKeys(byte value)
        {
            unchecked
            {
                _keys[0] = Crc32.Update(_keys[0], value);
                _keys[1] = _keys[1] + (_keys[0] & 0xFF);
                _keys[1] = _keys[1] * 134775813 + 1;
                _keys[2] = Crc32.Update(_keys[2], (byte)(_keys[1] >> 24));
            }
        }

        private byte DecryptByte()
        {
            unchecked
            {
                uint temp = _keys[2] | 2;
                return (byte)((temp * (temp ^ 1)) >> 8);
            }
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;

            int want = (int)Math.Min(count, remaining);
            int read = _inner.Read(buffer, offset, want);

            for (int i = 0; i < read; i++)
            {
                byte cipher = buffer[offset + i];
                buffer[offset + i] = (byte)(cipher ^ DecryptByte());
                UpdateKeys(cipher);
            }

            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------- 工具 ----------------

    internal static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
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

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0) return;

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[4096];
        long remaining = count;
        while (remaining > 0)
        {
            int n = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, n);
            if (read <= 0) break;
            remaining -= read;
        }
    }

    private static void CopyStream(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
        }
    }
}

/// <summary>限定长度的只读子流。</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public SubStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _length - _position;
        if (remaining <= 0) return 0;

        int want = (int)Math.Min(count, remaining);
        int read = _inner.Read(buffer, offset, want);
        if (read > 0) _position += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>标准 CRC32（与 ZIP 一致）。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Update(uint crc, byte value) =>
        (crc >> 8) ^ Table[(crc ^ value) & 0xFF];

    public static uint Compute(Stream stream)
    {
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[1 << 20];

        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (int i = 0; i < read; i++) crc = Update(crc, buffer[i]);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
