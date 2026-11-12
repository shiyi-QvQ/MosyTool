using System;
using System.IO;
using System.Threading;

namespace Mosytool.Services;

/// <summary>
/// Android sparse 镜像（magic 0xED26FF3A）识别与展开。
/// super 的逻辑分区数据必须是 raw，所以合包前要先展开；
/// fastboot 能直接吃 sparse，所以输出镜像时保持原样即可。
/// </summary>
public static class SparseImage
{
    public const uint Magic = 0xED26FF3A;

    private const ushort ChunkRaw      = 0xCAC1;
    private const ushort ChunkFill     = 0xCAC2;
    private const ushort ChunkDontCare = 0xCAC3;
    private const ushort ChunkCrc32    = 0xCAC4;

    public static bool IsSparse(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return ReadExactly(fs, head) == 4 && BitConverter.ToUInt32(head) == Magic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>镜像展开成 raw 后的大小（字节）。</summary>
    public static long GetRawLength(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[28];
        if (ReadExactly(fs, head) == 28 && BitConverter.ToUInt32(head) == Magic)
        {
            uint blkSize   = BitConverter.ToUInt32(head[12..]);
            uint totalBlks = BitConverter.ToUInt32(head[16..]);
            return (long)blkSize * totalBlks;
        }
        return fs.Length;
    }

    /// <summary>sparse 展开写入 <paramref name="output"/>，返回写入字节数。</summary>
    public static long Decompress(string path, Stream output,
                                  Action<double>? progress = null, CancellationToken ct = default)
    {
        using var fs = File.OpenRead(path);
        return Decompress(fs, output, progress, ct);
    }

    /// <summary>sparse 展开写入 <paramref name="output"/>，返回写入字节数。</summary>
    public static long Decompress(Stream input, Stream output,
                                  Action<double>? progress = null, CancellationToken ct = default)
    {
        Span<byte> head = stackalloc byte[28];
        if (ReadExactly(input, head) != 28 || BitConverter.ToUInt32(head) != Magic)
            throw new InvalidDataException("不是 Android sparse 镜像。");

        int    fileHdrSize  = BitConverter.ToUInt16(head[8..]);
        int    chunkHdrSize = BitConverter.ToUInt16(head[10..]);
        uint   blkSize      = BitConverter.ToUInt32(head[12..]);
        ulong  totalBlks    = BitConverter.ToUInt32(head[16..]);
        uint   totalChunks  = BitConverter.ToUInt32(head[20..]);

        if (blkSize == 0 || chunkHdrSize < 12) throw new InvalidDataException("sparse 头部参数非法。");

        // 跳过 file header 的剩余部分
        Skip(input, fileHdrSize - 28);

        long totalBytes = (long)totalBlks * blkSize;
        long written    = 0;

        var  chunkHdr = new byte[chunkHdrSize];
        var  buffer   = new byte[1 << 20];
        byte[] zeros  = new byte[1 << 20];

        for (uint i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadExactly(input, chunkHdr, 0, chunkHdrSize);

            ushort type    = BitConverter.ToUInt16(chunkHdr, 0);
            uint   chunkSz = BitConverter.ToUInt32(chunkHdr, 4);
            uint   totalSz = BitConverter.ToUInt32(chunkHdr, 8);
            long   dataSz  = (long)totalSz - chunkHdrSize;
            long   outSz   = (long)chunkSz * blkSize;

            switch (type)
            {
                case ChunkRaw:
                    Copy(input, output, dataSz, buffer, ref written, totalBytes, progress, ct);
                    break;

                case ChunkFill:
                {
                    var fill = new byte[4];
                    ReadExactly(input, fill, 0, 4);
                    Skip(input, dataSz - 4);
                    WriteRepeat(output, fill, outSz, buffer, ref written, totalBytes, progress, ct);
                    break;
                }

                case ChunkDontCare:
                {
                    Skip(input, dataSz);
                    WriteZeros(output, outSz, zeros, ref written, totalBytes, progress, ct);
                    break;
                }

                case ChunkCrc32:
                    Skip(input, dataSz);
                    break;

                default:
                    throw new InvalidDataException($"未知的 sparse chunk 类型 0x{type:X4}。");
            }
        }

        return written;
    }

    // ---------------- 内部工具 ----------------

    private static void Copy(Stream input, Stream output, long length, byte[] buffer,
                             ref long written, long total, Action<double>? progress, CancellationToken ct)
    {
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, length);
            int read = input.Read(buffer, 0, want);
            if (read <= 0) throw new EndOfStreamException("sparse 镜像数据不完整。");
            output.Write(buffer, 0, read);
            length  -= read;
            written += read;
            progress?.Invoke(total > 0 ? (double)written / total : 0);
        }
    }

    private static void WriteZeros(Stream output, long length, byte[] zeros,
                                   ref long written, long total, Action<double>? progress, CancellationToken ct)
    {
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(zeros.Length, length);
            output.Write(zeros, 0, n);
            length  -= n;
            written += n;
            progress?.Invoke(total > 0 ? (double)written / total : 0);
        }
    }

    private static void WriteRepeat(Stream output, byte[] pattern4, long length, byte[] buffer,
                                    ref long written, long total, Action<double>? progress, CancellationToken ct)
    {
        for (int i = 0; i + 4 <= buffer.Length; i += 4) pattern4.CopyTo(buffer, i);

        long rest = length;
        while (rest > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, rest);
            output.Write(buffer, 0, n);
            rest    -= n;
            written += n;
            progress?.Invoke(total > 0 ? (double)written / total : 0);
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0) return;

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> tmp = stackalloc byte[4096];
        while (count > 0)
        {
            int n = (int)Math.Min(tmp.Length, count);
            int read = ReadExactly(stream, tmp[..n]);
            if (read <= 0) throw new EndOfStreamException("镜像数据不完整。");
            count -= read;
        }
    }

    private static int ReadExactly(Stream stream, Span<byte> buffer) =>
        ReadExactly(stream, buffer, 0, buffer.Length);

    private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
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

    private static int ReadExactly(Stream stream, Span<byte> buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer.Slice(offset + total, count - total));
            if (read <= 0) break;
            total += read;
        }
        return total;
    }
}
