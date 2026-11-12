using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mosytool.Models;

namespace Mosytool.Services;

/// <summary>
/// penumbra_ffi.dll 的 P/Invoke 封装——把 Penumbra 的 core 直接内置进进程，
/// 取代原先「起 antumbra.exe 子进程 + 正则解析文本输出」的做法。
///
/// 相比 CLI 路径的收益：
///   * 不再有子进程、没有 -c 防 TUI 的 hack、不用 ArgumentList 转义；
///   * 分区表返回的是结构化数组，不需要再按文本行做正则解析；
///   * 设备等待有超时上限，不会再无限停在 "Waiting for MTK device"；
///   * 日志与进度是实时回调，不必再去逐行嗅探 stdout。
///
/// 调用约定：Rust 侧导出的是 extern "C"，x86 下即 cdecl，因此显式写 Cdecl。
/// 字符串统一走 UTF-8；Rust 侧分配、由 <c>pm_string_free</c> 释放，托管侧只读不写。
/// </summary>
internal static class PenumbraNative
{
    private const string DllName = "penumbra_ffi";

    /// <summary>
    /// 原生库统一放在 tools\ 下，默认解析不会去子目录找，因此需要一个自定义解析器。
    /// 解析器由 <see cref="NativeResolver"/> 集中注册——一个程序集只能注册一次，
    /// 后面的 qualcomm_ffi 还要共用它，所以这里只触发注册、不自己调用 SetDllImportResolver。
    /// </summary>
    static PenumbraNative()
    {
        NativeResolver.EnsureRegistered();
    }

    // ---------------------------------------------------------------- 原生声明

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr message, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ProgressCallback(ulong done, ulong total, IntPtr user);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pm_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pm_set_log_cb(LogCallback? cb, IntPtr user);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pm_set_log_level(int level);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pm_open([MarshalAs(UnmanagedType.LPUTF8Str)] string configJson,
                                         out IntPtr outErr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pm_close(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pm_set_progress_cb(IntPtr session, ProgressCallback? cb, IntPtr user);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pm_execute(IntPtr session,
                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string opJson,
                                         out IntPtr outJson,
                                         out IntPtr outErr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pm_string_free(IntPtr s);

    // ---------------------------------------------------------------- 可用性

    private static bool? _available;
    private static string? _version;

    /// <summary>penumbra_ffi.dll 是否可加载。首次调用后结果被缓存。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is { } cached) return cached;

            try
            {
                _version   = Marshal.PtrToStringUTF8(pm_version());
                _available = true;
            }
            catch (DllNotFoundException)      { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            catch (BadImageFormatException)    { _available = false; }

            return _available.Value;
        }
    }

    /// <summary>形如 "penumbra-ffi 0.1.0"，不可用时为 null。</summary>
    public static string? Version => IsAvailable ? _version : null;

    // ---------------------------------------------------------------- 全局日志

    private static LogCallback? _logTrampoline;

    /// <summary>Rust 侧的 log 消息。level 为 log crate 数值：1=Error 2=Warn 3=Info 4=Debug 5=Trace。</summary>
    public static event Action<int, string>? Log;

    /// <summary>安装全局日志转发。可重复调用。</summary>
    public static void AttachLogger()
    {
        if (!IsAvailable) return;

        _logTrampoline ??= OnNativeLog;
        // 保留委托实例，否则 GC 回收后原生侧调用会崩
        pm_set_log_cb(_logTrampoline, IntPtr.Zero);
    }

    /// <summary>
    /// 调整原生层日志上限（log crate 数值：1=Error 2=Warn 3=Info 4=Debug 5=Trace）。
    ///
    /// 原生层默认就是 Info。被挡住的 DEBUG 不是可有可无的冗余，而是真会淹掉界面：
    /// 找端口的循环 100ms 一轮、最长跑满 30 秒，而 nusb 每枚举一台 USB 设备就打一条 debug，
    /// 一轮的行数等于「这台电脑上所有 USB 设备」的数量。只有排查设备识别时才该调高。
    ///
    /// 注意 <see cref="AttachLogger"/> 会把上限复位回 Info，所以要在它之后再调用。
    /// 旧版 DLL 没有这个导出，这种情况静默保持默认值即可。
    /// </summary>
    public static void SetLogLevel(int level)
    {
        if (!IsAvailable) return;

        try { pm_set_log_level(level); }
        catch (EntryPointNotFoundException) { /* 旧版 DLL：无此导出 */ }
        catch (DllNotFoundException) { /* 加载失败，IsAvailable 已经兜住 */ }
    }

    private static void OnNativeLog(int level, IntPtr message, IntPtr user)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(message);
            if (!string.IsNullOrEmpty(text)) Log?.Invoke(level, text);
        }
        catch { /* 日志回调绝不能把异常抛回原生栈 */ }
    }

    /// <summary>把原生字符串读出来并释放。</summary>
    private static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUTF8(p) ?? string.Empty; }
        finally { pm_string_free(p); }
    }

    // ---------------------------------------------------------------- 配置

    private sealed record OpenConfig(
        [property: JsonPropertyName("vid")]        ushort? Vid,
        [property: JsonPropertyName("pid")]        ushort? Pid,
        [property: JsonPropertyName("backend")]    string  Backend,
        [property: JsonPropertyName("da")]         string? Da,
        [property: JsonPropertyName("preloader")]  string? Preloader,
        [property: JsonPropertyName("auth")]       string? Auth,
        [property: JsonPropertyName("usb_log")]    bool    UsbLog,
        [property: JsonPropertyName("timeout_ms")] int     TimeoutMs);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------------------------------------------------------- 会话

    /// <summary>
    /// 一次设备会话。持有原生句柄，内部用信号量保证同一句柄上的调用是串行的
    /// （Rust 侧契约要求串行；<see cref="ExecuteAsync"/> 会切到线程池，必须自己兜住）。
    /// </summary>
    public sealed class PenumbraSession : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ProgressCallback _progressTrampoline;
        private IntPtr _handle;
        private bool _disposed;

        /// <summary>传输进度回调，(done, total)。total 为 0 表示总量未知。</summary>
        public Action<ulong, ulong>? OnProgress { get; set; }

        /// <summary>打开设备并完成 BROM 握手 + DA 加载。失败抛 <see cref="PenumbraNativeException"/>。</summary>
        public PenumbraSession(MtkOptions options, ushort? vid = null, ushort? pid = null,
                               string backend = "auto", int timeoutMs = 30_000)
        {
            if (!IsAvailable)
                throw new PenumbraNativeException("penumbra_ffi.dll 不可用。");

            var cfg = new OpenConfig(
                vid, pid, backend,
                NullIfBlank(options.DaFile),
                NullIfBlank(options.PreloaderFile),
                NullIfBlank(options.AuthFile),
                options.UsbLog,
                timeoutMs);

            var handle = pm_open(JsonSerializer.Serialize(cfg, JsonOpts), out var errPtr);
            var error  = TakeString(errPtr);

            if (handle == IntPtr.Zero)
                throw new PenumbraNativeException(
                    string.IsNullOrEmpty(error) ? "打开设备失败。" : error);

            _handle = handle;

            _progressTrampoline = OnNativeProgress;
            pm_set_progress_cb(_handle, _progressTrampoline, IntPtr.Zero);
        }

        private void OnNativeProgress(ulong done, ulong total, IntPtr user)
        {
            try { OnProgress?.Invoke(done, total); }
            catch { /* 进度回调同样不能把异常抛回原生栈 */ }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        /// <summary>
        /// 执行一次操作。同步阻塞，成功返回结果 JSON，失败抛 <see cref="PenumbraNativeException"/>。
        /// </summary>
        public string Execute(object operation)
        {
            ThrowIfDisposed();
            _gate.Wait();
            try
            {
                var rc = pm_execute(_handle, JsonSerializer.Serialize(operation, JsonOpts),
                                    out var jsonPtr, out var errPtr);

                var json  = TakeString(jsonPtr);
                var error = TakeString(errPtr);

                if (rc != 0)
                    throw new PenumbraNativeException(
                        string.IsNullOrEmpty(error) ? $"原生调用失败（rc={rc}）。" : error);

                return json;
            }
            finally { _gate.Release(); }
        }

        /// <summary>在线程池上执行，避免长耗时读写卡住 UI 线程。</summary>
        public Task<string> ExecuteAsync(object operation, CancellationToken ct = default)
            => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return Execute(operation);
            }, ct);

        // -------------------------------------------------------- 具体操作

        public JsonElement GetInfo()
            => JsonDocument.Parse(Execute(new { op = "info" })).RootElement.Clone();

        /// <summary>读分区表。返回结构化数据，无需再解析文本。</summary>
        public List<MtkPartition> GetPartitions()
        {
            var json   = Execute(new { op = "partitions" });
            var result = new List<MtkPartition>();

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var size = item.GetProperty("size").GetUInt64();
                result.Add(new MtkPartition
                {
                    Name      = item.GetProperty("name").GetString() ?? string.Empty,
                    Address   = item.GetProperty("address").GetUInt64(),
                    Size      = size,
                    SizeHuman = HumanSize(size),
                    Section   = item.GetProperty("kind").GetString() ?? string.Empty,
                });
            }

            return result;
        }

        public Task<string> ReadFlashAsync(string partition, string file, CancellationToken ct = default)
            => ExecuteAsync(new { op = "read_flash", part = partition, file }, ct);

        public Task<string> WriteFlashAsync(string partition, string file, CancellationToken ct = default)
            => ExecuteAsync(new { op = "write_flash", part = partition, file }, ct);

        public Task<string> EraseFlashAsync(string partition, CancellationToken ct = default)
            => ExecuteAsync(new { op = "erase_flash", part = partition }, ct);

        public Task<string> ReadPartitionAsync(string partition, string file, CancellationToken ct = default)
            => ExecuteAsync(new { op = "read_partition", part = partition, file }, ct);

        public Task<string> WritePartitionAsync(string partition, string file, CancellationToken ct = default)
            => ExecuteAsync(new { op = "write_partition", part = partition, file }, ct);

        public Task<string> ErasePartitionAsync(string partition, CancellationToken ct = default)
            => ExecuteAsync(new { op = "erase_partition", part = partition }, ct);

        public Task<string> ReadOffsetAsync(ulong address, ulong length, string file,
                                            CancellationToken ct = default)
            => ExecuteAsync(new { op = "read_offset", address, length, file }, ct);

        public Task<string> WriteOffsetAsync(ulong address, ulong length, string file,
                                             CancellationToken ct = default)
            => ExecuteAsync(new { op = "write_offset", address, length, file }, ct);

        public Task<string> EraseOffsetAsync(ulong address, ulong length,
                                             CancellationToken ct = default)
            => ExecuteAsync(new { op = "erase_offset", address, length }, ct);

        public Task<string> PeekAsync(ulong address, ulong length, string file,
                                      CancellationToken ct = default)
            => ExecuteAsync(new { op = "peek", address, length, file }, ct);

        public Task<string> PokeAsync(ulong address, ulong length, string file,
                                      CancellationToken ct = default)
            => ExecuteAsync(new { op = "poke", address, length, file }, ct);

        // -------------------------------------------------------- 32 位寄存器

        /// <summary>读取一个 32 位寄存器。</summary>
        public uint ReadRegister(ulong address)
        {
            using var doc = JsonDocument.Parse(Execute(new { op = "peek32", address }));
            return doc.RootElement.GetProperty("value").GetUInt32();
        }

        /// <summary>写入一个 32 位寄存器。</summary>
        public void WriteRegister(ulong address, uint value)
            => Execute(new { op = "poke32", address, value });

        public Task<string> Peek32Async(ulong address, CancellationToken ct = default)
            => ExecuteAsync(new { op = "peek32", address }, ct);

        public Task<string> Poke32Async(ulong address, uint value, CancellationToken ct = default)
            => ExecuteAsync(new { op = "poke32", address, value }, ct);

        // -------------------------------------------------------- 安全区扩展

        /// <summary>设置 RPMB 锁状态。unlock 会把 RPMB 恢复到默认密钥状态，仅 UFS 存储生效。</summary>
        public Task<string> SetRpmbLockStateAsync(bool unlock, CancellationToken ct = default)
            => ExecuteAsync(new { op = "rpmb_lock", action = unlock ? "unlock" : "lock" }, ct);

        /// <summary>
        /// 用设备的安全引擎（SEJ）对文件做 AES 加解密。
        /// keyId：sw / hw / hw_wrapped / rid / custom；keySize：128 / 192 / 256。
        /// </summary>
        public Task<string> SejAesAsync(string inFile, string outFile, bool encrypt = true,
                                        string keyId = "sw", int keySize = 256,
                                        bool cbc = true, bool antiClone = false,
                                        bool legacy = false, bool xor = false,
                                        CancellationToken ct = default)
            => ExecuteAsync(new
            {
                op = "sej_aes",
                in_file     = inFile,
                out_file    = outFile,
                encrypt,
                key_id      = keyId,
                key_size    = keySize,
                cbc,
                anti_clone  = antiClone,
                legacy,
                xor,
            }, ct);

        /// <summary>
        /// 按自定义 label / salt 派生密钥，返回十六进制字符串。
        /// label / salt 以 0x 开头时按十六进制解析，否则按文本处理。
        /// </summary>
        public string DeriveKeyByInput(string label, string salt, int size = 256)
        {
            using var doc = JsonDocument.Parse(
                Execute(new { op = "key_derive", label, salt, size }));
            return doc.RootElement.GetProperty("key").GetString() ?? string.Empty;
        }

        /// <summary>mode: normal / homescreen / fastboot / test / meta。</summary>
        public Task<string> RebootAsync(string mode = "normal", CancellationToken ct = default)
            => ExecuteAsync(new { op = "reboot", mode }, ct);

        public Task<string> ShutdownAsync(CancellationToken ct = default)
            => ExecuteAsync(new { op = "shutdown" }, ct);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                try { pm_set_progress_cb(_handle, null, IntPtr.Zero); } catch { /* 忽略 */ }
                pm_close(_handle);
                _handle = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>把字节数格式化成 "64.00 MB" 这种形式，与界面原有显示保持一致。</summary>
    public static string HumanSize(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value   = bytes;
        var index      = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{bytes} B"
            : value.ToString("F2", CultureInfo.InvariantCulture) + " " + units[index];
    }
}

/// <summary>原生层返回的错误。</summary>
public sealed class PenumbraNativeException : Exception
{
    public PenumbraNativeException(string message) : base(message) { }
}
