using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosytool.Controls;

public static class Anim
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut   = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static void To(DependencyObject target, DependencyProperty property, double to,
                          double milliseconds = 420, IEasingFunction? easing = null)
    {
        var from = target.GetValue(property) is double current ? current : 0.0;

        if (!Enabled || Math.Abs(to - from) < 0.005)
        {
            (target as Animatable)?.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        if (target is not Animatable animatable) return;

        animatable.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing ?? EaseOut,
            FillBehavior   = FillBehavior.HoldEnd,
        });
    }

    public static void FadeRise(FrameworkElement element, double delayMs = 0, double offsetY = 10, double milliseconds = 320)
    {
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        element.Opacity = 0;

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime      = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = EaseOut,
            FillBehavior   = FillBehavior.HoldEnd,
        });

        if (!TryGetTranslate(element, out var translate)) return;

        translate.Y = offsetY;
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(milliseconds))
            {
                BeginTime      = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = EaseOut,
                FillBehavior   = FillBehavior.HoldEnd,
            });
    }

    public static void Stagger(IEnumerable<FrameworkElement> elements, double stepMs = 45, double offsetY = 10)
    {
        var index = 0;

        foreach (var element in elements)
        {
            FadeRise(element, index * stepMs, offsetY);
            index++;
        }
    }

    private static bool TryGetTranslate(FrameworkElement element, out TranslateTransform translate)
    {
        switch (element.RenderTransform)
        {
            case null:
                translate = new TranslateTransform();
                element.RenderTransform = translate;
                return true;

            case TranslateTransform existing:
                translate = existing;
                return true;

            default:
                translate = new TranslateTransform();
                return false;
        }
    }
}
