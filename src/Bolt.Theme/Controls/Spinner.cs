using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Bolt.Theme.Controls;

/// <summary>
/// Activity spinner: a faint ring with a ¼ arc in <see cref="Foreground"/> that rotates continuously. Drawn like
/// <see cref="StatusRing"/> (16-unit grid scaled to <see cref="Size"/>); the animation only runs while the control is
/// attached and effectively visible.
/// </summary>
public class Spinner : Control
{
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Spinner, double>(nameof(Size), 12);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Spinner>();
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Spinner, double>(nameof(StrokeThickness), 1.8);
    public static readonly StyledProperty<TimeSpan> PeriodProperty = AvaloniaProperty.Register<Spinner, TimeSpan>(nameof(Period), TimeSpan.FromSeconds(0.9));

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>Arc color; inherited like text (so a parent's Foreground applies).</summary>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Stroke on the 16-unit grid.</summary>
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    /// <summary>One full turn.</summary>
    public TimeSpan Period { get => GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }

    private readonly RotateTransform _rotate = new();
    private CancellationTokenSource? _spin;
    private readonly List<Visual> _watched = [];
    private bool _attached;

    static Spinner()
    {
        AffectsRender<Spinner>(ForegroundProperty, StrokeThicknessProperty);
        AffectsMeasure<Spinner>(SizeProperty);
    }

    public Spinner()
    {
        RenderTransform = _rotate;
        RenderTransformOrigin = RelativePoint.Center;
        IsHitTestVisible = true;
    }

    /// <summary>True while the rotation animation runs (tests).</summary>
    public bool IsSpinning => _spin is not null;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        // IsEffectivelyVisible has no public change event: watch IsVisible on this control and every ancestor.
        foreach (var v in this.GetSelfAndVisualAncestors()) { v.PropertyChanged += OnVisibilityChanged; _watched.Add(v); }
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        foreach (var v in _watched) v.PropertyChanged -= OnVisibilityChanged;
        _watched.Clear();
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PeriodProperty && _spin is not null) { Stop(); UpdateSpin(); }
    }

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == IsVisibleProperty) UpdateSpin(); }

    private void UpdateSpin()
    {
        var shouldSpin = _attached && _watched.All(v => v.IsVisible);
        if (shouldSpin && _spin is null) Start();
        else if (!shouldSpin) Stop();
    }

    // Avalonia refuses infinite animations through Animation.RunAsync ("Looping animations must not use the Run method"),
    // so the angle is driven by the top level's animation-frame callback while the spinner should spin.
    private void Start()
    {
        _spin = new CancellationTokenSource();
        var token = _spin.Token;
        TimeSpan? origin = null;
        void Frame(TimeSpan now)
        {
            if (token.IsCancellationRequested) return;
            origin ??= now;
            var period = Period.TotalMilliseconds <= 0 ? 900 : Period.TotalMilliseconds;
            _rotate.Angle = (now - origin.Value).TotalMilliseconds / period * 360 % 360;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }

    private void Stop()
    {
        _spin?.Cancel();
        _spin = null;
    }

    public override void Render(DrawingContext context)
    {
        var s = Size / 16.0;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = 5.5 * s;
        var brush = Foreground;
        if (brush is null) return;
        // Faint full ring, then a quarter arc on top.
        using (context.PushOpacity(0.25)) context.DrawEllipse(null, new Pen(brush, StrokeThickness * s), c, r, r);
        var ring = new Pen(brush, StrokeThickness * s, lineCap: PenLineCap.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false);
            g.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, ring, geometry);
    }
}
