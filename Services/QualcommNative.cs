using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mosytool.Services;

/// <summary>
/// qualcomm_ffi.dll 的 P/Invoke 封装——高通 EDL（9008）原生层，进程内调用。
///
/// 与 <see cref="PenumbraNative"/> 同风格：
///   * 导出统一走 qc_ 前缀，extern "C"（x86 下即 cdecl，因此显式写 Cdecl）；
///   * 字符串统一 UTF-8，Rust 侧分配、由 <c>qc_string_free</c> 释放；
///   * 设备会话是「打开即握手」的句柄，调用方负责 close。
///
/// 原生层只做只读：读 GPT / 备份分区 / 从 EDL 复位，不写任何存储。
/// </summary>
internal static class QualcommNative
{
    private const string DllName = "qualcomm_ffi";

    /// <summary>
    /// 原生库统一放在 tools\ 下，需要自定义解析器；由 <see cref="NativeResolver"/> 集中注册，
    /// 避免与 penumbra_ffi 的注册冲突（一个程序集只能注册一次 DllImport 解析器）。
    /// </summary>
    static QualcommNative()
    {
        NativeResolver.EnsureRegistered();
    }

    // ---------------------------------------------------------------- 原生声明

    /// <summary>
    /// 原生层日志回调。注意与 penumbra 不同：这条签名没有 user 指针。
    /// level 取值：0=Info 1=Warn 2=Error（对应 Rust 侧 emit 的数值）。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr qc_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_set_log_cb(LogCallback? cb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr qc_open([MarshalAs(UnmanagedType.LPUTF8Str)] string configJson,
                                         out IntPtr outErr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_execute(IntPtr session,
                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string opJson,
                                         out IntPtr outJson,
                                         out IntPtr outErr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qc_close(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qc_string_free(IntPtr s);

    // ---------------------------------------------------------------- 可用性

    private static bool? _available;
    private static string? _version;

    /// <summary>qualcomm_ffi.dll 是否可加载。首次调用后结果被缓存。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is { } cached) return cached;

            try
            {
                _version   = Marshal.PtrToStringUTF8(qc_version());
                _available = true;
            }
            catch (DllNotFoundException)        { _available = false; }
            catch (EntryPointNotFoundException)  { _available = false; }
            catch (BadImageFormatException)      { _available = false; }

            return _available.Value;
        }
    }

    /// <summary>形如 "qualcomm-ffi 0.1.0"，不可用时为 null。</summary>
    public static string? Version => IsAvailable ? _version : null;

    // ---------------------------------------------------------------- 全局日志

    private static LogCallback? _logTrampoline;

    /// <summary>Rust 侧的 log 消息。level：0=Info 1=Warn 2=Error。</summary>
    public static event Action<int, string>? Log;

    /// <summary>安装全局日志转发。可重复调用。</summary>
    public static void AttachLogger()
    {
        if (!IsAvailable) return;

        _logTrampoline ??= OnNativeLog;
        // 保留委托实例，否则 GC 回收后原生侧调用会崩
        qc_set_log_cb(_logTrampoline);
    }

    private static void OnNativeLog(int level, IntPtr message)
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
        finally { qc_string_free(p); }
    }

    // ---------------------------------------------------------------- 会话

    /// <summary>
    /// 一次 EDL 会话。持有原生句柄，内部用信号量保证同一句柄上的调用是串行的。
    ///
    /// 会话很贵：打开它要做 sahara 握手、把 firehose 装载器推给设备、再完成 configure。
    /// 而且 firehose 一跑起来，设备就不在「等装载器」的状态了，重复走 sahara 会失败，
    /// 所以读分区表、备份分区这些操作必须复用同一个句柄（见 <see cref="QualcommService"/>）。
    /// </summary>
    public sealed class QcSession : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IntPtr _handle;
        private bool _disposed;

        /// <summary>打开时的设备描述，例如 "Qualcomm ... (05C6:9008)"。</summary>
        public string Device { get; }

        /// <summary>建立会话后由 configure 回填的存储类型。</summary>
        public string MemoryName { get; internal set; } = string.Empty;

        /// <summary>扇区大小（字节）。</summary>
        public ulong SectorSize { get; internal set; }

        /// <summary>单次收发上限（字节）。</summary>
        public ulong MaxPayload { get; internal set; }

        internal QcSession(IntPtr handle, string device)
        {
            _handle = handle;
            Device  = device;
        }

        /// <summary>
        /// 打开设备并完成 sahara 握手 + 装载器推送 + firehose configure。
        /// 失败抛 <see cref="QualcommNativeException"/>。
        /// </summary>
        public static QcSession Open(QcOpenConfig config)
        {
            if (!IsAvailable)
                throw new QualcommNativeException("qualcomm_ffi.dll 不可用。");

            var json = config.ToJson();

            var handle = qc_open(json, out var errPtr);
            var error  = TakeString(errPtr);

            if (handle == IntPtr.Zero)
                throw new QualcommNativeException(
                    string.IsNullOrEmpty(error) ? "打开 EDL 设备失败。" : error);

            return new QcSession(handle, config.ProgrammerPath);
        }

        /// <summary>
        /// 执行一次操作。同步阻塞，成功返回结果 JSON，失败抛 <see cref="QualcommNativeException"/>。
        /// </summary>
        public string Execute(object operation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _gate.Wait();
            try
            {
                var rc = qc_execute(_handle,
                                    System.Text.Json.JsonSerializer.Serialize(operation),
                                    out var jsonPtr, out var errPtr);

                var json  = TakeString(jsonPtr);
                var error = TakeString(errPtr);

                if (rc != 0)
                    throw new QualcommNativeException(
                        string.IsNullOrEmpty(error) ? $"原生调用失败（rc={rc}）。" : error);

                return json;
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                qc_close(_handle);
                _handle = IntPtr.Zero;
            }

            _gate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>qc_open 的入参，序列化成原生层要求的 JSON。</summary>
internal sealed class QcOpenConfig
{
    public string ProgrammerPath { get; init; } = string.Empty;
    public string? MemoryName { get; init; }
    public ushort? Vid { get; init; }
    public ushort? Pid { get; init; }
    public int WaitMs { get; init; } = 15_000;

    public string ToJson()
    {
        var obj = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["programmer_path"] = ProgrammerPath,
            ["wait_ms"]         = WaitMs,
        };

        if (!string.IsNullOrWhiteSpace(MemoryName)) obj["memory_name"] = MemoryName;
        if (Vid is { } vid) obj["vid"] = vid;
        if (Pid is { } pid) obj["pid"] = pid;

        return System.Text.Json.JsonSerializer.Serialize(obj);
    }
}

/// <summary>原生层返回的错误。</summary>
public sealed class QualcommNativeException : Exception
{
    public QualcommNativeException(string message) : base(message) { }
}
