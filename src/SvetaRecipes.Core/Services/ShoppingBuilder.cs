using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Services;

/// <summary>
/// Turns recipes into shopping items: every ingredient, including those inside sub-recipes (scaled to the amount the
/// parent actually uses), totalled per ingredient in the ingredient's own unit where a conversion exists.
/// (The legacy app listed sub-recipe ingredients at full, unscaled quantity and never totalled duplicates.)
/// </summary>
public static class ShoppingBuilder
{
    public static List<ShoppingItem> FromRecipes(RecipeBook book, IEnumerable<(Recipe Recipe, double Factor)> recipes)
    {
        var calc = book.Calculator();
        var units = book.UnitConverter;
        var totals = new Dictionary<(int, string), (double Qty, List<string> Sources)>();

        foreach (var (recipe, factor) in recipes)
            Walk(recipe, factor, recipe.Name, []);

        var lastStore = LastStores(book);
        return totals
            .Select(kv =>
            {
                var ing = book.FindIngredient(kv.Key.Item1)!;
                return new ShoppingItem
                {
                    IngredientId = ing.Id,
                    Name = ing.Name,
                    Quantity = Math.Round(kv.Value.Qty, 1),
                    Unit = kv.Key.Item2,
                    Store = lastStore.GetValueOrDefault(ing.Id),
                    Notes = string.Join(", ", kv.Value.Sources.Distinct()),
                };
            })
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void Walk(Recipe recipe, double factor, string source, HashSet<int> path)
        {
            path.Add(recipe.Id);
            foreach (var line in recipe.Lines.OrderBy(l => l.Position))
            {
                if (line.Kind == LineKind.Ingredient && line.IngredientId is { } ingId && book.FindIngredient(ingId) is { } ing)
                {
                    var conv = units.Get(line.Unit, ing.Unit);
                    var (qty, unit) = conv.IsMissing ? (line.Amount * factor, line.Unit) : (line.Amount * conv.Factor * factor, ing.Unit);
                    var key = (ingId, unit);
                    var t = totals.GetValueOrDefault(key, (0, []));
                    t.Sources.Add(source);
                    totals[key] = (t.Qty + qty, t.Sources);
                }
                else if (line.Kind == LineKind.SubRecipe && line.SubRecipeId is { } subId && !path.Contains(subId)
                         && calc.Calculate(subId) is { TotalAmount: > 0 } sub)
                {
                    var used = units.Convert(line.Amount, line.Unit, sub.Recipe.Unit) * factor;
                    Walk(sub.Recipe, used / sub.TotalAmount, source, path);
                }
            }
            path.Remove(recipe.Id);
        }
    }

    /// <summary>The store each ingredient was most recently bought at.</summary>
    public static Dictionary<int, string> LastStores(RecipeBook book) =>
        book.ShoppingLists.OrderBy(l => l.Date)
            .SelectMany(l => l.Items)
            .Where(i => i.IngredientId is not null && !string.IsNullOrWhiteSpace(i.Store))
            .GroupBy(i => i.IngredientId!.Value)
            .ToDictionary(g => g.Key, g => g.Last().Store!);

    /// <summary>Merges items with the same ingredient (or name) and unit, keeping the first item's store.</summary>
    public static List<ShoppingItem> Merge(IEnumerable<ShoppingItem> items) =>
        items.GroupBy(i => (i.IngredientId?.ToString() ?? i.Name.Trim().ToLowerInvariant(), (i.Unit ?? "").ToLowerInvariant()))
            .Select(g =>
            {
                var first = g.First();
                return new ShoppingItem
                {
                    IngredientId = first.IngredientId,
                    Name = first.Name,
                    Unit = first.Unit,
                    Quantity = g.Any(i => i.Quantity is not null) ? g.Sum(i => i.Quantity ?? 0) : null,
                    QuantityText = string.Join(" + ", g.Select(i => i.QuantityText).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()) is { Length: > 0 } t ? t : null,
                    Store = g.Select(i => i.Store).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                    Notes = string.Join(", ", g.Select(i => i.Notes).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()) is { Length: > 0 } n ? n : null,
                    Done = g.All(i => i.Done),
                };
            })
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
