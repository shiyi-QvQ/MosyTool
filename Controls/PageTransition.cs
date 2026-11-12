using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosytool.Controls;

public static class PageTransition
{
    private const double DurationMs = 260;

    private const double OffsetY = 14;

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(Page),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPageLoaded));
    }

    private static void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Page page) return;
        if (SystemParameters.ClientAreaAnimation == false) return;
        if (page.RenderTransform is not null) return;

        var duration = TimeSpan.FromMilliseconds(DurationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var translate = new TranslateTransform(0, OffsetY);
        page.RenderTransform = translate;

        page.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var rise = new DoubleAnimation(OffsetY, 0, duration) { EasingFunction = ease };

        fade.Completed += (_, _) =>
        {
            page.BeginAnimation(UIElement.OpacityProperty, null);
            page.Opacity = 1;

            if (ReferenceEquals(page.RenderTransform, translate))
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;

                page.RenderTransform = null;
            }
        };

        page.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
