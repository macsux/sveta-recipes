using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bolt.Theme.Controls;

/// <summary>
/// Lightweight Markdown renderer for chat messages: headings, paragraphs, bullet/numbered lists,
/// pipe tables, fenced code blocks, inline code, bold, italic, links (underlined; click opens them).
/// Good enough for agent output; deliberately dependency-free so it survives Avalonia major versions.
/// </summary>
public partial class MarkdownBlock : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));
    public static readonly StyledProperty<double> BaseFontSizeProperty = AvaloniaProperty.Register<MarkdownBlock, double>(nameof(BaseFontSize), 13.5);
    public static readonly StyledProperty<double> LineHeightFactorProperty = AvaloniaProperty.Register<MarkdownBlock, double>(nameof(LineHeightFactor), 1.5);
    public static readonly StyledProperty<double> ParagraphSpacingProperty = AvaloniaProperty.Register<MarkdownBlock, double>(nameof(ParagraphSpacing), 8);
    public static readonly StyledProperty<bool> RenderListsProperty = AvaloniaProperty.Register<MarkdownBlock, bool>(nameof(RenderLists), true);
    public static readonly StyledProperty<FontWeight> BodyFontWeightProperty = AvaloniaProperty.Register<MarkdownBlock, FontWeight>(nameof(BodyFontWeight), FontWeight.Normal);
    public static readonly StyledProperty<double> TextMaxWidthProperty = AvaloniaProperty.Register<MarkdownBlock, double>(nameof(TextMaxWidth), double.PositiveInfinity);

    public string? Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public double BaseFontSize { get => GetValue(BaseFontSizeProperty); set => SetValue(BaseFontSizeProperty, value); }
    /// <summary>Weight of paragraph and list text (a question or ask shown as markdown keeps its emphasis).</summary>
    public FontWeight BodyFontWeight { get => GetValue(BodyFontWeightProperty); set => SetValue(BodyFontWeightProperty, value); }
    /// <summary>Line height as a multiple of the font size (CSS <c>line-height</c>).</summary>
    public double LineHeightFactor { get => GetValue(LineHeightFactorProperty); set => SetValue(LineHeightFactorProperty, value); }
    /// <summary>Vertical gap between blocks.</summary>
    public double ParagraphSpacing { get => GetValue(ParagraphSpacingProperty); set => SetValue(ParagraphSpacingProperty, value); }
    /// <summary>False keeps `- item` lines as typed (user messages read as written, not reformatted).</summary>
    public bool RenderLists { get => GetValue(RenderListsProperty); set => SetValue(RenderListsProperty, value); }
    /// <summary>
    /// Reading-column cap for prose blocks (paragraphs, lists, code). Tables ignore it and may use the full width of the
    /// control (the "table lane"), scrolling horizontally only when even that is not enough.
    /// </summary>
    public double TextMaxWidth { get => GetValue(TextMaxWidthProperty); set => SetValue(TextMaxWidthProperty, value); }

    /// <summary>Widest a table cell grows before its text wraps (keeps a paragraph-long cell from making a 2000px table).</summary>
    public const double MaxCellWidth = 560;

    private static readonly FontFamily FallbackMono = new("Menlo, SF Mono, Consolas, DejaVu Sans Mono, monospace");
    private FontFamily Mono => this.TryFindResource("Bolt.Font.Mono", out var f) && f is FontFamily family ? family : FallbackMono;

    static MarkdownBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        BaseFontSizeProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        BodyFontWeightProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        LineHeightFactorProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        ParagraphSpacingProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        RenderListsProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => c.Rebuild());
        TextMaxWidthProperty.Changed.AddClassHandler<MarkdownBlock>((c, _) => { if (c.Content is MarkdownStack s) s.TextMaxWidth = c.TextMaxWidth; });
    }

    public MarkdownBlock() => InitSelection();

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Rebuild();   // the mono font is a theme resource: resolvable only once attached
    }

    private void Rebuild()
    {
        var text = Markdown ?? "";
        ResetSelection();
        // Transparent background: presses in the gaps between blocks still reach the selection handlers.
        var panel = new MarkdownStack { Spacing = ParagraphSpacing, TextMaxWidth = TextMaxWidth, Background = Brushes.Transparent };
        var group = 0;
        foreach (var block in ParseBlocks(text, RenderLists)) panel.Children.Add(Render(block, group++));
        Content = panel;
    }

    // ------------------------------------------------------------------ parsing

    private abstract record Block;
    private sealed record Paragraph(string Text) : Block;
    private sealed record Heading(int Level, string Text) : Block;
    private sealed record CodeBlock(string Language, string Code) : Block;
    private sealed record ListBlock(bool Ordered, List<string> Items) : Block;
    private sealed record Quote(string Text) : Block;
    private sealed record Rule : Block;
    /// <summary>A GitHub pipe table: <paramref name="Rows"/>[0] is the header; <paramref name="Align"/> is one entry per column.</summary>
    private sealed record TableBlock(List<string[]> Rows, TextAlignment[] Align) : Block;

    private static IEnumerable<Block> ParseBlocks(string text, bool lists = true)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        var para = new List<string>();
        Block? Flush()
        {
            if (para.Count == 0) return null;
            var p = new Paragraph(string.Join("\n", para));
            para.Clear();
            return p;
        }
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("```"))
            {
                if (Flush() is { } f) yield return f;
                var lang = line[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```")) code.Add(lines[i++]);
                i++;
                yield return new CodeBlock(lang, string.Join("\n", code));
                continue;
            }
            // GitHub pipe table: a header row followed by a delimiter row (---, :--, --:, :-:).
            if (line.Contains('|') && i + 1 < lines.Length && TableDelimiter(lines[i + 1]) is { } align)
            {
                var header = SplitRow(line);
                if (header.Length > 0)
                {
                    if (Flush() is { } f2) yield return f2;
                    if (align.Length < header.Length) align = [.. align, .. Enumerable.Repeat(TextAlignment.Left, header.Length - align.Length)];
                    var rows = new List<string[]> { header };
                    i += 2;
                    while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0 && TableDelimiter(lines[i]) is null)
                        rows.Add(SplitRow(lines[i++]));
                    yield return new TableBlock(rows, align[..header.Length]);
                    continue;
                }
            }
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (hm.Success) { if (Flush() is { } f) yield return f; yield return new Heading(hm.Groups[1].Length, hm.Groups[2].Value); i++; continue; }
            if (Regex.IsMatch(line, @"^\s*(---|\*\*\*|___)\s*$")) { if (Flush() is { } f) yield return f; yield return new Rule(); i++; continue; }
            var lm = Regex.Match(line, @"^\s*([-*+]|\d+[.)])\s+(.*)$");
            if (lists && lm.Success)
            {
                if (Flush() is { } f) yield return f;
                var ordered = char.IsDigit(lm.Groups[1].Value[0]);
                var items = new List<string>();
                while (i < lines.Length)
                {
                    var m = Regex.Match(lines[i], @"^\s*([-*+]|\d+[.)])\s+(.*)$");
                    if (m.Success) { items.Add(m.Groups[2].Value); i++; }
                    else if (lines[i].StartsWith("  ") && items.Count > 0 && lines[i].Trim().Length > 0) { items[^1] += " " + lines[i].Trim(); i++; }
                    else break;
                }
                yield return new ListBlock(ordered, items);
                continue;
            }
            if (line.StartsWith("> "))
            {
                if (Flush() is { } f) yield return f;
                var q = new List<string>();
                while (i < lines.Length && lines[i].StartsWith(">")) q.Add(lines[i++].TrimStart('>').TrimStart());
                yield return new Quote(string.Join("\n", q));
                continue;
            }
            if (line.Trim().Length == 0) { if (Flush() is { } f) yield return f; i++; continue; }
            para.Add(line);
            i++;
        }
        if (Flush() is { } last) yield return last;
    }

    /// <summary>Column alignments when the line is a table delimiter row (`|---|:--:|`), else null.</summary>
    private static TextAlignment[]? TableDelimiter(string line)
    {
        var cells = SplitRow(line);
        if (cells.Length == 0) return null;
        var align = new TextAlignment[cells.Length];
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c].Trim();
            if (!Regex.IsMatch(cell, "^:?-{1,}:?$")) return null;
            align[c] = cell.StartsWith(':') && cell.EndsWith(':') ? TextAlignment.Center : cell.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left;
        }
        return align;
    }

    /// <summary>Split a table row on `|`, honouring `\|` escapes and pipes inside `code spans`.</summary>
    private static string[] SplitRow(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return [];
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inCode = false;
        for (var k = 0; k < text.Length; k++)
        {
            var ch = text[k];
            if (ch == '\\' && k + 1 < text.Length && text[k + 1] == '|') { current.Append('|'); k++; continue; }
            if (ch == '`') inCode = !inCode;
            if (ch == '|' && !inCode) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        cells.Add(current.ToString());
        // A leading/trailing pipe produces an empty edge cell; drop those, not empty cells in the middle.
        if (cells.Count > 0 && cells[0].Trim().Length == 0 && text.StartsWith('|')) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0 && text.EndsWith('|')) cells.RemoveAt(cells.Count - 1);
        return cells.Select(c => c.Trim()).ToArray();
    }

    // ------------------------------------------------------------------ rendering

    private Control Render(Block block, int group)
    {
        switch (block)
        {
            case Heading h:
                return Register(Text(h.Text, BaseFontSize + (4 - Math.Min(h.Level, 4)) * 2 + 1, FontWeight.SemiBold), null, SegKind.Para, group);
            case CodeBlock c:
            {
                var tb = new SelectableTextBlock { Text = c.Code, FontFamily = Mono, FontSize = BaseFontSize - 2, LineHeight = Math.Round((BaseFontSize - 2) * 1.6), TextWrapping = TextWrapping.Wrap };
                var border = new Border { Classes = { "code" }, Child = tb };
                Register(tb, border, SegKind.Code, group);
                return border;
            }
            case ListBlock l: return RenderList(l, group);
            case TableBlock t: return RenderTable(t, group);
            case Quote q:
            {
                var tb = Text(q.Text, BaseFontSize, FontWeight.Normal);
                var border = new Border { Classes = { "quote" }, Child = tb };
                Register(tb, border, SegKind.Para, group);
                return border;
            }
            case Rule: return new Border { Height = 1, Margin = new Thickness(0, 4), Classes = { "rule" } };
            case Paragraph p: return Register(Text(p.Text, BaseFontSize, BodyFontWeight), null, SegKind.Para, group);
            default: return new TextBlock();
        }
    }

    /// <summary>
    /// Table → a Grid of auto-sized columns (each as wide as its widest cell, up to <see cref="MaxCellWidth"/>), inside its
    /// own horizontal ScrollViewer: it uses the full width of the control (beyond <see cref="TextMaxWidth"/>) and scrolls
    /// sideways only when even that is too narrow — cells don't wrap just because the reading column is narrow.
    /// </summary>
    private Control RenderTable(TableBlock t, int group)
    {
        var columns = t.Align.Length;
        var grid = new Grid { Classes = { "table" } };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < t.Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var r = 0; r < t.Rows.Count; r++)
        {
            var isHeader = r == 0;
            for (var c = 0; c < columns; c++)
            {
                var content = c < t.Rows[r].Length ? t.Rows[r][c] : "";
                var body = Text(content, BaseFontSize, isHeader ? FontWeight.SemiBold : FontWeight.Normal);
                body.TextAlignment = t.Align[c];
                body.MaxWidth = MaxCellWidth;
                var cell = new Border
                {
                    Classes = { isHeader ? "tableheader" : "tablecell" },
                    // inner grid lines only (the outer border comes from the table itself)
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, r == 0 ? 0 : 1, 0, 0),
                    Child = body,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                Register(body, cell, SegKind.Cell, group, r, c);
            }
        }
        var frame = new Border { Classes = { "tableframe" }, Child = grid, HorizontalAlignment = HorizontalAlignment.Left };
        var scroll = new ScrollViewer
        {
            Classes = { "tablescroll" },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = frame,
        };
        // when it does scroll, leave room under the table so the (overlay) scrollbar doesn't cover its last row
        scroll.ScrollChanged += (_, _) =>
        {
            var pad = new Thickness(0, 0, 0, scroll.Extent.Width > scroll.Viewport.Width + 0.5 ? 10 : 0);
            if (scroll.Padding != pad) scroll.Padding = pad;
        };
        return scroll;
    }

    private Control RenderList(ListBlock l, int group)
    {
        var panel = new StackPanel { Spacing = 2 };
        var n = 1;
        foreach (var item in l.Items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(l.Ordered ? "24,*" : "18,*"), Margin = new Thickness(4, 0, 0, 0) };
            var bullet = new TextBlock { Text = l.Ordered ? $"{n++}." : "•", FontSize = BaseFontSize, LineHeight = LineHeight(BaseFontSize), VerticalAlignment = VerticalAlignment.Top, Classes = { "bullet" } };
            var body = Text(item, BaseFontSize, BodyFontWeight);
            Register(body, row, SegKind.ListItem, group, prefix: l.Ordered ? $"{n - 1}. " : "- ");
            Grid.SetColumn(body, 1);
            row.Children.Add(bullet); row.Children.Add(body);
            panel.Children.Add(row);
        }
        return panel;
    }

    private SelectableTextBlock Text(string text, double size, FontWeight weight)
    {
        var tb = new LinkTextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, LineHeight = LineHeight(size) };
        tb.Inlines ??= new InlineCollection();
        var offset = 0;
        foreach (var (inline, url) in ParseInlines(text))
        {
            var length = ((Run)inline).Text?.Length ?? 0;
            if (url is not null) tb.Links.Add((offset, offset + length, url));
            offset += length;
            tb.Inlines.Add(inline);
        }
        return tb;
    }

    /// <summary>Selectable text whose link runs open in the browser on a plain click (a drag still selects).</summary>
    private sealed class LinkTextBlock : SelectableTextBlock
    {
        public List<(int Start, int End, string Url)> Links { get; } = [];
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        /// <summary>The link under <paramref name="point"/> (in this block's coordinates), if any.</summary>
        public string? LinkAt(Point point)
        {
            if (Links.Count == 0) return null;
            var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
            if (!hit.IsInside) return null;
            var i = hit.CharacterHit.FirstCharacterIndex;
            foreach (var (start, end, url) in Links) if (i >= start && i < end) return url;
            return null;
        }

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Cursor = LinkAt(e.GetPosition(this)) is not null ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null;
        }
        // A plain click on a link is opened by MarkdownBlock's selection handler (it owns pointer presses/releases).
    }

    private double LineHeight(double size) => Math.Round(size * LineHeightFactor, 1);

    private static readonly Regex InlineRx = new(@"(`[^`]+`)|(\*\*[^*]+\*\*)|(__[^_]+__)|(\*[^*\n]+\*)|(_[^_\n]+_)|(\[[^\]]+\]\([^)]+\))|(https?://[^\s)]+)", RegexOptions.Compiled);

    /// <summary>Inline runs, each with the URL it links to (null for plain text).</summary>
    private IEnumerable<(Inline Inline, string? Url)> ParseInlines(string text)
    {
        var pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) yield return (new Run(text[pos..m.Index]), null);
            var v = m.Value;
            if (v.StartsWith('`')) yield return (new Run(v[1..^1]) { FontFamily = Mono, FontSize = BaseFontSize - 1.5, Classes = { "inlinecode" } }, null);
            else if (v.StartsWith("**") || v.StartsWith("__")) yield return (new Run(v[2..^2]) { FontWeight = FontWeight.Bold }, null);
            else if (v.StartsWith('*') || v.StartsWith('_')) yield return (new Run(v[1..^1]) { FontStyle = FontStyle.Italic }, null);
            else if (v.StartsWith('['))
            {
                var close = v.IndexOf("](", StringComparison.Ordinal);
                yield return (new Run(v[1..close]) { TextDecorations = TextDecorations.Underline, Classes = { "link" } }, v[(close + 2)..^1]);
            }
            else yield return (new Run(v) { TextDecorations = TextDecorations.Underline, Classes = { "link" } }, v.TrimEnd('.', ',', ';', ':', '!', '?'));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) yield return (new Run(text[pos..]), null);
    }
}
