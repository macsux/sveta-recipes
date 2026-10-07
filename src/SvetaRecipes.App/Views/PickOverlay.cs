using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SvetaRecipes.App.Services;

namespace SvetaRecipes.App.Views;

/// <summary>
/// "Point at part of the app" for the assistant, like Snoop's target selector in WPF: covers the window, outlines the
/// element under the mouse (the wheel picks a bigger or smaller area), and a click captures it as a screenshot with the
/// element outlined, a close-up, and a description of the element. Esc or a right-click cancels.
/// </summary>
public sealed class PickOverlay : Panel
{
    private static readonly Color Outline = Color.Parse("#E5484D");

    private readonly Window _window;
    private readonly string _folder;
    private readonly TaskCompletionSource<PickedArea?> _done = new();
    private readonly Border _box;
    private readonly Border _tag;
    private readonly TextBlock _tagText = new();
    private readonly Border _hint;
    /// <summary>The elements under the mouse, innermost first; the wheel moves <see cref="_level"/> along it.</summary>
    private List<Control> _chain = [];
    private int _level;

    public PickOverlay(Window window, string folder)
    {
        _window = window;
        _folder = folder;
        Background = Brushes.Transparent;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        ZIndex = 1000;

        _box = new Border { BorderThickness = new Thickness(2), IsHitTestVisible = false, IsVisible = false };
        _box.Bind(Border.BorderBrushProperty, _box.GetResourceObservable("Bolt.Accent"));
        _box.Bind(Border.BackgroundProperty, _box.GetResourceObservable("Bolt.Accent.Tint"));
        _tag = new Border { IsHitTestVisible = false, IsVisible = false, Child = _tagText, Classes = { "card" }, Padding = new Thickness(6, 2) };
        _tagText.Classes.Add("small");
        _tagText.Classes.Add("mono");
        _hint = new Border
        {
            Classes = { "card" }, Padding = new Thickness(14, 8), Margin = new Thickness(0, 12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Child = new TextBlock { Text = "Click the part of the app you mean  ·  scroll for a bigger or smaller area  ·  Esc to cancel" },
        };
        Children.Add(new Canvas { IsHitTestVisible = false, Children = { _box, _tag } });
        Children.Add(_hint);
    }

    /// <summary>What she clicked, or null if she cancelled.</summary>
    public Task<PickedArea?> Result => _done.Task;

    private Control? Target => _chain.Count > 0 ? _chain[Math.Min(_level, _chain.Count - 1)] : null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(_window);
        var chain = ChainAt(p);
        // Same innermost element: keep the level she scrolled to.
        if (chain.FirstOrDefault() != _chain.FirstOrDefault()) _level = 0;
        _chain = chain;
        // Keep the hint out of the way.
        _hint.VerticalAlignment = p.Y < 80 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Highlight();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _level = Math.Clamp(_level + (e.Delta.Y > 0 ? 1 : -1), 0, Math.Max(0, _chain.Count - 1));
        Highlight();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _done.TrySetResult(null); return; }
        if (_chain.Count == 0) OnPointerMoved(e);
        if (Target is not { } target) return;
        try { _done.TrySetResult(Capture(target)); }
        catch (Exception ex) { _done.TrySetException(ex); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _done.TrySetResult(null);
    }

    private void Highlight()
    {
        if (Target is not { } target || BoundsIn(target) is not { } r)
        {
            _box.IsVisible = _tag.IsVisible = false;
            return;
        }
        _box.IsVisible = _tag.IsVisible = true;
        Canvas.SetLeft(_box, r.X);
        Canvas.SetTop(_box, r.Y);
        _box.Width = r.Width;
        _box.Height = r.Height;
        _tagText.Text = $"{Summary(target)}   {r.Width:0}×{r.Height:0}" + (_chain.Count > 1 ? $"   ({_level + 1}/{_chain.Count})" : "");
        Canvas.SetLeft(_tag, Math.Max(0, r.X));
        Canvas.SetTop(_tag, r.Y >= 26 ? r.Y - 26 : r.Bottom + 4);
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>
    /// The element under <paramref name="p"/> and its ancestors, innermost first: only elements declared in a view
    /// (not the insides of a control's template), so the wheel steps through what the XAML has.
    /// </summary>
    private List<Control> ChainAt(Point p)
    {
        var chain = new List<Control>();
        for (var v = HitAt(_window, p, new Rect(_window.Bounds.Size)); v is not null && v != _window; v = v.GetVisualParent())
            if (v is Control c && c.TemplatedParent is null && !IsGeneratedText(c) && !chain.Contains(c))
                chain.Add(c);
        return chain;
    }

    /// <summary>The topmost visual drawn at <paramref name="p"/> (window coordinates), skipping this overlay.</summary>
    private Visual? HitAt(Visual parent, Point p, Rect clip)
    {
        var children = parent.GetVisualChildren().Select((v, i) => (v, i)).OrderByDescending(x => x.v.ZIndex).ThenByDescending(x => x.i);
        foreach (var (child, _) in children)
        {
            if (child == this || !child.IsVisible || child.Opacity <= 0 || BoundsIn(child) is not { } r || !r.Contains(p)) continue;
            var childClip = child.ClipToBounds ? clip.Intersect(r) : clip;
            if (!childClip.Contains(p)) continue;
            if (HitAt(child, p, childClip) is { } inner) return inner;
            if (DrawsItself(child)) return child;
        }
        return null;
    }

    /// <summary>Whether a visual paints its own area (text, a background), rather than just laying out children.</summary>
    private static bool DrawsItself(Visual v) => v switch
    {
        Panel panel => panel.Background is not null,
        Border border => border.Background is not null,
        ContentPresenter presenter => presenter.Background is not null,
        TemplatedControl or Decorator or ItemsPresenter or TopLevel => false,
        _ => true,
    };

    /// <summary>The TextBlock a ContentPresenter makes for a string Content (Content="Save"): the button is what's in the XAML.</summary>
    private static bool IsGeneratedText(Control c) =>
        c is TextBlock && c.GetVisualParent() is ContentPresenter { TemplatedParent: not null, Content: string };

    private Rect? BoundsIn(Visual v) =>
        v.TransformToVisual(_window) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : null;

    // ------------------------------------------------------------------ capture

    private PickedArea Capture(Control target)
    {
        var box = BoundsIn(target) ?? default;
        var description = Describe(target, box);
        IsVisible = false;   // not in the picture
        try
        {
            var size = _window.Bounds.Size;
            var scale = Math.Min(_window.RenderScaling, 1600 / size.Width);
            var pixels = new PixelSize((int)(size.Width * scale), (int)(size.Height * scale));
            var dpi = new Vector(96 * scale, 96 * scale);

            using var plain = new RenderTargetBitmap(pixels, dpi);
            plain.Render(_window);

            // The window with everything but the element dimmed and the element outlined.
            using var marked = new RenderTargetBitmap(pixels, dpi);
            using (var ctx = marked.CreateDrawingContext(true))
            {
                var all = new Rect(size);
                ctx.DrawImage(plain, all);
                ctx.DrawGeometry(new SolidColorBrush(Colors.Black, 0.28), null,
                    new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(box)));
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Outline), 3), box.Inflate(1.5));
            }

            // A close-up: the element with some of what's around it.
            var margin = Math.Max(40, Math.Max(box.Width, box.Height) * 0.25);
            var crop = box.Inflate(margin).Intersect(new Rect(size));
            var zoom = Math.Clamp(800 / Math.Max(crop.Width, crop.Height), 1, 2) * scale;
            using var close = new RenderTargetBitmap(new PixelSize((int)(crop.Width * zoom), (int)(crop.Height * zoom)), new Vector(96 * zoom, 96 * zoom));
            using (var ctx = close.CreateDrawingContext(true))
                ctx.DrawImage(marked, crop, new Rect(crop.Size));

            Directory.CreateDirectory(_folder);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss-fff");
            var (imagePath, closePath) = (Path.Combine(_folder, $"pick-{stamp}.png"), Path.Combine(_folder, $"pick-{stamp}-close.png"));
            marked.Save(imagePath, new PngBitmapEncoderOptions());
            close.Save(closePath, new PngBitmapEncoderOptions());
            return new PickedArea(imagePath, closePath, Summary(target), description);
        }
        finally
        {
            IsVisible = true;
        }
    }

    // ------------------------------------------------------------------ description

    /// <summary>A short name for the chip and the tag: TextBox "Servings", Button "Save".</summary>
    private static string Summary(Control c)
    {
        var what = c.GetType().Name;
        var label = Label(c) ?? Shows(c);
        return label is { Length: > 0 } ? $"{what} \"{Short(label, 40)}\"" : what;
    }

    private static string Identify(Control c)
    {
        var s = new StringBuilder(c.GetType().Name);
        if (!string.IsNullOrEmpty(c.Name)) s.Append('#').Append(c.Name);
        foreach (var cls in c.Classes.Where(x => !x.StartsWith(':'))) s.Append('.').Append(cls);
        return s.ToString();
    }

    /// <summary>Everything that helps find the element in the code and say what it is, as plain lines.</summary>
    private string Describe(Control target, Rect box)
    {
        var t = new StringBuilder("She pointed at part of the app: it is outlined in red in the first picture (the rest is dimmed); the second is a close-up.\n");
        t.AppendLine($"Element: {Identify(target)}");
        if (Label(target) is { } label) t.AppendLine($"Its label: \"{label}\"");
        if (Shows(target) is { } shows) t.AppendLine($"Shows: \"{Short(shows, 200)}\"");
        if (ToolTip.GetTip(target) is string tip) t.AppendLine($"Tooltip: \"{tip}\"");
        var bindings = Bindings(target);
        if (bindings.Count > 0) t.AppendLine($"Bindings: {string.Join(", ", bindings)}");
        var texts = TextsInside(target).Where(x => x != Shows(target)).Take(12).ToList();
        if (texts.Count > 0) t.AppendLine($"Text inside: {string.Join(" | ", texts.Select(x => $"\"{Short(x, 60)}\""))}");
        if (target.DataContext is { } dc) t.AppendLine($"Data: {dc.GetType().Name}{(NameOf(dc) is { } n ? $" \"{n}\"" : "")}");

        var ancestors = target.GetVisualAncestors().OfType<Control>().Where(c => c.TemplatedParent is null).ToList();
        if (ancestors.FirstOrDefault(a => a is UserControl or Window) is { } view)
        {
            var path = ancestors.TakeWhile(a => a != view).Reverse().Select(Identify).Append(Identify(target));
            t.AppendLine($"In view: {view.GetType().Name}, at {string.Join(" > ", path)}");
        }
        var views = ancestors.Where(a => a is UserControl or Window).Select(a => a.GetType().Name).Reverse();
        t.AppendLine($"View nesting: {string.Join(" > ", views)}");
        t.AppendLine($"Size and position on screen: {box.Width:0}×{box.Height:0} at ({box.X:0}, {box.Y:0}) of a {_window.Bounds.Width:0}×{_window.Bounds.Height:0} window");
        return t.ToString().TrimEnd();
    }

    /// <summary>The caption next to a field: the TextBlock one column to the left in the same Grid row.</summary>
    private static string? Label(Control c)
    {
        if (c.GetVisualParent() is not Grid grid) return null;
        var (row, col) = (Grid.GetRow(c), Grid.GetColumn(c));
        return grid.Children.OfType<TextBlock>()
            .Where(b => Grid.GetRow(b) == row && Grid.GetColumn(b) < col)
            .OrderByDescending(Grid.GetColumn).FirstOrDefault()?.Text;
    }

    private static string? Shows(Control c) => c switch
    {
        TextBlock b => b.Text,
        TextBox b => string.IsNullOrEmpty(b.Text) ? b.PlaceholderText : b.Text,
        HeaderedContentControl { Header: string h } => h,
        ContentControl { Content: string s } => s,
        ComboBox { SelectedItem: { } item } => item.ToString(),
        _ => null,
    } is { Length: > 0 } s2 ? s2 : null;

    private static IEnumerable<string> TextsInside(Control c) =>
        c.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => b.Text!.Trim()).Distinct();

    /// <summary>A view-model's own name for itself (a recipe line's Name, a recipe's Name), if it has one.</summary>
    private static string? NameOf(object o) =>
        o.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) as string;

    /// <summary>Property ← binding path for the element's data bindings (Avalonia's internal Description, read by reflection).</summary>
    private static List<string> Bindings(Control c)
    {
        var list = new List<string>();
        foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegistered(c))
        {
            if (BindingOperations.GetBindingExpressionBase(c, property) is not { } expression) continue;
            var path = expression.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(expression) as string;
            // Theme resources (Bolt brushes, sizes) say nothing about this element.
            if (path is null || path.StartsWith("DynamicResource") || path.StartsWith("StaticResource")) continue;
            list.Add($"{property.Name} ← {path}");
        }
        return list;
    }

    private static string Short(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
