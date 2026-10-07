using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Bolt.Theme.Controls;

/// <summary>
/// Document-wide text selection: every rendered block is its own SelectableTextBlock, so on their own a drag stops at the
/// block's edge. MarkdownBlock takes over pointer handling (tunnel, before the blocks see it), hit-tests the block under the
/// pointer, and spreads one selection across all blocks in document order by setting each block's SelectionStart/End —
/// so each shows its normal highlight while the layout stays exactly as rendered.
/// </summary>
public partial class MarkdownBlock
{
    private enum SegKind { Para, ListItem, Cell, Code }

    /// <summary>One selectable block; <paramref name="Hit"/> is the area that counts as "in" it (cell, list row, code box).</summary>
    private sealed record Seg(SelectableTextBlock Tb, Control Hit, SegKind Kind, int Group, int Row, int Col, string Prefix)
    {
        public string Text => (Tb.Inlines is { Count: > 0 } inl ? inl.Text : Tb.Text) ?? "";
    }

    private readonly record struct Pos(int Seg, int Index) : IComparable<Pos>
    {
        public int CompareTo(Pos other) => Seg != other.Seg ? Seg.CompareTo(other.Seg) : Index.CompareTo(other.Index);
    }

    private readonly List<Seg> _segs = [];
    private Pos? _anchor, _caret;
    private bool _dragging;
    private int _pressClicks;
    private bool _pressShift;
    private MenuFlyout? _menu;
    private TopLevel? _topLevel;
    private static WeakReference<MarkdownBlock>? s_active;

    /// <summary>Opens a clicked link (overridable so tests don't launch a browser).</summary>
    public static Action<string> LinkOpener { get; set; } = OpenLink;

    private static void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler for the link */ }
    }

    private void InitSelection()
    {
        Focusable = true;
        ScrollViewer.SetBringIntoViewOnFocusChange(this, false);   // focusing a tall message must not scroll the transcript
        AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoveTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleaseTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => _dragging = false);
        AddHandler(ContextRequestedEvent, OnContextRequested);
    }

    private T Register<T>(T tb, Control? hit, SegKind kind, int group, int row = 0, int col = 0, string prefix = "") where T : SelectableTextBlock
    {
        tb.Focusable = false;        // the MarkdownBlock owns focus, keys and the selection
        tb.ContextFlyout = null;     // the theme's per-block "Copy" would copy one block only; ours copies the whole selection
        _segs.Add(new Seg(tb, hit ?? tb, kind, group, row, col, prefix));
        return tb;
    }

    private void ResetSelection()
    {
        _segs.Clear();
        _anchor = _caret = null;
        _dragging = false;
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>True when some text is selected (possibly spanning several blocks).</summary>
    public bool HasSelection => _anchor is { } a && _caret is { } c && a != c;

    /// <summary>The selection as plain text: blank lines between blocks, "- "/"1. " list items, tab-separated table rows.</summary>
    public string SelectedText
    {
        get
        {
            if (!HasSelection) return "";
            var (start, end) = Ordered();
            var sb = new StringBuilder();
            Seg? prev = null;
            for (var i = start.Seg; i <= end.Seg; i++)
            {
                var seg = _segs[i];
                var text = seg.Text;
                var from = Math.Clamp(i == start.Seg ? start.Index : 0, 0, text.Length);
                var to = Math.Clamp(i == end.Seg ? end.Index : text.Length, from, text.Length);
                if (prev is not null) sb.Append(Separator(prev, seg));
                // a single partial block copies as-is; list items in a multi-block selection keep their marker
                if (seg.Kind == SegKind.ListItem && start.Seg != end.Seg && from == 0) sb.Append(seg.Prefix);
                sb.Append(text, from, to - from);
                prev = seg;
            }
            return sb.ToString();
        }
    }

    private static string Separator(Seg prev, Seg next)
    {
        if (prev.Group == next.Group && next.Kind == SegKind.Cell) return prev.Row == next.Row ? "\t" : "\n";
        if (prev.Group == next.Group && next.Kind == SegKind.ListItem) return "\n";
        return "\n\n";
    }

    public void SelectAll()
    {
        if (_segs.Count == 0) return;
        _anchor = new Pos(0, 0);
        _caret = new Pos(_segs.Count - 1, _segs[^1].Text.Length);
        Apply();
    }

    public void ClearSelection()
    {
        _anchor = _caret = null;
        Apply();
    }

    public async Task CopySelectionAsync()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // ------------------------------------------------------------------ pointer

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || IsInsideChrome(source)) return;
        var props = e.GetCurrentPoint(this).Properties;
        var pos = HitPos(e.GetPosition(this));
        if (props.IsRightButtonPressed)
        {
            // right-click outside the selection drops it; inside keeps it for the context menu's Copy
            if (HasSelection && (pos is not { } p || !Contains(p))) ClearSelection();
            return;
        }
        if (!props.IsLeftButtonPressed || pos is not { } hit) return;

        Focus(NavigationMethod.Pointer);
        TakeOver();
        _pressClicks = e.ClickCount;
        _pressShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.ClickCount)
        {
            case 1:
                if (_pressShift && _anchor is not null) _caret = hit;
                else _anchor = _caret = hit;
                break;
            case 2:
                var (ws, we) = WordAt(_segs[hit.Seg].Text, hit.Index);
                _anchor = new Pos(hit.Seg, ws);
                _caret = new Pos(hit.Seg, we);
                break;
            default:
                _anchor = new Pos(hit.Seg, 0);
                _caret = new Pos(hit.Seg, _segs[hit.Seg].Text.Length);
                break;
        }
        Apply();
        _dragging = e.ClickCount == 1;
        e.Pointer.Capture(this);   // drags keep reporting here even outside the start block (or this control)
        Cursor = new Cursor(StandardCursorType.Ibeam);
        e.Handled = true;          // the block's own single-block selection never starts
    }

    private void OnMoveTunnel(object? sender, PointerEventArgs e)
    {
        if (!_dragging || e.Pointer.Captured != this) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragging = false; return; }
        if (HitPos(e.GetPosition(this)) is { } pos && pos != _caret)
        {
            _caret = pos;
            Apply();
        }
        e.Handled = true;
    }

    private void OnReleaseTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Pointer.Captured != this) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Cursor = null;
        e.Handled = true;
        // a plain click (no drag, no selection) on a link opens it
        if (!HasSelection && _pressClicks == 1 && !_pressShift && _caret is { } at && _segs[at.Seg].Tb is LinkTextBlock link
            && this.TranslatePoint(e.GetPosition(this), link) is { } local && link.LinkAt(local) is { } url)
            LinkOpener(url);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!HasSelection) return;   // no selection: let the bubble / card's own menu open
        _menu ??= BuildMenu();
        _menu.ShowAt(this, showAtPointer: true);
        e.Handled = true;
    }

    private MenuFlyout BuildMenu()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = TextBox.CopyGesture };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var all = new MenuItem { Header = "Select all" };
        all.Click += (_, _) => SelectAll();
        var menu = new MenuFlyout { Items = { copy, all } };
        return menu;
    }

    // ------------------------------------------------------------------ keys / focus

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var keymap = this.GetPlatformSettings()?.HotkeyConfiguration ?? Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keymap is null) return;
        if (HasSelection && keymap.Copy.Any(g => g.Matches(e))) { _ = CopySelectionAsync(); e.Handled = true; }
        else if (keymap.SelectAll.Any(g => g.Matches(e))) { SelectAll(); e.Handled = true; }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_menu is not { IsOpen: true } && !_dragging) ClearSelection();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerPressedEvent, OnAnyPress, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(PointerPressedEvent, OnAnyPress);
        _topLevel = null;
    }

    /// <summary>A press anywhere outside this control clears its selection (focus doesn't always move, e.g. on empty space).</summary>
    private void OnAnyPress(object? sender, PointerPressedEventArgs e)
    {
        if (!HasSelection || e.Source is not Visual v) return;
        if (v == this || this.IsVisualAncestorOf(v)) return;
        ClearSelection();
    }

    /// <summary>This block becomes the only one with a selection (each message is its own scope).</summary>
    private void TakeOver()
    {
        if (s_active is not null && s_active.TryGetTarget(out var other) && other != this) other.ClearSelection();
        s_active = new WeakReference<MarkdownBlock>(this);
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Scrollbars of a table's ScrollViewer are not text: leave them alone.</summary>
    private bool IsInsideChrome(Visual source)
    {
        for (Visual? v = source; v is not null && v != this; v = v.GetVisualParent())
            if (v is Avalonia.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private (Pos Start, Pos End) Ordered()
    {
        var a = _anchor!.Value; var c = _caret!.Value;
        return a.CompareTo(c) <= 0 ? (a, c) : (c, a);
    }

    private bool Contains(Pos p)
    {
        var (s, e) = Ordered();
        return p.CompareTo(s) >= 0 && p.CompareTo(e) <= 0;
    }

    /// <summary>Pushes the document selection into each block's SelectionStart/End (empty for blocks outside it).</summary>
    private void Apply()
    {
        Pos? start = null, end = null;
        if (HasSelection) (start, end) = Ordered();
        for (var i = 0; i < _segs.Count; i++)
        {
            var tb = _segs[i].Tb;
            int from = 0, to = 0;
            if (start is { } s && end is { } e && i >= s.Seg && i <= e.Seg)
            {
                var len = _segs[i].Text.Length;
                from = i == s.Seg ? Math.Min(s.Index, len) : 0;
                to = i == e.Seg ? Math.Min(e.Index, len) : len;
            }
            if (tb.SelectionStart != from || tb.SelectionEnd != to)
            {
                tb.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, from);
                tb.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, to);
            }
        }
    }

    private Rect? BoundsOf(Control c)
    {
        if (!c.IsEffectivelyVisible || c.TransformToVisual(this) is not { } m) return null;
        return new Rect(c.Bounds.Size).TransformToAABB(m);
    }

    /// <summary>
    /// The text position under <paramref name="p"/> (this control's coordinates): the block whose area contains the point,
    /// else the nearest one (vertically first, then horizontally). Above a block → its start, below → its end, beside it →
    /// clamped onto its nearest line.
    /// </summary>
    private Pos? HitPos(Point p)
    {
        int best = -1; double bestDy = 0, bestDx = 0; Rect bestRect = default;
        for (var i = 0; i < _segs.Count; i++)
        {
            if (BoundsOf(_segs[i].Hit) is not { } r) continue;
            var dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            var dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            if (best < 0 || dy < bestDy - 0.5 || (Math.Abs(dy - bestDy) <= 0.5 && dx < bestDx))
            {
                best = i; bestDy = dy; bestDx = dx; bestRect = r;
            }
        }
        if (best < 0) return null;
        var seg = _segs[best];
        var length = seg.Text.Length;
        if (p.Y < bestRect.Top) return new Pos(best, 0);
        if (p.Y > bestRect.Bottom) return new Pos(best, length);
        if (BoundsOf(seg.Tb) is not { } tr) return new Pos(best, 0);
        var local = new Point(Math.Clamp(p.X, tr.Left, tr.Right) - tr.Left, Math.Clamp(p.Y, tr.Top, Math.Max(tr.Top, tr.Bottom - 0.5)) - tr.Top)
                    - new Point(seg.Tb.Padding.Left, seg.Tb.Padding.Top);
        var index = seg.Tb.TextLayout.HitTestPoint(local).TextPosition;
        return new Pos(best, Math.Clamp(index, 0, length));
    }

    private static (int Start, int End) WordAt(string text, int index)
    {
        static bool W(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        index = Math.Clamp(index, 0, text.Length);
        // a click on the right half of a word's last letter reports the index after it
        if ((index == text.Length || !W(text[index])) && index > 0 && W(text[index - 1])) index--;
        if (index >= text.Length) return (text.Length, text.Length);
        if (!W(text[index])) return (index, index + 1);
        int s = index, e = index;
        while (s > 0 && W(text[s - 1])) s--;
        while (e < text.Length && W(text[e])) e++;
        return (s, e);
    }
}
