using System;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace Mosytool.Controls;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    private const double DefaultFontSize = 13;

    private const double DefaultLineHeight = 22;


    public static FlowDocument ToFlowDocument(string? markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize    = DefaultFontSize,
            LineHeight  = DefaultLineHeight,
        };

        Paint(document, "TextFillColorSecondaryBrush", Color.FromRgb(0x5D, 0x5D, 0x5D));

        if (string.IsNullOrWhiteSpace(markdown)) return document;

        foreach (var block in Markdig.Markdown.Parse(markdown, Pipeline))
            AppendBlock(document.Blocks, block);

        return document;
    }

    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var builder = new StringBuilder();
        foreach (var block in Markdig.Markdown.Parse(markdown, Pipeline))
            AppendPlainBlock(builder, block);

        return Collapse(builder.ToString());
    }


    private const double SummaryFontSize = 12.5;

    private const int SummaryMaxLines = 4;

    public static FrameworkElement BuildSummary(string? markdown)
    {
        var panel = new StackPanel();

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var document  = Markdig.Markdown.Parse(markdown, Pipeline);
            var linesLeft = SummaryMaxLines;
            var index     = 0;

            foreach (var block in document)
            {
                if (linesLeft <= 0) break;

                if (index++ == 0 && block is HeadingBlock && document.Count > 1) continue;

                AppendSummaryBlock(panel, block, ref linesLeft);
            }
        }

        if (panel.Children.Count == 0)
        {
            var hint = new TextBlock { Text = "点击查看详情", FontSize = SummaryFontSize };
            PaintText(hint, "TextFillColorTertiaryBrush", Color.FromRgb(0x8A, 0x8A, 0x8A));
            panel.Children.Add(hint);
        }

        return panel;
    }

    private static void AppendSummaryBlock(StackPanel panel, MdBlock source, ref int linesLeft)
    {
        switch (source)
        {
            case HeadingBlock heading:
                linesLeft -= 1;
                panel.Children.Add(BuildSummaryLine(heading.Inline, lines: 1,
                    bold: true, fontSize: SummaryFontSize + 1));
                break;

            case ParagraphBlock paragraph:
            {
                var lines = Math.Min(3, linesLeft);
                linesLeft -= lines;
                panel.Children.Add(BuildSummaryLine(paragraph.Inline, lines));
                break;
            }

            case ListBlock list:
            {
                var index = 1;
                foreach (var child in list)
                {
                    if (linesLeft <= 0) break;
                    if (child is not ListItemBlock item) continue;

                    if (FirstParaInline(item) is not { } inline) continue;

                    var marker = list.IsOrdered ? $"{index++}. " : "• ";
                    linesLeft -= 1;
                    panel.Children.Add(BuildSummaryLine(inline, lines: 1, prefix: marker));
                }

                break;
            }

            case QuoteBlock quote:
            {
                if (linesLeft <= 0 || FirstParaInline(quote) is not { } inline) break;

                linesLeft -= 1;

                var line = BuildSummaryLine(inline, lines: 1);
                line.Margin = new Thickness(0);

                var bar = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding         = new Thickness(8, 0, 0, 0),
                    Margin          = new Thickness(0, 0, 0, 4),
                    Child           = line,
                };
                bar.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                panel.Children.Add(bar);
                break;
            }

            case CodeBlock code:
            {
                linesLeft -= 1;

                var codeLines = CodeToLines(code.Lines);
                var firstLine = codeLines.Count > 0 ? codeLines[0] : string.Empty;

                var text = new TextBlock
                {
                    Text         = firstLine.Length == 0 ? "…" : firstLine,
                    FontFamily   = MonoFont,
                    FontSize     = SummaryFontSize - 0.5,
                    Margin       = new Thickness(0, 0, 0, 4),
                    Padding      = new Thickness(6, 2, 6, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                PaintText(text, "TextFillColorPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
                PaintTextBackground(text, "ControlFillColorSecondaryBrush");
                panel.Children.Add(text);
                break;
            }

            case ThematicBreakBlock:
                break;

            default:
                if (source is ContainerBlock container)
                    foreach (var child in container)
                    {
                        if (linesLeft <= 0) break;
                        AppendSummaryBlock(panel, child, ref linesLeft);
                    }
                break;
        }
    }

    private static TextBlock BuildSummaryLine(ContainerInline? inlines, int lines,
                                              bool bold = false, string? prefix = null,
                                              double? fontSize = null)
    {
        var size = fontSize ?? SummaryFontSize;
        var lineHeight = size + 6;

        var text = new TextBlock
        {
            FontSize     = size,
            LineHeight   = lineHeight,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight    = lines * lineHeight + 1,
            Margin       = new Thickness(0, 0, 0, 4),
        };

        if (bold) text.FontWeight = FontWeights.SemiBold;

        PaintText(text, bold ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush",
                        bold ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0x5D, 0x5D, 0x5D));

        if (prefix is not null)
        {
            var marker = new Run(prefix);
            Paint(marker, "TextFillColorTertiaryBrush", Color.FromRgb(0x8A, 0x8A, 0x8A));
            text.Inlines.Add(marker);
        }

        AppendInlines(text.Inlines, inlines, interactiveLinks: false);
        return text;
    }

    private static ContainerInline? FirstParaInline(ContainerBlock container)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case ParagraphBlock paragraph:
                    return paragraph.Inline;

                case ContainerBlock nested:
                    if (FirstParaInline(nested) is { } inline) return inline;
                    break;
            }
        }

        return null;
    }


    private static void AppendBlock(BlockCollection target, MdBlock source)
    {
        switch (source)
        {
            case HeadingBlock heading:
                target.Add(BuildHeading(heading));
                break;

            case ParagraphBlock paragraph:
                target.Add(BuildParagraph(paragraph));
                break;

            case ListBlock list:
                target.Add(BuildList(list));
                break;

            case QuoteBlock quote:
                target.Add(BuildQuote(quote));
                break;

            case CodeBlock code:
                target.Add(BuildCode(code.Lines));
                break;

            case ThematicBreakBlock:
                target.Add(BuildDivider());
                break;

            case HtmlBlock html:
                var htmlText = LinesToText(html.Lines);
                if (htmlText.Length > 0) target.Add(new Paragraph(new Run(htmlText)));
                break;

            default:
                if (source is ContainerBlock container)
                    foreach (var child in container)
                        AppendBlock(target, child);
                break;
        }
    }

    private static WpfBlock BuildHeading(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            FontSize     = heading.Level switch { 1 => 19, 2 => 17, 3 => 15.5, 4 => 14.5, _ => 13.5 },
            FontWeight   = FontWeights.SemiBold,
            Margin       = new Thickness(0, 14, 0, 8),
            LineHeight   = double.NaN,
        };

        Paint(paragraph, "TextFillColorPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
        AppendInlines(paragraph.Inlines, heading.Inline);
        return paragraph;
    }

    private static WpfBlock BuildParagraph(ParagraphBlock paragraph)
    {
        var result = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        AppendInlines(result.Inlines, paragraph.Inline);
        return result;
    }

    private static WpfBlock BuildList(ListBlock list)
    {
        var result = new List
        {
            Margin      = new Thickness(0, 0, 0, 10),
            Padding     = new Thickness(20, 0, 0, 0),
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };

        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;

            var listItem = new ListItem();
            foreach (var sub in item)
                AppendBlock(listItem.Blocks, sub);

            result.ListItems.Add(listItem);
        }

        return result;
    }

    private static WpfBlock BuildQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            Margin          = new Thickness(0, 0, 0, 10),
            Padding         = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };

        Paint(section, "TextFillColorSecondaryBrush", Color.FromRgb(0x5D, 0x5D, 0x5D));
        PaintFill(section, "ControlFillColorSecondaryBrush", Color.FromArgb(0x14, 0, 0, 0));
        PaintBorder(section, "AccentFillColorDefaultBrush", Color.FromRgb(0x3B, 0x91, 0xD8));

        foreach (var child in quote)
            AppendBlock(section.Blocks, child);

        return section;
    }

    private static WpfBlock BuildCode(StringLineGroup source)
    {
        var paragraph = new Paragraph
        {
            FontFamily      = MonoFont,
            FontSize        = 12.5,
            Margin          = new Thickness(0, 0, 0, 10),
            Padding         = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            LineHeight      = double.NaN,
        };

        Paint(paragraph, "TextFillColorPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
        PaintFill(paragraph, "ControlFillColorSecondaryBrush", Color.FromArgb(0x14, 0, 0, 0));
        PaintBorder(paragraph, "CardStrokeColorDefaultBrush", Color.FromArgb(0x33, 0, 0, 0));

        var lines = CodeToLines(source);
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    private static WpfBlock BuildDivider()
    {
        var paragraph = new Paragraph
        {
            Margin          = new Thickness(0, 8, 0, 8),
            FontSize        = 1,
            LineHeight      = 1,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        PaintBorder(paragraph, "CardStrokeColorDefaultBrush", Color.FromArgb(0x33, 0, 0, 0));
        return paragraph;
    }


    private static void AppendInlines(InlineCollection target, ContainerInline? container, bool interactiveLinks = true)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case CodeInline code:
                    target.Add(BuildInlineCode(code.Content));
                    break;

                case EmphasisInline emphasis:
                    target.Add(BuildEmphasis(emphasis));
                    break;

                case LinkInline link:
                    target.Add(BuildLink(link, interactiveLinks));
                    break;

                case AutolinkInline autolink:
                    target.Add(BuildHyperlink(autolink.Url, autolink.Url, interactiveLinks));
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlInline html:
                    var plain = StripTags(html.Tag);
                    if (plain.Length > 0) target.Add(new Run(plain));
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested, interactiveLinks);
                    break;
            }
        }
    }

    private static WpfInline BuildInlineCode(string text)
    {
        var run = new Run(text)
        {
            FontFamily = MonoFont,
            FontSize   = 12.5,
        };

        Paint(run, "TextFillColorPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
        PaintFill(run, "ControlFillColorSecondaryBrush", Color.FromArgb(0x14, 0, 0, 0));
        return run;
    }

    private static WpfInline BuildEmphasis(EmphasisInline emphasis)
    {
        var span = new Span();

        switch (emphasis.DelimiterChar)
        {
            case '~':
                span.TextDecorations = emphasis.DelimiterCount >= 2
                    ? TextDecorations.Strikethrough
                    : null;
                break;

            case '+':
                span.TextDecorations = TextDecorations.Underline;
                break;

            case '^':
                break;

            default:
                if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                if (emphasis.DelimiterCount != 2) span.FontStyle  = FontStyles.Italic;
                break;
        }

        AppendInlines(span.Inlines, emphasis);
        return span;
    }

    private static WpfInline BuildLink(LinkInline link, bool interactive = true)
    {
        var text = PlainOf(link);
        var url  = link.Url;

        if (link.IsImage)
        {
            if (string.IsNullOrWhiteSpace(text)) text = "查看图片";
            return BuildHyperlink(url, text, interactive);
        }

        return BuildHyperlink(url, string.IsNullOrWhiteSpace(text) ? (url ?? string.Empty) : text, interactive);
    }

    private static WpfInline BuildHyperlink(string? url, string text, bool interactive = true)
    {
        if (!interactive)
        {
            var span = new Span(new Run(text));
            Paint(span, "AccentTextFillColorPrimaryBrush", Color.FromRgb(0x00, 0x5F, 0xB8));
            return span;
        }

        var hyperlink = new Hyperlink(new Run(text))
        {
            Cursor          = System.Windows.Input.Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            ToolTip         = string.IsNullOrWhiteSpace(url) ? null : url,
        };

        Paint(hyperlink, "AccentTextFillColorPrimaryBrush", Color.FromRgb(0x00, 0x5F, 0xB8));

        if (!string.IsNullOrWhiteSpace(url))
            hyperlink.Click += (_, _) => OpenUrl(url);

        return hyperlink;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch {  }
    }


    private static void AppendPlainBlock(StringBuilder builder, MdBlock source)
    {
        switch (source)
        {
            case HeadingBlock heading:
                AppendPlainInlines(builder, heading.Inline);
                builder.Append('\n');
                break;

            case ParagraphBlock paragraph:
                AppendPlainInlines(builder, paragraph.Inline);
                builder.Append('\n');
                break;

            case CodeBlock code:
                foreach (var line in CodeToLines(code.Lines)) builder.Append(line).Append('\n');
                break;

            case ListBlock list:
                foreach (var child in list)
                {
                    if (child is not ListItemBlock item) continue;
                    builder.Append("• ");
                    foreach (var sub in item) AppendPlainBlock(builder, sub);
                }
                break;

            case QuoteBlock quote:
                foreach (var child in quote) AppendPlainBlock(builder, child);
                break;

            case ThematicBreakBlock:
                break;

            case HtmlBlock html:
                builder.Append(StripTags(LinesToText(html.Lines))).Append('\n');
                break;

            default:
                if (source is ContainerBlock container)
                    foreach (var child in container)
                        AppendPlainBlock(builder, child);
                break;
        }
    }

    private static void AppendPlainInlines(StringBuilder builder, ContainerInline? container)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    builder.Append(code.Content);
                    break;

                case LineBreakInline:
                    builder.Append(' ');
                    break;

                case HtmlInline html:
                    builder.Append(StripTags(html.Tag));
                    break;

                case ContainerInline nested:
                    AppendPlainInlines(builder, nested);
                    break;
            }
        }
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string PlainOf(ContainerInline container)
    {
        var builder = new StringBuilder();
        AppendPlainInlines(builder, container);
        return Collapse(builder.ToString());
    }


    private static System.Collections.Generic.List<string> CodeToLines(StringLineGroup lines)
    {
        var result = new System.Collections.Generic.List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            result.Add(lines.Lines[i].Slice.ToString());

        return result;
    }

    private static string LinesToText(StringLineGroup lines)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    private static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var builder = new StringBuilder(html.Length);
        var inside = false;

        foreach (var ch in html)
        {
            if (ch == '<') { inside = true; continue; }
            if (ch == '>') { inside = false; continue; }
            if (!inside) builder.Append(ch);
        }

        return builder.ToString();
    }


    private static void Paint(FrameworkContentElement element, string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            element.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
        else
            element.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(fallback));
    }

    private static void PaintFill(FrameworkContentElement element, string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            element.SetResourceReference(TextElement.BackgroundProperty, resourceKey);
        else
            element.SetValue(TextElement.BackgroundProperty, new SolidColorBrush(fallback));
    }

    private static void PaintText(TextBlock element, string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            element.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
        else
            element.Foreground = new SolidColorBrush(fallback);
    }

    private static void PaintTextBackground(TextBlock element, string resourceKey)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            element.SetResourceReference(TextBlock.BackgroundProperty, resourceKey);
    }

    private static void PaintBorder(WpfBlock element, string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush)
            element.SetResourceReference(WpfBlock.BorderBrushProperty, resourceKey);
        else
            element.SetValue(WpfBlock.BorderBrushProperty, new SolidColorBrush(fallback));
    }
}
