using System.Windows;

namespace Mosytool.Services;

/// <summary>
/// 统一对话框入口：业务代码不直接 new <see cref="Controls.AppDialog"/>，
/// 保证所有提示框都是同一套圆角卡片样式。
/// </summary>
public static class Dialogs
{
    /// <summary>无 owner 的提示框（居中显示）。</summary>
    public static MessageBoxResult Show(string message, string title,
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage icon = MessageBoxImage.Information)
        => Show(null, message, title, buttons, icon);

    /// <summary>带 owner 的模态提示框，返回用户点击的按钮结果。</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title,
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage icon = MessageBoxImage.Information)
    {
        var dialog = new Controls.AppDialog(title, message, buttons, icon, owner);
        dialog.ShowDialog();
        return dialog.Result;
    }
}
