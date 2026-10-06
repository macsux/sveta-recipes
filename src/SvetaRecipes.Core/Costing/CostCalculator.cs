using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Costing;

public sealed record LineCost(
    RecipeLine Line,
    string Name,
    /// <summary>The line's quantity expressed in the parent recipe's unit (drives total weight and %).</summary>
    double AmountInRecipeUnit,
    decimal Cost,
    /// <summary>Share of the recipe's total weight, 0–100.</summary>
    double Percent,
    string? Warning)
{
    public bool IsSeparator => Line.Kind == LineKind.Separator;
}

public sealed record RecipeCost(
    Recipe Recipe,
    double TotalAmount,
    decimal ComponentsCost,
    decimal LabourCost,
    IReadOnlyList<LineCost> Lines,
    IReadOnlyList<string> Warnings)
{
    public decimal TotalCost => ComponentsCost + LabourCost;

    public decimal? CostPerServing => Recipe.CostMethod switch
    {
        ServingCostMethod.ByServingCount when Recipe.Servings is > 0 => TotalCost / Recipe.Servings.Value,
        ServingCostMethod.ByServingWeight when Recipe.WeightPerServing is > 0 && TotalAmount > 0 =>
            TotalCost * (decimal)Recipe.WeightPerServing.Value / (decimal)TotalAmount,
        _ => null,
    };
}

/// <summary>
/// Live recipe costing. Ported from the Access app (CalculateItemCost / CalculateRecipeCost / CalculateItemAmount),
/// with two deliberate differences:
/// <list type="bullet">
/// <item>Sub-recipe costs are computed recursively on demand instead of read from a cached <c>Cost</c> column that only
/// refreshed when someone opened that recipe, so a price change reaches every recipe immediately.</item>
/// <item>A unit pair missing from the table uses the reverse pair's reciprocal when there is one, and is reported as a
/// warning either way. The legacy app silently counted it as 1:1.</item>
/// </list>
/// Results are memoised per calculator; create a new one after data changes.
/// </summary>
public sealed class CostCalculator(
    Func<int, Recipe?> recipes,
    Func<int, Ingredient?> ingredients,
    UnitConverter units,
    decimal labourRate)
{
    private readonly Dictionary<int, RecipeCost> _cache = [];
    private readonly HashSet<int> _inProgress = [];

    public decimal LabourRate => labourRate;

    public RecipeCost Calculate(Recipe recipe)
    {
        if (recipe.Id != 0 && _cache.TryGetValue(recipe.Id, out var cached) && ReferenceEquals(cached.Recipe, recipe))
            return cached;

        var warnings = new List<string>();
        var rows = new List<(RecipeLine line, string name, double amount, decimal cost, string? warning)>();
        _inProgress.Add(recipe.Id);
        try
        {
            foreach (var line in recipe.Lines.OrderBy(l => l.Position))
            {
                var row = line.Kind switch
                {
                    LineKind.Ingredient => IngredientLine(recipe, line),
                    LineKind.SubRecipe => SubRecipeLine(recipe, line),
                    _ => (line, "", 0d, 0m, (string?)null),
                };
                if (row.Item5 is { } w) warnings.Add($"{row.Item2}: {w}");
                rows.Add(row);
            }
        }
        finally
        {
            _inProgress.Remove(recipe.Id);
        }

        var total = rows.Sum(r => r.amount);
        var lines = rows
            .Select(r => new LineCost(r.line, r.name, r.amount, r.cost, total > 0 ? r.amount / total * 100 : 0, r.warning))
            .ToList();
        var result = new RecipeCost(recipe, total, lines.Sum(l => l.Cost), (decimal)recipe.LabourHours * labourRate, lines, warnings);
        if (recipe.Id != 0) _cache[recipe.Id] = result;
        return result;
    }

    public RecipeCost? Calculate(int recipeId) => recipes(recipeId) is { } r ? Calculate(r) : null;

    private (RecipeLine, string, double, decimal, string?) IngredientLine(Recipe recipe, RecipeLine line)
    {
        if (line.IngredientId is not { } id || ingredients(id) is not { } ing)
            return (line, "(missing ingredient)", 0, 0, "ingredient no longer exists");

        // Only conversions that change the COST are warned about. A missing line→recipe conversion only skews the total
        // weight and % column, exactly as in the legacy app (recipes measured in "pc", typos like "gg").
        if (ing.PerPiece)
        {
            // PackPrice = price of one piece; PackQty = weight of one piece, in the ingredient's Unit.
            var pieces = IsPieces(line.Unit) ? line.Amount : PiecesFromWeight(line, ing, out _) ?? line.Amount;
            var weight = units.Get(ing.Unit, recipe.Unit);
            var warning = !IsPieces(line.Unit) && PiecesFromWeight(line, ing, out _) is null
                ? $"no conversion from {line.Unit} to pieces or {ing.Unit}, counted as pieces"
                : null;
            return (line, ing.Name, pieces * ing.PackQty * weight.Factor, (decimal)pieces * ing.PackPrice, warning);
        }

        var toPack = units.Get(line.Unit, ing.Unit);
        var cost = ing.PackQty > 0 ? (decimal)(line.Amount * toPack.Factor / ing.PackQty) * ing.PackPrice : 0;
        var amount = line.Amount * units.Get(line.Unit, recipe.Unit).Factor;
        var warn = ing.PackQty <= 0 ? "no pack size, so no price" : Describe(toPack, line.Unit, ing.Unit);
        return (line, ing.Name, amount, cost, warn);
    }

    private double? PiecesFromWeight(RecipeLine line, Ingredient ing, out double weight)
    {
        var c = units.Get(line.Unit, ing.Unit);
        weight = line.Amount * c.Factor;
        return c.IsMissing || ing.PackQty <= 0 ? null : weight / ing.PackQty;
    }

    private static bool IsPieces(string? unit) => string.Equals(unit, "pc", StringComparison.OrdinalIgnoreCase);

    private (RecipeLine, string, double, decimal, string?) SubRecipeLine(Recipe recipe, RecipeLine line)
    {
        if (line.SubRecipeId is not { } id || recipes(id) is not { } sub)
            return (line, "(missing recipe)", 0, 0, "sub-recipe no longer exists");
        if (_inProgress.Contains(id))
            return (line, sub.Name, 0, 0, "recipe includes itself");

        var subCost = Calculate(sub);
        var toSub = units.Get(line.Unit, sub.Unit);
        var toRecipe = units.Get(line.Unit, recipe.Unit);
        var amount = line.Amount * toRecipe.Factor;
        if (subCost.TotalAmount <= 0)
            return (line, sub.Name, amount, 0, "sub-recipe has no weight, so no cost");

        // The fraction of the sub-recipe used carries its labour along with its ingredients.
        var fraction = line.Amount * toSub.Factor / subCost.TotalAmount;
        var cost = (decimal)fraction * subCost.TotalCost;
        var warning = Describe(toSub, line.Unit, sub.Unit)
                      ?? (subCost.Warnings.Count > 0 ? $"its cost is incomplete ({subCost.Warnings.Count} problem(s) inside)" : null);
        return (line, sub.Name, amount, cost, warning);
    }

    private static string? Describe(Conversion c, string from, string to) =>
        c.IsMissing ? $"no conversion from {from} to {to}, counted 1:1" : null;

}
