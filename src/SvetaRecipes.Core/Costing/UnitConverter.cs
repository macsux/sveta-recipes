using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Costing;

public enum ConversionKind
{
    /// <summary>Same unit, factor 1.</summary>
    Identity,
    /// <summary>A From→To row exists in the conversion table.</summary>
    Direct,
    /// <summary>Only the To→From row exists; its reciprocal is used.</summary>
    Inverse,
    /// <summary>Two steps through an intermediate unit (L → ml → g).</summary>
    Chained,
    /// <summary>No conversion known. Treated as 1:1, which is what the legacy app did silently.</summary>
    Missing,
}

public readonly record struct Conversion(double Factor, ConversionKind Kind)
{
    public bool IsMissing => Kind == ConversionKind.Missing;
}

/// <summary>Unit-to-unit factors from the user's conversion table. Unit codes compare case-insensitively.</summary>
public sealed class UnitConverter
{
    private readonly Dictionary<(string, string), double> _factors;
    private readonly List<string> _units;

    public UnitConverter(IEnumerable<UnitConversion> conversions)
    {
        _factors = new Dictionary<(string, string), double>(PairComparer.Instance);
        foreach (var c in conversions)
            if (c.Factor != 0) _factors[(c.From, c.To)] = c.Factor;
        _units = _factors.Keys.SelectMany(k => new[] { k.Item1, k.Item2 }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private readonly Dictionary<(string, string), Conversion> _memo = new(PairComparer.Instance);

    public Conversion Get(string? from, string? to)
    {
        from ??= "";
        to ??= "";
        if (_memo.TryGetValue((from, to), out var known)) return known;
        return _memo[(from, to)] = Resolve(from, to);
    }

    private Conversion Resolve(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return new(1, ConversionKind.Identity);
        if (_factors.TryGetValue((from, to), out var f)) return new(f, ConversionKind.Direct);
        if (_factors.TryGetValue((to, from), out var r)) return new(1 / r, ConversionKind.Inverse);
        foreach (var via in _units)
            if (Step(from, via) is { } a && Step(via, to) is { } b) return new(a * b, ConversionKind.Chained);
        return new(1, ConversionKind.Missing);
    }

    private double? Step(string from, string to) =>
        string.Equals(from, to, StringComparison.OrdinalIgnoreCase) ? null
        : _factors.TryGetValue((from, to), out var f) ? f
        : _factors.TryGetValue((to, from), out var r) ? 1 / r
        : null;

    public double Convert(double amount, string? from, string? to) => amount * Get(from, to).Factor;

    private sealed class PairComparer : IEqualityComparer<(string, string)>
    {
        public static readonly PairComparer Instance = new();
        private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
        public bool Equals((string, string) x, (string, string) y) => C.Equals(x.Item1, y.Item1) && C.Equals(x.Item2, y.Item2);
        public int GetHashCode((string, string) o) => HashCode.Combine(C.GetHashCode(o.Item1), C.GetHashCode(o.Item2));
    }
}
