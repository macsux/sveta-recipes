using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Costing;

/// <summary>A pan: shape plus dimensions in cm. Round pans use <see cref="Length"/> as the diameter.</summary>
public readonly record struct Pan(PanShape Shape, double? Width, double? Length, double? Height)
{
    public static Pan Of(Recipe r) => new(r.Shape, r.Width, r.Length, r.Height);

    /// <summary>cm². Null when a needed dimension is missing.</summary>
    public double? Area => Shape switch
    {
        PanShape.Round when Length is > 0 => Math.PI * Math.Pow(Length.Value / 2, 2),
        PanShape.Rectangle when Width is > 0 && Length is > 0 => Width.Value * Length.Value,
        _ => null,
    };

    /// <summary>cm³. Null when a needed dimension is missing.</summary>
    public double? Volume => Area is { } a && Height is > 0 ? a * Height.Value : null;

    public bool IsMeasured => Area is not null;
}

public static class Scaling
{
    /// <summary>
    /// Factor for moving a recipe from one pan to another: the volume ratio when both pans have a height, otherwise
    /// the area ratio. Null when the pans can't be compared. (Legacy used π = 3.14; the ratio is unaffected unless the
    /// shapes differ, where the exact value is now used.)
    /// </summary>
    public static double? PanFactor(Pan original, Pan target)
    {
        if (original.Volume is { } v0 && target.Volume is { } v1 && v0 > 0) return v1 / v0;
        if (original.Area is { } a0 && target.Area is { } a1 && a0 > 0) return a1 / a0;
        return null;
    }

    public static double? AmountFactor(double originalAmount, double targetAmount) =>
        originalAmount > 0 && targetAmount > 0 ? targetAmount / originalAmount : null;

    public static double? ServingsFactor(int? originalServings, double targetServings) =>
        originalServings is > 0 && targetServings > 0 ? targetServings / originalServings.Value : null;
}

/// <summary>A line of a scaled, fully expanded recipe, for preview and printing.</summary>
public sealed record ExpandedLine(int Depth, LineKind Kind, string Name, double Amount, string Unit, double Percent, string? Notes);

public static class RecipeExpander
{
    /// <summary>
    /// The recipe scaled by <paramref name="factor"/>, with every sub-recipe's own lines listed under it (scaled to the
    /// amount the parent uses), recursively.
    /// </summary>
    public static List<ExpandedLine> Expand(CostCalculator calc, Recipe recipe, double factor, UnitConverter units)
    {
        var result = new List<ExpandedLine>();
        Walk(calc, recipe, factor, units, 0, result, []);
        return result;
    }

    private static void Walk(CostCalculator calc, Recipe recipe, double factor, UnitConverter units, int depth,
        List<ExpandedLine> output, HashSet<int> path)
    {
        var cost = calc.Calculate(recipe);
        path.Add(recipe.Id);
        foreach (var lc in cost.Lines)
        {
            var line = lc.Line;
            output.Add(new ExpandedLine(depth, line.Kind, lc.Name, line.Amount * factor, line.Unit, lc.Percent, line.Notes));
            if (line.Kind != LineKind.SubRecipe || line.SubRecipeId is not { } subId || path.Contains(subId)) continue;
            var sub = calc.Calculate(subId);
            if (sub is null || sub.TotalAmount <= 0) continue;
            var used = units.Convert(line.Amount, line.Unit, sub.Recipe.Unit) * factor;
            Walk(calc, sub.Recipe, used / sub.TotalAmount, units, depth + 1, output, path);
        }
        path.Remove(recipe.Id);
    }
}
