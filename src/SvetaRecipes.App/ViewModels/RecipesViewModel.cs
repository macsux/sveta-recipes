using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

namespace SvetaRecipes.App.ViewModels;

public enum RecipeGrouping { Category, Tag, Alphabetical }

/// <summary>What the search box searches (legacy Find Recipe: name or preparation instructions; plus ingredients).</summary>
public enum SearchScope { Name, NameAndIngredients, Method }

/// <summary>Order of the flat list (legacy Find Recipe "sort by").</summary>
public enum ListOrder { Name, Newest, RecentlyUpdated, MostIngredients }

public sealed record RecipeFilter(string Name, Func<RecipeBook, Recipe, bool> Match)
{
    public override string ToString() => Name;
}

public abstract partial class TreeNode : ObservableObject
{
    public abstract string Name { get; }
    [ObservableProperty] private bool _isExpanded;
}

public partial class RecipeGroupNode : TreeNode
{
    private readonly string _name;
    private bool _settingChildren;

    public RecipeGroupNode(string name, int? categoryId, IEnumerable<RecipeNode> children)
    {
        _name = name;
        CategoryId = categoryId;
        Children = new ObservableCollection<RecipeNode>(children);
        foreach (var c in Children) c.Group = this;
    }

    public override string Name => $"{_name} ({Children.Count})";
    public int? CategoryId { get; }
    public ObservableCollection<RecipeNode> Children { get; }
    [ObservableProperty] private bool? _isChecked = false;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (value is null || _settingChildren) return;
        _settingChildren = true;
        foreach (var c in Children) c.IsChecked = value.Value;
        _settingChildren = false;
    }

    public void ChildChanged()
    {
        if (_settingChildren) return;
        _settingChildren = true;
        var n = Children.Count(c => c.IsChecked);
        IsChecked = n == 0 ? false : n == Children.Count ? true : null;
        _settingChildren = false;
    }
}

public partial class RecipeNode(Recipe recipe, bool isChecked, Action<RecipeNode> checkedChanged) : TreeNode
{
    public Recipe Recipe { get; } = recipe;
    public override string Name => Recipe.Name;
    public RecipeGroupNode? Group { get; set; }
    public bool LabelPrinted => Recipe.LabelPrinted;
    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        checkedChanged(this);
        Group?.ChildChanged();
    }
}

public partial class RecipesViewModel : ViewModelBase, ISection
{
    public MainViewModel Main { get; }
    public RecipeBook Book => Main.Book;
    public IDialogs Dialogs => Main.Dialogs;

    private readonly HashSet<int> _checked = [];
    private int _seenVersion = -1;
    private bool _revertingSelection;

    public RecipesViewModel(MainViewModel main)
    {
        Main = main;
        _filter = Filters[0];
    }

    public ObservableCollection<TreeNode> Nodes { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Option<SearchScope> _searchIn = SearchScopes[0];
    [ObservableProperty] private Option<ListOrder> _order = ListOrders[0];

    public static IReadOnlyList<Option<SearchScope>> SearchScopes { get; } =
    [
        new(SearchScope.Name, "Search names"), new(SearchScope.NameAndIngredients, "Names and ingredients"), new(SearchScope.Method, "Method text"),
    ];

    public static IReadOnlyList<Option<ListOrder>> ListOrders { get; } =
    [
        new(ListOrder.Name, "A–Z"), new(ListOrder.Newest, "Newest first"), new(ListOrder.RecentlyUpdated, "Recently updated"),
        new(ListOrder.MostIngredients, "Most ingredients"),
    ];
    [ObservableProperty] private RecipeGrouping _grouping = RecipeGrouping.Category;
    [ObservableProperty] private RecipeFilter _filter;
    [ObservableProperty] private TreeNode? _selectedNode;
    [ObservableProperty] private RecipeEditorViewModel? _editor;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasChecks))] private int _checkedCount;
    public bool HasChecks => CheckedCount > 0;

    public bool ByCategory { get => Grouping == RecipeGrouping.Category; set { if (value) Grouping = RecipeGrouping.Category; } }
    public bool ByTag { get => Grouping == RecipeGrouping.Tag; set { if (value) Grouping = RecipeGrouping.Tag; } }
    public bool Alphabetical { get => Grouping == RecipeGrouping.Alphabetical; set { if (value) Grouping = RecipeGrouping.Alphabetical; } }

    public static IReadOnlyList<RecipeFilter> Filters { get; } =
    [
        new("All recipes", (_, _) => true),
        new("Used inside other recipes", (b, r) => b.ParentsOf(r.Id).Any()),
        new("Containing other recipes", (_, r) => r.Lines.Any(l => l.Kind == LineKind.SubRecipe)),
        new("With a method", (_, r) => !string.IsNullOrWhiteSpace(r.Instructions)),
        new("With label text", (_, r) => !string.IsNullOrWhiteSpace(r.LabelText)),
        new("Without label text", (_, r) => string.IsNullOrWhiteSpace(r.LabelText)),
        new("Label printed", (_, r) => r.LabelPrinted),
        new("With photos", (_, r) => r.Files.Any(f => f.Kind == RecipeFileKind.Image)),
        new("With layers", (_, r) => r.Layers.Count > 0),
        new("Without ingredients", (_, r) => r.Lines.All(l => l.Kind == LineKind.Separator)),
        new("Without a price", (_, r) => r.SellPrice is null && r.PricePerServing is null),
        new("Without a charge code", (_, r) => string.IsNullOrWhiteSpace(r.ChargeCode)),
        new("With costing problems", (b, r) => b.Calculator().Calculate(r).Warnings.Count > 0),
    ];

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        _seenVersion = Book.Version;
        Rebuild();
        // Prices or sub-recipes may have changed elsewhere; the open recipe's numbers must follow.
        Editor?.Recalculate();
    }

    partial void OnSearchChanged(string value) => Rebuild();
    partial void OnSearchInChanged(Option<SearchScope> value) => Rebuild();
    partial void OnOrderChanged(Option<ListOrder> value) => Rebuild();
    partial void OnFilterChanged(RecipeFilter value) => Rebuild();

    partial void OnGroupingChanged(RecipeGrouping value)
    {
        OnPropertyChanged(nameof(ByCategory));
        OnPropertyChanged(nameof(ByTag));
        OnPropertyChanged(nameof(Alphabetical));
        Rebuild();
    }

    // ------------------------------------------------------------------ tree

    private void Rebuild()
    {
        var expanded = Nodes.OfType<RecipeGroupNode>().Where(g => g.IsExpanded).Select(g => g.Name.Split(" (")[0]).ToHashSet();
        var term = Search.Trim();
        var recipes = Book.Recipes.Where(r => Filter.Match(Book, r) && Matches(r, term)).ToList();
        var searching = term.Length > 0 || Filter != Filters[0];

        RecipeNode Node(Recipe r) => new(r, _checked.Contains(r.Id), OnNodeChecked);

        Nodes.Clear();
        switch (Grouping)
        {
            case RecipeGrouping.Category:
                foreach (var c in Book.Categories)
                    AddGroup(c.Name, c.Id, recipes.Where(r => r.CategoryId == c.Id));
                AddGroup("No category", null, recipes.Where(r => r.CategoryId is null || Book.Categories.All(c => c.Id != r.CategoryId)));
                break;
            case RecipeGrouping.Tag:
                foreach (var t in Book.Tags)
                    AddGroup(t.Name, null, recipes.Where(r => r.Tags.Any(x => x.Id == t.Id)));
                AddGroup("No tag", null, recipes.Where(r => r.Tags.Count == 0));
                break;
            default:
                IEnumerable<Recipe> ordered = Order.Value switch
                {
                    ListOrder.Newest => recipes.OrderByDescending(r => r.Id),
                    ListOrder.RecentlyUpdated => recipes.OrderByDescending(r => r.UpdatedAt ?? DateTime.MinValue),
                    ListOrder.MostIngredients => recipes.OrderByDescending(r => r.Lines.Count(l => l.Kind != LineKind.Separator)),
                    _ => recipes,
                };
                foreach (var r in ordered) Nodes.Add(Node(r));
                break;
        }
        Summary = recipes.Count == Book.Recipes.Count ? $"{recipes.Count} recipes" : $"{recipes.Count} of {Book.Recipes.Count} recipes";

        if (Editor is { IsNew: false } e && FindNode(e.Id) is { } node)
        {
            _revertingSelection = true;
            if (node.Group is { } g) g.IsExpanded = true;
            SelectedNode = node;
            _revertingSelection = false;
        }

        void AddGroup(string name, int? categoryId, IEnumerable<Recipe> items)
        {
            var list = items.ToList();
            if (list.Count == 0 && (searching || categoryId is null)) return;
            Nodes.Add(new RecipeGroupNode(name, categoryId, list.Select(Node)) { IsExpanded = searching || expanded.Contains(name) });
        }
    }

    private bool Matches(Recipe r, string term)
    {
        if (term.Length == 0) return true;
        var c = StringComparison.CurrentCultureIgnoreCase;
        return SearchIn.Value switch
        {
            SearchScope.Method => r.Instructions?.Contains(term, c) == true,
            SearchScope.NameAndIngredients => r.Name.Contains(term, c) || r.Lines.Any(l =>
                (l.IngredientId is { } i && Book.FindIngredient(i)?.Name.Contains(term, c) == true) ||
                (l.SubRecipeId is { } sid && Book.FindRecipe(sid)?.Name.Contains(term, c) == true)),
            _ => r.Name.Contains(term, c),
        };
    }

    private RecipeNode? FindNode(int recipeId) =>
        Nodes.SelectMany(n => n is RecipeGroupNode g ? g.Children : n is RecipeNode r ? [r] : Enumerable.Empty<RecipeNode>())
            .FirstOrDefault(n => n.Recipe.Id == recipeId);

    private void OnNodeChecked(RecipeNode node)
    {
        if (node.IsChecked) _checked.Add(node.Recipe.Id); else _checked.Remove(node.Recipe.Id);
        // The same recipe can appear under several tags.
        foreach (var other in Nodes.OfType<RecipeGroupNode>().SelectMany(g => g.Children).Where(n => n != node && n.Recipe.Id == node.Recipe.Id))
            if (other.IsChecked != node.IsChecked) other.IsChecked = node.IsChecked;
        CheckedCount = _checked.Count;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        _checked.Clear();
        CheckedCount = 0;
        foreach (var g in Nodes.OfType<RecipeGroupNode>()) g.IsChecked = false;
        foreach (var n in Nodes.OfType<RecipeNode>()) n.IsChecked = false;
    }

    [RelayCommand]
    private void ExpandAll(bool? expand)
    {
        foreach (var g in Nodes.OfType<RecipeGroupNode>()) g.IsExpanded = expand ?? true;
    }

    // ------------------------------------------------------------------ selection and unsaved edits

    /// <summary>
    /// Swaps the open recipe. Clears first: otherwise the outgoing view briefly inherits the incoming editor as its
    /// DataContext and its combo boxes write the previous recipe's values into the new one.
    /// </summary>
    private void SetEditor(RecipeEditorViewModel? editor)
    {
        Editor = null;
        Editor = editor;
    }

    partial void OnSelectedNodeChanged(TreeNode? oldValue, TreeNode? newValue)
    {
        if (_revertingSelection || newValue is not RecipeNode node) return;
        if (Editor is { } e && e.Id == node.Recipe.Id) return;
        _ = Open(node.Recipe, oldValue);
    }

    private async Task Open(Recipe recipe, TreeNode? previous)
    {
        if (!await ConfirmLeave())
        {
            _revertingSelection = true;
            SelectedNode = previous;
            _revertingSelection = false;
            return;
        }
        SetEditor(new RecipeEditorViewModel(this, recipe));
    }

    /// <summary>Saves or discards pending edits after asking. False when she chose to stay.</summary>
    public async Task<bool> ConfirmLeave()
    {
        if (Editor is not { IsDirty: true } e) return true;
        switch (await Dialogs.AskSave(string.IsNullOrWhiteSpace(e.Name) ? "the new recipe" : e.Name))
        {
            case SaveChoice.Save:
                if (!await e.Save()) return false;
                AfterSave();
                return true;
            case SaveChoice.Discard:
                if (e.IsNew) Editor = null; else e.Revert();
                return true;
            default:
                return false;
        }
    }

    public async Task SelectRecipe(int id)
    {
        if (Editor?.Id == id) return;
        if (Book.FindRecipe(id) is not { } recipe) return;
        if (!await ConfirmLeave()) return;
        if (FindNode(id) is null)
        {
            // Not visible under the current search or filter; clear them so the tree shows where it lives.
            Search = "";
            Filter = Filters[0];
        }
        SetEditor(new RecipeEditorViewModel(this, recipe));
        if (FindNode(id) is { } node)
        {
            _revertingSelection = true;
            if (node.Group is { } g) g.IsExpanded = true;
            SelectedNode = node;
            _revertingSelection = false;
        }
    }

    /// <summary>After a backup is restored: drop the open recipe (it may not exist any more) and rebuild.</summary>
    public void Reset()
    {
        _checked.Clear();
        CheckedCount = 0;
        SetEditor(null);
        _seenVersion = -1;
        Activate();
    }

    // ------------------------------------------------------------------ recipe commands

    [RelayCommand]
    private async Task New()
    {
        if (!await ConfirmLeave()) return;
        var category = SelectedNode switch
        {
            RecipeGroupNode g => g.CategoryId,
            RecipeNode n when Grouping == RecipeGrouping.Category => n.Recipe.CategoryId,
            _ => null,
        };
        SetEditor(new RecipeEditorViewModel(this, new Recipe { Name = "", CategoryId = category, Unit = "g" }, dirty: true));
        _revertingSelection = true;
        SelectedNode = null;
        _revertingSelection = false;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Editor is null || !await Editor.Save()) return;
        AfterSave();
    }

    private void AfterSave()
    {
        _seenVersion = Book.Version;
        Rebuild();
    }

    [RelayCommand]
    private void Revert()
    {
        if (Editor is { IsNew: true }) Editor = null;
        else Editor?.Revert();
    }

    [RelayCommand]
    private async Task Duplicate()
    {
        if (Editor is null || !await ConfirmLeave()) return;
        var copy = Editor.ToRecipe().Clone();
        copy.Id = 0;
        copy.Name += " (copy)";
        copy.CreatedAt = copy.UpdatedAt = null;
        copy.LabelPrinted = false;
        foreach (var f in copy.Files) f.Id = 0;
        SetEditor(new RecipeEditorViewModel(this, copy, dirty: true));
        _revertingSelection = true;
        SelectedNode = null;
        _revertingSelection = false;
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Editor is not { } e) return;
        if (e.IsNew) { Editor = null; return; }
        if (!await Dialogs.Confirm("Delete recipe", $"Delete “{e.Name}”? This cannot be undone (but yesterday's backup still has it).", "Delete")) return;
        var parents = Book.DeleteRecipe(e.Id);
        if (parents.Count > 0)
        {
            await Dialogs.Alert("Recipe is in use",
                $"“{e.Name}” is used inside {parents.Count} other recipe(s), so it cannot be deleted:\n\n• " + string.Join("\n• ", parents.Take(15)));
            return;
        }
        _checked.Remove(e.Id);
        Editor = null;
        AfterSave();
    }

    [RelayCommand]
    private void ScaleAndPreview()
    {
        if (Editor is null) return;
        var vm = new ScaleViewModel(this, Editor.ToRecipe());
        Dialogs.Open(vm, $"Scale & preview — {Editor.Name}", 760, 760);
    }

    [RelayCommand]
    private void PrintRecipe()
    {
        if (Editor is null) return;
        var recipe = Editor.ToRecipe();
        var calc = Book.Calculator(recipe);
        var path = Launcher.ExportPath(recipe.Name);
        Pdf.Recipe(path, recipe, Book.Categories.FirstOrDefault(c => c.Id == recipe.CategoryId)?.Name, calc.Calculate(recipe),
            RecipeExpander.Expand(calc, recipe, 1, Book.UnitConverter), PrintSettings.Options(Book));
        Launcher.Open(path);
    }

    /// <summary>The ticked recipes, or the open one when nothing is ticked.</summary>
    private List<Recipe> Selection() =>
        _checked.Count > 0
            ? _checked.Select(Book.FindRecipe).OfType<Recipe>().OrderBy(r => r.Name).ToList()
            : Editor is { IsNew: false } e && Book.FindRecipe(e.Id) is { } r ? [r] : [];

    [RelayCommand]
    private async Task PrintLabels()
    {
        var recipes = Selection();
        if (recipes.Count == 0)
        {
            await Dialogs.Alert("Print labels", "Tick the recipes you want labels for (or open one), then try again.");
            return;
        }
        if (await Dialogs.Show(new PrintLabelsViewModel(Book, recipes), "Print labels", 620, 600) is true)
        {
            _seenVersion = -1;
            Activate();
        }
    }

    /// <summary>Opens a read-only copy of the recipe in its own window, to keep beside another (legacy "Open in Tab").</summary>
    [RelayCommand]
    private void OpenInWindow()
    {
        if (Editor is null) return;
        var recipe = Editor.ToRecipe();
        Dialogs.Open(new RecipeSheetViewModel(Book, recipe), recipe.Name, 900, 720);
    }

    [RelayCommand]
    private async Task AddToShopping()
    {
        var recipes = Selection();
        if (recipes.Count == 0)
        {
            await Dialogs.Alert("Shopping list", "Tick the recipes you want to shop for (or open one), then try again.");
            return;
        }
        if (await Dialogs.Show(new AddToShoppingViewModel(Book, recipes), "Add to shopping list", 620, 560) is int listId)
            Main.GoToShoppingList(listId);
    }
}
