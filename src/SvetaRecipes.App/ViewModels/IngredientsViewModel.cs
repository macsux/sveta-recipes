using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

/// <summary>An editable ingredient row. Saved when the grid commits the row.</summary>
public partial class IngredientRow : ObservableObject
{
    private readonly IngredientsViewModel _owner;

    public IngredientRow(IngredientsViewModel owner, Ingredient i)
    {
        _owner = owner;
        Id = i.Id;
        _name = i.Name;
        _unit = i.Unit;
        _packQty = Math.Round(i.PackQty, 3);
        _packPrice = Math.Round(i.PackPrice, 2);
        _perPiece = i.PerPiece;
        _category = owner.CategoryOptions.FirstOrDefault(c => c.Value == i.GroceryCategoryId) ?? owner.CategoryOptions[0];
        _comments = i.Comments;
        Refresh();
    }

    public int Id { get; set; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private double _packQty;
    [ObservableProperty] private decimal _packPrice;
    [ObservableProperty] private bool _perPiece;
    [ObservableProperty] private Option<int?> _category;
    [ObservableProperty] private string? _comments;
    [ObservableProperty] private string _pricePerKg = "";
    [ObservableProperty] private int _usedIn;
    [ObservableProperty] private int _suppliers;

    public bool IsDirty { get; set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Unit) or nameof(PackQty) or nameof(PackPrice) or nameof(PerPiece) or nameof(Category) or nameof(Comments))
        {
            IsDirty = true;
            if (e.PropertyName is nameof(Unit) or nameof(PackQty) or nameof(PackPrice) or nameof(PerPiece)) Refresh();
            // Check boxes and combo boxes commit straight away; text cells commit when the row is left.
            if (e.PropertyName is nameof(PerPiece) or nameof(Category)) _owner.Commit(this);
        }
    }

    public void Refresh()
    {
        var units = _owner.Book.UnitConverter;
        PricePerKg = PerPiece
            ? $"{PackPrice:C2}/pc"
            : DensityConverter.PricePerKg(units, Unit, PackQty, PackPrice) is { } p ? p.ToString("C2") : "";
        UsedIn = _owner.Book.RecipesUsing(Id).Count();
        Suppliers = _owner.Book.FindIngredient(Id)?.SupplierPrices.Count ?? 0;
    }

    public Ingredient ToIngredient() => new()
    {
        Id = Id, Name = TextCase.CapitalizeFirst(Name.Trim()), Unit = Unit, PackQty = PackQty, PackPrice = PackPrice, PerPiece = PerPiece,
        GroceryCategoryId = Category.Value, Comments = string.IsNullOrWhiteSpace(Comments) ? null : Comments,
    };
}

public sealed record RecipeUse(int RecipeId, string Recipe, string Amount);

public partial class SupplierPriceRow : ObservableObject
{
    private readonly IngredientsViewModel _owner;

    public SupplierPriceRow(IngredientsViewModel owner, SupplierPrice p)
    {
        _owner = owner;
        Price = p;
        _supplier = owner.SupplierOptions.FirstOrDefault(s => s.Value == p.SupplierId) ?? owner.SupplierOptions.FirstOrDefault();
        _brand = p.Brand;
        _unit = p.Unit;
        _packQty = Math.Round(p.PackQty, 3);
        _packPrice = Math.Round(p.PackPrice, 2);
        _comments = p.Comments;
    }

    public SupplierPrice Price { get; }
    [ObservableProperty] private Option<int>? _supplier;
    [ObservableProperty] private string? _brand;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private double _packQty;
    [ObservableProperty] private decimal _packPrice;
    [ObservableProperty] private string? _comments;

    public string PricePerKg => DensityConverter.PricePerKg(_owner.Book.UnitConverter, Unit, PackQty, PackPrice)?.ToString("C2") ?? "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Unit) or nameof(PackQty) or nameof(PackPrice)) OnPropertyChanged(nameof(PricePerKg));
        if (e.PropertyName is nameof(Supplier)) _owner.CommitPrice(this);
    }

    public SupplierPrice ToPrice() => new()
    {
        Id = Price.Id, IngredientId = Price.IngredientId, SupplierId = Supplier?.Value ?? 0, Brand = Brand, Unit = Unit,
        PackQty = PackQty, PackPrice = PackPrice, Comments = Comments,
    };
}

public partial class IngredientsViewModel : ViewModelBase, ISection
{
    private readonly MainViewModel _main;
    private int _seenVersion = -1;
    private List<IngredientRow> _all = [];
    private bool _committing;
    private bool _reloading;

    public IngredientsViewModel(MainViewModel main)
    {
        _main = main;
        _filter = FilterOptions[0];
    }

    public RecipeBook Book => _main.Book;

    public ObservableCollection<IngredientRow> Rows { get; } = [];
    public ObservableCollection<RecipeUse> UsedInRecipes { get; } = [];
    public ObservableCollection<SupplierPriceRow> Prices { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Option<int?> _filter;
    [ObservableProperty] private IngredientRow? _selected;
    [ObservableProperty] private Option<int?>? _batchCategory;
    [ObservableProperty] private string _summary = "";

    /// <summary>Grocery categories for the grid: "—" first (no category).</summary>
    public List<Option<int?>> CategoryOptions { get; private set; } = [];
    public List<Option<int>> SupplierOptions { get; private set; } = [];
    public List<string> Units { get; private set; } = [];

    /// <summary>The filter list: all, uncategorised, unused, then each category (sentinels -1, -2, -3).</summary>
    public List<Option<int?>> FilterOptions { get; private set; } = [new(-1, "All categories")];

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        Reload();
    }

    private void Reload()
    {
        _seenVersion = Book.Version;
        CategoryOptions = [new(null, "—"), .. Book.GroceryCategories.Select(c => new Option<int?>(c.Id, c.Name))];
        SupplierOptions = Book.Companies.Where(c => c.IsSupplier).Select(s => new Option<int>(s.Id, s.Name)).ToList();
        Units = Book.Units.Select(u => u.Code).ToList();
        var filterId = Filter?.Value;
        FilterOptions =
        [
            new(-1, "All categories"), new(-2, "No category"), new(-3, "Not used in any recipe"),
            .. Book.GroceryCategories.Select(c => new Option<int?>(c.Id, c.Name)),
        ];
        OnPropertyChanged(nameof(CategoryOptions));
        OnPropertyChanged(nameof(SupplierOptions));
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(FilterOptions));
        _reloading = true;
        Filter = FilterOptions.FirstOrDefault(f => f.Value == filterId) ?? FilterOptions[0];
        _reloading = false;
        var selectedId = Selected?.Id;
        _all = Book.Ingredients.Select(i => new IngredientRow(this, i)).ToList();
        ApplyFilter();
        if (selectedId is { } id) Select(id);
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(Option<int?> value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_reloading) return;
        var term = Search.Trim();
        var rows = _all.Where(r =>
            (term.Length == 0 || r.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                              || (r.Comments?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)) &&
            Filter?.Value switch
            {
                -1 or null => true,
                -2 => r.Category.Value is null,
                -3 => r.UsedIn == 0,
                var id => r.Category.Value == id,
            }).ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Summary = rows.Count == _all.Count ? $"{rows.Count} ingredients" : $"{rows.Count} of {_all.Count} ingredients";
    }

    partial void OnSelectedChanged(IngredientRow? value)
    {
        UsedInRecipes.Clear();
        Prices.Clear();
        if (value is null) return;
        foreach (var r in Book.RecipesUsing(value.Id))
            foreach (var l in r.Lines.Where(l => l.Kind == LineKind.Ingredient && l.IngredientId == value.Id))
                UsedInRecipes.Add(new RecipeUse(r.Id, r.Name, $"{l.Amount:0.###} {l.Unit}"));
        foreach (var p in Book.FindIngredient(value.Id)?.SupplierPrices ?? []) Prices.Add(new SupplierPriceRow(this, p));
    }

    public void Select(int id)
    {
        Activate();
        if (_all.FirstOrDefault(r => r.Id == id) is not { } row) return;
        if (!Rows.Contains(row))
        {
            Search = "";
            Filter = FilterOptions[0];
        }
        Selected = row;
    }

    /// <summary>Saves a row the grid has finished editing.</summary>
    public void Commit(IngredientRow row)
    {
        if (!row.IsDirty || _committing) return;
        if (string.IsNullOrWhiteSpace(row.Name)) return;
        _committing = true;
        try
        {
            var saved = Book.SaveIngredient(row.ToIngredient());
            row.Id = saved.Id;
            if (row.Name != saved.Name) row.Name = saved.Name;
            row.IsDirty = false;
            row.IsDirty = false;
            row.Refresh();
            _seenVersion = Book.Version;
        }
        finally { _committing = false; }
    }

    [RelayCommand]
    private void Add()
    {
        var saved = Book.SaveIngredient(new Ingredient { Name = "New ingredient", Unit = "g", PackQty = 1000 });
        _seenVersion = Book.Version;
        var row = new IngredientRow(this, saved);
        _all.Add(row);
        Search = "";
        Filter = FilterOptions[0];
        Rows.Insert(0, row);
        Selected = row;
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } row) return;
        if (row.UsedIn > 0)
        {
            await _main.Dialogs.Alert("Ingredient is in use",
                $"“{row.Name}” is used in {row.UsedIn} recipe(s) — see “Used in” below. Remove it from those recipes first.");
            return;
        }
        if (!await _main.Dialogs.Confirm("Delete ingredient", $"Delete “{row.Name}”?", "Delete")) return;
        Book.DeleteIngredient(row.Id);
        _seenVersion = Book.Version;
        _all.Remove(row);
        Rows.Remove(row);
    }

    /// <summary>Sets the grocery category on every selected row.</summary>
    [RelayCommand]
    private void SetCategory(IList? selection)
    {
        if (BatchCategory is null || selection is null || selection.Count == 0) return;
        var rows = selection.OfType<IngredientRow>().ToList();
        Book.SetGroceryCategory(rows.Select(r => r.Id), BatchCategory.Value);
        _seenVersion = Book.Version;
        var option = CategoryOptions.First(c => c.Value == BatchCategory.Value);
        _committing = true;
        foreach (var r in rows) { r.Category = option; r.IsDirty = false; }
        _committing = false;
    }

    [RelayCommand]
    private void OpenRecipe(RecipeUse? use)
    {
        if (use is not null) _main.GoToRecipe(use.RecipeId);
    }

    // ------------------------------------------------------------------ supplier prices

    [RelayCommand]
    private async Task AddPrice()
    {
        if (Selected is null) return;
        if (SupplierOptions.Count == 0)
        {
            await _main.Dialogs.Alert("No suppliers", "Add a supplier on the Suppliers tab first.");
            return;
        }
        var p = Book.SaveSupplierPrice(new SupplierPrice
        {
            IngredientId = Selected.Id, SupplierId = SupplierOptions[0].Value, Unit = Selected.Unit, PackQty = Selected.PackQty, PackPrice = Selected.PackPrice,
        });
        _seenVersion = Book.Version;
        Prices.Add(new SupplierPriceRow(this, p));
        Selected.Refresh();
    }

    [RelayCommand]
    private void RemovePrice(SupplierPriceRow? row)
    {
        if (row is null) return;
        Book.DeleteSupplierPrice(row.Price.Id);
        _seenVersion = Book.Version;
        Prices.Remove(row);
        Selected?.Refresh();
    }

    public void CommitPrice(SupplierPriceRow row)
    {
        if (row.Supplier is null) return;
        Book.SaveSupplierPrice(row.ToPrice());
        _seenVersion = Book.Version;
    }

    /// <summary>Copies a supplier's price onto the ingredient (the price recipes are costed with).</summary>
    [RelayCommand]
    private void UsePrice(SupplierPriceRow? row)
    {
        if (row is null || Selected is null) return;
        Selected.Unit = row.Unit;
        Selected.PackQty = row.PackQty;
        Selected.PackPrice = row.PackPrice;
        Commit(Selected);
    }
}
