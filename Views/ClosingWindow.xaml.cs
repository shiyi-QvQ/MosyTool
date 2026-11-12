using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mosytool.Views;

/// <summary>
/// 关闭程序时的收尾提示。
///
/// 退出要按顺序做三件事：关 adb server / 关 fastboot / 清理其余工具进程。
/// 以前这些都在 <see cref="App.OnExit"/> 里同步跑，界面早已消失，用户只看到进程
/// 在任务管理器里赖着不走、或者点了关闭半天没反应。现在把窗口留着，
/// 一步步把进度写出来，清完再真正退出。
/// </summary>
public partial class ClosingWindow : Window
{
    private readonly TextBlock[] _marks;
    private readonly TextBlock[] _labels;

    /// <summary>收尾完成、允许真正关窗。默认 false，期间 Alt+F4 会被拦下。</summary>
    private bool _allowClose;

    public ClosingWindow()
    {
        InitializeComponent();

        _marks  = new[] { AdbMark, FastbootMark, CleanMark };
        _labels = new[] { AdbText, FastbootText, CleanText };

        // 淡入，别在桌面正中“啪”地砸出来一个方块
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>
    /// 更新进度。<paramref name="completed"/> 是“已完成”的最后一步下标（-1 表示还没完成任何一步），
    /// <paramref name="message"/> 显示在底部。
    /// </summary>
    public void Report(string message, int completed = -1)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(message, completed)), DispatcherPriority.Render);
            return;
        }

        for (var i = 0; i < _marks.Length; i++)
        {
            var done = i <= completed;

            _marks[i].Text = done ? "✓" : "○";
            _marks[i].Foreground = ResolveBrush(done ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            _labels[i].Foreground = ResolveBrush(done ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        }

        StatusText.Text = message;
    }

    private Brush ResolveBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>收尾期间不允许用 Alt+F4 之类的办法打断。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>清理完成、要真正退出时由调用方放行关闭。</summary>
    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }
}
