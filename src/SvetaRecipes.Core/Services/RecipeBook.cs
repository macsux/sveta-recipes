using Microsoft.EntityFrameworkCore;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Data;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Services;

/// <summary>
/// The whole database held in memory (it is a few MB), with write-through persistence. The UI reads these lists
/// directly and never attaches them to a DbContext; every save opens a short-lived context, applies the change to a
/// tracked copy and commits.
/// </summary>
public sealed class RecipeBook
{
    public const decimal DefaultLabourRate = 20m;

    private readonly string _dbPath;

    public List<Recipe> Recipes { get; private set; } = [];
    public List<Ingredient> Ingredients { get; private set; } = [];
    public List<RecipeCategory> Categories { get; private set; } = [];
    public List<Tag> Tags { get; private set; } = [];
    public List<GroceryCategory> GroceryCategories { get; private set; } = [];
    public List<SupplierCategory> SupplierCategories { get; private set; } = [];
    public List<Company> Companies { get; private set; } = [];
    public List<MeasureUnit> Units { get; private set; } = [];
    public List<UnitConversion> Conversions { get; private set; } = [];
    public List<Substance> Substances { get; private set; } = [];
    public List<DensityConversionUnit> DensityUnits { get; private set; } = [];
    public List<ShoppingList> ShoppingLists { get; private set; } = [];
    public List<Invoice> Invoices { get; private set; } = [];
    private Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<int, Recipe> _recipeById = [];
    private Dictionary<int, Ingredient> _ingredientById = [];

    /// <summary>Bumped on every write, so views can tell their derived numbers (costs, counts) are stale.</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    private RecipeBook(string dbPath) => _dbPath = dbPath;

    public string DbPath => _dbPath;

    /// <summary>Opens (creating or upgrading as needed) the database and loads it.</summary>
    public static RecipeBook Open(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        using (var db = RecipesDbContext.Open(dbPath))
        {
            if (db.Database.GetPendingMigrations().Any() && File.Exists(dbPath) && db.Database.GetAppliedMigrations().Any())
                Backups.Snapshot(dbPath, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "pre-upgrade"), keep: 5);
            db.Database.Migrate();
            Seed(db);
        }
        var book = new RecipeBook(dbPath);
        book.Reload();
        return book;
    }

    public void Reload()
    {
        using var db = Context();
        Recipes = db.Recipes.Include(r => r.Lines).Include(r => r.Tags).Include(r => r.Layers).Include(r => r.Files)
            .AsSplitQuery().OrderBy(r => r.Name).ToList();
        Ingredients = db.Ingredients.Include(i => i.SupplierPrices).OrderBy(i => i.Name).ToList();
        Categories = db.RecipeCategories.OrderBy(c => c.Name).ToList();
        Tags = db.Tags.OrderBy(t => t.Name).ToList();
        GroceryCategories = db.GroceryCategories.OrderBy(c => c.Name).ToList();
        SupplierCategories = db.SupplierCategories.OrderBy(c => c.Name).ToList();
        Companies = db.Companies.Include(s => s.Categories).Include(s => s.Prices).OrderBy(s => s.Name).ToList();
        Units = db.Units.OrderBy(u => u.Code).ToList();
        Conversions = db.UnitConversions.OrderBy(c => c.From).ThenBy(c => c.To).ToList();
        Substances = db.Substances.OrderBy(s => s.Name).ToList();
        DensityUnits = db.DensityConversionUnits.OrderBy(u => u.Name).ToList();
        ShoppingLists = db.ShoppingLists.Include(l => l.Items).OrderByDescending(l => l.Date).ToList();
        Invoices = db.Invoices.Include(i => i.Items).OrderByDescending(i => i.Id).ToList();
        _settings = db.Settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
        // Recipe.Tags are tracked instances, but Tag.Recipes fix-up is not wanted in memory.
        foreach (var t in Tags) t.Recipes.Clear();
        foreach (var c in SupplierCategories) c.Companies.Clear();
        Reindex();
        Touch();
    }

    private void Reindex()
    {
        _recipeById = Recipes.ToDictionary(r => r.Id);
        _ingredientById = Ingredients.ToDictionary(i => i.Id);
    }

    private RecipesDbContext Context() => RecipesDbContext.Open(_dbPath);

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ lookups and calculation

    public Recipe? FindRecipe(int id) => _recipeById.GetValueOrDefault(id);
    public Ingredient? FindIngredient(int id) => _ingredientById.GetValueOrDefault(id);

    public UnitConverter UnitConverter => new(Conversions);

    /// <summary>A calculator over the saved data, optionally seeing <paramref name="overlay"/> in place of its saved copy.</summary>
    public CostCalculator Calculator(Recipe? overlay = null) =>
        new(id => overlay is not null && overlay.Id == id ? overlay : FindRecipe(id), FindIngredient, UnitConverter, LabourRate);

    /// <summary>Recipes that use <paramref name="recipeId"/> as a sub-recipe.</summary>
    public IEnumerable<Recipe> ParentsOf(int recipeId) =>
        Recipes.Where(r => r.Lines.Any(l => l.Kind == LineKind.SubRecipe && l.SubRecipeId == recipeId));

    public IEnumerable<Recipe> RecipesUsing(int ingredientId) =>
        Recipes.Where(r => r.Lines.Any(l => l.Kind == LineKind.Ingredient && l.IngredientId == ingredientId));

    // ------------------------------------------------------------------ settings

    public string? GetSetting(string key) => _settings.GetValueOrDefault(key);

    public void SetSetting(string key, string? value)
    {
        using var db = Context();
        var row = db.Settings.Find(key);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        db.SaveChanges();
        _settings[key] = value;
        Touch();
    }

    public decimal LabourRate
    {
        get => decimal.TryParse(GetSetting(SettingKeys.LabourRate), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : DefaultLabourRate;
        set => SetSetting(SettingKeys.LabourRate, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ recipes

    /// <summary>Saves a recipe (new or edited copy) and returns the stored instance.</summary>
    public Recipe SaveRecipe(Recipe edited)
    {
        var now = DateTime.Now;
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.Recipes.Include(r => r.Lines).Include(r => r.Tags).Include(r => r.Layers)
                .Include(r => r.Files).AsSplitQuery().Single(r => r.Id == edited.Id);
            if (row is null)
            {
                row = new Recipe();
                CopyScalars(edited, row);
                row.CreatedAt = now;
                db.Recipes.Add(row);
            }
            else
            {
                // Imported recipes have no creation date (the legacy app never filled it); don't invent one.
                db.Entry(row).CurrentValues.SetValues(edited);
            }
            row.UpdatedAt = now;

            db.RecipeLines.RemoveRange(row.Lines);
            row.Lines = edited.Lines.OrderBy(l => l.Position).Select((l, i) => new RecipeLine
            {
                Position = i,
                Kind = l.Kind,
                IngredientId = l.Kind == LineKind.Ingredient ? l.IngredientId : null,
                SubRecipeId = l.Kind == LineKind.SubRecipe ? l.SubRecipeId : null,
                Amount = l.Kind == LineKind.Separator ? 0 : l.Amount,
                Unit = l.Unit,
                Notes = l.Notes,
            }).ToList();

            var tagIds = edited.Tags.Select(t => t.Id).ToHashSet();
            row.Tags.RemoveAll(t => !tagIds.Contains(t.Id));
            foreach (var t in db.Tags.Where(t => tagIds.Contains(t.Id)).ToList())
                if (row.Tags.All(x => x.Id != t.Id)) row.Tags.Add(t);

            db.RecipeLayers.RemoveRange(row.Layers);
            row.Layers = edited.Layers.Select((l, i) => new RecipeLayer
                { Position = i, Name = l.Name, BackColor = l.BackColor, ForeColor = l.ForeColor }).ToList();

            var keepFiles = edited.Files.Where(f => f.Id != 0).Select(f => f.Id).ToHashSet();
            db.RecipeFiles.RemoveRange(row.Files.Where(f => !keepFiles.Contains(f.Id)));
            foreach (var f in edited.Files)
            {
                if (f.Id == 0) row.Files.Add(new RecipeFile { Kind = f.Kind, FileName = f.FileName, Caption = f.Caption, Data = f.Data });
                else if (row.Files.FirstOrDefault(x => x.Id == f.Id) is { } existing) existing.Caption = f.Caption;
            }

            db.SaveChanges();
            edited.Id = row.Id;
        }
        var stored = LoadRecipe(edited.Id);
        var index = Recipes.FindIndex(r => r.Id == stored.Id);
        if (index >= 0) Recipes[index] = stored; else Recipes.Add(stored);
        Recipes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Reindex();
        Touch();
        return stored;
    }

    private Recipe LoadRecipe(int id)
    {
        using var db = Context();
        var r = db.Recipes.Include(x => x.Lines).Include(x => x.Tags).Include(x => x.Layers).Include(x => x.Files)
            .AsSplitQuery().AsNoTracking().Single(x => x.Id == id);
        // Share the in-memory Tag instances so equality by reference keeps working in the UI.
        r.Tags = r.Tags.Select(t => Tags.FirstOrDefault(x => x.Id == t.Id) ?? t).ToList();
        foreach (var t in r.Tags) t.Recipes.Clear();
        return r;
    }

    /// <summary>Deletes a recipe. Refused (returns the names of the parents) when other recipes use it.</summary>
    public IReadOnlyList<string> DeleteRecipe(int id)
    {
        var parents = ParentsOf(id).Select(r => r.Name).ToList();
        if (parents.Count > 0) return parents;
        using (var db = Context())
        {
            db.Recipes.Where(r => r.Id == id).ExecuteDelete();
        }
        Recipes.RemoveAll(r => r.Id == id);
        Reindex();
        Touch();
        return [];
    }

    public void SetLabelPrinted(IEnumerable<int> recipeIds, bool printed)
    {
        var ids = recipeIds.ToHashSet();
        using (var db = Context())
            db.Recipes.Where(r => ids.Contains(r.Id)).ExecuteUpdate(s => s.SetProperty(r => r.LabelPrinted, printed));
        foreach (var r in Recipes.Where(r => ids.Contains(r.Id))) r.LabelPrinted = printed;
        Touch();
    }

    // ------------------------------------------------------------------ ingredients

    public Ingredient SaveIngredient(Ingredient edited)
    {
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.Ingredients.Find(edited.Id);
            if (row is null)
            {
                row = new Ingredient();
                CopyScalars(edited, row);
                db.Ingredients.Add(row);
            }
            else
            {
                db.Entry(row).CurrentValues.SetValues(edited);
            }
            db.SaveChanges();
            edited.Id = row.Id;
        }
        var stored = FindIngredient(edited.Id);
        if (stored is null)
        {
            Ingredients.Add(edited);
            Ingredients.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }
        else if (!ReferenceEquals(stored, edited))
        {
            CopyScalars(edited, stored);
        }
        Reindex();
        Touch();
        return FindIngredient(edited.Id)!;
    }

    /// <summary>Deletes an ingredient. Refused (returns the recipes using it) when it is in use.</summary>
    public IReadOnlyList<string> DeleteIngredient(int id)
    {
        var users = RecipesUsing(id).Select(r => r.Name).ToList();
        if (users.Count > 0) return users;
        using (var db = Context()) db.Ingredients.Where(i => i.Id == id).ExecuteDelete();
        Ingredients.RemoveAll(i => i.Id == id);
        foreach (var s in Companies) s.Prices.RemoveAll(p => p.IngredientId == id);
        Reindex();
        Touch();
        return [];
    }

    public void SetGroceryCategory(IEnumerable<int> ingredientIds, int? categoryId)
    {
        var ids = ingredientIds.ToHashSet();
        using (var db = Context())
            db.Ingredients.Where(i => ids.Contains(i.Id)).ExecuteUpdate(s => s.SetProperty(i => i.GroceryCategoryId, categoryId));
        foreach (var i in Ingredients.Where(i => ids.Contains(i.Id))) i.GroceryCategoryId = categoryId;
        Touch();
    }

    // ------------------------------------------------------------------ suppliers

    public Company SaveCompany(Company edited)
    {
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.Companies.Include(s => s.Categories).Single(s => s.Id == edited.Id);
            if (row is null)
            {
                row = new Company();
                CopyScalars(edited, row);
                db.Companies.Add(row);
            }
            else
            {
                db.Entry(row).CurrentValues.SetValues(edited);
            }
            var catIds = edited.Categories.Select(c => c.Id).ToHashSet();
            row.Categories.RemoveAll(c => !catIds.Contains(c.Id));
            foreach (var c in db.SupplierCategories.Where(c => catIds.Contains(c.Id)).ToList())
                if (row.Categories.All(x => x.Id != c.Id)) row.Categories.Add(c);
            db.SaveChanges();
            edited.Id = row.Id;
        }
        var stored = Companies.FirstOrDefault(s => s.Id == edited.Id);
        if (stored is null)
        {
            edited.Categories = edited.Categories.Select(c => SupplierCategories.First(x => x.Id == c.Id)).ToList();
            Companies.Add(edited);
            Companies.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            stored = edited;
        }
        else if (!ReferenceEquals(stored, edited))
        {
            CopyScalars(edited, stored);
            stored.Categories = edited.Categories.Select(c => SupplierCategories.First(x => x.Id == c.Id)).ToList();
        }
        Touch();
        return stored;
    }

    /// <summary>Deletes a company. Refused (returns false) when it has invoices, as in the legacy app.</summary>
    public bool DeleteCompany(int id)
    {
        if (Invoices.Any(i => i.CompanyId == id)) return false;
        using (var db = Context()) db.Companies.Where(s => s.Id == id).ExecuteDelete();
        Companies.RemoveAll(s => s.Id == id);
        foreach (var i in Ingredients) i.SupplierPrices.RemoveAll(p => p.SupplierId == id);
        Touch();
        return true;
    }

    public SupplierPrice SaveSupplierPrice(SupplierPrice edited)
    {
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.SupplierPrices.Find(edited.Id);
            if (row is null)
            {
                row = new SupplierPrice();
                CopyScalars(edited, row);
                db.SupplierPrices.Add(row);
            }
            else
            {
                db.Entry(row).CurrentValues.SetValues(edited);
            }
            db.SaveChanges();
            edited.Id = row.Id;
        }
        Place(Companies.FirstOrDefault(s => s.Id == edited.SupplierId)?.Prices, edited);
        Place(FindIngredient(edited.IngredientId)?.SupplierPrices, edited);
        Touch();
        return edited;

        static void Place(List<SupplierPrice>? list, SupplierPrice p)
        {
            if (list is null) return;
            var i = list.FindIndex(x => x.Id == p.Id);
            if (i >= 0) list[i] = p; else list.Add(p);
        }
    }

    public void DeleteSupplierPrice(int id)
    {
        using (var db = Context()) db.SupplierPrices.Where(p => p.Id == id).ExecuteDelete();
        foreach (var s in Companies) s.Prices.RemoveAll(p => p.Id == id);
        foreach (var i in Ingredients) i.SupplierPrices.RemoveAll(p => p.Id == id);
        Touch();
    }

    // ------------------------------------------------------------------ simple lookup lists

    public RecipeCategory SaveCategory(RecipeCategory c) => SaveSimple(c, c.Id, Categories, x => x.Name);
    public Tag SaveTag(Tag t) => SaveSimple(t, t.Id, Tags, x => x.Name);
    public GroceryCategory SaveGroceryCategory(GroceryCategory c) => SaveSimple(c, c.Id, GroceryCategories, x => x.Name);
    public SupplierCategory SaveSupplierCategory(SupplierCategory c) => SaveSimple(c, c.Id, SupplierCategories, x => x.Name);
    public Substance SaveSubstance(Substance s) => SaveSimple(s, s.Id, Substances, x => x.Name);

    private T SaveSimple<T>(T edited, int id, List<T> list, Func<T, string> sortKey) where T : class
    {
        using (var db = Context())
        {
            var row = id == 0 ? null : db.Set<T>().Find(id);
            if (row is null) db.Set<T>().Add(edited);
            else db.Entry(row).CurrentValues.SetValues(edited);
            db.SaveChanges();
        }
        if (!list.Contains(edited))
        {
            var existing = id == 0 ? null : list.FirstOrDefault(x => KeyOf(x) == id);
            if (existing is not null) CopyScalars(edited, existing);
            else list.Add(edited);
        }
        list.Sort((a, b) => string.Compare(sortKey(a), sortKey(b), StringComparison.CurrentCultureIgnoreCase));
        Touch();
        return edited;
    }

    private static int KeyOf(object o) => (int)o.GetType().GetProperty("Id")!.GetValue(o)!;

    public void DeleteCategory(int id)
    {
        using (var db = Context()) db.RecipeCategories.Where(c => c.Id == id).ExecuteDelete();
        Categories.RemoveAll(c => c.Id == id);
        foreach (var r in Recipes.Where(r => r.CategoryId == id)) r.CategoryId = null;
        Touch();
    }

    public void DeleteTag(int id)
    {
        using (var db = Context()) db.Tags.Where(t => t.Id == id).ExecuteDelete();
        Tags.RemoveAll(t => t.Id == id);
        foreach (var r in Recipes) r.Tags.RemoveAll(t => t.Id == id);
        Touch();
    }

    public void DeleteGroceryCategory(int id)
    {
        using (var db = Context()) db.GroceryCategories.Where(c => c.Id == id).ExecuteDelete();
        GroceryCategories.RemoveAll(c => c.Id == id);
        foreach (var i in Ingredients.Where(i => i.GroceryCategoryId == id)) i.GroceryCategoryId = null;
        Touch();
    }

    public void DeleteSupplierCategory(int id)
    {
        using (var db = Context()) db.SupplierCategories.Where(c => c.Id == id).ExecuteDelete();
        SupplierCategories.RemoveAll(c => c.Id == id);
        foreach (var s in Companies) s.Categories.RemoveAll(c => c.Id == id);
        Touch();
    }

    public void DeleteSubstance(int id)
    {
        using (var db = Context()) db.Substances.Where(s => s.Id == id).ExecuteDelete();
        Substances.RemoveAll(s => s.Id == id);
        Touch();
    }

    // ------------------------------------------------------------------ units

    public void SaveUnit(MeasureUnit unit)
    {
        using (var db = Context())
        {
            var row = db.Units.Find(unit.Code);
            if (row is null) db.Units.Add(new MeasureUnit { Code = unit.Code, Description = unit.Description, IsVolume = unit.IsVolume });
            else db.Entry(row).CurrentValues.SetValues(unit);
            db.SaveChanges();
        }
        if (!Units.Contains(unit))
        {
            Units.RemoveAll(u => string.Equals(u.Code, unit.Code, StringComparison.OrdinalIgnoreCase));
            Units.Add(unit);
        }
        Units.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
        Touch();
    }

    public void DeleteUnit(string code)
    {
        using (var db = Context()) db.Units.Where(u => u.Code == code).ExecuteDelete();
        Units.RemoveAll(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));
        Touch();
    }

    /// <summary>Adds or replaces the factor for a unit pair.</summary>
    public void SaveConversion(string from, string to, double factor, (string From, string To)? previousKey = null)
    {
        using (var db = Context())
        {
            if (previousKey is { } k && (!string.Equals(k.From, from, StringComparison.OrdinalIgnoreCase) || !string.Equals(k.To, to, StringComparison.OrdinalIgnoreCase)))
                db.UnitConversions.Where(c => c.From == k.From && c.To == k.To).ExecuteDelete();
            var row = db.UnitConversions.Find(from, to);
            if (row is null) db.UnitConversions.Add(new UnitConversion { From = from, To = to, Factor = factor });
            else row.Factor = factor;
            db.SaveChanges();
        }
        if (previousKey is { } p) Conversions.RemoveAll(c => SamePair(c, p.From, p.To));
        Conversions.RemoveAll(c => SamePair(c, from, to));
        Conversions.Add(new UnitConversion { From = from, To = to, Factor = factor });
        Conversions.Sort((a, b) => string.Compare(a.From + "\0" + a.To, b.From + "\0" + b.To, StringComparison.OrdinalIgnoreCase));
        Touch();
    }

    public void DeleteConversion(string from, string to)
    {
        using (var db = Context()) db.UnitConversions.Where(c => c.From == from && c.To == to).ExecuteDelete();
        Conversions.RemoveAll(c => SamePair(c, from, to));
        Touch();
    }

    private static bool SamePair(UnitConversion c, string from, string to) =>
        string.Equals(c.From, from, StringComparison.OrdinalIgnoreCase) && string.Equals(c.To, to, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ invoices

    public int NextInvoiceNumber => Invoices.Count == 0 ? 1 : Invoices.Max(i => i.Id) + 1;

    /// <summary>Saves an invoice (new when Id is 0, numbered after the last one) and returns the stored copy.</summary>
    public Invoice SaveInvoice(Invoice edited)
    {
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.Invoices.Include(i => i.Items).SingleOrDefault(i => i.Id == edited.Id);
            if (row is null)
            {
                row = new Invoice { Id = edited.Id == 0 ? NextInvoiceNumber : edited.Id };
                db.Invoices.Add(row);
            }
            row.Date = edited.Date;
            row.CompanyId = edited.CompanyId;
            row.Discount = edited.Discount;
            row.Tax1 = edited.Tax1;
            row.Tax2 = edited.Tax2;
            row.AmountReceived = edited.AmountReceived;
            row.IsPaid = edited.IsPaid;
            row.Notes = edited.Notes;
            db.InvoiceItems.RemoveRange(row.Items);
            row.Items = edited.Items.Where(i => !string.IsNullOrWhiteSpace(i.Name) || i.Price != 0)
                .Select((i, n) => new InvoiceItem { Position = n, Name = i.Name.Trim(), Price = i.Price, Quantity = i.Quantity }).ToList();
            db.SaveChanges();
            edited.Id = row.Id;
        }
        var stored = LoadInvoice(edited.Id);
        var index = Invoices.FindIndex(i => i.Id == stored.Id);
        if (index >= 0) Invoices[index] = stored; else Invoices.Add(stored);
        Invoices.Sort((a, b) => b.Id.CompareTo(a.Id));
        Touch();
        return stored;
    }

    private Invoice LoadInvoice(int id)
    {
        using var db = Context();
        return db.Invoices.Include(i => i.Items).AsNoTracking().Single(i => i.Id == id);
    }

    /// <summary>Records a payment state change from the invoice list without reopening the invoice.</summary>
    public void SetInvoicePayment(int id, decimal received, bool paid)
    {
        using (var db = Context())
            db.Invoices.Where(i => i.Id == id).ExecuteUpdate(s => s.SetProperty(i => i.AmountReceived, received).SetProperty(i => i.IsPaid, paid));
        if (Invoices.FirstOrDefault(i => i.Id == id) is { } inv) { inv.AmountReceived = received; inv.IsPaid = paid; }
        Touch();
    }

    public void DeleteInvoice(int id)
    {
        using (var db = Context()) db.Invoices.Where(i => i.Id == id).ExecuteDelete();
        Invoices.RemoveAll(i => i.Id == id);
        Touch();
    }

    // ------------------------------------------------------------------ shopping lists

    public ShoppingList SaveShoppingList(ShoppingList edited)
    {
        using (var db = Context())
        {
            var row = edited.Id == 0 ? null : db.ShoppingLists.Include(l => l.Items).Single(l => l.Id == edited.Id);
            if (row is null)
            {
                row = new ShoppingList();
                db.ShoppingLists.Add(row);
            }
            row.Date = edited.Date;
            row.Name = edited.Name;
            db.ShoppingItems.RemoveRange(row.Items);
            row.Items = edited.Items.Select(i => new ShoppingItem
            {
                IngredientId = i.IngredientId, Name = i.Name, Quantity = i.Quantity, Unit = i.Unit, QuantityText = i.QuantityText,
                Store = i.Store, Notes = i.Notes, Done = i.Done,
            }).ToList();
            db.SaveChanges();
            edited.Id = row.Id;
            for (var i = 0; i < edited.Items.Count; i++)
            {
                edited.Items[i].Id = row.Items[i].Id;
                edited.Items[i].ShoppingListId = row.Id;
            }
        }
        var index = ShoppingLists.FindIndex(l => l.Id == edited.Id);
        if (index >= 0) ShoppingLists[index] = edited; else ShoppingLists.Add(edited);
        ShoppingLists.Sort((a, b) => b.Date.CompareTo(a.Date));
        Touch();
        return edited;
    }

    public void DeleteShoppingList(int id)
    {
        using (var db = Context()) db.ShoppingLists.Where(l => l.Id == id).ExecuteDelete();
        ShoppingLists.RemoveAll(l => l.Id == id);
        Touch();
    }

    /// <summary>Stores already used on shopping lists, for the store picker.</summary>
    public IEnumerable<string> KnownStores() =>
        ShoppingLists.SelectMany(l => l.Items).Select(i => i.Store).Concat(Companies.Select(s => s.Name))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);

    // ------------------------------------------------------------------ helpers

    private static void CopyScalars<T>(T from, T to)
    {
        foreach (var p in typeof(T).GetProperties())
        {
            if (!p.CanWrite) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime))
                p.SetValue(to, p.GetValue(from));
        }
    }

    private static void Seed(RecipesDbContext db)
    {
        // A fresh database gets the measuring units every recipe needs; real data comes from the legacy import.
        if (db.Units.Any()) return;
        db.Units.AddRange(
            new MeasureUnit { Code = "g", Description = "Gram" },
            new MeasureUnit { Code = "kg", Description = "Kilogram" },
            new MeasureUnit { Code = "mg", Description = "Milligram" },
            new MeasureUnit { Code = "ml", Description = "Millilitre", IsVolume = true },
            new MeasureUnit { Code = "L", Description = "Litre", IsVolume = true },
            new MeasureUnit { Code = "pc", Description = "Piece" },
            new MeasureUnit { Code = "tsp", Description = "Tea spoon", IsVolume = true },
            new MeasureUnit { Code = "TBsp", Description = "Table spoon", IsVolume = true },
            new MeasureUnit { Code = "cup", Description = "Cup", IsVolume = true },
            new MeasureUnit { Code = "oz", Description = "Ounce" },
            new MeasureUnit { Code = "lb", Description = "Pound" });
        db.UnitConversions.AddRange(
            new UnitConversion { From = "kg", To = "g", Factor = 1000 },
            new UnitConversion { From = "g", To = "mg", Factor = 1000 },
            new UnitConversion { From = "L", To = "ml", Factor = 1000 },
            new UnitConversion { From = "ml", To = "g", Factor = 1 },
            new UnitConversion { From = "lb", To = "g", Factor = 453.59237 },
            new UnitConversion { From = "oz", To = "g", Factor = 28.349523125 });
        db.SaveChanges();
    }
}

public static class SettingKeys
{
    public const string LabourRate = "LabourRate";
    public const string BackupFolder = "BackupFolder";
    public const string Theme = "Theme";
    public const string LabelColumns = "LabelColumns";
    public const string LabelRows = "LabelRows";
    public const string PrintFontSize = "PrintFontSize";
    // Your business, printed on invoices (legacy tblSystemSettings)
    public const string BusinessName = "BusinessName";
    public const string BusinessAddress1 = "BusinessAddress1";
    public const string BusinessAddress2 = "BusinessAddress2";
    public const string BusinessCity = "BusinessCity";
    public const string BusinessProvince = "BusinessProvince";
    public const string BusinessPostal = "BusinessPostal";
    public const string BusinessPhone = "BusinessPhone";
    public const string BusinessEmail = "BusinessEmail";
}
