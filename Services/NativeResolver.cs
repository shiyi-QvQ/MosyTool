using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mosytool.Services;

/// <summary>
/// 本程序集唯一的 DllImport 解析器。
///
/// 一个程序集只能注册一个解析器——<see cref="NativeLibrary.SetDllImportResolver"/> 重复调用会抛
/// InvalidOperationException。项目里 penumbra_ffi 与 qualcomm_ffi 两套原生层都在同一个程序集，
/// 各自注册会让第二个在静态构造时抛异常（表现为 TypeInitializationException，启动即崩）。
/// 因此解析器必须集中到这一处，由两边共用。
///
/// 规则：原生库统一放在 tools\ 下，按「库名.dll」查找；找不到再退回程序集目录。
/// tools\ 到底是 exe 同级的真实目录，还是首次运行时从内嵌资源解压出来的
/// %LOCALAPPDATA%\Mosytool\tools（单文件发布形态），由 <see cref="ToolPaths"/> 统一决定。
/// 未命中的库名返回 Zero，交回运行时默认逻辑（System32 里的系统 DLL 由此兜住）。
/// </summary>
internal static class NativeResolver
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>注册共享解析器。幂等，可被任意原生封装类的静态构造函数调用。</summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;

            NativeLibrary.SetDllImportResolver(typeof(NativeResolver).Assembly, Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var fileName = libraryName + ".dll";

        foreach (var dir in ToolPaths.Dirs)
        {
            var candidate = Path.Combine(dir, fileName);
            try
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
            catch { /* 单个候选失败不影响后续候选 */ }
        }

        return IntPtr.Zero;
    }
}
