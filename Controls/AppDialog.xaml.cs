using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Mosytool.Controls;

/// <summary>
/// 统一风格的提示卡片：圆角卡片、彩色图标、主次按钮，替代系统 MessageBox。
/// 由 <see cref="Services.Dialogs"/> 包进窗口内的遮罩层显示——
/// 背景模糊、居中、点空白关闭、入场/退场动效都由遮罩层负责，这里只管卡片内容。
/// </summary>
public partial class AppDialog : UserControl
{
    private readonly List<(Wpf.Ui.Controls.Button Button, MessageBoxResult Result, bool Primary)> _actions = new();
    private bool _finished;

    /// <summary>用户选择的按钮结果；点击空白处或按 Esc 时返回取消类结果。</summary>
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    /// <summary>卡片结束时触发，遮罩层据此播放退场动效并退出模态循环。</summary>
    public event Action<MessageBoxResult>? Finished;

    public AppDialog(string title, string message, MessageBoxButton buttons, MessageBoxImage icon,
                     MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? DefaultTitle(icon) : title;
        MessageText.Text = message ?? string.Empty;

        ApplyIcon(icon);
        BuildButtons(buttons, defaultResult);

        Loaded += (_, _) => FocusDefaultButton();
    }

    private void FocusDefaultButton()
    {
        // 让默认按钮拿到焦点，直接敲回车即可确认。
        foreach (var child in ButtonHost.Children)
        {
            if (child is Button { IsDefault: true } button)
            {
                button.Focus();
                return;
            }
        }
    }

    /// <summary>关闭卡片。result 会作为 <see cref="Result"/> 返回给调用方。</summary>
    public void Dismiss(MessageBoxResult result)
    {
        if (_finished) return;

        _finished = true;
        Result = result;
        Finished?.Invoke(result);
    }

    /// <summary>点击空白处 / 按 Esc 的结果：确认类对话框一律当作「取消/否」。</summary>
    public MessageBoxResult DismissResult()
    {
        if (_actions.Any(a => a.Result == MessageBoxResult.Cancel)) return MessageBoxResult.Cancel;
        if (_actions.Any(a => a.Result == MessageBoxResult.No)) return MessageBoxResult.No;
        return _actions.Count > 0 ? _actions[0].Result : MessageBoxResult.OK;
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

        button.Click += (_, _) => Dismiss(result);

        _actions.Add((button, result, primary));
        ButtonHost.Children.Add(button);
    }
}
