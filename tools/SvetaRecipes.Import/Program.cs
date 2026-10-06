using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SvetaRecipes.Core.Data;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

// Usage: SvetaRecipes.Import <SRD_data.mdb> <SRD_2018_u.mdb> <output.db> [--force]
// Reads both legacy Access files through mdb-export and writes a fresh database for the new app.

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SvetaRecipes.Import <SRD_data.mdb> <SRD_2018_u.mdb> <output.db> [--force]");
    return 2;
}
var (dataMdb, frontMdb, output) = (args[0], args[1], args[2]);
if (File.Exists(output))
{
    if (!args.Contains("--force"))
    {
        Console.Error.WriteLine($"{output} exists; pass --force to replace it.");
        return 2;
    }
    File.Delete(output);
}

var log = new List<string>();
var mdb = new Mdb(dataMdb);
var front = new Mdb(frontMdb);

using (var db = RecipesDbContext.Open(output))
{
    db.Database.EnsureDeleted();
    db.Database.Migrate();

    // Lookups
    db.RecipeCategories.AddRange(mdb.Rows("tblRecipeCategory").Select(r => new RecipeCategory { Id = r.Int("RecipeCategoryID"), Name = r.Str("RecipeCategoryName") }));
    var tags = mdb.Rows("tblRecipeTag").Select(r => new Tag { Id = r.Int("RecipeTagID"), Name = r.Str("RecipeTagName") }).ToDictionary(t => t.Id);
    db.Tags.AddRange(tags.Values);
    var groceryIds = new HashSet<int>();
    foreach (var r in mdb.Rows("tblGroceryCategories"))
    {
        db.GroceryCategories.Add(new GroceryCategory { Id = r.Int("GroceryCategoryID"), Name = r.Str("GroceryCategory") });
        groceryIds.Add(r.Int("GroceryCategoryID"));
    }
    var supplierCats = mdb.Rows("tblSupplierCategory").Select(r => new SupplierCategory { Id = r.Int("SupplierCategoryID"), Name = r.Str("SupplierCategory") }).ToDictionary(c => c.Id);
    db.SupplierCategories.AddRange(supplierCats.Values);

    // Units
    db.Units.AddRange(mdb.Rows("tblUnitOfMeasure").Select(r => new MeasureUnit { Code = r.Str("UofM"), Description = r.Str("Description"), IsVolume = r.Bool("IsVolume") }));
    db.UnitConversions.AddRange(mdb.Rows("tblUofMConversion").Select(r => new UnitConversion { From = r.Str("UofMFrom"), To = r.Str("UofMTo"), Factor = r.Dbl("Coefficient") }));
    db.Substances.AddRange(front.Rows("tlkConvSubstances").Select(r => new Substance { Id = r.Int("SubstanceID"), Name = r.Str("SubstanceName"), Density = r.Dbl("SubstanceDensity") }));
    db.DensityConversionUnits.AddRange(front.Rows("tlkConvVolumeWeightUnits").Select(r => new DensityConversionUnit { Name = r.Str("VolWeightUnit"), Coefficient = r.Dbl("Coefficient") }));

    // Ingredients
    var ingredientIds = new HashSet<int>();
    foreach (var r in mdb.Rows("tblIngredients"))
    {
        var name = r.Str("IngredientName").Trim();
        if (name.Length == 0)
        {
            log.Add($"ingredient #{r.Int("IngredientID")} has no name; imported as '(unnamed)'");
            name = "(unnamed)";
        }
        var cat = r.IntN("GroceryCategoryID");
        db.Ingredients.Add(new Ingredient
        {
            Id = r.Int("IngredientID"), Name = name, Unit = r.StrN("UofM") ?? "g", PackQty = r.DblN("Qty") ?? 0,
            PackPrice = r.DecN("Price") ?? 0, PerPiece = r.Bool("PerPiece"),
            GroceryCategoryId = cat is { } c && groceryIds.Contains(c) ? c : null, Comments = r.StrN("Comments"),
        });
        ingredientIds.Add(r.Int("IngredientID"));
    }

    // Recipes
    var recipes = new Dictionary<int, Recipe>();
    var categoryIds = db.ChangeTracker.Entries<RecipeCategory>().Select(e => e.Entity.Id).ToHashSet();
    foreach (var r in mdb.Rows("tblRecipes"))
    {
        var cat = r.IntN("RecipeCategoryID");
        var recipe = new Recipe
        {
            Id = r.Int("RecipeID"), Name = r.Str("RecipeName").Trim(), Instructions = r.StrN("PreparationInstructions"),
            CategoryId = cat is { } c && categoryIds.Contains(c) ? c : null, Unit = r.StrN("UofM") ?? "g",
            LabourHours = r.DblN("Labour") ?? 0, Servings = r.IntN("NumberOfServings"), WeightPerServing = r.DblN("QtyPerServing"),
            CostMethod = r.IntN("PriceCalcMethod") == 2 ? ServingCostMethod.ByServingWeight : ServingCostMethod.ByServingCount,
            SellPrice = r.DecN("SellPrice"), PricePerServing = r.DecN("PricePerServing"), Notes = r.StrN("Notes"),
            Description = r.StrN("RecipeDescription"), LabelTitle = r.StrN("RecipeLabelTitle"), LabelText = r.StrN("RecipeLabelText"),
            ChargeCode = r.StrN("ChargeCode"), LabelPrinted = r.Bool("IsLabelPrinted"),
            Shape = r.IntN("CakeShape") == 2 ? PanShape.Rectangle : PanShape.Round,
            Width = r.DblN("Width"), Length = r.DblN("Length"), Height = r.DblN("Height"),
            CreatedAt = r.DateN("EnterDate"), UpdatedAt = r.DateN("LastUpdateDate"),
        };
        if (recipe.Notes is "Enter notes here") recipe.Notes = null;
        recipes[recipe.Id] = recipe;
    }

    foreach (var g in mdb.Rows("tblRecipeComponents").GroupBy(r => r.Int("RecipeID")))
    {
        if (!recipes.TryGetValue(g.Key, out var recipe))
        {
            log.Add($"{g.Count()} lines belong to missing recipe #{g.Key}; skipped");
            continue;
        }
        var pos = 0;
        foreach (var r in g.OrderBy(r => r.IntN("SeqOrder") ?? int.MaxValue).ThenBy(r => r.Int("ItemID")))
        {
            var compId = r.IntN("ComponentID") ?? 0;
            var type = r.IntN("ComponentType") ?? 0;
            var line = new RecipeLine { Position = pos, Amount = r.DblN("Amount") ?? 0, Unit = r.StrN("UofM") ?? recipe.Unit, Notes = r.StrN("Notes") };
            if (compId == 0) line.Kind = LineKind.Separator;
            else if (type == 2 && recipes.ContainsKey(compId)) { line.Kind = LineKind.SubRecipe; line.SubRecipeId = compId; }
            else if (type == 1 && ingredientIds.Contains(compId)) { line.Kind = LineKind.Ingredient; line.IngredientId = compId; }
            else
            {
                log.Add($"'{recipe.Name}': line {pos + 1} points at a deleted {(type == 2 ? "recipe" : "ingredient")} #{compId} ({line.Amount} {line.Unit}); skipped");
                continue;
            }
            recipe.Lines.Add(line);
            pos++;
        }
    }

    foreach (var r in mdb.Rows("tblRecipe_Tags"))
        if (recipes.TryGetValue(r.Int("RecipeID"), out var recipe) && tags.TryGetValue(r.Int("RecipeTagID"), out var tag))
            recipe.Tags.Add(tag);

    foreach (var r in mdb.Rows("tblRecipeLayers"))
        if (recipes.TryGetValue(r.Int("RecipeID"), out var recipe))
            recipe.Layers.Add(new RecipeLayer
            {
                Position = r.IntN("OrderSeq") ?? 0, Name = r.Str("LayerName"),
                BackColor = AccessColor(r.IntN("LayerBGColor") ?? 0xFFFFFF), ForeColor = AccessColor(r.IntN("LayerFontColor") ?? 0),
            });

    foreach (var r in mdb.Rows("tblRecipeImages", hexBlobs: true))
    {
        if (!recipes.TryGetValue(r.IntN("RecipeID") ?? 0, out var recipe)) continue;
        var data = Convert.FromHexString(r.Str("ImageBLOB"));
        var ext = data is [0x89, 0x50, ..] ? ".png" : data is [0xFF, 0xD8, ..] ? ".jpg" : ".bin";
        recipe.Files.Add(new RecipeFile { Kind = RecipeFileKind.Image, FileName = $"image-{r.Int("ImageID")}{ext}", Caption = r.StrN("ImageCaption"), Data = data });
    }
    var unlinked = front.Rows("tblRecipeAttachments").Count(r => r.IntN("RecipeID") is null);
    if (unlinked > 0) log.Add($"{unlinked} attachments in the front-end file are not linked to any recipe; skipped");

    db.Recipes.AddRange(recipes.Values);

    // Companies: customers and suppliers (legacy tblClient), with supplier categories and prices.
    var companies = new Dictionary<int, Company>();
    foreach (var r in mdb.Rows("tblClient"))
    {
        var id = r.Int("CompanyID");
        companies[id] = new Company
        {
            Id = id, Name = r.Str("CompanyName").Trim(), IsCustomer = r.Bool("IsCustomer"), IsSupplier = r.Bool("IsSupplier"),
            DateEntered = r.DateN("DateEntered"), Contact = r.StrN("ContactName"), Street = r.StrN("Street"), City = r.StrN("City"),
            Province = r.StrN("Province"), PostalCode = r.StrN("PostalCode"), Country = r.StrN("Country"), Phone = r.StrN("TelephoneNo"),
            Fax = r.StrN("FaxNo"), TollFree = r.StrN("TollFree"), Website = r.StrN("WebSite"), Email = r.StrN("Email"), Notes = r.StrN("Notes"),
        };
    }
    foreach (var r in mdb.Rows("tblSupplierToCategories"))
        if (companies.TryGetValue(r.Int("SupplierID"), out var s) && supplierCats.TryGetValue(r.Int("SupplierCategoryID"), out var c))
            s.Categories.Add(c);
    foreach (var r in mdb.Rows("tblSupplierIngredients"))
    {
        if (!companies.TryGetValue(r.Int("CompanyID"), out var s) || !ingredientIds.Contains(r.Int("IngredientID"))) continue;
        s.IsSupplier = true;
        s.Prices.Add(new SupplierPrice
        {
            IngredientId = r.Int("IngredientID"), Brand = r.StrN("Brand"), Unit = r.StrN("UofM") ?? "g", PackQty = r.DblN("Qty") ?? 0,
            PackPrice = r.DecN("Price") ?? 0, Comments = r.StrN("Comments"),
        });
    }
    db.Companies.AddRange(companies.Values);

    // Invoices
    var invoices = new Dictionary<int, Invoice>();
    foreach (var r in mdb.Rows("tblInvoice"))
    {
        var companyId = r.IntN("CompanyID") ?? 0;
        if (!companies.ContainsKey(companyId))
        {
            log.Add($"invoice #{r.Int("InvoiceNo")} has no customer; skipped");
            continue;
        }
        companies[companyId].IsCustomer = true;
        invoices[r.Int("InvoiceNo")] = new Invoice
        {
            Id = r.Int("InvoiceNo"), Date = r.DateN("InvoiceDate") ?? DateTime.Today, CompanyId = companyId,
            Discount = r.DecN("Discount") ?? 0, Tax1 = r.DecN("Tax1Amount") ?? 0, Tax2 = r.DecN("Tax2Amount") ?? 0,
            AmountReceived = r.DecN("AmountReceived") ?? 0, IsPaid = r.Bool("IsPaid"),
        };
    }
    foreach (var g in mdb.Rows("tblInvoiceItem").GroupBy(r => r.Int("InvoiceNo")))
    {
        if (!invoices.TryGetValue(g.Key, out var inv)) continue;
        var pos = 0;
        foreach (var r in g.OrderBy(r => r.Int("ItemID")))
            inv.Items.Add(new InvoiceItem { Position = pos++, Name = r.Str("ItemName").Trim(), Price = r.DecN("ItemPrice") ?? 0, Quantity = r.DblN("ItemQty") ?? 0 });
    }
    var mismatched = invoices.Values.Count(i => mdb.Rows("tblInvoice").Any(r => r.Int("InvoiceNo") == i.Id && Math.Abs((r.DecN("GrossAmount") ?? 0) - i.Total) > 0.01m));
    if (mismatched > 0) log.Add($"{mismatched} invoice(s) whose stored total differs from the sum of their items");
    db.Invoices.AddRange(invoices.Values);

    // Her business details, printed on invoices
    foreach (var r in mdb.Rows("tblSystemSettings").Take(1))
        foreach (var (key, col) in new[]
                 {
                     (SettingKeys.BusinessName, "CompanyName"), (SettingKeys.BusinessAddress1, "Address1"), (SettingKeys.BusinessAddress2, "Address2"),
                     (SettingKeys.BusinessCity, "City"), (SettingKeys.BusinessProvince, "Province"), (SettingKeys.BusinessPostal, "Postal"),
                     (SettingKeys.BusinessPhone, "Phone"), (SettingKeys.BusinessEmail, "Email"),
                 })
            db.Settings.Add(new AppSetting { Key = key, Value = r.StrN(col) });

    // Recipe file attachments (front-end tblRecipeAttachments); only those linked to a recipe can be kept.
    foreach (var r in front.Rows("tblRecipeAttachments", hexBlobs: true))
    {
        if (r.IntN("RecipeID") is not { } rid || !recipes.TryGetValue(rid, out var recipe)) continue;
        recipe.Files.Add(new RecipeFile { Kind = RecipeFileKind.Attachment, FileName = r.StrN("AttachmentName") ?? $"attachment-{r.Int("AttachmentID")}", Caption = r.StrN("Keywords"), Data = Convert.FromHexString(r.Str("Attachment")) });
    }

    // Shopping lists
    var lists = mdb.Rows("tblShopList").ToDictionary(r => r.Int("ShopListID"), r => new ShoppingList { Id = r.Int("ShopListID"), Date = r.DateN("ShopListDate") ?? DateTime.Today });
    foreach (var r in mdb.Rows("tblShopListItems"))
    {
        if (!lists.TryGetValue(r.Int("ShopListID"), out var list)) continue;
        var ingId = r.IntN("IngredientID");
        var qty = r.DblN("Qty");
        list.Items.Add(new ShoppingItem
        {
            IngredientId = ingId is { } i && ingredientIds.Contains(i) ? i : null, Name = r.StrN("ShopItemName") ?? "",
            Quantity = qty, Unit = qty is null ? null : r.StrN("UofM"), QuantityText = qty is null ? r.StrN("ShopQty") : null,
            Store = r.StrN("ShopStore"), Notes = r.StrN("Notes"),
        });
    }
    db.ShoppingLists.AddRange(lists.Values);

    var labour = front.Rows("tblVersion").Select(r => r.DecN("LabourRate")).FirstOrDefault() ?? RecipeBook.DefaultLabourRate;
    db.Settings.Add(new AppSetting { Key = SettingKeys.LabourRate, Value = labour.ToString(CultureInfo.InvariantCulture) });

    db.SaveChanges();

    Console.WriteLine($"Imported {recipes.Count} recipes ({recipes.Values.Sum(r => r.Lines.Count)} lines), {ingredientIds.Count} ingredients, " +
                      $"{companies.Count} customers/suppliers, {invoices.Count} invoices, {lists.Count} shopping lists into {output}");
}
foreach (var l in log) Console.WriteLine("  note: " + l);
return 0;

static string AccessColor(int bgr) => $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";

/// <summary>Reads a table out of an Access file with mdb-export.</summary>
sealed class Mdb(string path)
{
    public IEnumerable<Row> Rows(string table, bool hexBlobs = false)
    {
        var psi = new ProcessStartInfo("mdb-export") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-D");
        psi.ArgumentList.Add("%Y-%m-%d %H:%M:%S");
        if (hexBlobs) { psi.ArgumentList.Add("-b"); psi.ArgumentList.Add("hex"); }
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add(table);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("mdb-export not found (brew install mdbtools)");
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"mdb-export {table}: {p.StandardError.ReadToEnd()}");
        var records = Csv.Parse(text);
        if (records.Count == 0) yield break;
        var header = records[0].Select((h, i) => (h: h ?? "", i)).ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records.Skip(1)) yield return new Row(header, rec);
    }
}

sealed class Row(Dictionary<string, int> header, List<string?> values)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public string? StrN(string col) => values[header[col]] is { Length: > 0 } s ? s : null;
    public string Str(string col) => StrN(col) ?? "";
    public int? IntN(string col) => StrN(col) is { } s ? (int)double.Parse(s, Inv) : null;
    public int Int(string col) => IntN(col) ?? 0;
    public double? DblN(string col) => StrN(col) is { } s ? double.Parse(s, Inv) : null;
    public double Dbl(string col) => DblN(col) ?? 0;
    public decimal? DecN(string col) => StrN(col) is { } s ? decimal.Parse(s, NumberStyles.Float, Inv) : null;
    public bool Bool(string col) => StrN(col) is "1" or "-1" or "true" or "True";
    public DateTime? DateN(string col) => StrN(col) is { } s ? DateTime.Parse(s, Inv) : null;
}

static class Csv
{
    /// <summary>RFC 4180: quoted fields may contain commas, doubled quotes and newlines. Unquoted empty = null.</summary>
    public static List<List<string?>> Parse(string text)
    {
        var rows = new List<List<string?>>();
        var row = new List<string?>();
        var field = new System.Text.StringBuilder();
        bool quoted = false, wasQuoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = wasQuoted = true; break;
                case ',': End(); break;
                case '\r': break;
                case '\n': End(); rows.Add(row); row = []; break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { End(); rows.Add(row); }
        return rows;

        void End()
        {
            row.Add(field.Length == 0 && !wasQuoted ? null : field.ToString());
            field.Clear();
            wasQuoted = false;
        }
    }
}
