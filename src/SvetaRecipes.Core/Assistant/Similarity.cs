using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Assistant;

/// <summary>A line of a recipe being compared: an ingredient, a sub-recipe, or (neither) an ingredient she doesn't have.</summary>
public sealed record DraftLine(int? IngredientId, int? SubRecipeId, string? Name, double Grams);

public sealed record SimilarRecipe(
    int Id,
    string Name,
    /// <summary>Shared weight fraction of the two compositions, 0–1 (1 = same ingredients in the same proportions).</summary>
    double Overlap,
    /// <summary>Character-trigram similarity of the names, 0–1.</summary>
    double NameSimilarity,
    /// <summary>Biggest ingredients of the draft the candidate lacks, as "Butter 18%".</summary>
    IReadOnlyList<string> OnlyInDraft,
    /// <summary>Biggest ingredients of the candidate the draft lacks.</summary>
    IReadOnlyList<string> OnlyInCandidate);

public sealed record SimilarityResult(
    /// <summary>Share of the draft's weight that is made of her ingredients (lines with neither id count against it).</summary>
    double Coverage,
    IReadOnlyList<SimilarRecipe> Matches);

/// <summary>
/// Finds recipes with a similar composition. Every recipe is flattened to its leaf ingredients (sub-recipes expanded,
/// scaled by the amount used) as weight fractions, so structure doesn't matter: a flat recipe and one built from
/// sub-recipes compare equal when the ingredients and proportions are the same. Score = Σ min(share in A, share in B).
/// </summary>
public sealed class Similarity(IReadOnlyList<Recipe> recipes, CostCalculator calculator, UnitConverter units, Func<int, Ingredient?> ingredients)
{
    private const double NameWeight = 0.1;
    private readonly Dictionary<int, Dictionary<string, double>> _fractions = [];
    private readonly HashSet<int> _inProgress = [];

    public SimilarityResult Find(IReadOnlyList<DraftLine> draft, int top = 10, int? excludeRecipeId = null, string? draftName = null)
    {
        var grams = new Dictionary<string, double>();
        var matched = 0d;
        foreach (var line in draft.Where(l => l.Grams > 0))
        {
            if (line.IngredientId is { } i && ingredients(i) is not null) { Add(grams, Key(i), line.Grams); matched += line.Grams; }
            else if (line.SubRecipeId is { } s && recipes.FirstOrDefault(r => r.Id == s) is { } sub)
            {
                foreach (var (k, f) in Fractions(sub)) Add(grams, k, line.Grams * f);
                matched += line.Grams;
            }
            else Add(grams, "new:" + (line.Name ?? "?").Trim().ToLowerInvariant(), line.Grams);
        }
        var total = grams.Values.Sum();
        var a = Normalize(grams);
        var nameGrams = draftName is null ? null : Trigrams(draftName);

        var matches = recipes
            .Where(r => r.Id != excludeRecipeId)
            .Select(r => (r, b: Fractions(r)))
            .Where(x => x.b.Count > 0)
            .Select(x =>
            {
                var overlap = a.Sum(kv => Math.Min(kv.Value, x.b.GetValueOrDefault(kv.Key)));
                var name = nameGrams is null ? 0 : Jaccard(nameGrams, Trigrams(x.r.Name));
                return (x.r, x.b, overlap, name, score: overlap + NameWeight * name);
            })
            .Where(x => x.overlap > 0 || x.name > 0.3)
            .OrderByDescending(x => x.score)
            .Take(Math.Clamp(top, 1, 50))
            .Select(x => new SimilarRecipe(x.r.Id, x.r.Name, Math.Round(x.overlap, 3), Math.Round(x.name, 3),
                Missing(a, x.b), Missing(x.b, a)))
            .ToList();
        return new SimilarityResult(total > 0 ? Math.Round(matched / total, 3) : 0, matches);
    }

    /// <summary>Compares an existing recipe against all the others.</summary>
    public SimilarityResult Find(Recipe recipe, int top = 10)
    {
        var lines = Fractions(recipe).Select(kv => new DraftLine(int.Parse(kv.Key[2..]), null, null, kv.Value)).ToList();
        return Find(lines, top, recipe.Id, recipe.Name);
    }

    /// <summary>Leaf-ingredient weight fractions of a recipe (sums to 1; empty when it has no weight).</summary>
    public IReadOnlyDictionary<string, double> Composition(Recipe recipe) => Fractions(recipe);

    private Dictionary<string, double> Fractions(Recipe recipe)
    {
        if (_fractions.TryGetValue(recipe.Id, out var cached)) return cached;
        if (!_inProgress.Add(recipe.Id)) return [];
        try
        {
            var grams = new Dictionary<string, double>();
            var toGrams = units.Get(recipe.Unit, "g").Factor;
            foreach (var line in calculator.Calculate(recipe).Lines)
            {
                var w = line.AmountInRecipeUnit * toGrams;
                if (w <= 0) continue;
                if (line.Line is { Kind: LineKind.Ingredient, IngredientId: { } i }) Add(grams, Key(i), w);
                else if (line.Line is { Kind: LineKind.SubRecipe, SubRecipeId: { } s } && recipes.FirstOrDefault(r => r.Id == s) is { } sub)
                    foreach (var (k, f) in Fractions(sub)) Add(grams, k, w * f);
            }
            return _fractions[recipe.Id] = Normalize(grams);
        }
        finally
        {
            _inProgress.Remove(recipe.Id);
        }
    }

    private IReadOnlyList<string> Missing(Dictionary<string, double> from, Dictionary<string, double> other) =>
        from.Where(kv => kv.Value >= 0.02 && !other.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value).Take(3)
            .Select(kv => $"{NameOf(kv.Key)} {kv.Value * 100:0}%").ToList();

    private string NameOf(string key) =>
        key.StartsWith("i:") && ingredients(int.Parse(key[2..])) is { } i ? i.Name : key.StartsWith("new:") ? key[4..] : key;

    private static string Key(int ingredientId) => "i:" + ingredientId;

    private static void Add(Dictionary<string, double> d, string key, double value) =>
        d[key] = d.GetValueOrDefault(key) + value;

    private static Dictionary<string, double> Normalize(Dictionary<string, double> grams)
    {
        var total = grams.Values.Sum();
        return total <= 0 ? [] : grams.ToDictionary(kv => kv.Key, kv => kv.Value / total);
    }

    private static HashSet<string> Trigrams(string name)
    {
        var s = " " + new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()) + " ";
        var set = new HashSet<string>();
        for (var i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b) =>
        a.Count + b.Count == 0 ? 0 : (double)a.Count(b.Contains) / a.Union(b).Count();
}
