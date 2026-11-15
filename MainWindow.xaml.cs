using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Mosytool.Services;
using Mosytool.Services.Api;
using Wpf.Ui.Controls;

namespace Mosytool;

public partial class MainWindow : Controls.MtWindow
{
    private static bool _autoChecked;

    private const double WideThreshold = 1100;

    private bool _paneAutoOpen = true;

    public MainWindow()
    {
        InitializeComponent();
        SidebarVersion.Text = $"v{AppInfo.DisplayVersion}";

        _paneAutoOpen = Width >= WideThreshold;
        SizeChanged += OnWindowSizeChanged;

        Loaded += async (_, _) =>
        {
            RootNavigation.Navigate(typeof(Views.HomePage));

            if (!_autoChecked)
            {
                _autoChecked = true;
                await CheckUpdateAsync(silent: true);
            }
        };
    }

    private void NavAdbFeature_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;

        var mirror = (item.Tag as string) == "mirror";

        Views.AdbPage.PendingFeature = mirror;

        Dispatcher.BeginInvoke(
            new Action(() => Views.AdbPage.Current?.SelectFeature(mirror)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var open = e.NewSize.Width >= WideThreshold;
        if (open == _paneAutoOpen) return;

        _paneAutoOpen = open;
        RootNavigation.IsPaneOpen = open;
    }

    public void NavigateTo(Type pageType) => RootNavigation.Navigate(pageType);


    private bool _closing;
    private bool _closeAllowed;

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeAllowed || App.ForceExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closing) return;
        _closing = true;

        var window = new Views.ClosingWindow { Owner = this };
        window.Show();

        try
        {
            await App.RunShutdownCleanupAsync(window.Report);
        }
        catch (Exception)
        {
        }
        finally
        {
            _closeAllowed = true;

            try { window.AllowClose(); }
            catch (InvalidOperationException) {  }

            // 清理收尾后再淡出，避免窗口“啪”地消失。
            await PlayCloseAnimationAsync();

            _ = Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = CheckUpdateAsync(silent: false);

    private async Task CheckUpdateAsync(bool silent)
    {
        try
        {
            var check = await UpdateService.CheckAsync(ignoreCache: !silent);

            if (!UpdateService.ShouldPrompt(check, SettingsService.SkippedVersion))
            {
                SettingsService.SaveUpdateState(DateTime.UtcNow, SettingsService.SkippedVersion);

                if (!silent)
                    Dialogs.Show(this, "当前已是最新版本。", "检查更新",
                                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var window = new Views.UpdateWindow(check!) { Owner = this };
            window.ShowDialog();
        }
        catch (ApiException ex)
        {
            if (!silent)
                Dialogs.Show(this, ex.Message, "检查更新失败",
                                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            if (!silent)
                Dialogs.Show(this, ex.Message, "检查更新失败",
                                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
