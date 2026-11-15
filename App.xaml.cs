using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Mosytool.Services;
using Wpf.Ui.Appearance;

namespace Mosytool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (DriverOps.TryHandle(e.Args, out var driverExitCode))
        {
            Shutdown(driverExitCode);
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            Dialogs.Show(
                $"程序发生未处理的错误，详细信息已写入：\n{GetLogPath()}\n\n{args.Exception.Message}",
                "Mosytool 错误", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        SettingsService.Load();
        AdbService.Initialize();
        MagiskbootService.Initialize();
        KernelSuPatchService.Initialize();
        LkmPatchService.Initialize();
        PenumbraService.Initialize();
        QualcommService.Initialize();

        ThemeService.Initialize();

        Controls.PageTransition.Install();
        Controls.PressMotion.Install();

        new MainWindow().Show();
    }

    public static bool CleanupCompleted { get; private set; }

    public static bool ForceExiting { get; private set; }

    public static void ExitImmediately()
    {
        ForceExiting = true;
        Current?.Shutdown();
    }

    public static async Task RunShutdownCleanupAsync(Action<string, int>? onStep = null)
    {
        if (CleanupCompleted) return;
        CleanupCompleted = true;

        onStep?.Invoke("正在关闭 adb 进程…", -1);
        await Task.Run(AdbService.StopAdbServer).ConfigureAwait(true);
        await Task.Delay(160).ConfigureAwait(true);

        onStep?.Invoke("正在关闭 fastboot 进程…", 0);
        await Task.Run(() => AdbService.KillOwnProcesses("fastboot")).ConfigureAwait(true);
        await Task.Delay(160).ConfigureAwait(true);

        onStep?.Invoke("正在清理软件进程…", 1);
        ScrcpyService.StopRunning();
        await Task.Run(() => AdbService.KillOwnProcesses("scrcpy", "magiskboot", "kptools", "ksud", "adb"))
                  .ConfigureAwait(true);

        try { PenumbraService.CloseSession(); } catch {  }

        await Task.Delay(200).ConfigureAwait(true);

        onStep?.Invoke("清理完成，正在退出…", 2);
        await Task.Delay(260).ConfigureAwait(true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!CleanupCompleted)
        {
            CleanupCompleted = true;

            ScrcpyService.StopRunning();

            AdbService.Shutdown();

            PenumbraService.CloseSession();
        }

        base.OnExit(e);
    }

    private static string GetLogPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mosytool");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "crash.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Mosytool-crash.log");
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(GetLogPath(),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{typeof(App).Assembly.GetName().Version}\n" +
                $"{ex}\n\n");
        }
        catch
        {
        }
    }

    public static void ApplyAccent(Color color)
    {
        ApplicationAccentColorManager.Apply(color, ThemeService.CurrentTheme, false, false);
    }
}
