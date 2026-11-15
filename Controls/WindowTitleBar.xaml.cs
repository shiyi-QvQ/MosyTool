using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Mosytool.Controls;

/// <summary>
/// 自绘窗口标题栏：左侧图标 + 标题，右侧最小化 / 最大化 / 关闭。
///
/// 拖动和双击最大化的命中判定由 <see cref="MtWindow"/> 在 WM_NCHITTEST 里
/// 把标题栏区域交还给系统处理，这里只负责画和转发按钮点击。
/// </summary>
public partial class WindowTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(WindowTitleBar),
            new PropertyMetadata(string.Empty, (d, e) =>
                ((WindowTitleBar)d).TitleText.Text = (string)(e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(WindowTitleBar),
            new PropertyMetadata(SymbolRegular.Empty, (d, e) => ((WindowTitleBar)d).ApplyIcon()));

    private Window? _window;

    public WindowTitleBar()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null) return;

        _window.StateChanged += OnWindowStateChanged;

        if (string.IsNullOrEmpty(Title))
            Title = _window.Title;

        UpdateMaximizeGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;

        _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void ApplyIcon()
    {
        TitleIcon.Symbol = Icon;
        TitleIcon.Visibility = Icon == SymbolRegular.Empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnWindowStateChanged(object? sender, System.EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;

        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is MtWindow window) window.ToggleMaximize();
        else if (_window is not null)
        {
            _window.WindowState = _window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
