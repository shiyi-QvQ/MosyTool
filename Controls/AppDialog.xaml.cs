using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Mosytool.Controls;

/// <summary>
/// 统一风格的模态对话框：圆角卡片、彩色图标、主次按钮，替代系统 MessageBox。
/// 由 <see cref="Services.Dialogs"/> 创建，业务代码不直接 new。
/// </summary>
public partial class AppDialog : Window
{
    private MessageBoxButton _buttons = MessageBoxButton.OK;
    private readonly List<(Wpf.Ui.Controls.Button Button, MessageBoxResult Result, bool Primary)> _actions = new();

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public AppDialog(string title, string message, MessageBoxButton buttons, MessageBoxImage icon,
                     Window? owner, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        InitializeComponent();

        if (owner is not null && !ReferenceEquals(owner, this))
            Owner = owner;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? DefaultTitle(icon) : title;
        MessageText.Text = message ?? string.Empty;
        Title = TitleText.Text;

        ApplyIcon(icon);
        BuildButtons(buttons, defaultResult);

        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 让默认按钮拿到焦点，直接敲回车即可确认。
        foreach (var child in ButtonHost.Children)
        {
            if (child is System.Windows.Controls.Button { IsDefault: true } button)
            {
                button.Focus();
                break;
            }
        }
    }

    private static string DefaultTitle(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => "出错了",
        MessageBoxImage.Warning => "注意",
        MessageBoxImage.Question => "请确认",
        _ => "提示",
    };

    private void ApplyIcon(MessageBoxImage icon)
    {
        (SymbolRegular symbol, string brushKey) = icon switch
        {
            MessageBoxImage.Error    => (SymbolRegular.ErrorCircle24,    "SystemFillColorCriticalBrush"),
            MessageBoxImage.Warning  => (SymbolRegular.Warning24,        "SystemFillColorCautionBrush"),
            MessageBoxImage.Question => (SymbolRegular.QuestionCircle24, "AccentFillColorDefaultBrush"),
            _                        => (SymbolRegular.Info24,           "AccentFillColorDefaultBrush"),
        };

        IconGlyph.Symbol = symbol;

        if (TryFindResource(brushKey) is not SolidColorBrush source) return;

        IconGlyph.Foreground = source;
        IconHost.Background = new SolidColorBrush(
            Color.FromArgb(0x24, source.Color.R, source.Color.G, source.Color.B));
    }

    private void BuildButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        _buttons = buttons;
        _actions.Clear();
        ButtonHost.Children.Clear();

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("确定", MessageBoxResult.OK, primary: true);
                break;

            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, primary: false);
                AddButton("确定", MessageBoxResult.OK, primary: true);
                break;

            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, primary: false);
                AddButton("是", MessageBoxResult.Yes, primary: true);
                break;

            case MessageBoxButton.YesNoCancel:
                AddButton("取消", MessageBoxResult.Cancel, primary: false);
                AddButton("否", MessageBoxResult.No, primary: false);
                AddButton("是", MessageBoxResult.Yes, primary: true);
                break;
        }

        // 默认按钮：优先尊重调用方指定的 defaultResult，否则落在主按钮上。
        var target = _actions.FirstOrDefault(a => a.Result == defaultResult).Button
                     ?? _actions.LastOrDefault(a => a.Primary).Button
                     ?? _actions.FirstOrDefault().Button;

        if (target is not null) target.IsDefault = true;
    }

    private void AddButton(string text, MessageBoxResult result, bool primary)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = text,
            MinWidth = 92,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
        };

        button.Click += (_, _) =>
        {
            Result = result;
            Close();
        };

        _actions.Add((button, result, primary));
        ButtonHost.Children.Add(button);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        var cancelable = _buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel;
        if (!cancelable) return;

        Result = MessageBoxResult.Cancel;
        Close();
        e.Handled = true;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起时忽略，属于正常情况。
        }
    }
}
