using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// Penumbra（联发科 MTK）设备通信封装。
///
/// 全部能力都由内置原生层 penumbra_ffi.dll 提供（进程内 P/Invoke），
/// 不再依赖 antumbra.exe 子进程，也没有文本输出解析。
///
/// 许可提示：
/// penumbra 由 Shomy 开发，以 AGPL-3.0-or-later 授权。
/// 源码已收编进仓库（native\penumbra），许可证全文见 tools\LICENSE-Penumbra.txt。
/// 上游：https://github.com/shomykohai/penumbra
/// </summary>
public static class PenumbraService
{
    /// <summary>内置原生层（penumbra_ffi.dll）是否可用。为 false 时所有设备操作都无法执行。</summary>
    public static bool IsAvailable => PenumbraNative.IsAvailable;

    /// <summary>
    /// 原生层的实时日志出口，由界面订阅。
    /// 原生层直接用 Penumbra 的 core，日志走 Rust 的 log facade，所以是全局回调。
    /// </summary>
    public static event Action<string>? NativeLog;

    private static bool _verboseLogging;

    /// <summary>
    /// 本次探测里，原生层是否报过「DA 已运行，但没有可用的 SLA 签名」。
    ///
    /// 一旦成立，DA 就停在受限模式：只注册了 DOWNLOAD-FILE / UPLOAD-FILE 这类无害命令，
    /// CMD:GET-HW-INFO、CMD:READ-PARTITION、CMD:SET-BOOT-MODE 一概不在命令表里，
    /// 全部回 0xc0010003 "Unsupported command"。这和「DA 没进去」是两回事——
    /// 此时分区表往往还能正常读出来，再提示「DA 没进到 DA 模式」就和日志自相矛盾了。
    ///
    /// 由 <see cref="OnNativeLog"/> 置位，<see cref="LogDeviceInfo"/> 在探测前后重置 / 读取。
    /// </summary>
    private static bool _daRestrictedBySla;

    /// <summary>
    /// 是否把原生层的 DEBUG / TRACE 日志也送进界面。默认关闭。
    ///
    /// 关掉时原生层上限停在 Info：找端口阶段 nusb 的逐设备枚举明细（"Probing device ..."）
    /// 是 debug 级，一轮一行、一秒十轮，开着会直接刷屏。排查设备识别不了的问题时再打开。
    /// </summary>
    public static bool VerboseLogging
    {
        get => _verboseLogging;
        set
        {
            _verboseLogging = value;
            // 5=Trace 覆盖 Debug 及以上；关闭时回到 3=Info，与原生层默认一致
            PenumbraNative.SetLogLevel(value ? 5 : 3);
        }
    }

    public static void Initialize()
    {
        if (PenumbraNative.IsAvailable)
        {
            PenumbraNative.AttachLogger();
            PenumbraNative.Log += OnNativeLog;
            // AttachLogger 会把上限复位成 Info，所以本机偏好要在它之后应用
            PenumbraNative.SetLogLevel(_verboseLogging ? 5 : 3);
        }
    }

    private static void OnNativeLog(int level, string message)
    {
        // 与 Rust log crate 的数值对齐：1=Error 2=Warn 3=Info 4=Debug 5=Trace
        var tag = level switch
        {
            1 => "ERROR",
            2 => "WARN ",
            3 => "INFO ",
            4 => "DEBUG",
            _ => "TRACE",
        };

        NativeLog?.Invoke($"[{tag}] {message}");

        // 原生层是在 DA 跑起来之后才发现没有签名可用的，这时会连着报：
        //   [INFO ] DA SLA is enabled
        //   [ERROR] No signer available for DA SLA! Can't proceed any further.
        //   [WARN ] DA SLA 未通过（Auth Error: ...），DA 已运行；需要 SLA 授权的操作可能被设备拒绝。
        // 只认后两句（它们才是「没签成」的结论），否则会把中性的 "SLA is enabled" 也算成失败。
        if (message.Contains("DA SLA 未通过", StringComparison.Ordinal)
            || message.Contains("No signer available for DA SLA", StringComparison.Ordinal))
        {
            _daRestrictedBySla = true;
        }
    }

    // ---------- 设备检测 ----------

    /// <summary>
    /// Penumbra 认可的 VID/PID，对应 native/penumbra/core/src/port/mod.rs 里的 KNOWN_PORTS。
    ///
    /// penumbra 只匹配这张表，表外的设备它完全视而不见——这是
    /// 「Waiting for MTK device...」一直不动的最常见原因：
    /// 手机停在普通 Android 模式时 PID 是 0x201C 之类，不在表内；
    /// 必须让设备回到 BROM（0x0003）或 Preloader（0x2000 / 0x6000 / 0x20FF / 0x3000）才会被认出来。
    /// </summary>
    private static readonly (ushort Vid, ushort Pid, string Mode)[] KnownPorts =
    {
        (0x0E8D, 0x0003, "BROM"),
        (0x0E8D, 0x6000, "Preloader"),
        (0x0E8D, 0x2000, "Preloader"),
        (0x0E8D, 0x2001, "DA"),
        (0x0E8D, 0x20FF, "Preloader"),
        (0x0E8D, 0x3000, "Preloader"),
        (0x1004, 0x6000, "Preloader（LG）"),
        (0x22D9, 0x0006, "Preloader（OPPO）"),
        (0x0FCE, 0xF200, "BROM（Sony）"),
        (0x0FCE, 0xD1E9, "BROM（Sony）"),
        (0x0FCE, 0xD1E2, "BROM（Sony）"),
        (0x0FCE, 0xD1EC, "BROM（Sony）"),
        (0x0FCE, 0xD1DD, "BROM（Sony）"),
    };

    /// <summary>设备管理器里的一个 MTK 相关 USB 端口。</summary>
    public sealed record MtkUsbPort(string InstanceId, ushort Vid, ushort Pid, string Mode,
                                    string? Driver, bool Online, bool Recognized)
    {
        /// <summary>形如 "0E8D:0003"。</summary>
        public string Id => $"{Vid:X4}:{Pid:X4}";

        /// <summary>该驱动能否被 penumbra 的 USB 后端直接打开。</summary>
        public bool DriverUsable => Driver is not null
            && (Driver.Equals("WinUSB",  StringComparison.OrdinalIgnoreCase)
             || Driver.Equals("libusbK", StringComparison.OrdinalIgnoreCase)
             || Driver.Equals("libusb0", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 枚举设备管理器里所有 MTK 相关端口，包含已经拔掉的残留记录。
    /// 目的是让界面能说清楚「有没有设备、是哪个模式、挂在什么驱动上」，
    /// 而不是只丢一句 Waiting for MTK device 让人干等。
    /// </summary>
    public static List<MtkUsbPort> EnumeratePorts()
    {
        var result = new List<MtkUsbPort>();

        try
        {
            const string query =
                "SELECT DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_0E8D%' " +
                "OR DeviceID LIKE '%VID_1004%' OR DeviceID LIKE '%VID_22D9%' OR DeviceID LIKE '%VID_0FCE%'";

            using var searcher = new ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get())
            {
                var instanceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                if (!TryParseVidPid(instanceId, out var vid, out var pid)) continue;

                var status = obj["Status"]?.ToString() ?? string.Empty;
                var known  = KnownPorts.FirstOrDefault(p => p.Vid == vid && p.Pid == pid);

                result.Add(new MtkUsbPort(
                    instanceId, vid, pid,
                    string.IsNullOrEmpty(known.Mode) ? "未知 PID（penumbra 不识别）" : known.Mode,
                    DriverOf(instanceId),
                    status.Equals("OK", StringComparison.OrdinalIgnoreCase),
                    !string.IsNullOrEmpty(known.Mode)));
            }
        }
        catch { /* WMI 不可用时忽略 */ }

        return result;
    }

    private static bool TryParseVidPid(string instanceId, out ushort vid, out ushort pid)
    {
        vid = pid = 0;

        var m = Regex.Match(instanceId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        if (!m.Success) return false;

        return ushort.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out pid);
    }

    /// <summary>
    /// 读设备绑定的驱动服务名。WinUSB / libusbK / libusb0 才能被 penumbra 的 USB 后端直接打开；
    /// 若是 usbser，说明设备挂在 VCOM 串口驱动上——Kamakiri 这类漏洞利用必须先用 Zadig 换成 WinUSB。
    /// </summary>
    private static string? DriverOf(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return key?.GetValue("Service") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// 面向界面的设备体检。返回 (是否可直接执行, 说明文本, 建议文本)。
    /// Ready 为 false 时说明 penumbra 注定等不到设备，此时应当先提示用户，而不是直接起进程干等。
    /// </summary>
    public static (bool Ready, string Text, string Hint) InspectDevice()
    {
        var ports = EnumeratePorts();

        if (ports.Count == 0)
            return (false, "当前设备端口：无", string.Empty);

        var online = ports.Where(p => p.Online).ToList();
        if (online.Count == 0)
        {
            var last = ports[0];
            return (false, $"当前设备端口：{last.Id}（离线）", string.Empty);
        }

        // 命中的都是表外 PID 时也不再拦一道：端口表是静态的，换过驱动的机型常以
        // 非标准 PID 出现，预判失败反而会挡住本来能连上的设备，直接交给原生层尝试。
        var ready = online.FirstOrDefault(p => p.Recognized) ?? online[0];

        return (true, $"当前设备端口：{ready.Id}（{ready.Mode}）", string.Empty);
    }

    // ---------- DA 文件 ----------
    //
    // DA 完全由用户显式选择：程序不再内置 DA，也不会按报错里的芯片号自动换一份。
    // 设备停在 Preloader / BROM 时必须先上传 DA 才能读写，而 DA 是按 hw_code + hw_sub_code
    // 双精确匹配挑条目的（device.rs 的 get_da_entry），配错就会在握手之后才失败。
    // 这里只做一件事：把所选 DA 支持的芯片写进日志备查，方便与设备实际芯片对照。

    // ---------- 设备熔断状态 ----------
    //
    // target_config 的位定义与 core/src/preloader/protocol.rs 的 get_target_config 注释一一对应。
    // 这几个位决定了失败到底属于哪一类：
    //   SLA / DAA        没有 auth 文件时 DA 根本传不上去（BROM 阶段就被拒）
    //   MemReadAuth      能进 DA 模式，但读内存仍需签名
    //   MemWriteAuth     能进 DA 模式，但写内存仍需签名
    // 没有这一行日志，「读不出分区」到底是没权限、没 auth 还是驱动问题，只能靠猜。

    private const uint SbcBit          = 0x1;
    private const uint SlaBit          = 0x2;
    private const uint DaaBit          = 0x4;
    private const uint EppParamBit     = 0x8;
    private const uint RootCertBit     = 0x10;
    private const uint MemReadAuthBit  = 0x20;
    private const uint MemWriteAuthBit = 0x40;
    private const uint CacheOpAuthBit  = 0x80;
    private const uint SctrlCertBit    = 0x100;

    /// <summary>把 target_config 拆成「开了哪些开关」，形如 "SBC、SLA、DAA"。</summary>
    private static string DescribeTargetConfig(uint config)
    {
        if (config == 0) return "未熔断（SBC/SLA/DAA 全关）";

        var flags = new List<string>(8);
        if ((config & SbcBit)          != 0) flags.Add("SBC");
        if ((config & SlaBit)          != 0) flags.Add("SLA");
        if ((config & DaaBit)          != 0) flags.Add("DAA");
        if ((config & EppParamBit)     != 0) flags.Add("EppParam");
        if ((config & RootCertBit)     != 0) flags.Add("RootCert");
        if ((config & MemReadAuthBit)  != 0) flags.Add("MemReadAuth");
        if ((config & MemWriteAuthBit) != 0) flags.Add("MemWriteAuth");
        if ((config & CacheOpAuthBit)  != 0) flags.Add("CacheOpAuth");
        if ((config & SctrlCertBit)    != 0) flags.Add("SctrlCert");

        var unknown = config & ~0x1FFu;
        if (unknown != 0) flags.Add($"未知位=0x{unknown:X8}");

        return string.Join("、", flags);
    }

    private static uint ReadUInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
           v.TryGetUInt32(out var n)
            ? n
            : 0u;

    private static string ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "未知"
            : "未知";

    /// <summary>
    /// 会话一建立就把设备的 hw_code / target_config 写进日志。
    ///
    /// 这是「读不出分区」这类问题的分诊点：MT6991 配原厂 oplus DA 时，
    /// 失败可能来自 BROM 阶段的 SLA/DAA 拒绝、DA 内部的内存读写授权位，
    /// 也可能只是 DA 没匹配上。三者表现都是「连上了但读不出」，只有这行日志能区分。
    ///
    /// 注意：这个操作**不是只读**。原生层为了报出 storage 会调用 `Device::get_storage()`，
    /// 而它内部是 `ensure_da_mode()`——也就是会真的进一次 DA 模式（含完整的 DA 上传）。
    /// 所以这里失败时设备可能已经被推进 DA／异常状态，不能当成无害的查询。
    /// </summary>
    private static PenumbraNative.PenumbraSession LogDeviceInfo(
        PenumbraNative.PenumbraSession session, MtkOptions options, Action<string> onOutput)
    {
        try
        {
            // GetInfo 会真的进一次 DA 模式（含完整 DA 上传，以及随之而来的 SLA 尝试），
            // 期间原生层若报出「SLA 未通过」，就说明 DA 起来了但停在受限模式。
            // 先清零，保证这个标志只反映本次探测，不带上一次会话的残留。
            _daRestrictedBySla = false;

            var info = session.GetInfo();

            var hwCode  = ReadUInt(info, "hw_code");
            var hwSub   = ReadUInt(info, "hw_subcode");
            var config  = ReadUInt(info, "target_config");
            var storage = ReadString(info, "storage");

            onOutput($"设备：hw_code=0x{hwCode:X4}/hw_sub=0x{hwSub:X4}，存储={storage}，" +
                     $"target_config=0x{config:X8}（{DescribeTargetConfig(config)}）");

            // 存储报不出来，基本就是「这一步隐式进 DA 模式没成功」——原生层会先打一条
            // [DA] 开头的 warn 说明原因，这里补一句指路，免得只看到一个 Unknown。
            if (string.Equals(storage, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (_daRestrictedBySla)
                {
                    onOutput("提示：存储类型未知，但不是「DA 没进去」——上面那条 [WARN] 已经说清楚了：DA 已运行，" +
                             "只是 SLA 没通过，于是停在受限模式。");
                    onOutput("提示：受限模式下该 DA 只注册了少数无害命令，读存储要用的 CMD:GET-HW-INFO、" +
                             "以及 CMD:READ-PARTITION / CMD:SET-BOOT-MODE 等都不在命令表里，一律回 0xc0010003 Unsupported command。");
                    onOutput("提示：这与补丁无关，卡点是缺一份匹配本机的 SLA 签名（Auth 文件）；没有它，这些命令不会开放。" +
                             "注意此时分区表通常仍能读出，两者病象不同。");
                }
                else
                {
                    onOutput("提示：存储类型未知，通常意味着 DA 没进到 DA 模式（上面若有 [DA] 开头的警告，" +
                             "那条才是真正原因；常见于设备拒了这份 DA）。");
                }
            }

            // 只对「会拦住 DA 上传」的两个位告警，MemReadAuth/MemWriteAuth 留给人自己判断：
            // 它们在部分平台上是常态，一律告警会变成狼来了。
            // 实测（MT6991 / DA v5）：无 Auth 文件时 DA 仍能上传并跑起来，真正卡住的是 DA 内部的
            // SLA 校验（dummy 签名被 "Not get DevFWInfo." 拒掉），所以这里不再说「BROM 会拒绝上传」。
            if ((config & (SlaBit | DaaBit)) != 0 && string.IsNullOrWhiteSpace(options.AuthFile))
            {
                onOutput("提示：设备报了 SLA/DAA 位，且未配 Auth 文件。有匹配机型的 Auth 文件就在「Auth 文件」里选上，" +
                         "否则 DA 可能在 BROM 或 DA 内部校验时被拒绝。");
            }
        }
        catch (Exception ex)
        {
            onOutput($"提示：读取设备目标配置失败（{ex.Message}），不影响后续操作。");
        }

        return session;
    }

    /// <summary>
    /// 把所选 DA 支持的芯片写进日志备查。
    ///
    /// DA 只认用户选的那一份：不做内置兜底，也不会按报错自动换一份。
    /// 没选就直说，剩下的交给原生层给出更权威的报错。
    /// 配置的 DA 解析不了时不拦截——文件未必真有问题。
    /// </summary>
    private static void LogDaFile(MtkOptions options, Action<string> onOutput)
    {
        if (string.IsNullOrWhiteSpace(options.DaFile))
        {
            onOutput("提示：未选择 DA 文件。设备停在 Preloader / BROM 时无法上传 DA，多数操作会失败。");
            return;
        }

        if (MtkDaFile.TryRead(options.DaFile, out var configured, out var error))
            onOutput($"DA：{configured!.FileName}（{configured.DescribeChips()}）");
        else
            onOutput($"提示：所选 DA 无法解析（{error}）——仍交给原生层尝试。");
    }

    // ---------- 会话生命周期 ----------

    /// <summary>
    /// 当前设备会话（跨操作复用）。
    ///
    /// 原生会话很贵：打开它要做 BROM 握手、上传并修补 DA。更要命的是设备一旦被推进 DA 模式
    /// 就会一直停在 DA 模式——关掉我们这边的句柄并不会让它退回 BROM。于是「每个操作各开一个会话」
    /// 的写法必然崩在第二次：拿着已经在跑 DA 的设备去做 BROM 握手，一直握到超时，
    /// 日志里只剩一句「设备初始化失败（BROM 握手 / DA 加载）: Timeout」。
    /// 所以这里把会话留着，配置没变就继续用同一个。
    /// </summary>
    private static readonly object SessionGate = new();

    /// <summary>只允许一个线程走「关旧会话 → 开新会话」：两个会话会同时占着同一台设备的 WinUSB 句柄。</summary>
    private static readonly SemaphoreSlim SessionOpenGate = new(1, 1);

    private static PenumbraNative.PenumbraSession? _session;
    private static string? _sessionKey;

    /// <summary>
    /// 会话身份：只有这几项变了才值得重开（重握手 + 重传 DA）。
    /// 用换行分隔，Windows 路径里不会出现换行符。
    /// </summary>
    private static string SessionKey(MtkOptions options, string backend)
        => string.Join('\n',
            backend,
            options.DaFile        ?? string.Empty,
            options.PreloaderFile ?? string.Empty,
            options.AuthFile      ?? string.Empty,
            options.UsbLog ? "1" : "0");

    /// <summary>配置对得上就返回缓存里的会话，否则返回 null。</summary>
    private static PenumbraNative.PenumbraSession? TryGetCachedSession(string key)
    {
        lock (SessionGate)
        {
            return _session is { } cached && _sessionKey == key ? cached : null;
        }
    }

    /// <summary>
    /// 取当前会话：配置没变就复用已经连上的那个，变了才重开。
    /// </summary>
    private static PenumbraNative.PenumbraSession AcquireSession(
        MtkOptions options, string backend, int timeoutMs, Action<string> onOutput)
    {
        // 身份按调用方传进来的配置算，不依赖任何「解析后回写」的结果：
        // 一是配置里本来就有全部身份信息，二是算身份不该为了读 DA 文件去碰磁盘。
        var key = SessionKey(options, backend);

        if (TryGetCachedSession(key) is { } hit)
        {
            onOutput("复用已连接的设备会话（跳过 BROM 握手）。");
            return hit;
        }

        SessionOpenGate.Wait();
        try
        {
            // 等锁这段时间别的操作可能已经把会话开好了。
            if (TryGetCachedSession(key) is { } ready)
            {
                onOutput("复用已连接的设备会话（跳过 BROM 握手）。");
                return ready;
            }

            // 配置变了：必须先关掉旧会话再开新的，否则后开的那个 Claim interface 会直接失败。
            CloseSession();

            var session = OpenSession(options, backend, timeoutMs, onOutput);

            lock (SessionGate)
            {
                _session    = session;
                _sessionKey = key;
            }

            return session;
        }
        finally
        {
            SessionOpenGate.Release();
        }
    }

    /// <summary>
    /// 丢掉缓存的会话。
    ///
    /// 只在确认「连接本身已经没了」时调用：像「设备只发日志不响应」这种只是协议层被拒，
    /// 连接其实还好好的，丢掉会话只会让下一次点操作又去重走一遍 BROM 握手。
    /// </summary>
    private static void DropSession(PenumbraNative.PenumbraSession session)
    {
        lock (SessionGate)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session    = null;
            _sessionKey = null;
        }

        session.Dispose();
    }

    /// <summary>
    /// 关掉当前设备会话并释放 WinUSB 句柄；下一次操作会重新做完整的 BROM 握手 + DA 加载。
    /// 换设备、改配置，或设备确实已经断开时调用。
    /// </summary>
    public static void CloseSession()
    {
        PenumbraNative.PenumbraSession? session;

        lock (SessionGate)
        {
            session     = _session;
            _session    = null;
            _sessionKey = null;
        }

        session?.Dispose();
    }

    /// <summary>
    /// 这个原生失败是不是「链路已经断了」。
    ///
    /// 判据是 penumbra 的错误前缀（见 core/src/error.rs）：Connection Error / I/O Error
    /// 都来自连接层，句柄已经不能再用；而 Protocol Error（设备只发日志不响应）之类只是协议层拒绝。
    /// </summary>
    private static bool IsConnectionLost(string message)
        => message.Contains("Connection Error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("I/O Error", StringComparison.OrdinalIgnoreCase);

    /// <summary>取操作的 JSON 名（"partitions" / "reboot" …）；取不到返回 null。</summary>
    private static string? OpName(object op)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(op));
            return doc.RootElement.TryGetProperty("op", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 打开原生会话。用哪份 DA 完全由调用方的配置决定，这一层不做任何替换／回退。
    /// </summary>
    /// <remarks>
    /// 保持 private：返回类型 <c>PenumbraNative.PenumbraSession</c> 的有效可访问性是 internal，
    /// 公开放出去会触发 CS0050（可访问性不一致）。
    /// </remarks>
    private static PenumbraNative.PenumbraSession OpenSession(MtkOptions options, string backend,
                                                              int timeoutMs, Action<string> onOutput)
    {
        onOutput($"正在连接设备（需处于 BROM / Preloader 模式），最长等待 {timeoutMs / 1000} 秒…");

        LogDaFile(options, onOutput);

        return LogDeviceInfo(
            new PenumbraNative.PenumbraSession(options, backend: backend, timeoutMs: timeoutMs),
            options, onOutput);
    }

    /// <summary>
    /// 读取设备分区表。
    ///
    /// 走内置原生层：拿到的是结构化数组，没有子进程、没有文本解析，
    /// 而且设备等待有超时上限，不会像 CLI 那样无限停在 "Waiting for MTK device"。
    /// </summary>
    /// <param name="useLibusbBackend">
    /// 必须与后续操作（<see cref="ExecuteNativeAsync"/> / <see cref="ExecuteBatchAsync"/>）传的值一致。
    ///
    /// 后端是会话身份的一部分（见 <see cref="SessionKey"/>）：这里写死 auto、读分区却传 libusb 的话，
    /// 两边算出来是两个不同的会话，于是「读分区表 → 读/写分区」每换一次操作都会先关掉旧会话再重开。
    /// 而设备一旦进了 DA 模式就再也握不上 BROM 的手，重开的会话必然超时，用户只能拔插一次设备。
    /// </param>
    public static async Task<(List<MtkPartition> Partitions, int ExitCode)> GetPartitionsAsync(
        MtkOptions options, Action<string> onOutput, CancellationToken ct = default,
        bool useLibusbBackend = false)
    {
        var backend = useLibusbBackend ? "libusb" : "auto";

        if (!PenumbraNative.IsAvailable)
        {
            onOutput("未找到 penumbra_ffi.dll，无法读取分区表。");
            return (new List<MtkPartition>(), 1);
        }

        try
        {
            // 原生层的失败要在与抛出点同一个调用栈里落地：只要异常跨出 Task.Run 才被捕获，
            // 调试器（"仅我的代码"）就会把「设备没找到」这种预期结果当成未处理异常停下来。
            var (partitions, code) = await Task.Run(() =>
            {
                PenumbraNative.PenumbraSession? session = null;

                try
                {
                    session = AcquireSession(options, backend, 30_000, onOutput);
                    return (session.GetPartitions(), 0);
                }
                catch (OperationCanceledException) { throw; }
                catch (PenumbraNativeException ex)
                {
                    onOutput($"原生调用失败：{ex.Message}");

                    // 只有链路真断了才丢掉会话；「设备只发日志不响应」这类协议层失败要留着会话，
                    // 否则下一次点操作又要对着已经在跑 DA 的设备重走一遍 BROM 握手。
                    if (session is not null && IsConnectionLost(ex.Message)) DropSession(session);

                    return (new List<MtkPartition>(), 1);
                }
            }, ct);

            return (partitions, code);
        }
        catch (OperationCanceledException)
        {
            onOutput("已取消。");
            return (new List<MtkPartition>(), -1);
        }
    }

    /// <summary>
    /// 统一的原生执行入口：取会话（已有就复用，没有才做 BROM 握手 + DA 加载）→ 执行一个操作
    /// → 成功时返回 (0, 结果 JSON)。
    ///
    /// op 是一个会被序列化成 JSON 的对象，例如：
    ///   new { op = "read_flash", part = "boot", file = @"C:\boot.img" }
    /// 全部操作名见 penumbra_ffi 的 pm_supported_ops()。
    /// 原生日志会通过 <see cref="NativeLog"/> 事件实时流出（界面提前订阅即可）。
    /// </summary>
    public static async Task<(int Code, string Result)> ExecuteNativeAsync(
        MtkOptions options, object op, Action<string> onOutput,
        CancellationToken ct = default, bool useLibusbBackend = false)
    {
        var backend = useLibusbBackend ? "libusb" : "auto";

        try
        {
            // 同上：抛出点即捕获点，避免「设备未就绪」这类预期失败在调试器里被当成未处理异常中断。
            return await Task.Run(() =>
            {
                PenumbraNative.PenumbraSession? session = null;

                try
                {
                    session = AcquireSession(options, backend, 30_000, onOutput);

                    // 进度节流：每 400ms 一条，结束时必出一条
                    var last = DateTime.MinValue;
                    session.OnProgress = (done, total) =>
                    {
                        var now    = DateTime.UtcNow;
                        var finish = total > 0 && done >= total;
                        if (!finish && (now - last).TotalMilliseconds < 400) return;
                        last = now;
                        onOutput(total > 0
                            ? $"进度: {done}/{total}（{done * 100 / total}%）"
                            : $"进度: {done} 字节");
                    };

                    var result = session.Execute(op);

                    // 重启 / 关机之后设备就不在原来的连接上了，句柄已经作废，别留着下次误用。
                    if (OpName(op) is "reboot" or "shutdown") CloseSession();

                    return (0, result);
                }
                catch (OperationCanceledException) { throw; }
                catch (PenumbraNativeException ex)
                {
                    onOutput($"失败：{ex.Message}");
                    if (session is not null && IsConnectionLost(ex.Message)) DropSession(session);
                    return (1, string.Empty);
                }
                catch (Exception ex)
                {
                    onOutput($"错误: {ex.Message}");
                    return (1, string.Empty);
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            onOutput("已取消。");
            return (-1, string.Empty);
        }
    }

    /// <summary>批量执行里的一项：label 只用于日志，op 才是交给原生层的 JSON 操作。</summary>
    public sealed record NativeBatchItem(string Label, object Op);

    /// <summary>
    /// 在一个会话里连续执行多个操作（批量备份 / 批量擦除）。
    ///
    /// 会话本身很贵：打开它要做 BROM 握手、加载并修补 DA。若逐个分区各开一次会话，
    /// 备份 30 个分区就要重复 30 遍。这里只握手一次，后续操作复用同一会话。
    ///
    /// 单个操作失败不会中断整批，但连续失败会被视为设备已断开并提前中止——
    /// 继续硬撑只会刷出一屏无意义的报错。返回 (成功数, 失败数)。
    /// </summary>
    public static async Task<(int Succeeded, int Failed)> ExecuteBatchAsync(
        MtkOptions options, IReadOnlyList<NativeBatchItem> items,
        Action<string> onOutput, CancellationToken ct = default, bool useLibusbBackend = false)
    {
        var backend     = useLibusbBackend ? "libusb" : "auto";
        var succeeded   = 0;
        var failed      = 0;
        var consecutive = 0;

        try
        {
            await Task.Run(() =>
            {
                // 同上：会话阶段的原生异常就地落地，别让它跨出 Task.Run。
                PenumbraNative.PenumbraSession? opened = null;

                try
                {
                    var session = AcquireSession(options, backend, 30_000, onOutput);
                    opened = session;

                    // 进度节流：每 400ms 一条，结束时必出一条
                    var last = DateTime.MinValue;
                    session.OnProgress = (done, total) =>
                    {
                        var now    = DateTime.UtcNow;
                        var finish = total > 0 && done >= total;
                        if (!finish && (now - last).TotalMilliseconds < 400) return;
                        last = now;
                        onOutput(total > 0
                            ? $"进度: {done}/{total}（{done * 100 / total}%）"
                            : $"进度: {done} 字节");
                    };

                    for (var i = 0; i < items.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var item = items[i];

                        try
                        {
                            session.Execute(item.Op);
                            succeeded++;
                            consecutive = 0;
                            onOutput($"[{i + 1}/{items.Count}] {item.Label} 完成");
                        }
                        catch (PenumbraNativeException ex)
                        {
                            failed++;
                            consecutive++;
                            onOutput($"[{i + 1}/{items.Count}] {item.Label} 失败：{ex.Message}");

                            if (consecutive < 3) continue;

                            onOutput("连续 3 项失败，设备可能已断开，批量操作提前中止。");
                            if (IsConnectionLost(ex.Message)) DropSession(session);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (PenumbraNativeException ex)
                {
                    onOutput($"会话失败：{ex.Message}");
                    if (opened is not null && IsConnectionLost(ex.Message)) DropSession(opened);
                    failed++;
                }
                catch (Exception ex)
                {
                    onOutput($"错误: {ex.Message}");
                    failed++;
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            onOutput($"已取消（成功 {succeeded} 项，失败 {failed} 项）。");
        }

        return (succeeded, failed);
    }

}
