using Avalonia;
using Avalonia.Controls;

namespace Bolt.Theme.Controls;

/// <summary>
/// The vertical stack of rendered Markdown blocks. Prose blocks are capped at <see cref="TextMaxWidth"/> (the reading
/// column, left-aligned); tables (<c>.tablescroll</c>) get the full width — the "table lane".
/// </summary>
public sealed class MarkdownStack : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<MarkdownStack, double>(nameof(Spacing));
    public static readonly StyledProperty<double> TextMaxWidthProperty = AvaloniaProperty.Register<MarkdownStack, double>(nameof(TextMaxWidth), double.PositiveInfinity);

    static MarkdownStack() => AffectsMeasure<MarkdownStack>(SpacingProperty, TextMaxWidthProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double TextMaxWidth { get => GetValue(TextMaxWidthProperty); set => SetValue(TextMaxWidthProperty, value); }

    private static bool IsWide(Control c) => c.Classes.Contains("tablescroll");

    private double LaneWidth(Control c, double available) => IsWide(c) ? available : Math.Min(available, TextMaxWidth);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(LaneWidth(child, availableSize.Width), double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
            visible++;
        }
        if (visible > 1) height += Spacing * (visible - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (!first) y += Spacing;
            first = false;
            var h = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, LaneWidth(child, finalSize.Width), h));
            y += h;
        }
        return finalSize;
    }
}
