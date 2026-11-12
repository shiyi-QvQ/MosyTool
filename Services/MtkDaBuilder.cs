using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Mosytool.Services;

/// <summary>
/// 把「DA1 + DA2 分体」的文件打包成 penumbra 认得的 <c>MTK_DOWNLOAD_AGENT</c> 容器。
///
/// 为什么需要它：刷机匣这类第三方工具的资源包里，一个芯片配两份文件
/// （<c>MT6895_DA1.bin</c> 加载到 0x200000、<c>MT6895_DA2.bin</c> 加载到 0x40000000），
/// 而 penumbra 只接受容器格式——它按 <c>hw_code + hw_sub_code</c> 在容器条目里找 DA，
/// 再从条目的 region 里分别取 DA1/DA2 的偏移、长度和加载地址（见 hacc 的 da.rs）。
///
/// 容器布局（逐字段与 hacc::Da 的解析对齐）：
///   0x00  [32]  魔数 "MTK_DOWNLOAD_AGENT"
///   0x20  [64]  描述（留空）
///   0x60  u32   头部版本 4（V4 才带 hw_sub_code）
///   0x64  u32   容器签名 0x22668899
///   0x68  u32   条目数
///   0x6C  条目（V4 固定 0xDC 字节）
///         +0x00 u16 0xDADA / +0x02 hw_code / +0x04 hw_sub_code
///         +0x06 hw_ver / +0x08 sw_ver / +0x0A 保留 / +0x0C feature_set
///         +0x10 u16 entry_index / +0x12 u16 region_count
///         +0x14 region[10]，每个 0x14 字节：offset / length / addr / region_length / sig_len
///
/// 注意 <c>region_count</c> 必须是 2：hacc 的 <c>da1()</c>/<c>da2()</c> 在 count==2 时
/// 取 region[0]/region[1]，其余情况才偏移一位去跳过某个头 region。
/// </summary>
public static class MtkDaBuilder
{
    private const int    HeaderSize  = 0x6C;
    private const int    EntrySize   = 0xDC;
    private const int    RegionSize  = 0x14;
    private const int    MaxRegions  = 10;
    private const uint   Magic       = 0x22668899;
    private const uint   HeaderVer   = 4;      // V4：条目里带 hw_sub_code
    private const ushort EntryMagic  = 0xDADA;
    private const string IdMagic     = "MTK_DOWNLOAD_AGENT";

    /// <summary>DA1 常见加载地址（与刷机匣配置里的 DA1Address 一致）。</summary>
    public const uint DefaultDa1Address = 0x0020_0000;

    /// <summary>DA2 常见加载地址。</summary>
    public const uint DefaultDa2Address = 0x4000_0000;

    /// <summary>
    /// 打包成容器。失败时 <paramref name="error"/> 给出能直接看懂的原因。
    /// </summary>
    public static bool TryBuild(string da1Path,
                                string da2Path,
                                ushort hwCode,
                                ushort hwSubCode,
                                uint da1Address,
                                uint da2Address,
                                string outputPath,
                                out string error)
    {
        error = string.Empty;

        byte[] da1, da2;
        try
        {
            if (!File.Exists(da1Path)) { error = $"DA1 不存在：{da1Path}"; return false; }
            if (!File.Exists(da2Path)) { error = $"DA2 不存在：{da2Path}"; return false; }

            da1 = File.ReadAllBytes(da1Path);
            da2 = File.ReadAllBytes(da2Path);
        }
        catch (Exception ex)
        {
            error = $"读取 DA 失败：{ex.Message}";
            return false;
        }

        if (da1.Length == 0) { error = "DA1 是空文件。"; return false; }
        if (da2.Length == 0) { error = "DA2 是空文件。"; return false; }

        // 容器里 offset/length 都是 u32，超了就没法表示
        var total = (long)HeaderSize + EntrySize + da1.Length + da2.Length;
        if (total > uint.MaxValue) { error = "DA 体积超出容器上限（4 GB）。"; return false; }

        try
        {
            using var fs = File.Create(outputPath);
            using var w  = new BinaryWriter(fs);

            // ---- 头部 ----
            var id = new byte[32];
            Encoding.ASCII.GetBytes(IdMagic).CopyTo(id, 0);
            w.Write(id);                          // 0x00 魔数
            w.Write(new byte[64]);                // 0x20 描述
            w.Write(HeaderVer);                   // 0x60
            w.Write(Magic);                       // 0x64
            w.Write(1u);                          // 0x68 条目数

            // ---- 条目 ----
            var entryStart = HeaderSize;
            var da1Offset  = (uint)(HeaderSize + EntrySize);
            var da2Offset  = (uint)(da1Offset + da1.Length);

            w.Write(EntryMagic);                  // +0x00
            w.Write(hwCode);                      // +0x02
            w.Write(hwSubCode);                   // +0x04
            w.Write((ushort)0);                   // +0x06 hw_ver
            w.Write((ushort)0);                   // +0x08 sw_ver
            w.Write((ushort)0);                   // +0x0A 保留
            w.Write(0u);                          // +0x0C feature_set
            w.Write((ushort)0);                   // +0x10 entry_index
            w.Write((ushort)2);                   // +0x12 region_count：必须为 2

            WriteRegion(w, da1Offset, (uint)da1.Length, da1Address);
            WriteRegion(w, da2Offset, (uint)da2.Length, da2Address);

            // 剩下 8 个 region 槽位补零，条目总长固定 0xDC
            var written = 2 * RegionSize;
            w.Write(new byte[MaxRegions * RegionSize - written]);

            // 防御：条目必须正好 0xDC，否则后面所有偏移都会错位
            if (fs.Position != entryStart + EntrySize)
            {
                error = $"内部错误：条目长度 {fs.Position - entryStart} 不等于 {EntrySize}。";
                return false;
            }

            // ---- 数据区 ----
            w.Write(da1);
            w.Write(da2);
            w.Flush();

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"写入容器失败：{ex.Message}";
            return false;
        }
    }

    private static void WriteRegion(BinaryWriter w, uint offset, uint length, uint address)
    {
        w.Write(offset);         // 数据在文件里的偏移
        w.Write(length);         // 该 region 的总长度
        w.Write(address);        // 加载到设备的地址
        w.Write(length);         // region_length = length - sig_len（此处不剥离签名）
        w.Write(0u);             // sig_len
    }

    /// <summary>
    /// 猜测 DA 的协议版本：hacc 用 DA1 里是否含特定串来判断。
    /// 这里只用于提示，实际判定由原生层做。
    /// </summary>
    public static string DescribeProtocol(string da1Path)
    {
        try
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(da1Path));
            if (text.Contains("REVEIVING DEV CTRL CMD", StringComparison.Ordinal)) return "V5 (XFlash)";
            if (text.Contains("<?xml version=", StringComparison.Ordinal))         return "V6 (XML)";
            return "V3 (不支持)";
        }
        catch
        {
            return "未知";
        }
    }
}
