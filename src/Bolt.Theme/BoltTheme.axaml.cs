using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Bolt.Theme;

/// <summary>
/// The Bolt theme: Fluent underneath, Bolt tokens/templates on top. One knob — <see cref="Accent"/> — drives every
/// accent-derived resource (buttons, focus ring, tab underline, tinted user bubble), in both theme variants.
/// </summary>
public class BoltTheme : Styles
{
    public static readonly Color DefaultAccent = Color.Parse("#7C9BF7");

    // The surface the tinted bubble is mixed into, per variant (design: color-mix(in oklab, accent 13%, #141518)).
    private static readonly Color DarkBubbleBase = Color.Parse("#141518");
    private static readonly Color LightBubbleBase = Color.Parse("#FFFFFF");
    private static readonly Color DarkSecondaryText = Color.Parse("#B8BAC0");
    private static readonly Color LightText = Color.Parse("#1B1C1F");

    public BoltTheme(IServiceProvider? serviceProvider = null)
    {
        AvaloniaXamlLoader.Load(serviceProvider, this);
        ApplyAccent();
    }

    /// <summary>The single brand color. Changing it at runtime restyles everything that references Bolt.Accent*.</summary>
    public Color Accent
    {
        get;
        set { field = value; ApplyAccent(); }
    } = DefaultAccent;

    /// <summary>User chat bubbles tinted with the accent (true, the design default) or neutral.</summary>
    public bool TintedBubbles
    {
        get;
        set { field = value; ApplyAccent(); }
    } = true;

    /// <summary>The Bolt theme registered in the current application, if any.</summary>
    public static BoltTheme? Current => Application.Current?.Styles.OfType<BoltTheme>().FirstOrDefault();

    private void ApplyAccent()
    {
        if (Resources is not ResourceDictionary root) return;
        var accent = Accent;
        Set(root, ThemeVariant.Dark, accent, DarkBubbleBase, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Color.Parse("#17191E"),
            textAccent: Mix(accent, DarkSecondaryText, 0.25), hover: Mix(accent, Colors.White, 0.12));
        Set(root, ThemeVariant.Light, accent, LightBubbleBase, Color.FromArgb(0x1F, 0, 0, 0), Color.Parse("#F3F3F5"),
            textAccent: Mix(accent, LightText, 0.35), hover: Mix(accent, Colors.Black, 0.08));

        if (this.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
            foreach (var palette in fluent.Palettes.Values) palette.Accent = accent;
    }

    private void Set(ResourceDictionary root, ThemeVariant variant, Color accent, Color bubbleBase, Color neutralBubbleBorder, Color neutralBubble,
        Color textAccent, Color hover)
    {
        if (!root.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary dict)
            root.ThemeDictionaries[variant] = dict = new ResourceDictionary();

        dict["Bolt.Accent.Color"] = accent;
        dict["Bolt.Accent"] = new SolidColorBrush(accent);
        dict["Bolt.Accent.Hover"] = new SolidColorBrush(hover);
        dict["Bolt.Accent.Text"] = new SolidColorBrush(textAccent);
        dict["Bolt.Accent.Border"] = new SolidColorBrush(WithAlpha(accent, 0.55));
        dict["Bolt.Accent.Tint"] = new SolidColorBrush(WithAlpha(accent, 0.14));
        dict["Bolt.Accent.Selection"] = new SolidColorBrush(WithAlpha(accent, 0.35));
        dict["Bolt.Accent.Underline"] = new SolidColorBrush(WithAlpha(accent, 0.4));
        dict["Bolt.Shadow.FocusRing"] = new BoxShadows(new BoxShadow { Spread = 3, Color = WithAlpha(accent, 0.14) });
        dict["Bolt.Bubble.Bg"] = new SolidColorBrush(TintedBubbles ? Mix(bubbleBase, accent, 0.13) : neutralBubble);
        dict["Bolt.Bubble.Border"] = new SolidColorBrush(TintedBubbles ? WithAlpha(accent, 0.26) : neutralBubbleBorder);
    }

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    // ------------------------------------------------------------------ CSS color-mix(in oklab, a, b t)

    /// <summary>Mix <paramref name="a"/> toward <paramref name="b"/> by <paramref name="t"/> in OKLab, like CSS <c>color-mix(in oklab …)</c>.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        var (l1, a1, b1) = ToOklab(a);
        var (l2, a2, b2) = ToOklab(b);
        var c = FromOklab(l1 + (l2 - l1) * t, a1 + (a2 - a1) * t, b1 + (b2 - b1) * t);
        return Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * t), c.R, c.G, c.B);
    }

    private static double ToLinear(byte c) { var v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
    private static byte FromLinear(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }

    private static (double L, double A, double B) ToOklab(Color c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static Color FromOklab(double L, double A, double B)
    {
        var l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
        var m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
        var s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
        return Color.FromRgb(
            FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }
}
