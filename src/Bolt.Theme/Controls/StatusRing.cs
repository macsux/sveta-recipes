using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace Bolt.Theme.Controls;

public enum StatusRingKind
{
    /// <summary>Nothing drawn.</summary>
    None,
    /// <summary>Dashed ring: not started / triage.</summary>
    Pending,
    /// <summary>Plain ring: backlog / idle.</summary>
    Empty,
    /// <summary>Ring with a pie filled to <see cref="StatusRing.Fraction"/> in the accent.</summary>
    Progress,
    /// <summary>Solid disc in the success color.</summary>
    Done,
    /// <summary>Ring with a slash: cancelled / closed.</summary>
    Cancelled,
}

/// <summary>
/// Linear-style status marker: a 12px ring that is empty, partially filled (in progress / in review), solid (done)
/// or struck through (cancelled). Colors come from Bolt tokens unless overridden.
/// </summary>
public class StatusRing : Control
{
    public static readonly StyledProperty<StatusRingKind> KindProperty = AvaloniaProperty.Register<StatusRing, StatusRingKind>(nameof(Kind), StatusRingKind.Empty);
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<StatusRing, double>(nameof(Fraction), 0.5);
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<StatusRing, double>(nameof(Size), 12);
    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<StatusRing, IBrush?>(nameof(RingBrush));
    public static readonly StyledProperty<IBrush?> FillBrushProperty = AvaloniaProperty.Register<StatusRing, IBrush?>(nameof(FillBrush));
    public static readonly StyledProperty<IBrush?> DoneBrushProperty = AvaloniaProperty.Register<StatusRing, IBrush?>(nameof(DoneBrush));

    public StatusRingKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>0..1 share of the pie for <see cref="StatusRingKind.Progress"/>.</summary>
    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? RingBrush { get => GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? DoneBrush { get => GetValue(DoneBrushProperty); set => SetValue(DoneBrushProperty, value); }

    static StatusRing()
    {
        AffectsRender<StatusRing>(KindProperty, FractionProperty, RingBrushProperty, FillBrushProperty, DoneBrushProperty);
        AffectsMeasure<StatusRing>(SizeProperty);
    }

    public StatusRing()
    {
        // Token defaults; a local value or style still wins.
        Bind(RingBrushProperty, this.GetResourceObservable("Bolt.Fg.Faint"), BindingPriority.Template);
        Bind(FillBrushProperty, this.GetResourceObservable("Bolt.Accent"), BindingPriority.Template);
        Bind(DoneBrushProperty, this.GetResourceObservable("Bolt.Success"), BindingPriority.Template);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Kind == StatusRingKind.None) return;
        var s = Size / 16.0;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = 5.5 * s;
        var pen = new Pen(RingBrush, 1.6 * s, lineCap: PenLineCap.Round);
        switch (Kind)
        {
            case StatusRingKind.Pending:
                pen = new Pen(RingBrush, 1.6 * s, dashStyle: new DashStyle([1.4, 1.6], 0), lineCap: PenLineCap.Flat);
                context.DrawEllipse(null, pen, c, r, r);
                break;
            case StatusRingKind.Empty:
                context.DrawEllipse(null, pen, c, r, r);
                break;
            case StatusRingKind.Progress:
                context.DrawEllipse(null, pen, c, r, r);
                DrawPie(context, FillBrush, c, r, Math.Clamp(Fraction, 0, 1));
                break;
            case StatusRingKind.Done:
                context.DrawEllipse(DoneBrush, null, c, r + 0.8 * s, r + 0.8 * s);
                break;
            case StatusRingKind.Cancelled:
                context.DrawEllipse(null, pen, c, r, r);
                var d = r * 0.7071;
                context.DrawLine(pen, new Point(c.X - d, c.Y + d), new Point(c.X + d, c.Y - d));
                break;
        }
    }

    private static void DrawPie(DrawingContext context, IBrush? brush, Point c, double r, double fraction)
    {
        if (brush is null || fraction <= 0) return;
        if (fraction >= 1) { context.DrawEllipse(brush, null, c, r, r); return; }
        var angle = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(c, true);
            g.LineTo(start);
            g.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }
}
