using Avalonia;
using Avalonia.Controls;

namespace Bolt.Theme.Controls;

/// <summary>
/// A single row whose children share the width by weight (CSS <c>display:flex</c> with <c>flex: w; min-width: 0</c>):
/// every child gets <c>width × weight / Σweights</c>, so content trims instead of overflowing. Set
/// <see cref="WeightProperty"/> on a child (or its item container, e.g. via a <c>:selected</c> style) to widen it.
/// </summary>
public class ShareRowPanel : Panel
{
    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<ShareRowPanel, Control, double>("Weight", 1.0);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ShareRowPanel, double>(nameof(Spacing));
    public static readonly StyledProperty<double> MaxItemWidthProperty = AvaloniaProperty.Register<ShareRowPanel, double>(nameof(MaxItemWidth), double.PositiveInfinity);

    public static double GetWeight(Control c) => c.GetValue(WeightProperty);
    public static void SetWeight(Control c, double value) => c.SetValue(WeightProperty, value);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    /// <summary>Cap for a weight-1 child (scaled by its weight); with few children the row stays left-packed instead of stretching.</summary>
    public double MaxItemWidth { get => GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }

    static ShareRowPanel()
    {
        AffectsParentMeasure<ShareRowPanel>(WeightProperty);
        AffectsMeasure<ShareRowPanel>(SpacingProperty, MaxItemWidthProperty);
    }

    private IEnumerable<Control> Visible => Children.Where(c => c.IsVisible);

    private double[] Widths(double available)
    {
        var kids = Visible.ToList();
        var total = kids.Sum(GetWeight);
        var free = Math.Max(0, available - Spacing * Math.Max(0, kids.Count - 1));
        return kids.Select(c => total <= 0 ? 0 : Math.Min(free * GetWeight(c) / total, MaxItemWidth * GetWeight(c))).ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = Visible.ToList();
        if (double.IsInfinity(availableSize.Width))
        {
            // Unconstrained: natural widths side by side.
            double w = 0, h = 0;
            foreach (var c in kids) { c.Measure(availableSize); w += c.DesiredSize.Width; h = Math.Max(h, c.DesiredSize.Height); }
            return new Size(w + Spacing * Math.Max(0, kids.Count - 1), h);
        }
        var widths = Widths(availableSize.Width);
        double height = 0;
        for (var i = 0; i < kids.Count; i++)
        {
            kids[i].Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, kids[i].DesiredSize.Height);
        }
        return new Size(Math.Min(availableSize.Width, widths.Sum() + Spacing * Math.Max(0, kids.Count - 1)), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = Visible.ToList();
        var widths = Widths(finalSize.Width);
        double x = 0;
        for (var i = 0; i < kids.Count; i++)
        {
            kids[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + Spacing;
        }
        return finalSize;
    }
}
