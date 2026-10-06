using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Tests;

public class CostingTests
{
    private static readonly UnitConversion[] Table =
    [
        new() { From = "kg", To = "g", Factor = 1000 },
        new() { From = "g", To = "kg", Factor = 0.001 },
        new() { From = "ml", To = "g", Factor = 1 },
        new() { From = "kg", To = "lb", Factor = 2.2026 },
    ];

    private static readonly Ingredient Cream = new() { Id = 1, Name = "Cream 35%", Unit = "ml", PackQty = 1000, PackPrice = 7m };
    private static readonly Ingredient Sugar = new() { Id = 2, Name = "Sugar", Unit = "kg", PackQty = 2, PackPrice = 6m };
    // As in her data: a per-piece ingredient is priced per piece and its Unit/PackQty give the weight of one piece.
    private static readonly Ingredient Egg = new() { Id = 3, Name = "Egg (per pc)", Unit = "g", PackQty = 50, PackPrice = 0.4m, PerPiece = true };

    private static CostCalculator Calc(params Recipe[] recipes) =>
        new(id => recipes.FirstOrDefault(r => r.Id == id), id => new[] { Cream, Sugar, Egg }.FirstOrDefault(i => i.Id == id),
            new UnitConverter(Table), labourRate: 20m);

    private static RecipeLine Ing(Ingredient i, double amount, string unit, int pos = 0) =>
        new() { Kind = LineKind.Ingredient, IngredientId = i.Id, Amount = amount, Unit = unit, Position = pos };

    [Fact]
    public void Units_identity_direct_inverse_and_missing()
    {
        var u = new UnitConverter(Table);
        Assert.Equal(new Conversion(1, ConversionKind.Identity), u.Get("G", "g"));
        Assert.Equal(ConversionKind.Direct, u.Get("KG", "g").Kind);
        var inv = u.Get("lb", "kg");
        Assert.Equal(ConversionKind.Inverse, inv.Kind);
        Assert.Equal(1 / 2.2026, inv.Factor, 10);
        Assert.True(u.Get("cup", "g").IsMissing);
        Assert.Equal(1, u.Get("cup", "g").Factor);
        var chained = u.Get("kg", "ml");   // kg → g → ml (via the reciprocal of ml → g)
        Assert.Equal(ConversionKind.Chained, chained.Kind);
        Assert.Equal(1000, chained.Factor, 9);
    }

    [Fact]
    public void Ingredient_line_cost_uses_pack_price_and_converts_units()
    {
        var r = new Recipe { Id = 10, Unit = "g", Lines = [Ing(Cream, 100, "ml"), Ing(Sugar, 10, "g", 1)] };
        var c = Calc(r).Calculate(r);
        Assert.Equal(0.70m, c.Lines[0].Cost);               // 100 ml of 1000 ml @ $7
        Assert.Equal(0.03m, decimal.Round(c.Lines[1].Cost, 2)); // 10 g = 0.01 kg of 2 kg @ $6
        Assert.Equal(110, c.TotalAmount);                    // 100 ml→g (1:1) + 10 g
        Assert.Equal(100 / 110.0 * 100, c.Lines[0].Percent, 6);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Per_piece_ingredient_costs_per_piece_and_weighs_pack_qty_each()
    {
        var r = new Recipe { Id = 10, Unit = "g", Lines = [Ing(Egg, 3, "pc")] };
        var c = Calc(r).Calculate(r);
        Assert.Equal(1.2m, c.Lines[0].Cost);
        Assert.Equal(150, c.TotalAmount);
        Assert.Empty(c.Warnings);

        var byWeight = new Recipe { Id = 11, Unit = "g", Lines = [Ing(Egg, 100, "g")] }; // 100 g of egg = 2 eggs
        Assert.Equal(0.8m, Calc(byWeight).Calculate(byWeight).Lines[0].Cost);
    }

    [Fact]
    public void Sub_recipe_cost_is_live_and_carries_prorated_labour()
    {
        var sub = new Recipe { Id = 20, Name = "Crème", Unit = "g", LabourHours = 0.5, Lines = [Ing(Cream, 400, "ml"), Ing(Sugar, 100, "g", 1)] };
        var parent = new Recipe
        {
            Id = 21, Unit = "g", LabourHours = 1,
            Lines = [new() { Kind = LineKind.SubRecipe, SubRecipeId = 20, Amount = 250, Unit = "g" }],
        };
        var calc = Calc(sub, parent);
        var subCost = calc.Calculate(sub);
        Assert.Equal(500, subCost.TotalAmount);
        Assert.Equal(2.8m + 0.3m, subCost.ComponentsCost);
        Assert.Equal(10m, subCost.LabourCost);

        var p = calc.Calculate(parent);
        Assert.Equal((2.8m + 0.3m + 10m) / 2, p.ComponentsCost); // half the sub-recipe, labour included
        Assert.Equal(20m, p.LabourCost);
    }

    [Fact]
    public void A_price_change_reaches_parents_with_a_fresh_calculator()
    {
        var sub = new Recipe { Id = 20, Unit = "g", Lines = [Ing(Cream, 1000, "ml")] };
        var parent = new Recipe { Id = 21, Unit = "g", Lines = [new() { Kind = LineKind.SubRecipe, SubRecipeId = 20, Amount = 500, Unit = "g" }] };
        var before = Calc(sub, parent).Calculate(parent).TotalCost;
        var original = Cream.PackPrice;
        try
        {
            Cream.PackPrice = 14m;
            Assert.Equal(before * 2, Calc(sub, parent).Calculate(parent).TotalCost);
        }
        finally { Cream.PackPrice = original; }
    }

    [Fact]
    public void Self_inclusion_is_reported_not_infinite()
    {
        var a = new Recipe { Id = 1, Name = "A", Unit = "g", Lines = [new() { Kind = LineKind.SubRecipe, SubRecipeId = 2, Amount = 10, Unit = "g" }] };
        var b = new Recipe { Id = 2, Name = "B", Unit = "g", Lines = [Ing(Cream, 10, "ml"), new() { Kind = LineKind.SubRecipe, SubRecipeId = 1, Amount = 5, Unit = "g", Position = 1 }] };
        var calc = Calc(a, b);
        var all = calc.Calculate(a).Warnings.Concat(calc.Calculate(b).Warnings).ToList();
        Assert.Contains(all, w => w.Contains("includes itself"));
        Assert.NotEmpty(calc.Calculate(a).Warnings);
        Assert.NotEmpty(calc.Calculate(b).Warnings);
    }

    [Fact]
    public void Missing_conversion_counts_one_to_one_and_warns()
    {
        var r = new Recipe { Id = 10, Unit = "g", Lines = [Ing(Sugar, 1, "cup")] };
        var c = Calc(r).Calculate(r);
        Assert.Single(c.Warnings);
        Assert.Contains("no conversion from cup to kg", c.Warnings[0]);
    }

    [Theory]
    [InlineData(ServingCostMethod.ByServingCount, 10, null, 2.14)]
    [InlineData(ServingCostMethod.ByServingWeight, null, 50.0, 5.35)]
    public void Cost_per_serving(ServingCostMethod method, int? servings, double? weight, double expected)
    {
        // 200 ml cream ($1.40) + 1 h labour ($20) = $21.40 over 200 g
        var r = new Recipe { Id = 10, Unit = "g", LabourHours = 1, CostMethod = method, Servings = servings, WeightPerServing = weight, Lines = [Ing(Cream, 200, "ml")] };
        var c = Calc(r).Calculate(r);
        Assert.Equal(21.4m, c.TotalCost);
        Assert.Equal((decimal)expected, decimal.Round(c.CostPerServing!.Value, 4));
    }

    [Fact]
    public void Pan_scaling_prefers_volume_then_area()
    {
        var round20 = new Pan(PanShape.Round, null, 20, 5);
        var round24 = new Pan(PanShape.Round, null, 24, 5);
        Assert.Equal(1.44, Scaling.PanFactor(round20, round24)!.Value, 9);
        Assert.Equal(1.44, Scaling.PanFactor(round20 with { Height = null }, round24)!.Value, 9);
        var square = new Pan(PanShape.Rectangle, 20, 20, 10);
        Assert.Equal(400 * 10 / (Math.PI * 100 * 5), Scaling.PanFactor(round20, square)!.Value, 9);
        Assert.Null(Scaling.PanFactor(new Pan(PanShape.Round, null, null, null), round24));
    }

    [Fact]
    public void Density_conversion_cup_of_ground_almonds_to_grams()
    {
        var cup = new DensityConversionUnit { Name = "cup [US]", Coefficient = -236.5882365 };
        var gram = new DensityConversionUnit { Name = "gram", Coefficient = 1 };
        Assert.Equal(236.5882365 * 0.36, DensityConverter.Convert(1, cup, gram, 0.36), 9);
        Assert.Equal(1, DensityConverter.Convert(236.5882365 * 0.36, gram, cup, 0.36), 9);
    }

    [Fact]
    public void Expansion_scales_sub_recipe_lines_by_amount_used()
    {
        var sub = new Recipe { Id = 20, Name = "Crème", Unit = "g", Lines = [Ing(Cream, 400, "ml"), Ing(Sugar, 100, "g", 1)] };
        var parent = new Recipe { Id = 21, Unit = "g", Lines = [new() { Kind = LineKind.SubRecipe, SubRecipeId = 20, Amount = 250, Unit = "g" }] };
        var lines = RecipeExpander.Expand(Calc(sub, parent), parent, 2, new UnitConverter(Table));
        Assert.Equal(3, lines.Count);
        Assert.Equal(500, lines[0].Amount);       // 250 g × 2
        Assert.Equal(400, lines[1].Amount, 9);    // whole sub-recipe (500 g) used → 400 ml cream
        Assert.Equal(1, lines[1].Depth);
    }
}
