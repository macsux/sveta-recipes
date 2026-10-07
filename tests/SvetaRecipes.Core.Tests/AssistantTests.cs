using Microsoft.EntityFrameworkCore;
using SvetaRecipes.Core.Assistant;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.Core.Tests;

public class AssistantTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ schema

    [Fact]
    public void Schema_notes_point_at_real_tables_and_columns()
    {
        var columns = DbSchema.Tables()
            .SelectMany(e => e.GetProperties().Select(p => $"{e.GetTableName()}.{p.GetColumnName()}")
                .Append(e.GetTableName()!))
            .ToHashSet();
        Assert.All(DbSchema.Notes.Keys, k => Assert.Contains(k, columns));
    }

    [Fact]
    public void Schema_lists_every_table_with_enum_values_and_foreign_keys()
    {
        var text = DbSchema.Describe();
        output.WriteLine(text);
        foreach (var e in DbSchema.Tables()) Assert.Contains(e.GetTableName() + "(", text);
        Assert.Contains("Kind enum(0 Separator, 1 Ingredient, 2 SubRecipe)", text);
        Assert.Contains("SubRecipeId int? →Recipes", text);
        Assert.Contains("SellPrice money?", text);
        Assert.Contains("RecipeTags(", text);
    }

    // ------------------------------------------------------------------ similarity

    private static readonly Ingredient Milk = new() { Id = 1, Name = "Milk", Unit = "ml", PackQty = 1000, PackPrice = 3m };
    private static readonly Ingredient Yolks = new() { Id = 2, Name = "Egg yolks", Unit = "g", PackQty = 100, PackPrice = 2m };
    private static readonly Ingredient Sugar = new() { Id = 3, Name = "Sugar", Unit = "kg", PackQty = 2, PackPrice = 6m };
    private static readonly Ingredient Starch = new() { Id = 4, Name = "Corn starch", Unit = "g", PackQty = 500, PackPrice = 3m };
    private static readonly Ingredient Butter = new() { Id = 5, Name = "Butter", Unit = "g", PackQty = 454, PackPrice = 6m };
    private static readonly Ingredient Flour = new() { Id = 6, Name = "Flour", Unit = "kg", PackQty = 10, PackPrice = 12m };
    private static readonly Ingredient[] All = [Milk, Yolks, Sugar, Starch, Butter, Flour];

    private static readonly UnitConversion[] Units =
    [
        new() { From = "kg", To = "g", Factor = 1000 },
        new() { From = "ml", To = "g", Factor = 1 },
    ];

    private static RecipeLine Ing(Ingredient i, double amount, string unit = "g") =>
        new() { Kind = LineKind.Ingredient, IngredientId = i.Id, Amount = amount, Unit = unit };

    private static RecipeLine Sub(Recipe r, double grams) =>
        new() { Kind = LineKind.SubRecipe, SubRecipeId = r.Id, Amount = grams, Unit = "g" };

    private static Recipe R(int id, string name, params RecipeLine[] lines)
    {
        for (var i = 0; i < lines.Length; i++) lines[i].Position = i;
        return new Recipe { Id = id, Name = name, Unit = "g", Lines = [.. lines] };
    }

    private static Similarity Engine(params Recipe[] recipes)
    {
        var units = new UnitConverter(Units);
        Ingredient? Find(int id) => All.FirstOrDefault(i => i.Id == id);
        var calc = new CostCalculator(id => recipes.FirstOrDefault(r => r.Id == id), Find, units, 20m);
        return new Similarity(recipes, calc, units, Find);
    }

    private static readonly Recipe PastryCream = R(10, "Pastry cream", Ing(Milk, 500, "ml"), Ing(Yolks, 100), Ing(Sugar, 0.1, "kg"), Ing(Starch, 40));
    private static readonly Recipe Shortbread = R(11, "Shortbread", Ing(Butter, 200), Ing(Sugar, 100), Ing(Flour, 300));

    [Fact]
    public void Same_proportions_in_other_units_and_size_score_one()
    {
        // Doubled, sugar in grams instead of kg, milk in grams instead of ml.
        var draft = new[]
        {
            new DraftLine(Milk.Id, null, "whole milk", 1000), new DraftLine(Yolks.Id, null, "yolks", 200),
            new DraftLine(Sugar.Id, null, "sugar", 200), new DraftLine(Starch.Id, null, "cornstarch", 80),
        };
        var result = Engine(PastryCream, Shortbread).Find(draft);
        Assert.Equal(1, result.Coverage);
        Assert.Equal(PastryCream.Id, result.Matches[0].Id);
        Assert.Equal(1, result.Matches[0].Overlap, 3);
        Assert.Empty(result.Matches[0].OnlyInDraft);
    }

    [Fact]
    public void Sub_recipes_are_expanded_so_flat_and_nested_versions_match()
    {
        // A tart that uses the pastry cream as a sub-recipe vs the same tart written out flat.
        var nested = R(20, "Tart nested", Sub(PastryCream, 300), Ing(Butter, 100));
        var cream = 500 + 100 + 100 + 40d;
        var flat = new[]
        {
            new DraftLine(Milk.Id, null, null, 300 * 500 / cream), new DraftLine(Yolks.Id, null, null, 300 * 100 / cream),
            new DraftLine(Sugar.Id, null, null, 300 * 100 / cream), new DraftLine(Starch.Id, null, null, 300 * 40 / cream),
            new DraftLine(Butter.Id, null, null, 100),
        };
        var result = Engine(PastryCream, Shortbread, nested).Find(flat);
        Assert.Equal(nested.Id, result.Matches[0].Id);
        Assert.Equal(1, result.Matches[0].Overlap, 3);

        // And a draft that names the sub-recipe directly scores the same.
        var bySub = Engine(PastryCream, Shortbread, nested).Find([new DraftLine(null, PastryCream.Id, null, 300), new DraftLine(Butter.Id, null, null, 100)]);
        Assert.Equal(1, bySub.Matches.First(m => m.Id == nested.Id).Overlap, 3);
    }

    [Fact]
    public void Unknown_ingredients_lower_coverage_and_overlap()
    {
        var draft = new[]
        {
            new DraftLine(Butter.Id, null, null, 200), new DraftLine(Sugar.Id, null, null, 100), new DraftLine(Flour.Id, null, null, 300),
            new DraftLine(null, null, "Yuzu zest", 400),
        };
        var result = Engine(PastryCream, Shortbread).Find(draft);
        Assert.Equal(0.6, result.Coverage, 3);
        Assert.Equal(Shortbread.Id, result.Matches[0].Id);
        Assert.Equal(0.6, result.Matches[0].Overlap, 3);
        Assert.Equal(["yuzu zest 40%"], result.Matches[0].OnlyInDraft);
    }

    [Fact]
    public void Existing_recipe_finds_its_near_copy_but_not_itself()
    {
        var copy = R(30, "Pastry cream (less sugar)", Ing(Milk, 500, "ml"), Ing(Yolks, 100), Ing(Sugar, 60), Ing(Starch, 40));
        var result = Engine(PastryCream, Shortbread, copy).Find(PastryCream);
        Assert.DoesNotContain(result.Matches, m => m.Id == PastryCream.Id);
        Assert.Equal(copy.Id, result.Matches[0].Id);
        Assert.True(result.Matches[0].Overlap > 0.9);
        Assert.True(result.Matches[0].NameSimilarity > 0.3);
    }

    // ------------------------------------------------------------------ her data

    private static string Db => Path.Combine(FindRoot(), ".data", "recipes.db");

    /// <summary>A recipe from her data, rescaled with one ingredient swapped for an unknown one, still finds the original first.</summary>
    [Fact]
    public void Her_recipes_find_themselves_after_rescaling_and_a_swap()
    {
        Assert.SkipUnless(File.Exists(Db), "imported database missing");
        var book = RecipeBook.Open(Db);
        var engine = book.Similarity();
        var candidates = book.Recipes.Where(r => r.Lines.Count(l => l.Kind == LineKind.Ingredient) >= 5).Take(60).ToList();
        var misses = 0;
        foreach (var r in candidates)
        {
            var comp = engine.Composition(r).OrderByDescending(kv => kv.Value).ToList();
            var draft = comp.Select((kv, i) => i == comp.Count - 1
                ? new DraftLine(null, null, "something new", kv.Value * 2.5)
                : new DraftLine(int.Parse(kv.Key[2..]), null, null, kv.Value * 2.5)).ToList();
            var top = engine.Find(draft, top: 10).Matches;
            // Identical-composition duplicates are legitimately tied; the original must be among the best-scoring.
            if (top.Count == 0 || top.All(m => m.Id != r.Id) || top.First(m => m.Id == r.Id).Overlap < top[0].Overlap - 1e-9) misses++;
        }
        output.WriteLine($"{candidates.Count - misses}/{candidates.Count} found at the top");
        Assert.Equal(0, misses);
    }

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
