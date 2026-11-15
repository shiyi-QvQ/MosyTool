using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mosytool.Controls;

/// <summary>
/// 连续曲率圆角的容器：自己画背景与描边，并按同一套 squircle 轮廓裁剪子内容。
///
/// 和 <see cref="Border"/> 的区别只在圆角形状——Border 是圆弧角，这里是连续曲率角。
/// 窗口外框、对话框卡片都用它，保证整个应用的圆角语言一致。
/// </summary>
public class SquircleBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty =
        Panel.BackgroundProperty.AddOwner(typeof(SquircleBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner(typeof(SquircleBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(nameof(BorderThickness), typeof(double), typeof(SquircleBorder),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(SquircleBorder),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty PaddingProperty =
        DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(SquircleBorder),
            new FrameworkPropertyMetadata(default(Thickness),
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ClipChildProperty =
        DependencyProperty.Register(nameof(ClipChild), typeof(bool), typeof(SquircleBorder),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => (double)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    /// <summary>圆角半径（连续曲率，不是圆弧）。</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>是否把子内容裁进圆角轮廓；默认开启。</summary>
    public bool ClipChild
    {
        get => (bool)GetValue(ClipChildProperty);
        set => SetValue(ClipChildProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var child = Child;
        if (child is null) return new Size();

        var chrome = ChromeSize();
        var available = new Size(
            Math.Max(0, constraint.Width - chrome.Width),
            Math.Max(0, constraint.Height - chrome.Height));

        child.Measure(available);

        return new Size(
            child.DesiredSize.Width + chrome.Width,
            child.DesiredSize.Height + chrome.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var child = Child;
        if (child is null) return arrangeSize;

        var rect = ChildRect(arrangeSize);
        child.Arrange(rect);

        if (ClipChild && rect.Width > 0 && rect.Height > 0)
        {
            var padding = Math.Max(
                Math.Max(Padding.Left, Padding.Right),
                Math.Max(Padding.Top, Padding.Bottom));

            child.Clip = SquircleGeometry.Create(
                rect.Width, rect.Height,
                Math.Max(0, CornerRadius - BorderThickness - padding));
        }
        else
        {
            child.Clip = null;
        }

        return arrangeSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        drawingContext.DrawGeometry(Background, null,
            SquircleGeometry.Create(width, height, CornerRadius));

        if (BorderThickness <= 0 || BorderBrush is null) return;

        // 描边画在边界内侧：路径整体向内缩半个线宽，线条正好贴住外轮廓。
        var inset = BorderThickness;
        var border = SquircleGeometry.Create(
            Math.Max(0, width - inset),
            Math.Max(0, height - inset),
            Math.Max(0, CornerRadius - inset / 2));

        drawingContext.PushTransform(new TranslateTransform(inset / 2, inset / 2));
        drawingContext.DrawGeometry(null, new Pen(BorderBrush, inset), border);
        drawingContext.Pop();
    }

    private Size ChromeSize() => new(
        BorderThickness * 2 + Padding.Left + Padding.Right,
        BorderThickness * 2 + Padding.Top + Padding.Bottom);

    private Rect ChildRect(Size size)
    {
        var left = BorderThickness + Padding.Left;
        var top = BorderThickness + Padding.Top;
        var width = Math.Max(0, size.Width - BorderThickness * 2 - Padding.Left - Padding.Right);
        var height = Math.Max(0, size.Height - BorderThickness * 2 - Padding.Top - Padding.Bottom);

        return new Rect(left, top, width, height);
    }
}
