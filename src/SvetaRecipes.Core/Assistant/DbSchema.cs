using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SvetaRecipes.Core.Data;

namespace SvetaRecipes.Core.Assistant;

/// <summary>
/// The database schema as the assistant sees it, generated from the EF model so it always matches the migrations.
/// Column types are the CLR meaning (enum values, money, bool), not just SQLite's storage class. <see cref="Notes"/>
/// holds the only hand-written part: things the names don't say.
/// </summary>
public static class DbSchema
{
    /// <summary>"Table" or "Table.Column" → note. A test checks every key still exists.</summary>
    public static readonly IReadOnlyDictionary<string, string> Notes = new Dictionary<string, string>
    {
        ["Recipes"] = "costs and total weight are not stored (use get_costing)",
        ["Recipes.Unit"] = "unit of the yield; every line is converted into it",
        ["Recipes.Notes"] = "short note under the title (\"270F 30min\")",
        ["Recipes.Length"] = "diameter when Shape = Round",
        ["RecipeLines"] = "ordered by Position; Kind says which of IngredientId / SubRecipeId is set; a Separator is a blank divider row",
        ["Ingredients"] = "price per Unit = PackPrice / PackQty",
        ["Ingredients.PerPiece"] = "PackPrice is the price of one piece and PackQty its weight in Unit; recipe lines use Unit 'pc'",
        ["SupplierPrices"] = "reference prices per supplier; costing uses only Ingredients.PackPrice",
        ["UnitConversions"] = "1 From = Factor × To",
        ["Invoices.Id"] = "the invoice number; a new one is MAX(Id) + 1",
        ["Invoices"] = "total = Σ items Price × Quantity − Discount + Tax1 + Tax2 (not stored)",
        ["Settings"] = "LabourRate ($/h; labour cost = Recipes.LabourHours × LabourRate), Business* (invoice header), the rest are UI preferences",
        ["Substances.Density"] = "g/ml",
        ["DensityConversionUnits.Coefficient"] = "> 0: grams per unit; < 0: −ml per unit",
        ["RecipeFiles.Data"] = "file contents; never select it",
    };

    public const string Legend =
        "Types: money = decimal stored as TEXT (CAST(x AS REAL) to sort or compare); datetime = TEXT 'yyyy-MM-dd HH:mm:ss[.fffffff]'; " +
        "bool = 0/1; enum = INTEGER with the values listed; ? = nullable; → = foreign key. Text compares case-insensitively.";

    public static IModel Model { get; } = RecipesDbContext.Open(":memory:").Model;

    public static string Describe()
    {
        var text = new StringBuilder(Legend).AppendLine().AppendLine();
        foreach (var entity in Tables())
        {
            var table = entity.GetTableName()!;
            var columns = entity.GetProperties().Select(p => Column(p));
            text.Append(table).Append('(').AppendJoin(", ", columns).AppendLine(")");
            foreach (var (key, note) in Notes.Where(n => n.Key == table || n.Key.StartsWith(table + ".")))
                text.Append("  - ").Append(key == table ? "" : key[(table.Length + 1)..] + ": ").AppendLine(note);
        }
        return text.ToString();
    }

    public static IEnumerable<IEntityType> Tables() =>
        Model.GetEntityTypes().Where(e => e.GetTableName() is not null).OrderBy(e => e.GetTableName());

    private static string Column(IProperty p)
    {
        var parts = new List<string> { p.GetColumnName() };
        if (p.IsPrimaryKey()) parts.Add("pk");
        var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        var name = type switch
        {
            _ when type.IsEnum => "enum(" + string.Join(", ", Enum.GetValues(type).Cast<object>().Select(v => $"{Convert.ToInt32(v)} {v}")) + ")",
            _ when type == typeof(string) => "text",
            _ when type == typeof(int) || type == typeof(long) => "int",
            _ when type == typeof(double) => "real",
            _ when type == typeof(decimal) => "money",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(DateTime) => "datetime",
            _ when type == typeof(byte[]) => "blob",
            _ => type.Name,
        };
        parts.Add(p.IsNullable ? name + "?" : name);
        if (p.GetContainingForeignKeys().FirstOrDefault() is { } fk)
            parts.Add("→" + fk.PrincipalEntityType.GetTableName());
        return string.Join(" ", parts);
    }
}
