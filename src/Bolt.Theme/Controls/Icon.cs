using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Bolt.Theme.Controls;

/// <summary>
/// A line icon drawn from a 16×16 path, stroked (or filled) with the inherited <see cref="Foreground"/> — the
/// counterpart of an inline SVG with <c>stroke="currentColor"</c>. Geometries live in the theme as <c>Bolt.Icon.*</c>.
/// </summary>
public class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 14);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.5);
    public static readonly StyledProperty<bool> IsFilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    /// <summary>Path in a 16-unit box (the SVG <c>viewBox="0 0 16 16"</c>).</summary>
    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>Rendered width and height in pixels.</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>Stroke width in 16-unit box coordinates (so it scales with <see cref="Size"/>, like SVG).</summary>
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    /// <summary>Fill the path instead of stroking it (dots, pie segments).</summary>
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeThicknessProperty, IsFilledProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null) return;
        var scale = Size / 16.0;
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (IsFilled) context.DrawGeometry(Foreground, null, Data);
            else context.DrawGeometry(null, new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Data);
        }
    }
}
