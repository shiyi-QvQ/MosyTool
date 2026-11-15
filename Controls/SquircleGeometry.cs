using System;
using System.Windows;
using System.Windows.Media;

namespace Mosytool.Controls;

/// <summary>
/// 连续曲率圆角（squircle）几何。
///
/// 普通圆角是一段圆弧，圆弧和直边相接处曲率从 1/r 直接跳到 0，视觉上能看出「折点」。
/// 这里用指数 n=4 的超椭圆采样圆角：曲率从圆角中点向两端连续收敛到 0，
/// 与直边相接时没有突变，这就是常说的连续曲率圆角。
/// </summary>
internal static class SquircleGeometry
{
    /// <summary>每个圆角的采样段数。16px 半径下 22 段已经看不出折线。</summary>
    private const int SegmentsPerCorner = 22;

    /// <summary>超椭圆指数：2 是普通椭圆（圆弧），4 是常见的 squircle。</summary>
    private const double Exponent = 4;

    /// <summary>生成宽 <paramref name="width"/>、高 <paramref name="height"/>、圆角半径 <paramref name="radius"/> 的闭合轮廓。</summary>
    public static Geometry Create(double width, double height, double radius)
    {
        if (width <= 0.01 || height <= 0.01) return Geometry.Empty;

        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };

        using (var context = geometry.Open())
        {
            if (r <= 0.5)
            {
                context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                context.LineTo(new Point(width, 0), true, false);
                context.LineTo(new Point(width, height), true, false);
                context.LineTo(new Point(0, height), true, false);
            }
            else
            {
                // 顺时针：上边 → 右上 → 右边 → 右下 → 下边 → 左下 → 左边 → 左上
                context.BeginFigure(new Point(r, 0), isFilled: true, isClosed: true);
                context.LineTo(new Point(width - r, 0), true, false);
                AppendCorner(context, new Point(width - r, r), r, 0);

                context.LineTo(new Point(width, height - r), true, false);
                AppendCorner(context, new Point(width - r, height - r), r, 1);

                context.LineTo(new Point(r, height), true, false);
                AppendCorner(context, new Point(r, height - r), r, 2);

                context.LineTo(new Point(0, r), true, false);
                AppendCorner(context, new Point(r, r), r, 3);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>追加一个超椭圆圆角上的采样点（不含起点，包含终点）。</summary>
    private static void AppendCorner(StreamGeometryContext context, Point center, double radius, int corner)
    {
        for (var i = 1; i <= SegmentsPerCorner; i++)
        {
            var angle = Math.PI / 2 * i / SegmentsPerCorner;
            var sin = Math.Pow(Math.Sin(angle), 2 / Exponent);
            var cos = Math.Pow(Math.Cos(angle), 2 / Exponent);

            var (dx, dy) = corner switch
            {
                0 => (radius * sin, -radius * cos), // 右上：上 → 右
                1 => (radius * cos, radius * sin),  // 右下：右 → 下
                2 => (-radius * sin, radius * cos), // 左下：下 → 左
                _ => (-radius * cos, -radius * sin), // 左上：左 → 上
            };

            context.LineTo(new Point(center.X + dx, center.Y + dy), true, false);
        }
    }
}
