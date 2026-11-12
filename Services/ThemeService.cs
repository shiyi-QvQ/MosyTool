using System;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Mosytool.Services;

/// <summary>
/// 界面主题模式。
///
/// 名字刻意不叫 ThemeMode：<c>System.Windows.ThemeMode</c> 已经占了这个名字，
/// 页面里同时 using 两个命名空间时会 CS0104。
/// </summary>
public enum AppTheme
{
    /// <summary>跟随系统深浅色设置。</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// 主题切换。
///
/// 界面里所有颜色都走 <c>{DynamicResource ...}</c> 引用 App.xaml 里那套设计令牌，
/// 所以切主题不需要动任何页面：把令牌的值换掉，DynamicResource 会自己刷新。
/// WPF-UI 自带的控件（Button / TextBox / NavigationView …）则由
/// <see cref="ApplicationThemeManager"/> 切到对应的 Light / Dark 资源字典。
/// </summary>
public static class ThemeService
{
    /// <summary>当前模式（可能是「跟随系统」）。</summary>
    public static AppTheme Mode { get; private set; } = AppTheme.System;

    /// <summary>当前实际是否深色（跟随系统时已解析成具体结果）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>供强调色管理器使用：强调色在不同主题下要取不同的对比色。</summary>
    public static ApplicationTheme CurrentTheme => IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

    /// <summary>主题变化后触发（页面若有自绘颜色可订阅重新取色）。</summary>
    public static event Action? Changed;

    /// <summary>启动时调用：恢复上次选择的主题（不写配置、不触发事件）。</summary>
    public static void Initialize() => Apply(ParseMode(SettingsService.ThemeMode), persist: false, notify: false);

    /// <summary>用户在「关于 → 外观」里切换主题。</summary>
    public static void SetMode(AppTheme mode) => Apply(mode, persist: true, notify: true);

    public static AppTheme ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark"  => AppTheme.Dark,
        _       => AppTheme.System,
    };

    public static string ModeText(AppTheme mode) => mode switch
    {
        AppTheme.Light => "浅色",
        AppTheme.Dark  => "深色",
        _              => "跟随系统",
    };

    /// <summary>系统是否是深色（跟随系统模式用）。取不到时按浅色处理。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            SystemThemeManager.UpdateSystemThemeCache();
            return SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply(AppTheme mode, bool persist, bool notify)
    {
        var dark = mode == AppTheme.Dark || (mode == AppTheme.System && IsSystemDark());

        Mode   = mode;
        IsDark = dark;

        // 1) 切 WPF-UI 的控件资源字典（同时会把已打开窗口的标题栏一起换色）
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                                      WindowBackdropType.Acrylic, updateAccent: false);

        // 2) 覆盖本应用自己的设计令牌
        ApplyPalette(dark);

        // 3) 强调色跟着换一套（深色下用更亮的一档，浅色下用深一档，保证对比度）
        App.ApplyAccent(SettingsService.AccentColor);

        if (persist) SettingsService.SaveThemeMode(mode.ToString().ToLowerInvariant());
        if (notify) Changed?.Invoke();
    }

    // ===================== 调色板 =====================

    private static void ApplyPalette(bool dark)
    {
        if (Application.Current is null) return;

        if (dark) ApplyDarkPalette();
        else ApplyLightPalette();
    }

    private static void ApplyLightPalette()
    {
        SetBrush("Mt.Accent.SoftBrush",                  "#1F3B91D8");
        SetBrush("Mt.Accent.SoftStrokeBrush",            "#3D3B91D8");

        SetGradient("Win12MicaBrush", "#FFF5F8FC", "#FFD3DCE9");

        SetBrush("CardBackgroundFillColorDefaultBrush",  "#E8FFFFFF");
        SetBrush("CardBackgroundFillColorSecondaryBrush", "#B8FFFFFF");
        SetBrush("CardStrokeColorDefaultBrush",          "#1F2C3E52");
        SetBrush("CardStrokeColorDefaultSolidBrush",     "#FF7C8794");

        SetBrush("Mt.InsetFillBrush",                    "#B8E9EDF3");
        SetBrush("Mt.InsetStrokeBrush",                  "#1A2C3E52");
        SetBrush("ControlFillColorSecondaryBrush",       "#D8E9EDF3");

        SetBrush("TextFillColorPrimaryBrush",            "#FF0F151C");
        SetBrush("TextFillColorSecondaryBrush",          "#FF4D575F");
        SetBrush("TextFillColorTertiaryBrush",           "#FF77808A");
        SetBrush("TextFillColorDisabledBrush",           "#FFA4ACB4");

        SetBrush("Mt.Status.IdleBrush",                  "#FF98A1AB");
        SetBrush("Mt.Status.BusyBrush",                  "#FF3B91D8");
        SetBrush("Mt.Status.OkBrush",                    "#FF2E9E6B");
        SetBrush("Mt.Status.WarnBrush",                  "#FFCC8A2F");
        SetBrush("Mt.Status.ErrorBrush",                 "#FFD1504E");

        SetBrush("ApplicationBackgroundBrush",           "#00FFFFFF");
        SetBrush("SolidBackgroundFillColorBaseBrush",    "#F7FFFFFF");
        SetBrush("SolidBackgroundFillColorSecondaryBrush", "#F7FFFFFF");
        SetBrush("SolidBackgroundFillColorTertiaryBrush",  "#F7FFFFFF");
        SetBrush("LayerFillColorDefaultBrush",           "#A6FFFFFF");
        SetBrush("LayerFillColorAltBrush",               "#A6FFFFFF");

        SetBrush("NavigationViewContentBackground",      "#99FFFFFF");
        SetBrush("NavigationViewContentGridBorderBrush", "#142C3E52");

        SetBrush("SlimScrollBarThumbFill",               "#59000000");
        SetBrush("SlimScrollBarThumbFillHover",          "#8C000000");
        SetBrush("SlimScrollBarThumbFillDrag",           "#B3000000");
    }

    private static void ApplyDarkPalette()
    {
        // 深色不是「浅色反相」：底面保持冷调，分层靠三档亮度 + 白色低透明描边，
        // 强调色淡底降一点透明度，避免在深底上发脏。
        SetBrush("Mt.Accent.SoftBrush",                  "#2E3B91D8");
        SetBrush("Mt.Accent.SoftStrokeBrush",            "#593B91D8");

        SetGradient("Win12MicaBrush", "#FF1C2330", "#FF0C1017");

        SetBrush("CardBackgroundFillColorDefaultBrush",  "#D9181F29");
        SetBrush("CardBackgroundFillColorSecondaryBrush", "#99181F29");
        SetBrush("CardStrokeColorDefaultBrush",          "#26FFFFFF");
        SetBrush("CardStrokeColorDefaultSolidBrush",     "#FF3E4854");

        // 内凹面比卡片更暗，才能读出「凹进去」
        SetBrush("Mt.InsetFillBrush",                    "#B30B0F14");
        SetBrush("Mt.InsetStrokeBrush",                  "#1FFFFFFF");
        SetBrush("ControlFillColorSecondaryBrush",       "#14FFFFFF");

        SetBrush("TextFillColorPrimaryBrush",            "#FFE9EEF4");
        SetBrush("TextFillColorSecondaryBrush",          "#FFAAB4C0");
        SetBrush("TextFillColorTertiaryBrush",           "#FF7C8794");
        SetBrush("TextFillColorDisabledBrush",           "#FF5B646E");

        SetBrush("Mt.Status.IdleBrush",                  "#FF6D7783");
        SetBrush("Mt.Status.BusyBrush",                  "#FF4DA3EA");
        SetBrush("Mt.Status.OkBrush",                    "#FF43C08B");
        SetBrush("Mt.Status.WarnBrush",                  "#FFDCA44E");
        SetBrush("Mt.Status.ErrorBrush",                 "#FFE06A66");

        SetBrush("ApplicationBackgroundBrush",           "#00000000");
        SetBrush("SolidBackgroundFillColorBaseBrush",    "#F710141B");
        SetBrush("SolidBackgroundFillColorSecondaryBrush", "#F710141B");
        SetBrush("SolidBackgroundFillColorTertiaryBrush",  "#F710141B");
        SetBrush("LayerFillColorDefaultBrush",           "#A6181F29");
        SetBrush("LayerFillColorAltBrush",               "#A6181F29");

        SetBrush("NavigationViewContentBackground",      "#99101419");
        SetBrush("NavigationViewContentGridBorderBrush", "#1FFFFFFF");

        SetBrush("SlimScrollBarThumbFill",               "#5CFFFFFF");
        SetBrush("SlimScrollBarThumbFillHover",          "#8CFFFFFF");
        SetBrush("SlimScrollBarThumbFillDrag",           "#B3FFFFFF");
    }

    /// <summary>
    /// 改一个画刷令牌。
    ///
    /// 优先「原地改色」而不是换新对象：代码里用 <c>FindResource</c> 取走画刷再赋给控件的地方
    /// （首页那些手工拼的卡片）拿到的是对象引用，换个新画刷它们不会刷新，
    /// 深色模式下就会留下浅色主题的墨色文字。原地改同一个实例，两边都能跟着变。
    /// 对象被 Freeze 过时无法改色，退回「替换资源」。
    /// </summary>
    private static void SetBrush(string key, string hex)
    {
        if (ColorConverter.ConvertFromString(hex) is not Color color) return;

        if (Application.Current.Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void SetGradient(string key, string from, string to)
    {
        if (ColorConverter.ConvertFromString(from) is not Color start) return;
        if (ColorConverter.ConvertFromString(to) is not Color end) return;

        if (Application.Current.Resources[key] is LinearGradientBrush existing &&
            !existing.IsFrozen && existing.GradientStops.Count >= 2)
        {
            existing.GradientStops[0].Color = start;
            existing.GradientStops[1].Color = end;
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.787, 0.09),
            EndPoint   = new Point(0.213, 0.91),
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(end, 1));

        Application.Current.Resources[key] = brush;
    }
}
