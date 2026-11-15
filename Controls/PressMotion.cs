using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosytool.Controls;

/// <summary>
/// 按下动效：按钮、列表项、可点击卡片在按下时轻微缩小，松开（或指针移出）后回弹。
///
/// 用 Window 的类处理器统一接管，新出来的控件自动生效，不用逐个挂事件。
/// 判定规则：从命中元素向上找第一个「可点击」的祖先——按钮、列表项、侧边导航项，
/// 或者 Cursor 是手型的区域（例如首页的公告卡）。
/// </summary>
public static class PressMotion
{
    private const double PressedScale = 0.96;
    private const double PressInMs     = 90;
    private const double ReleaseMs     = 170;

    private static bool _installed;

    /// <summary>当前被按下的元素（鼠标左键同一时刻只有一个）。</summary>
    private static FrameworkElement? _pressed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewDown));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnPreviewUp));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPreviewMove));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.LostMouseCaptureEvent,
            new MouseEventHandler(OnPreviewUp));
    }

    /// <summary>给不满足默认判定规则的元素（如自绘的可点击卡片）补上按下动效。</summary>
    public static void Attach(FrameworkElement element)
        => element.PreviewMouseLeftButtonDown += (sender, _) => Press(sender as FrameworkElement);

    // ---------------------------------------------------------------- 事件

    private static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source) return;

        Press(Resolve(source));
    }

    private static void OnPreviewUp(object sender, MouseEventArgs e)
    {
        if (_pressed is not null) Release(_pressed);
    }

    private static void OnPreviewMove(object sender, MouseEventArgs e)
    {
        // 按下后指针可能拖出控件范围：离开就回弹，不必等松开。
        if (_pressed is not null && !_pressed.IsMouseOver) Release(_pressed);
    }

    // ---------------------------------------------------------------- 命中判定

    private static FrameworkElement? Resolve(DependencyObject source)
    {
        var current = source;

        for (var guard = 0; current is not null && guard < 64; guard++)
        {
            switch (current)
            {
                // 侧边导航项自身就是可点击区域
                case Wpf.Ui.Controls.NavigationViewItem navItem:
                    return navItem;

                case ButtonBase button:
                    // 滚动条 / 滑块的重复按钮不缩放，否则一眼看去像在抖
                    return InScrollChrome(button) ? null : button;

                case ListBoxItem or TreeViewItem:
                    return current as FrameworkElement;

                case FrameworkElement element when element.Cursor == Cursors.Hand:
                    return element;
            }

            current = Parent(current);
        }

        return null;
    }

    private static bool InScrollChrome(DependencyObject source)
    {
        var current = source;

        for (var guard = 0; current is not null && guard < 8; guard++)
        {
            if (current is ScrollBar or Slider or ProgressBar or Thumb) return true;
            current = Parent(current);
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject current)
        => current is Visual || current is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    // ---------------------------------------------------------------- 动效

    private static void Press(FrameworkElement? element)
    {
        if (element is null || !Anim.Enabled) return;
        if (ReferenceEquals(_pressed, element)) return;

        if (_pressed is not null) Release(_pressed);

        if (EnsureScale(element) is not { } scale) return;

        _pressed = element;

        // 缩小时指针有可能正好落到控件外面，这时要立刻回弹，否则会一直停在按下状态
        element.MouseLeave += OnPressedElementLeave;
        element.Unloaded += OnPressedElementUnloaded;

        Animate(scale, PressedScale, PressInMs, Anim.EaseOut);
    }

    private static void Release(FrameworkElement element)
    {
        if (ReferenceEquals(_pressed, element)) _pressed = null;

        element.MouseLeave -= OnPressedElementLeave;
        element.Unloaded -= OnPressedElementUnloaded;

        if (EnsureScale(element) is not { } scale) return;
        Animate(scale, 1.0, ReleaseMs, Anim.EaseOut);
    }

    private static void OnPressedElementLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) Release(element);
    }

    private static void OnPressedElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Release(element);
    }

    private static void Animate(ScaleTransform scale, double to, double milliseconds, IEasingFunction easing)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var animation = new DoubleAnimation(to, duration) { EasingFunction = easing };

        // 动画结束后把值落回属性，避免 HoldEnd 一直占着一次性的动画对象
        animation.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = to;
            scale.ScaleY = to;
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>
    /// 取出（或补上）元素的缩放变换。元素已有别的 RenderTransform 时包一层 TransformGroup，
    /// 不覆盖原有位移/旋转。
    /// </summary>
    private static ScaleTransform? EnsureScale(FrameworkElement element)
    {
        switch (element.RenderTransform)
        {
            case ScaleTransform existing:
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                return existing;

            case TransformGroup group:
            {
                foreach (var child in group.Children)
                    if (child is ScaleTransform found) return found;

                var added = new ScaleTransform(1, 1);
                group.Children.Add(added);
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                return added;
            }

            case null:
            {
                var created = new ScaleTransform(1, 1);
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = created;
                return created;
            }

            default:
            {
                var original = element.RenderTransform;
                var scale = new ScaleTransform(1, 1);
                var wrapper = new TransformGroup();
                wrapper.Children.Add(original);
                wrapper.Children.Add(scale);

                element.RenderTransform = wrapper;
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                return scale;
            }
        }
    }
}
