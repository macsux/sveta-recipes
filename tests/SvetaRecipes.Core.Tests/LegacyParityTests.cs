using Microsoft.Data.Sqlite;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.Core.Tests;

/// <summary>
/// Checks the new engine against the legacy app, using the imported database (.data/recipes.db) and the mdbtools SQLite
/// dump (legacy/extracted/srd.sqlite). Skipped without them.
/// </summary>
public class LegacyParityTests(ITestOutputHelper output)
{
    private static readonly string Root = FindRoot();
    private static string NewDb => Path.Combine(Root, ".data", "recipes.db");
    private static string LegacyDb => Path.Combine(Root, "legacy", "extracted", "srd.sqlite");
    private static bool HaveData => File.Exists(NewDb) && File.Exists(LegacyDb);

    /// <summary>
    /// Ground truth from Sveta's screenshot of the legacy recipe screen (legacy/extracted/screenshots/01.png), taken with
    /// the recipe freshly opened, i.e. with live prices.
    /// </summary>
    [Fact]
    public void Caramel_choc_cremeux_matches_the_legacy_screen()
    {
        Assert.SkipUnless(HaveData, "imported database missing");
        var book = RecipeBook.Open(NewDb);
        var c = book.Calculator().Calculate(book.Recipes.Single(r => r.Name == "Caramel choc cremeux M"));

        Assert.Equal(201, c.TotalAmount);
        Assert.Equal(20.00m, c.LabourCost);
        Assert.Equal(5.72m, decimal.Round(c.ComponentsCost, 2));
        Assert.Equal(25.72m, decimal.Round(c.TotalCost, 2));
        Assert.Equal(25.72m, decimal.Round(c.CostPerServing!.Value, 2));

        var rows = c.Lines.Where(l => !l.IsSeparator).Select(l => (l.Name, decimal.Round(l.Cost, 2), Math.Round(l.Percent, 2))).ToList();
        Assert.Equal(
        [
            ("Cream 35%", 0.70m, 49.75), ("Egg yolks", 0.39m, 9.95), ("Sugar", 0.06m, 4.98),
            ("Gelatin Fish 200 Bloom", 0.02m, 0.50), ("Chocolate milk", 4.55m, 34.83),
        ], rows);
    }

    /// <summary>
    /// Total weight is derived the same way in both apps and the legacy app re-saved it on every open, so it must match.
    /// Legacy *costs* are a cache refreshed only when a recipe was opened; they are reported (not asserted) to show how
    /// stale they had become — a new cost may only ever be higher or equal if prices only rose since.
    /// </summary>
    [Fact]
    public void Total_weights_match_and_cached_costs_are_reported()
    {
        Assert.SkipUnless(HaveData, "imported database missing");
        var book = RecipeBook.Open(NewDb);
        var calc = book.Calculator();
        var legacy = ReadLegacy();

        int compared = 0, qtyMatch = 0, same = 0, higher = 0, lower = 0, warned = 0;
        var odd = new List<string>();
        foreach (var recipe in book.Recipes.Where(r => r.Lines.Any(l => l.Kind != LineKind.Separator)))
        {
            if (!legacy.TryGetValue(recipe.Id, out var old)) continue;
            var c = calc.Calculate(recipe);
            if (c.Warnings.Count > 0) warned++;
            compared++;
            if (Math.Abs(c.TotalAmount - old.Qty) < 0.6) qtyMatch++;
            else odd.Add($"weight {recipe.Name}: new {c.TotalAmount:0.##} vs legacy {old.Qty:0.##}");

            var diff = decimal.Round(c.ComponentsCost, 2) - decimal.Round(old.Cost, 2);
            if (Math.Abs(diff) <= 0.01m) same++;
            else if (diff > 0) higher++;
            else { lower++; odd.Add($"cost   {recipe.Name}: new {c.ComponentsCost:0.00} vs legacy {old.Cost:0.00}"); }
        }

        output.WriteLine($"{compared} recipes compared, {warned} with warnings");
        output.WriteLine($"total weight matches: {qtyMatch}/{compared}");
        output.WriteLine($"cached cost: {same} same, {higher} now higher (stale cache), {lower} now lower");
        foreach (var m in odd) output.WriteLine("  " + m);

        Assert.True(qtyMatch >= compared * 0.9, $"total weight matched only {qtyMatch}/{compared}");
    }

    /// <summary>The legacy invoice screen's footer (screenshot 06): Total Billed / Total Received / Balance.</summary>
    [Fact]
    public void Invoice_totals_match_the_legacy_screen()
    {
        Assert.SkipUnless(HaveData, "imported database missing");
        var book = RecipeBook.Open(NewDb);
        // Expected values come from the legacy data itself, so none of her figures live in this (public) repo.
        // As on the legacy list: invoices joined to a customer; billed = stored GrossAmount.
        using var conn = new SqliteConnection($"Data Source={LegacyDb};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(GrossAmount),0), IFNULL(SUM(AmountReceived),0) FROM tblInvoice i JOIN tblClient c ON c.CompanyID = i.CompanyID";
        using var r = cmd.ExecuteReader();
        r.Read();
        Assert.Equal(r.GetInt32(0), book.Invoices.Count);
        Assert.Equal(decimal.Round((decimal)r.GetDouble(1), 2), decimal.Round(book.Invoices.Sum(i => i.Total), 2));
        Assert.Equal(decimal.Round((decimal)r.GetDouble(2), 2), decimal.Round(book.Invoices.Sum(i => i.AmountReceived), 2));
        Assert.True(book.Companies.Single(c => c.Id == book.Invoices.Single(i => i.Id == 98).CompanyId).IsCustomer);
    }

    private static Dictionary<int, (double Qty, decimal Cost)> ReadLegacy()
    {
        using var conn = new SqliteConnection($"Data Source={LegacyDb};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT RecipeID, IFNULL(Qty,0), IFNULL(Cost,0) FROM tblRecipes";
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<int, (double, decimal)>();
        while (r.Read()) result[r.GetInt32(0)] = (r.GetDouble(1), (decimal)r.GetDouble(2));
        return result;
    }

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SvetaRecipes.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? ".";
    }
}
