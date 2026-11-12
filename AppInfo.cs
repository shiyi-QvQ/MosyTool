using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mosytool;

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.1.0.2";

    public static string ShortVersion { get; } = Build();

    public static string DisplayVersion { get; } = BuildDisplay();

    private static string BuildDisplay()
    {
        var parts = ShortVersion.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
            return string.Join('.', parts[0], parts[1], parts[2]);

        return ShortVersion;
    }

    public static string Platform { get; } = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X86   => "win-x86",
        _                  => "win-x64",
    };

    private static string Build()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null) return "1.1.0.2";

        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}";
    }
}
