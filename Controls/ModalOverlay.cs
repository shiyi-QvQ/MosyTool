using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosytool.Controls;

/// <summary>
/// 窗口内的模态遮罩：铺满窗口内容区，压暗底下的页面、让卡片居中，
/// 并统一负责入场/退场动效与「点空白处关闭」。
///
/// 它挂在 <see cref="MtWindow.OverlayLayer"/> 上（内容层的兄弟节点），
/// 所以内容的模糊不会波及卡片本身。
/// </summary>
public sealed class ModalOverlay : Grid
{
    private static readonly TimeSpan DimIn = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CardIn = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DimOut = TimeSpan.FromMilliseconds(130);

    private const double CardScaleFrom = 0.94;
    private const double CardScaleTo = 0.97;

    private readonly Border _dim;
    private readonly FrameworkElement _card;

    private bool _closing;
    private bool _closed;

    public ModalOverlay(FrameworkElement card)
    {
        _card = card;

        _dim = new Border { Opacity = 0 };
        _dim.SetResourceReference(Border.BackgroundProperty, "Mt.Overlay.DimBrush");

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;

        Children.Add(_dim);
        Children.Add(card);

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    /// <summary>退场动效播完（或需要立即收起）时触发。</summary>
    public event Action? Closed;

    /// <summary>点了卡片外的空白处。由调用方决定怎么关卡片（可能还要回填结果）。</summary>
    public event Action? DismissRequested;

    public bool IsClosing => _closing;

    /// <summary>遮罩淡入 + 卡片从 94% 弹到 100%，和窗口还原动效保持同一套节奏。</summary>
    public void PlayEntrance()
    {
        if (_closing) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            _dim.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, DimIn));

        _card.Opacity = 0;
        _card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, CardIn) { EasingFunction = ease });

        _card.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = new ScaleTransform(CardScaleFrom, CardScaleFrom);
        _card.RenderTransform = scale;

        var grow = new DoubleAnimation(CardScaleFrom, 1, CardIn) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>播放退场动效，结束后触发 <see cref="Closed"/>。重复调用无效。</summary>
    public void RequestClose()
    {
        if (_closing) return;
        _closing = true;

        if (!SystemParameters.ClientAreaAnimation)
        {
            RaiseClosed();
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, DimOut));

        var fade = new DoubleAnimation(0, DimOut) { EasingFunction = ease };
        fade.Completed += (_, _) => RaiseClosed();
        _card.BeginAnimation(OpacityProperty, fade);

        if (_card.RenderTransform is ScaleTransform scale)
        {
            var shrink = new DoubleAnimation(CardScaleTo, DimOut) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
    }

    /// <summary>不播动效直接收起（宿主窗口正在关闭时用）。</summary>
    public void CloseImmediately()
    {
        _closing = true;
        RaiseClosed();
    }

    /// <summary>请求关闭（Esc / 点空白）：先交给卡片回填结果，没有处理器则直接退场。</summary>
    public void RequestDismiss()
    {
        if (_closing) return;

        if (DismissRequested is { } handler) handler();
        else RequestClose();
    }

    private void RaiseClosed()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_card.IsMouseOver) return;

        e.Handled = true;
        RequestDismiss();
    }
}
