using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mosytool.Controls;

public enum LogLevel
{
    Info,

    Detail,

    Success,

    Warning,

    Error,

    Section,
}

public static class LogClassifier
{
    private static readonly Regex SummaryRegex =
        new(@"成功\s*(\d+)\s*项，失败\s*(\d+)\s*项", RegexOptions.Compiled);

    public static LogLevel Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return LogLevel.Info;

        var text = line.TrimEnd();
        if (text.Length == 0) return LogLevel.Info;

        if (text[0] == '>') return LogLevel.Section;

        if (text.StartsWith("[ERROR]", StringComparison.Ordinal)) return LogLevel.Error;
        if (text.StartsWith("[WARN", StringComparison.Ordinal)) return LogLevel.Warning;
        if (text.StartsWith("[DEBUG]", StringComparison.Ordinal) ||
            text.StartsWith("[TRACE]", StringComparison.Ordinal)) return LogLevel.Detail;

        var summary = SummaryRegex.Match(text);
        if (summary.Success)
            return int.Parse(summary.Groups[2].Value) > 0 ? LogLevel.Error : LogLevel.Success;

        if (Has(text, "[错误", "[失败", "失败]", "[无法执行", "[不可执行", "[异常", "错误]"))
            return LogLevel.Error;

        if (Has(text, "[警告", "警告]", "[注意", "[停止", "[已取消", "[已中断", "[中断", "[超时"))
            return LogLevel.Warning;

        if (Has(text, "[成功", "成功]", "[完成", "[连接成功", "[复位成功", "[已连接"))
            return LogLevel.Success;

        if (Has(text, "[提示", "[建议", "[跳过", "[已跳过", "[信息"))
            return LogLevel.Detail;

        if (text[0] == ' ' || text[0] == '\t') return LogLevel.Detail;

        if (Has(text, "失败", "错误", "异常", "无法", "不成功", "Failed", "failed", "FAILED", "Error:", "error:"))
            return LogLevel.Error;

        if (Has(text, "未检测到", "未能读到", "未找到", "警告", "注意", "超时", "不支持"))
            return LogLevel.Warning;

        if (Has(text, "成功", "完成", "已就绪", "Done", "done"))
            return LogLevel.Success;

        if (Has(text, "提示", "建议")) return LogLevel.Detail;

        return LogLevel.Info;
    }

    private static bool Has(string text, params string[] needles)
    {
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.Ordinal))
                return true;

        return false;
    }
}

public class LogView : RichTextBox
{
    public static readonly DependencyProperty MaxCharsProperty = DependencyProperty.Register(
        nameof(MaxChars), typeof(int), typeof(LogView),
        new PropertyMetadata(200_000, OnMaxCharsChanged));

    public static readonly DependencyProperty NoWrapProperty = DependencyProperty.Register(
        nameof(NoWrap), typeof(bool), typeof(LogView),
        new PropertyMetadata(false, OnNoWrapChanged));

    private const double NoWrapPageWidth = 20_000;

    private int _chars;

    public LogView()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        AcceptsReturn = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalContentAlignment = VerticalAlignment.Top;
        SpellCheck.SetIsEnabled(this, false);

        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            LineHeight = double.NaN,
        };
    }

    public int MaxChars
    {
        get => (int)GetValue(MaxCharsProperty);
        set => SetValue(MaxCharsProperty, value);
    }

    public bool NoWrap
    {
        get => (bool)GetValue(NoWrapProperty);
        set => SetValue(NoWrapProperty, value);
    }

    public void Append(string? line) => Append(line, LogClassifier.Classify(line));

    public void Append(string? line, LogLevel level)
    {
        AppendBatch(new[] { line ?? string.Empty }, level);
    }

    public void AppendBatch(IReadOnlyList<string> lines) => AppendBatch(lines, null);

    private void AppendBatch(IReadOnlyList<string> lines, LogLevel? fixedLevel)
    {
        if (lines.Count == 0) return;

        var blocks = Document.Blocks;
        Paragraph? para = null;
        LogLevel? paraLevel = null;
        var paraChars = 0;

        foreach (var raw in lines)
        {
            var line = raw ?? string.Empty;
            var level = fixedLevel ?? LogClassifier.Classify(line);

            if (para is null || level != paraLevel)
            {
                para = NewParagraph();
                paraLevel = level;
                paraChars = 0;
                blocks.Add(para);
            }
            else
            {
                para.Inlines.Add(new LineBreak());
                paraChars++;
            }

            para.Inlines.Add(NewRun(line, level));
            paraChars += line.Length;
            _chars += line.Length + 1;
        }

        if (para is not null) para.Tag = paraChars;

        Trim();
        ScrollToEnd();
    }

    public void Reset(string? firstLine = null)
    {
        Clear();
        if (!string.IsNullOrEmpty(firstLine)) Append(firstLine, LogLevel.Detail);
    }

    public void Clear()
    {
        Document.Blocks.Clear();
        _chars = 0;
    }

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0) };

    private static Run NewRun(string text, LogLevel level)
    {
        var run = new Run(text);

        switch (level)
        {
            case LogLevel.Success:
                Paint(run, "SystemFillColorSuccessBrush", Color.FromRgb(0x0F, 0x7B, 0x0F));
                break;
            case LogLevel.Warning:
                Paint(run, "SystemFillColorCautionBrush", Color.FromRgb(0x9D, 0x5D, 0x00));
                break;
            case LogLevel.Error:
                Paint(run, "SystemFillColorCriticalBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
                break;
            case LogLevel.Detail:
                Paint(run, "TextFillColorSecondaryBrush", Color.FromRgb(0x5D, 0x5D, 0x5D));
                break;
            case LogLevel.Section:
                Paint(run, "AccentTextFillColorPrimaryBrush", Color.FromRgb(0x00, 0x5F, 0xB8));
                run.FontWeight = FontWeights.SemiBold;
                break;
        }

        return run;
    }

    private static void Paint(Run run, string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            run.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
        else
            run.Foreground = new SolidColorBrush(fallback);
    }

    private void Trim()
    {
        var max = MaxChars;
        if (max <= 0) return;

        var blocks = Document.Blocks;
        while (_chars > max && blocks.FirstBlock is { } first)
        {
            var length = first.Tag is int n ? n : 0;
            blocks.Remove(first);
            _chars -= length + 1;
        }
    }

    private static void OnMaxCharsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LogView)d).Trim();

    private static void OnNoWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LogView)d;
        view.Document.PageWidth = view.NoWrap ? NoWrapPageWidth : double.NaN;
    }
}
