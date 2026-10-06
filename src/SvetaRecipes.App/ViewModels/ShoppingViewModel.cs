using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

namespace SvetaRecipes.App.ViewModels;

public sealed record ListSummary(int Id, string Title, string Detail);

public partial class ShoppingItemRow : ObservableObject
{
    private readonly ShoppingViewModel _owner;

    public ShoppingItemRow(ShoppingViewModel owner, ShoppingItem item)
    {
        _owner = owner;
        IngredientId = item.IngredientId;
        _done = item.Done;
        _name = item.Name;
        _quantity = Pdf.QuantityText(item);
        _store = item.Store;
        _notes = item.Notes;
    }

    public int? IngredientId { get; }
    [ObservableProperty] private bool _done;
    [ObservableProperty] private string _name;
    /// <summary>"2 kg", "30", or free text like "2 bags".</summary>
    [ObservableProperty] private string _quantity;
    [ObservableProperty] private string? _store;
    [ObservableProperty] private string? _notes;

    public string StoreGroup => string.IsNullOrWhiteSpace(Store) ? "Any store" : Store.Trim();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Store)) OnPropertyChanged(nameof(StoreGroup));
        if (e.PropertyName == nameof(Done)) _owner.SaveSoon();
    }

    private static readonly Regex NumberAndUnit = new(@"^\s*([0-9]+(?:[.,][0-9]+)?)\s*([^\d\s].*)?$");

    public ShoppingItem ToItem()
    {
        var item = new ShoppingItem { IngredientId = IngredientId, Name = Name.Trim(), Store = Blank(Store), Notes = Blank(Notes), Done = Done };
        var m = NumberAndUnit.Match(Quantity ?? "");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
        {
            item.Quantity = q;
            item.Unit = Blank(m.Groups[2].Value);
        }
        else
        {
            item.QuantityText = Blank(Quantity);
        }
        return item;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Legacy CapitalizeFirstLetter on every item name typed in.
    partial void OnNameChanged(string value)
    {
        var fixedName = TextCase.CapitalizeFirst(value);
        if (fixedName != value) Name = fixedName;
    }
}

public static class TextCase
{
    /// <summary>"cream 35%" → "Cream 35%" (only the first letter; the rest is left as typed).</summary>
    public static string CapitalizeFirst(string? s) =>
        string.IsNullOrEmpty(s) ? s ?? "" : char.ToUpper(s.TrimStart()[0], CultureInfo.CurrentCulture) + s.TrimStart()[1..];
}

public partial class ShoppingViewModel : ViewModelBase, ISection
{
    private readonly MainViewModel _main;
    private int _seenVersion = -1;
    private bool _loading;

    public ShoppingViewModel(MainViewModel main) => _main = main;

    private RecipeBook Book => _main.Book;

    public ObservableCollection<ListSummary> Lists { get; } = [];
    public ObservableCollection<ShoppingItemRow> Items { get; } = [];

    [ObservableProperty] private ListSummary? _selectedList;
    [ObservableProperty] private int _currentId;
    [ObservableProperty] private string? _listName;
    [ObservableProperty] private DateTime? _listDate;
    [ObservableProperty] private bool _groupByStore = true;
    [ObservableProperty] private bool _hasList;

    [ObservableProperty] private PickItem? _newPick;
    [ObservableProperty] private string? _newText;
    [ObservableProperty] private string? _newQuantity;
    [ObservableProperty] private string? _newStore;
    [ObservableProperty] private string? _batchStore;

    public IReadOnlyList<PickItem> IngredientCandidates =>
        Book.Ingredients.Select(i => new PickItem(i.Id, i.Name, LineKind.Ingredient, i.Unit, "")).ToList();

    public IReadOnlyList<string> Stores => Book.KnownStores().ToList();

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        _seenVersion = Book.Version;
        RefreshLists(CurrentId == 0 ? Book.ShoppingLists.FirstOrDefault()?.Id : CurrentId);
        OnPropertyChanged(nameof(IngredientCandidates));
        OnPropertyChanged(nameof(Stores));
    }

    private void RefreshLists(int? select)
    {
        Lists.Clear();
        foreach (var l in Book.ShoppingLists)
        {
            var open = l.Items.Count(i => !i.Done);
            Lists.Add(new ListSummary(l.Id, string.IsNullOrWhiteSpace(l.Name) ? l.Date.ToString("D", CultureInfo.CurrentCulture) : l.Name!,
                (string.IsNullOrWhiteSpace(l.Name) ? "" : l.Date.ToString("d", CultureInfo.CurrentCulture) + "  ·  ") + $"{open} to buy"));
        }
        SelectedList = Lists.FirstOrDefault(l => l.Id == select) ?? Lists.FirstOrDefault();
        if (SelectedList is null) Load(null);
    }

    public void Select(int listId)
    {
        _seenVersion = -1;
        CurrentId = listId;
        Activate();
        SelectedList = Lists.FirstOrDefault(l => l.Id == listId);
    }

    partial void OnSelectedListChanged(ListSummary? value)
    {
        if (value is not null && value.Id != CurrentId) Load(Book.ShoppingLists.FirstOrDefault(l => l.Id == value.Id));
        else if (value is not null && Items.Count == 0) Load(Book.ShoppingLists.FirstOrDefault(l => l.Id == value.Id));
    }

    private void Load(ShoppingList? list)
    {
        _loading = true;
        CurrentId = list?.Id ?? 0;
        HasList = list is not null;
        ListName = list?.Name;
        ListDate = list?.Date;
        Items.Clear();
        foreach (var i in Order(list?.Items ?? [])) Items.Add(new ShoppingItemRow(this, i));
        _loading = false;
    }

    private IEnumerable<ShoppingItem> Order(IEnumerable<ShoppingItem> items) => GroupByStore
        ? items.OrderBy(i => i.Done).ThenBy(i => string.IsNullOrWhiteSpace(i.Store)).ThenBy(i => i.Store, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
        : items.OrderBy(i => i.Done).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);

    partial void OnGroupByStoreChanged(bool value) => Save(reorder: true);
    partial void OnListNameChanged(string? value) => SaveSoon();
    partial void OnListDateChanged(DateTime? value) => SaveSoon();

    public void SaveSoon()
    {
        if (!_loading) Save(reorder: false);
    }

    private ShoppingList Current() => new()
    {
        Id = CurrentId, Date = ListDate?.Date ?? DateTime.Today, Name = string.IsNullOrWhiteSpace(ListName) ? null : ListName.Trim(),
        Items = Items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.ToItem()).ToList(),
    };

    /// <summary>Lists save on every change; there is no Save button to forget.</summary>
    public void Save(bool reorder)
    {
        if (_loading || CurrentId == 0) return;
        var saved = Book.SaveShoppingList(Current());
        _seenVersion = Book.Version;
        if (reorder) Load(saved);
        var summary = Lists.FirstOrDefault(l => l.Id == saved.Id);
        if (summary is not null)
        {
            var open = saved.Items.Count(i => !i.Done);
            var updated = summary with
            {
                Title = string.IsNullOrWhiteSpace(saved.Name) ? saved.Date.ToString("D", CultureInfo.CurrentCulture) : saved.Name!,
                Detail = (string.IsNullOrWhiteSpace(saved.Name) ? "" : saved.Date.ToString("d", CultureInfo.CurrentCulture) + "  ·  ") + $"{open} to buy",
            };
            if (updated != summary)
            {
                _loading = true;
                Lists[Lists.IndexOf(summary)] = updated;
                SelectedList = updated;
                _loading = false;
            }
        }
    }

    // ------------------------------------------------------------------ list commands

    [RelayCommand]
    private void NewList()
    {
        var list = Book.SaveShoppingList(new ShoppingList { Date = DateTime.Today });
        _seenVersion = Book.Version;
        RefreshLists(list.Id);
        Load(list);
    }

    [RelayCommand]
    private async Task DeleteList()
    {
        if (CurrentId == 0) return;
        if (!await _main.Dialogs.Confirm("Delete list", "Delete this shopping list?", "Delete")) return;
        Book.DeleteShoppingList(CurrentId);
        _seenVersion = Book.Version;
        CurrentId = 0;
        RefreshLists(null);
    }

    [RelayCommand]
    private async Task AddRecipes()
    {
        if (CurrentId == 0) NewList();
        var picks = await _main.Dialogs.Show(new PickRecipesViewModel(Book), "Add recipes to the list", 640, 640) as List<(Recipe, double)>;
        if (picks is null || picks.Count == 0) return;
        var list = Current();
        list.Items = ShoppingBuilder.Merge(list.Items.Concat(ShoppingBuilder.FromRecipes(Book, picks)));
        Load(Book.SaveShoppingList(list));
        _seenVersion = Book.Version;
        Save(reorder: true);
    }

    [RelayCommand]
    private void AddItem()
    {
        var name = NewPick?.Name ?? TextCase.CapitalizeFirst(NewText?.Trim());
        if (string.IsNullOrWhiteSpace(name)) return;
        if (CurrentId == 0) NewList();
        var ingredientId = NewPick?.Id;
        var store = NewStore ?? (ingredientId is { } id ? ShoppingBuilder.LastStores(Book).GetValueOrDefault(id) : null);
        Items.Insert(0, new ShoppingItemRow(this, new ShoppingItem { IngredientId = ingredientId, Name = name, QuantityText = NewQuantity, Store = store }));
        NewPick = null;
        NewText = null;
        NewQuantity = null;
        Save(reorder: false);
    }

    /// <summary>Tick several ingredients at once (legacy "Add Items"), optionally all for one store.</summary>
    [RelayCommand]
    private async Task AddSeveral()
    {
        if (CurrentId == 0) NewList();
        var onList = Items.Where(i => i.IngredientId is not null).Select(i => i.IngredientId!.Value).ToHashSet();
        var vm = new PickIngredientsViewModel(Book, onList, Stores);
        if (await _main.Dialogs.Show(vm, "Add items", 560, 640) is not IngredientPickResult { Picked.Count: > 0 } result) return;
        var last = ShoppingBuilder.LastStores(Book);
        foreach (var ing in result.Picked)
            Items.Insert(0, new ShoppingItemRow(this, new ShoppingItem { IngredientId = ing.Id, Name = ing.Name, Store = result.Store ?? last.GetValueOrDefault(ing.Id) }));
        Save(reorder: true);
    }

    /// <summary>Saves the list as a CSV file (opens in Excel) — legacy "Export".</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (CurrentId == 0) return;
        var list = Current();
        var path = await _main.Dialogs.PickSaveFile("Export shopping list", $"Shopping list {list.Date:yyyy-MM-dd}.csv", "CSV (Excel)", ".csv");
        if (path is null) return;
        static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Bought,Item,Quantity,Store,Notes\r\n");
        foreach (var i in list.Items)
            sb.Append($"{(i.Done ? "yes" : "")},{Q(i.Name)},{Q(Pdf.QuantityText(i))},{Q(i.Store)},{Q(i.Notes)}\r\n");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Launcher.Open(path);
    }

    [RelayCommand]
    private void RemoveItems(IList? selection)
    {
        if (selection is null) return;
        foreach (var row in selection.OfType<ShoppingItemRow>().ToList()) Items.Remove(row);
        Save(reorder: false);
    }

    [RelayCommand]
    private void ClearDone()
    {
        foreach (var row in Items.Where(i => i.Done).ToList()) Items.Remove(row);
        Save(reorder: false);
    }

    [RelayCommand]
    private void MergeDuplicates()
    {
        var list = Current();
        list.Items = ShoppingBuilder.Merge(list.Items);
        Load(Book.SaveShoppingList(list));
        _seenVersion = Book.Version;
    }

    [RelayCommand]
    private void SetStore(IList? selection)
    {
        if (selection is null) return;
        foreach (var row in selection.OfType<ShoppingItemRow>()) row.Store = string.IsNullOrWhiteSpace(BatchStore) ? null : BatchStore.Trim();
        Save(reorder: true);
        OnPropertyChanged(nameof(Stores));
    }

    [RelayCommand]
    private void Print()
    {
        if (CurrentId == 0) return;
        var path = Launcher.ExportPath("Shopping list");
        Pdf.ShoppingList(path, Current(), GroupByStore);
        Launcher.Open(path);
    }

    [RelayCommand]
    private async Task Copy()
    {
        if (CurrentId == 0) return;
        var sb = new StringBuilder();
        var groups = GroupByStore
            ? Current().Items.Where(i => !i.Done).GroupBy(i => string.IsNullOrWhiteSpace(i.Store) ? "Any store" : i.Store!)
            : Current().Items.Where(i => !i.Done).GroupBy(_ => "");
        foreach (var g in groups)
        {
            if (g.Key.Length > 0) sb.AppendLine(g.Key + ":");
            foreach (var i in g) sb.AppendLine($"- {i.Name} {Pdf.QuantityText(i)}".TrimEnd());
            sb.AppendLine();
        }
        await _main.Dialogs.CopyText(sb.ToString().Trim());
    }
}

public partial class RecipePick(Recipe recipe) : ObservableObject
{
    public Recipe Recipe { get; } = recipe;
    public string Name => Recipe.Name;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private double? _times = 1;
}

public sealed record IngredientPickResult(List<Ingredient> Picked, string? Store);

public partial class IngredientPick(Ingredient ingredient) : ObservableObject
{
    public Ingredient Ingredient { get; } = ingredient;
    public string Name => Ingredient.Name;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Legacy frmAddToShopList: tick ingredients not yet on the list; one store for all of them.</summary>
public partial class PickIngredientsViewModel : DialogViewModel
{
    private readonly List<IngredientPick> _all;

    public PickIngredientsViewModel(RecipeBook book, HashSet<int> alreadyOnList, IReadOnlyList<string> stores)
    {
        _all = book.Ingredients.Where(i => !alreadyOnList.Contains(i.Id) && i.Name != "(unnamed)").Select(i => new IngredientPick(i)).ToList();
        Stores = stores;
        Filter();
    }

    public IReadOnlyList<string> Stores { get; }
    public ObservableCollection<IngredientPick> Visible { get; } = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _store;

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        Visible.Clear();
        foreach (var p in _all.Where(p => p.IsChecked || p.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))) Visible.Add(p);
    }

    [RelayCommand]
    private void Add() => Close(new IngredientPickResult(_all.Where(p => p.IsChecked).Select(p => p.Ingredient).ToList(), string.IsNullOrWhiteSpace(Store) ? null : Store.Trim()));

    [RelayCommand]
    private void Cancel() => Close();
}

/// <summary>Search and tick recipes, with a batch count each.</summary>
public partial class PickRecipesViewModel : DialogViewModel
{
    private readonly List<RecipePick> _all;

    public PickRecipesViewModel(RecipeBook book)
    {
        _all = book.Recipes.Select(r => new RecipePick(r)).ToList();
        Filter();
    }

    public ObservableCollection<RecipePick> Visible { get; } = [];
    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        Visible.Clear();
        foreach (var p in _all.Where(p => p.IsChecked || p.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))) Visible.Add(p);
    }

    [RelayCommand]
    private void Add() => Close(_all.Where(p => p.IsChecked && p.Times is > 0).Select(p => (p.Recipe, p.Times!.Value)).ToList());

    [RelayCommand]
    private void Cancel() => Close();
}
