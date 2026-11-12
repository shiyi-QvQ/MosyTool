using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mosytool.Services;

/// <summary>DA 容器里的一个芯片条目，只保留判断「配得上配不上」所必需的两个字段。</summary>
/// <param name="HwCode">芯片号，对应设备 devinfo 的 hw_code（MT6835 = 0x1209）。</param>
/// <param name="HwSubCode">芯片子型号号（本机 MT6835 = 0x8A00）。</param>
public sealed record MtkDaEntry(ushort HwCode, ushort HwSubCode);

/// <summary>一份解析成功的 DA 文件。</summary>
public sealed record MtkDaInfo(string Path, IReadOnlyList<MtkDaEntry> Entries)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>penumbra 的匹配规则：hw_code 与 hw_sub_code 必须同时相等。</summary>
    public bool Supports(ushort hwCode, ushort hwSubCode)
        => Entries.Any(e => e.HwCode == hwCode && e.HwSubCode == hwSubCode);

    /// <summary>形如 "hw_code=0x1209/hw_sub=0x8A00"，多个条目用「、」连接。</summary>
    public string DescribeChips()
        => Entries.Count == 0
            ? "未解析到芯片条目"
            : string.Join("、", Entries.Select(e => $"hw_code=0x{e.HwCode:X4}/hw_sub=0x{e.HwSubCode:X4}"));
}

/// <summary>
/// MTK DA（Download Agent）容器的最小只读解析。
///
/// 存在的理由：penumbra 进 DA 模式时按 hw_code + hw_sub_code **双精确匹配**在 DA 里找条目
/// （见 native/penumbra/core/src/device.rs 的 <c>get_da_entry</c>），找不到就抛 <c>NoCompatibleDa</c>。
/// 也就是说「DA 选错了」的表现不是文件损坏，而是握手成功之后莫名其妙地失败——
/// 这类问题最难自查，所以这里把 DA 声明支持的芯片提前列出来，让「配错 DA」变成
/// 一句能直接看懂的结论。
///
/// 容器布局（与 core/src/da 的解析一致，已用实机 DA 逐字段核对）：
///   0x00  ASCII[..0x20]  魔数 "MTK_DOWNLOAD_AGENT"
///   0x60  u32            DA 版本
///   0x64  u32            容器签名 0x22668899
///   0x68  u32            条目数
///   0x6C  条目数组，每条固定 0xDC 字节：
///         +0x00 u16 条目魔数 0xDADA
///         +0x02 u16 hw_code
///         +0x04 u16 hw_sub_code
/// </summary>
public static class MtkDaFile
{
    private const int    MagicLength     = 0x20;
    private const string Magic           = "MTK_DOWNLOAD_AGENT";
    private const int    SignatureOffset = 0x64;
    private const uint   Signature       = 0x22668899;
    private const int    CountOffset     = 0x68;
    private const int    EntryBase       = 0x6C;
    private const int    EntrySize       = 0xDC;
    private const ushort EntryMagic      = 0xDADA;

    /// <summary>条目数的合理上界：实机 DA 只有个位数条目，超过说明文件已损坏。</summary>
    private const uint MaxEntries = 512;

    /// <summary>
    /// 解析 DA 文件。失败只说明「这不是一份能认出来的 DA」，不会改动文件本身。
    /// </summary>
    public static bool TryRead(string? path, out MtkDaInfo? info, out string error)
    {
        info  = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "未指定文件。";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = $"读取失败：{ex.Message}";
            return false;
        }

        if (bytes.Length < EntryBase + EntrySize)
        {
            error = "文件过小，不可能是 DA 容器。";
            return false;
        }

        var magic = Encoding.ASCII
            .GetString(bytes, 0, Math.Min(MagicLength, bytes.Length))
            .TrimEnd('\0', ' ');

        if (!magic.StartsWith(Magic, StringComparison.Ordinal))
        {
            error = $"文件头不是 \"{Magic}\"（实际 \"{magic}\"）。";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SignatureOffset)) != Signature)
        {
            error = "DA 容器签名不匹配，文件可能已损坏。";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(CountOffset));
        if (count == 0 || count > MaxEntries)
        {
            error = $"条目数异常（{count}）。";
            return false;
        }

        var entries = new List<MtkDaEntry>((int)count);

        for (uint i = 0; i < count; i++)
        {
            var offset = EntryBase + (int)i * EntrySize;
            if (offset + EntrySize > bytes.Length) break;

            var entry = bytes.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) != EntryMagic) break;

            entries.Add(new MtkDaEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[4..])));
        }

        info = new MtkDaInfo(path, entries);
        return true;
    }
}
