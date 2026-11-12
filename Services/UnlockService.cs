using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// 设备解锁流程编排。
///
/// 目前只做 OPPO K10（PGJM10 / 天玑 8000-MAX / MT6895）的强制解锁：
/// 往 <c>ocdt</c> 分区写一份「一加 Ace 竞速版」的 ocdt.img，绕开 ColorOS 的 BL 锁校验，
/// 之后用 <c>fastboot flashing unlock</c> 正常解锁。步骤对照 <c>D:\解锁bl教程（oppok10）</c>。
///
/// 与教程里「刷机匣」的区别：这里不走第三方工具，直接调项目自带的 MTK 原生层
/// （penumbra）进 BROM/DA 模式写分区。代价是 DA 格式要对上：
/// penumbra 只认 <c>MTK_DOWNLOAD_AGENT</c> 容器，而刷机匣资源包给的是 DA1/DA2 分体文件，
/// 所以中间用 <see cref="MtkDaBuilder"/> 现打一个只含当前芯片条目的容器。
/// </summary>
public static class UnlockService
{
    /// <summary>从设备 devinfo 里读到的、选 DA 需要的字段。</summary>
    public sealed record DeviceInfo(ushort HwCode, ushort HwSubCode, string Chip, string Storage)
    {
        public string Describe()
            => $"{Chip} · hw_code=0x{HwCode:X4} · hw_sub=0x{HwSubCode:X4} · {Storage}";
    }

    public const string OcdtPartition = "ocdt";

    // ==================== 1. 读设备信息 ====================

    /// <summary>
    /// 只读 BROM/Preloader 的 devinfo，不进入 DA 模式，因此不会占用设备。
    /// 拿到的 hw_code / hw_sub_code 决定后面用哪个 DA 条目。
    /// </summary>
    public static async Task<(bool Ok, DeviceInfo? Info, string Message)> ReadDeviceInfoAsync(
        MtkOptions options, Action<string> log, CancellationToken ct = default)
    {
        log("正在读取设备信息（设备需处于关机后插线的 BROM/Preloader 状态）…");

        var (code, json) = await PenumbraService.ExecuteNativeAsync(options, new { op = "info" }, log, ct);

        if (code != 0 || string.IsNullOrWhiteSpace(json))
            return (false, null, "读取设备信息失败，原因见上方日志。");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hwCode = root.TryGetProperty("hw_code", out var hw) && hw.TryGetUInt16(out var h) ? h : (ushort)0;
            var hwSub  = root.TryGetProperty("hw_subcode", out var sub) && sub.TryGetUInt16(out var s) ? s : (ushort)0;
            var chip    = root.TryGetProperty("chip", out var c)    ? c.GetString() ?? "未知" : "未知";
            var storage = root.TryGetProperty("storage", out var st) ? st.GetString() ?? "未知" : "未知";

            if (hwCode == 0)
                return (false, null, "设备没有返回有效的芯片号，无法匹配 DA。");

            var info = new DeviceInfo(hwCode, hwSub, chip, storage);
            log($"设备信息：{info.Describe()}");
            return (true, info, "读取成功。");
        }
        catch (Exception ex)
        {
            return (false, null, $"解析设备信息失败：{ex.Message}");
        }
    }

    // ==================== 2. 准备 DA ====================

    /// <summary>
    /// 准备一份 penumbra 能用的 DA 容器。
    ///
    /// 两条路：
    ///   1. 目录里已经有容器 DA（MTK_DOWNLOAD_AGENT）且支持当前芯片 → 直接用；
    ///   2. 只有 DA1/DA2 分体文件（刷机匣资源包那种）→ 现场打包成容器。
    /// </summary>
    public static bool PrepareDa(string daDirectory,
                                 ushort hwCode,
                                 ushort hwSubCode,
                                 string outputContainer,
                                 Action<string> log,
                                 out string error)
    {
        error = string.Empty;

        if (!Directory.Exists(daDirectory))
        {
            error = $"DA 目录不存在：{daDirectory}";
            return false;
        }

        // ---- 优先找现成的容器 DA ----
        foreach (var file in Directory.EnumerateFiles(daDirectory, "*.bin", SearchOption.AllDirectories))
        {
            if (!MtkDaFile.TryRead(file, out var info, out _) || info is null) continue;

            if (info.Supports(hwCode, hwSubCode))
            {
                log($"找到匹配的 DA 容器：{Path.GetFileName(file)}");
                File.Copy(file, outputContainer, true);
                return true;
            }
        }

        // ---- 退化到 DA1/DA2 分体 ----
        var chipTag = hwCode.ToString("X4");
        var da1 = FindByKeyword(daDirectory, "DA1", chipTag);
        var da2 = FindByKeyword(daDirectory, "DA2", chipTag);

        if (da1 is null || da2 is null)
        {
            error = "DA 目录里既没有支持该芯片的容器 DA，也没有成对的 DA1/DA2 文件。\n\n" +
                    $"当前设备 hw_code=0x{hwCode:X4}、hw_sub=0x{hwSubCode:X4}，请准备对应芯片的 DA。";
            return false;
        }

        log($"未找到现成容器，改用分体 DA 打包：{Path.GetFileName(da1)} + {Path.GetFileName(da2)}");
        log($"协议版本推测：{MtkDaBuilder.DescribeProtocol(da1)}");

        if (!MtkDaBuilder.TryBuild(da1, da2, hwCode, hwSubCode,
                                   MtkDaBuilder.DefaultDa1Address, MtkDaBuilder.DefaultDa2Address,
                                   outputContainer, out error))
            return false;

        // 打完立刻用同一套解析器自检，避免把坏容器交给原生层再报一句看不懂的错
        if (!MtkDaFile.TryRead(outputContainer, out var built, out var readError) || built is null)
        {
            error = $"打包出的 DA 容器自检失败：{readError}";
            return false;
        }

        if (!built.Supports(hwCode, hwSubCode))
        {
            error = "打包出的 DA 容器条目与设备芯片不匹配（内部错误）。";
            return false;
        }

        log($"已生成 DA 容器：{outputContainer}");
        return true;
    }

    /// <summary>在目录里按关键词找 DA 分体文件，优先取文件名里带芯片号的那份。</summary>
    private static string? FindByKeyword(string directory, string keyword, string? preferredChip)
    {
        var candidates = Directory
            .EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;

        // 目录里可能有好几套 DA，文件名通常带芯片号（MT6895_DA1.bin），优先用对得上的
        if (!string.IsNullOrWhiteSpace(preferredChip))
        {
            var hit = candidates.FirstOrDefault(f =>
                Path.GetFileName(f).Contains(preferredChip, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return candidates[0];
    }

    // ==================== 3. 写 ocdt ====================

    /// <summary>
    /// 把 ocdt.img 写进 ocdt 分区。调用前必须已完成 <see cref="PrepareDa"/> 并把
    /// 生成的容器路径放进 <see cref="MtkOptions.DaFile"/>。
    /// </summary>
    public static async Task<(bool Ok, string Message)> FlashOcdtAsync(
        MtkOptions options, string ocdtImage, Action<string> log, CancellationToken ct = default)
    {
        if (!File.Exists(ocdtImage))
            return (false, $"ocdt 镜像不存在：{ocdtImage}");

        var size = new FileInfo(ocdtImage).Length;
        log($"开始写入 {OcdtPartition} 分区（{size / 1024} KB）…");

        var (code, _) = await PenumbraService.ExecuteNativeAsync(
            options,
            new { op = "write_partition", part = OcdtPartition, file = ocdtImage },
            log, ct);

        return code == 0
            ? (true, "ocdt 写入完成。")
            : (false, "ocdt 写入失败，原因见上方日志。");
    }

    // ==================== 4. 重启 ====================

    /// <summary>写完后重启进系统，让 ColorOS 重新读取 ocdt。</summary>
    public static async Task<(bool Ok, string Message)> RebootAsync(
        MtkOptions options, Action<string> log, CancellationToken ct = default)
    {
        log("正在重启设备…");

        var (code, _) = await PenumbraService.ExecuteNativeAsync(
            options, new { op = "reboot", mode = "normal" }, log, ct);

        return code == 0 ? (true, "已发送重启指令。") : (false, "重启指令发送失败。");
    }

    // ==================== 5. 槽位检查 ====================

    /// <summary>
    /// 目标槽位。教程要求必须是 A 槽：B 槽刷完 ocdt 有极大概率卡在开机无限重启。
    /// </summary>
    public const string TargetSlot = "A";

    /// <summary>
    /// 通过 adb 读当前槽位，返回 "A" / "B"。
    /// 手机关机或没开 USB 调试时读不到，这时返回 Ok=false，由界面提示用户手动确认。
    /// </summary>
    public static async Task<(bool Ok, string Slot, string Message)> ReadSlotAsync(
        Action<string> log, CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
            return (false, string.Empty, "未找到 adb，无法自动读取槽位。");

        log("正在读取当前槽位（手机需开机并已开启 USB 调试）…");

        foreach (var prop in new[] { "ro.boot.slot_suffix", "ro.boot.slot" })
        {
            var raw = await Task.Run(
                () => AdbService.Run(AdbService.AdbPath!, $"shell getprop {prop}"), ct);

            var slot = NormalizeSlot(raw);
            if (slot.Length == 0) continue;

            log($"当前槽位：{slot}（{prop} = {raw.Trim()}）");
            return (true, slot, slot == TargetSlot ? "当前是 A 槽位。" : "当前是 B 槽位，建议先切槽。");
        }

        return (false, string.Empty, "没能读到槽位：请确认手机已开机、USB 调试已开启并已授权本机。");
    }

    /// <summary>从 adb 输出里挑出 a / b 那一行，兼容 _a 与 a 两种写法。</summary>
    private static string NormalizeSlot(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var text = line.Trim().TrimStart('_').ToLowerInvariant();
            if (text is "a" or "b") return text.ToUpperInvariant();
        }

        return string.Empty;
    }

    // ==================== 6. FastBoot 衔接（教程 10~12） ====================

    /// <summary>让手机从系统重启到 bootloader，之后才能用 fastboot 下指令。</summary>
    public static async Task<bool> RebootToBootloaderAsync(Action<string> log, CancellationToken ct = default)
    {
        if (AdbService.AdbPath is null)
        {
            log("未找到 adb，无法自动重启到 FastBoot。");
            return false;
        }

        log("正在让手机重启到 FastBoot…");
        var output = await Task.Run(() => AdbService.Run(AdbService.AdbPath!, "reboot bootloader"), ct);

        // 成功时 adb 不输出任何内容；带 error 说明手机没连上或命令被拒
        if (output.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            log(output.Trim());
            return false;
        }

        return true;
    }

    /// <summary>轮询等设备进入 FastBoot，用于「刚重启完接着解锁」这种衔接场景。</summary>
    public static async Task<bool> WaitForFastbootAsync(
        Action<string> log, int timeoutMs = 45000, CancellationToken ct = default)
    {
        var waited = 0;

        while (waited < timeoutMs)
        {
            await Task.Delay(1500, ct);
            waited += 1500;

            if (FastbootService.DetectDevice() is not null)
            {
                log($"已检测到 FastBoot 设备（等待 {waited / 1000} 秒）。");
                return true;
            }
        }

        log("没有等到 FastBoot 设备。");
        return false;
    }

    /// <summary>
    /// 支持的机型表。界面按这张表列出可选设备，选中后从这里取资源目录与步骤，
    /// 所以以后加机型不用改界面代码。
    /// </summary>
    public static IReadOnlyList<UnlockDevice> Devices { get; } = new[]
    {
        new UnlockDevice
        {
            Id     = "oppok10",
            Name   = "OPPO K10",
            Model  = "PGJM10",
            Chip   = "MT6895",
            HwCode = 0x6895,
            Method = "ocdt 替换",
            Folder = Path.Combine("unlock", "oppok10"),
            Steps  = new[]
            {
                "确认系统是 ColorOS 13，打开 OEM 解锁与 USB 调试",
                "检查槽位，必须是 A 槽位；B 槽位先去切槽",
                "手机关机，按住音量键插线，进 BROM / Preloader 下载模式",
                "读取设备信息，核对芯片与所选机型一致",
                "开始解锁，写入一加 Ace 竞速版 ocdt",
                "开机若停在简体中文界面：格式化数据分区",
                "第 9 步：重新打开 OEM 解锁与 USB 调试，数据线重插一次选「文件传输」",
                "第 10 步：进入 FastBoot 并解锁，执行 fastboot flashing unlock",
                "第 11 步：手机上按一下音量上键，再按一下电源键（5 秒内完成）",
                "第 12 步：重启后开机出现三行橙色英文，才算解锁成功",
                "不刷 C15+ 系统的话，点「还原 ocdt」刷回原厂",
            },
        },
    };
}
