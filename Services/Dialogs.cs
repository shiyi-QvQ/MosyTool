using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mosytool.Controls;
using Mosytool.Models.Api;

namespace Mosytool.Services;

/// <summary>
/// 统一对话框入口：业务代码不直接 new <see cref="AppDialog"/>，
/// 保证所有提示框都是同一套圆角卡片样式。
///
/// 卡片不再是一个独立窗口，而是挂在当前窗口的遮罩层里：
/// 背景变模糊、卡片在窗口内居中、点空白处或按 Esc 即关闭。
/// </summary>
public static class Dialogs
{
    /// <summary>无 owner 的提示框。</summary>
    public static MessageBoxResult Show(string message, string title,
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage icon = MessageBoxImage.Information,
                                        MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, message, title, buttons, icon, defaultResult);

    /// <summary>带 owner 的模态提示框，返回用户点击的按钮结果。</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title,
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage icon = MessageBoxImage.Information,
                                        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new AppDialog(title, message, buttons, icon, defaultResult);

        if (ResolveHost(owner) is not { } host)
        {
            ShowStandalone(owner, dialog, title);
            return dialog.Result;
        }

        var overlay = new ModalOverlay(dialog);
        dialog.Finished += _ => overlay.RequestClose();
        overlay.DismissRequested += () => dialog.Dismiss(dialog.DismissResult());

        ShowModal(host, overlay);
        return dialog.Result;
    }

    /// <summary>
    /// 把任意卡片当成模态弹到窗口中央：背景模糊、点空白关闭、Esc 关闭。
    /// <paramref name="dismiss"/> 由卡片提供（用于回填「取消」这类结果）。
    /// </summary>
    public static void ShowCard(Window? owner, FrameworkElement card, Action? dismiss = null)
    {
        if (ResolveHost(owner) is not { } host) return;

        var overlay = new ModalOverlay(card);
        if (dismiss is not null) overlay.DismissRequested += dismiss;

        ShowModal(host, overlay);
    }

    /// <summary>
    /// 公告卡片：启动弹窗与首页详情共用一张卡，区别只在 <paramref name="isPopup"/>。
    /// </summary>
    public static void ShowAnnouncement(Window? owner, IReadOnlyList<Announcement> announcements,
                                        bool isPopup)
    {
        if (announcements.Count == 0) return;
        if (ResolveHost(owner) is not { } host) return;

        var card = new AnnouncementCard(announcements, isPopup);
        var overlay = new ModalOverlay(card);

        void Close() => overlay.RequestClose();

        card.Finished += Close;
        overlay.DismissRequested += Close;

        ShowModal(host, overlay);
    }

    public static void ShowAnnouncement(Window? owner, Announcement announcement, bool isPopup)
        => ShowAnnouncement(owner, new[] { announcement }, isPopup);

    /// <summary>优先用传入的 owner；否则退回当前激活窗口，最后退回主窗口。</summary>
    private static MtWindow? ResolveHost(Window? owner)
    {
        if (owner is MtWindow { IsLoaded: true } self) return self;

        if (Application.Current is not { } app) return null;

        var active = app.Windows.OfType<MtWindow>().FirstOrDefault(w => w.IsActive && w.IsLoaded);
        if (active is not null) return active;

        if (app.MainWindow is MtWindow { IsLoaded: true } main) return main;

        return null;
    }

    private static void ShowModal(MtWindow host, ModalOverlay overlay)
    {
        var frame = new DispatcherFrame();

        void OnHostClosed(object? sender, EventArgs e) => overlay.CloseImmediately();

        void OnHostKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            e.Handled = true;
            overlay.RequestDismiss();
        }

        overlay.Closed += () => frame.Continue = false;
        host.Closed += OnHostClosed;
        host.PreviewKeyDown += OnHostKeyDown;

        host.SetContentBlurred(true);
        host.PushOverlay(overlay);
        overlay.PlayEntrance();

        try
        {
            // 嵌套消息循环：调用方依旧同步阻塞（等价于 ShowDialog），
            // 但卡片活在窗口自己的视觉树里，才能做到背景模糊 + 点击空白关闭。
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            host.Closed -= OnHostClosed;
            host.PreviewKeyDown -= OnHostKeyDown;
            host.PopOverlay(overlay);
            host.SetContentBlurred(false);
        }
    }

    /// <summary>兜底：窗口还没准备好时（启动期异常等），用独立无边框窗口承载同一张卡片。</summary>
    private static void ShowStandalone(Window? owner, AppDialog dialog, string title)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = title,
            Content = new Border { Padding = new Thickness(16), Child = dialog },
        };

        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.Finished += _ => window.Close();
        window.ShowDialog();
    }
}
