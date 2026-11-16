using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Mosytool.Controls;

/// <summary>
/// 本应用的自绘窗口基类：不用系统标题栏和边框，窗口外轮廓是一圈连续曲率圆角。
///
/// 实现方式：WindowStyle=None + AllowsTransparency，模板（App.xaml）在透明底上画
/// 阴影层和 squircle 外框。这样在 Windows 10 上也能拿到真正的连续曲率，
/// 代价是失去系统 Acrylic——所以窗口背景继续用应用自己的 Win12MicaBrush 渐变。
///
/// 自己维护的部分：标题栏拖动/双击最大化（交给系统非客户区）、
/// 拖动透明边缘缩放、最大化时贴合工作区（WM_GETMINMAXINFO，避开任务栏）。
/// </summary>
public class MtWindow : Window
{
    /// <summary>非最大化时四周留给阴影的透明边距。</summary>
    private const double NormalFrameMargin = 16;

    /// <summary>非最大化时的圆角半径，和卡片语言保持一致。</summary>
    private const double NormalCornerRadius = 16;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_GETMINMAXINFO = 0x0024;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static readonly DependencyProperty WindowBackgroundProperty =
        DependencyProperty.Register(nameof(WindowBackground), typeof(Brush), typeof(MtWindow),
            new PropertyMetadata(null));

    public static readonly DependencyProperty WindowBorderBrushProperty =
        DependencyProperty.Register(nameof(WindowBorderBrush), typeof(Brush), typeof(MtWindow),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FrameMarginProperty =
        DependencyProperty.Register(nameof(FrameMargin), typeof(Thickness), typeof(MtWindow),
            new PropertyMetadata(new Thickness(NormalFrameMargin)));

    public static readonly DependencyProperty FrameCornerRadiusProperty =
        DependencyProperty.Register(nameof(FrameCornerRadius), typeof(double), typeof(MtWindow),
            new PropertyMetadata(NormalCornerRadius));

    /// <summary>还原动画的缩放起点：从 96% 弹回 100%。</summary>
    private const double RestoreScaleFrom = 0.96;

    private static readonly TimeSpan RestoreFadeDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan RestoreScaleDuration = TimeSpan.FromMilliseconds(220);

    private ResizeEdge _resizeEdge;
    private Point _resizeStartScreen;
    private Rect _resizeStartBounds;

    /// <summary>上一次的窗口状态，用来识别「从最小化还原」这一次跳变。</summary>
    private WindowState _lastState = WindowState.Normal;

    public MtWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        SourceInitialized += (_, _) => AttachHook();
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += OnFirstLoaded;

        // 显式引用 App.xaml 里以类型为键的隐式样式：
        // 自绘窗口是 MtWindow 的子类，不依赖隐式样式对派生类型的解析行为。
        SetResourceReference(StyleProperty, typeof(MtWindow));
    }

    static MtWindow()
    {
        // 自绘窗口的底色必须永远是透明的：阴影就画在窗口四周那圈 16px 边距里，
        // 底色一旦被别人写成不透明色（WPF-UI 换主题时会把窗口 Background 写成
        // 主题底色，实测是 #FFFAFAFA），外面就会多出一圈白边，切回浅色也不恢复。
        // 这里直接把 Background 强制约束成透明，谁写都改不掉。
        BackgroundProperty.OverrideMetadata(typeof(MtWindow),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.None,
                null, new CoerceValueCallback((_, _) => Brushes.Transparent)));
    }

    /// <summary>窗口外框的底色（由 App.xaml 的隐式样式绑定到 Win12MicaBrush）。</summary>
    public Brush? WindowBackground
    {
        get => (Brush?)GetValue(WindowBackgroundProperty);
        set => SetValue(WindowBackgroundProperty, value);
    }

    public Brush? WindowBorderBrush
    {
        get => (Brush?)GetValue(WindowBorderBrushProperty);
        set => SetValue(WindowBorderBrushProperty, value);
    }

    /// <summary>外框与窗口边界之间的透明边距，阴影画在这里。最大化时归零。</summary>
    public Thickness FrameMargin
    {
        get => (Thickness)GetValue(FrameMarginProperty);
        set => SetValue(FrameMarginProperty, value);
    }

    /// <summary>外框圆角半径。最大化时归零，避免屏幕角上出现缺口。</summary>
    public double FrameCornerRadius
    {
        get => (double)GetValue(FrameCornerRadiusProperty);
        set => SetValue(FrameCornerRadiusProperty, value);
    }

    /// <summary>在最大化与还原之间切换。</summary>
    public void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged()
    {
        var previous = _lastState;
        _lastState = WindowState;

        UpdateFrameForState();

        // 自绘窗口是分层窗口（AllowsTransparency），系统不会给它做还原动画，
        // 所以自己补一段「淡入 + 轻微放大」，从任务栏还原时才有反馈。
        if (previous == WindowState.Minimized && WindowState != WindowState.Minimized)
            PlayRestoreAnimation();
    }

    private void PlayRestoreAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, RestoreFadeDuration) { EasingFunction = ease });

        // 模板根是那层透明 Grid（阴影层 + 外框都画在里面），
        // 缩放它等于把整圈外框一起从 96% 撑到 100%，窗口尺寸本身不动。
        if (VisualTreeHelper.GetChild(this, 0) is not FrameworkElement root) return;

        var scale = new ScaleTransform(RestoreScaleFrom, RestoreScaleFrom);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;

        var grow = new DoubleAnimation(RestoreScaleFrom, 1, RestoreScaleDuration)
        {
            EasingFunction = ease,
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private bool _entrancePlayed;

    /// <summary>窗口第一次出现：淡入 + 从 98.5% 轻微放大，避免窗口“啪”地出现。</summary>
    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;

        if (!SystemParameters.ClientAreaAnimation) return;
        if (WindowState != WindowState.Normal) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        if (VisualTreeHelper.GetChild(this, 0) is not FrameworkElement root) return;

        var scale = new ScaleTransform(0.985, 0.985);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;

        var grow = new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>关闭窗口前播一段淡出 + 轻微收缩，返回的 Task 在动效结束时完成。</summary>
    public System.Threading.Tasks.Task PlayCloseAnimationAsync()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return System.Threading.Tasks.Task.CompletedTask;

        var completion = new System.Threading.Tasks.TaskCompletionSource<bool>();
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(170);

        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => completion.TrySetResult(true);

        IsHitTestVisible = false;
        BeginAnimation(OpacityProperty, fade);

        if (VisualTreeHelper.GetChild(this, 0) is FrameworkElement root)
        {
            var scale = new ScaleTransform(1, 1);
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = scale;

            var shrink = new DoubleAnimation(0.985, duration) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        return completion.Task;
    }

    /// <summary>遮罩层：模态卡片挂在这里，盖在内容之上且不会被内容模糊影响。</summary>
    internal Panel? OverlayLayer => GetTemplateChild("OverlayHost") as Panel;
    /// <summary>窗口内容层：弹出模态卡片时对它加高斯模糊。</summary>
    internal UIElement? ContentLayer => GetTemplateChild("ContentHost") as UIElement;

    internal void PushOverlay(UIElement overlay)
    {
        if (OverlayLayer is not { } layer) return;

        layer.Children.Add(overlay);
        layer.Visibility = Visibility.Visible;
    }

    internal void PopOverlay(UIElement overlay)
    {
        if (OverlayLayer is not { } layer) return;

        layer.Children.Remove(overlay);
        if (layer.Children.Count == 0) layer.Visibility = Visibility.Collapsed;
    }

    /// <summary>整窗内容模糊/还原。遮罩层是内容层的兄弟，所以卡片本身保持清晰。</summary>
    internal void SetContentBlurred(bool blurred)
    {
        if (ContentLayer is not { } content) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            content.Effect = blurred ? new BlurEffect { Radius = 12 } : null;
            return;
        }

        if (blurred)
        {
            var effect = content.Effect as BlurEffect ?? new BlurEffect { Radius = 0 };
            content.Effect = effect;
            effect.BeginAnimation(BlurEffect.RadiusProperty,
                new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            return;
        }

        if (content.Effect is not BlurEffect current) return;

        var clear = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        clear.Completed += (_, _) =>
        {
            if (ReferenceEquals(content.Effect, current)) content.Effect = null;
        };
        current.BeginAnimation(BlurEffect.RadiusProperty, clear);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (e.ChangedButton != MouseButton.Left || WindowState != WindowState.Normal) return;

        var edge = HitTestResizeEdge(e.GetPosition(this));
        if (edge == ResizeEdge.None) return;

        _resizeEdge = edge;
        _resizeStartScreen = ScreenPoint(e);
        _resizeStartBounds = new Rect(Left, Top, ActualWidth, ActualHeight);

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        if (_resizeEdge != ResizeEdge.None)
        {
            ResizeTo(ScreenPoint(e));
            e.Handled = true;
            return;
        }

        // 只有窗口四周的透明阴影区参与缩放，避免和标题栏按钮、页面内容抢点击。
        Cursor = WindowState == WindowState.Normal
            ? CursorForEdge(HitTestResizeEdge(e.GetPosition(this)))
            : null;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        EndResize();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _resizeEdge = ResizeEdge.None;
    }

    private void AttachHook()
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        UpdateFrameForState();
    }

    /// <summary>
    /// 把窗口的合成底色复位成透明，并清掉可能被套上的系统背景。
    ///
    /// WPF-UI 每次切主题都会给已打开的窗口做一次「恢复内容背景」：
    /// 把 <see cref="HwndSource.CompositionTarget"/> 的 BackgroundColor 设成
    /// SystemColors.WindowColor（白色），同时给窗口写一个不透明的主题底色。
    /// 自绘窗口是分层窗口，阴影就画在窗口四周那圈 16px 透明边距里，
    /// 合成底色一变白，整圈边距就被填成白色 —— 看起来就是阴影变成了白色光晕，
    /// 而且切回浅色也不会恢复。这里把它复位成透明。
    /// </summary>
    public void ResetWindowSurface()
    {
        Background = Brushes.Transparent;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } composition)
            composition.BackgroundColor = Colors.Transparent;

        // 顺带清掉 DWM 系统背景（Mica / Acrylic），自绘窗口用不上它
        var none = new MARGINS();
        _ = DwmExtendFrameIntoClientArea(handle, ref none);

        // DWMWA_SYSTEMBACKDROP_TYPE = 38；DWMSBT_NONE = 1（Windows 11 22H2+ 才有）
        var backdrop = DwmsbtNone;
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // 标题栏交给系统做非客户区：拖动、双击最大化都是原生行为；
                // 标题栏上的按钮仍然命中 HTCLIENT，不会被系统吞掉。
                handled = true;
                return (IntPtr)(IsCaptionPoint(ScreenToWindowDip(hwnd, GetScreenPoint(lParam)))
                    ? HTCAPTION
                    : HTCLIENT);

            case WM_NCLBUTTONDBLCLK:
                if ((int)wParam != HTCAPTION) break;

                ToggleMaximize();
                handled = true;
                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
                ApplyWorkingArea(hwnd, lParam);
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void ApplyWorkingArea(IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.rcWork;
                var screen = monitorInfo.rcMonitor;

                // 最大化时贴合工作区，不盖住任务栏；位置相对显示器左上角。
                info.ptMaxPosition.X = work.Left - screen.Left;
                info.ptMaxPosition.Y = work.Top - screen.Top;
                info.ptMaxSize.X = work.Right - work.Left;
                info.ptMaxSize.Y = work.Bottom - work.Top;
                info.ptMaxTrackSize.X = work.Right - work.Left;
                info.ptMaxTrackSize.Y = work.Bottom - work.Top;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        info.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        info.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

        Marshal.StructureToPtr(info, lParam, true);
    }

    /// <summary>判断某个窗口内坐标是否落在自绘标题栏的空白区域（按钮除外）。</summary>
    private bool IsCaptionPoint(Point point)
    {
        var current = InputHitTest(point) as DependencyObject;
        var inTitleBar = false;

        for (var guard = 0; current is not null && guard < 64; guard++)
        {
            if (current is ButtonBase) return false;
            if (current is WindowTitleBar) inTitleBar = true;
            if (ReferenceEquals(current, this)) break;

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return inTitleBar;
    }

    private static Point GetScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>物理屏幕像素 → 窗口内 DIP 坐标。</summary>
    private Point ScreenToWindowDip(IntPtr hwnd, Point screen)
    {
        if (!GetWindowRect(hwnd, out var rect)) return screen;

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(
            (screen.X - rect.Left) / dpi.DpiScaleX,
            (screen.Y - rect.Top) / dpi.DpiScaleY);
    }

    private void UpdateFrameForState()
    {
        var maximized = WindowState == WindowState.Maximized;

        FrameMargin = maximized ? new Thickness(0) : new Thickness(NormalFrameMargin);
        FrameCornerRadius = maximized ? 0 : NormalCornerRadius;
        Cursor = null;
    }

    private ResizeEdge HitTestResizeEdge(Point point)
    {
        var margin = FrameMargin;
        if (margin.Left <= 0 || margin.Top <= 0) return ResizeEdge.None;

        var left = point.X < margin.Left;
        var right = point.X > ActualWidth - margin.Right;
        var top = point.Y < margin.Top;
        var bottom = point.Y > ActualHeight - margin.Bottom;

        if (!left && !right && !top && !bottom) return ResizeEdge.None;

        var edge = ResizeEdge.None;
        if (left) edge |= ResizeEdge.Left;
        if (right) edge |= ResizeEdge.Right;
        if (top) edge |= ResizeEdge.Top;
        if (bottom) edge |= ResizeEdge.Bottom;

        return edge;
    }

    private void ResizeTo(Point screenPoint)
    {
        var dx = screenPoint.X - _resizeStartScreen.X;
        var dy = screenPoint.Y - _resizeStartScreen.Y;

        var bounds = _resizeStartBounds;
        var minWidth = Math.Max(MinWidth, 0);
        var minHeight = Math.Max(MinHeight, 0);

        if ((_resizeEdge & ResizeEdge.Left) != 0)
        {
            var width = Math.Max(minWidth, bounds.Width - dx);
            bounds.X = bounds.Right - width;
            bounds.Width = width;
        }
        else if ((_resizeEdge & ResizeEdge.Right) != 0)
        {
            bounds.Width = Math.Max(minWidth, bounds.Width + dx);
        }

        if ((_resizeEdge & ResizeEdge.Top) != 0)
        {
            var height = Math.Max(minHeight, bounds.Height - dy);
            bounds.Y = bounds.Bottom - height;
            bounds.Height = height;
        }
        else if ((_resizeEdge & ResizeEdge.Bottom) != 0)
        {
            bounds.Height = Math.Max(minHeight, bounds.Height + dy);
        }

        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void EndResize()
    {
        if (_resizeEdge == ResizeEdge.None) return;

        _resizeEdge = ResizeEdge.None;
        ReleaseMouseCapture();
        Cursor = null;
    }

    /// <summary>把鼠标位置换算成以 DIP 为单位的屏幕坐标（Window.Left/Top 也是 DIP）。</summary>
    private Point ScreenPoint(MouseEventArgs e)
    {
        var screen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);

        return new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);
    }

    private static Cursor CursorForEdge(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.TopLeft or ResizeEdge.BottomRight => Cursors.SizeNWSE,
        ResizeEdge.TopRight or ResizeEdge.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Left | Top,
        TopRight = Right | Top,
        BottomLeft = Left | Bottom,
        BottomRight = Right | Bottom,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
