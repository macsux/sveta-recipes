using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

namespace SvetaRecipes.App.ViewModels;

public enum ScaleMode { Amount, Servings, Pan, Factor }

public sealed record PreviewLine(ExpandedLine Line)
{
    public string Name => Line.Kind == LineKind.Separator ? "" : new string(' ', Line.Depth * 4) + Line.Name;
    public string Amount => Line.Kind == LineKind.Separator ? "" : Line.Amount.ToString(Line.Amount >= 100 ? "#,##0" : "#,##0.#", CultureInfo.CurrentCulture);
    public string Unit => Line.Kind == LineKind.Separator ? "" : Line.Unit;
    public string Percent => Line.Kind == LineKind.Separator || Line.Depth > 0 ? "" : Line.Percent.ToString("0.0", CultureInfo.CurrentCulture);
    public string Notes => Line.Notes ?? "";
    public bool IsSubRecipe => Line.Kind == LineKind.SubRecipe;
    public bool IsNested => Line.Depth > 0;
    public bool IsSeparator => Line.Kind == LineKind.Separator;
}

/// <summary>
/// Legacy "Scale recipe up or down": by target amount, by servings, or by moving to another pan, plus the fully expanded
/// preview (sub-recipes listed under the line that uses them) that prints.
/// </summary>
public partial class ScaleViewModel : DialogViewModel
{
    private readonly RecipesViewModel _owner;
    private readonly Recipe _recipe;
    private readonly RecipeCost _cost;
    private RecipeBook Book => _owner.Book;

    public ScaleViewModel(RecipesViewModel owner, Recipe recipe)
    {
        _owner = owner;
        _recipe = recipe;
        _cost = Book.Calculator(recipe).Calculate(recipe);
        var pan = Pan.Of(recipe);
        _targetShape = RecipeEditorViewModel.Shapes.FirstOrDefault(s => s.Value == recipe.Shape && s.Value != PanShape.None) ?? RecipeEditorViewModel.Shapes[0];
        _targetWidth = recipe.Width;
        _targetLength = recipe.Length;
        _targetHeight = recipe.Height;
        CanScaleByPan = pan.IsMeasured;
        Update();
    }

    public string RecipeName => _recipe.Name;
    public string OriginalAmount => $"{_cost.TotalAmount:#,##0.#} {_recipe.Unit}";
    public string OriginalServings => _recipe.Servings is > 0 ? _recipe.Servings.ToString()! : "—";
    public string OriginalPan
    {
        get
        {
            var p = Pan.Of(_recipe);
            if (!p.IsMeasured) return "no pan size recorded";
            var dims = _recipe.Shape == PanShape.Round ? $"Ø {_recipe.Length:0.#}" : $"{_recipe.Width:0.#} × {_recipe.Length:0.#}";
            if (_recipe.Height is > 0) dims += $" × {_recipe.Height:0.#}";
            return $"{(_recipe.Shape == PanShape.Round ? "Round" : "Rectangle")} {dims} cm";
        }
    }

    public bool CanScaleByPan { get; }

    [ObservableProperty] private ScaleMode _mode = ScaleMode.Amount;
    [ObservableProperty] private double? _targetAmount;
    [ObservableProperty] private double? _targetServings;
    [ObservableProperty] private double? _factorInput = 1;
    [ObservableProperty] private Option<PanShape> _targetShape;
    [ObservableProperty] private double? _targetWidth;
    [ObservableProperty] private double? _targetLength;
    [ObservableProperty] private double? _targetHeight;
    [ObservableProperty] private bool _showNotes = true;
    [ObservableProperty] private bool _printInstructions = true;
    [ObservableProperty] private bool _printCosts;

    [ObservableProperty] private double _factor = 1;
    [ObservableProperty] private string _result = "";
    public ObservableCollection<PreviewLine> Lines { get; } = [];

    public IReadOnlyList<Option<PanShape>> PanShapes { get; } = RecipeEditorViewModel.Shapes.Where(s => s.Value != PanShape.None).ToList();
    public bool TargetRound => TargetShape.Value == PanShape.Round;

    public bool ByAmount { get => Mode == ScaleMode.Amount; set { if (value) Mode = ScaleMode.Amount; } }
    public bool ByServings { get => Mode == ScaleMode.Servings; set { if (value) Mode = ScaleMode.Servings; } }
    public bool ByPan { get => Mode == ScaleMode.Pan; set { if (value) Mode = ScaleMode.Pan; } }
    public bool ByFactor { get => Mode == ScaleMode.Factor; set { if (value) Mode = ScaleMode.Factor; } }

    partial void OnModeChanged(ScaleMode value)
    {
        OnPropertyChanged(nameof(ByAmount));
        OnPropertyChanged(nameof(ByServings));
        OnPropertyChanged(nameof(ByPan));
        OnPropertyChanged(nameof(ByFactor));
        Update();
    }

    partial void OnTargetAmountChanged(double? value) => Update();
    partial void OnTargetServingsChanged(double? value) => Update();
    partial void OnFactorInputChanged(double? value) => Update();
    partial void OnTargetShapeChanged(Option<PanShape> value) { OnPropertyChanged(nameof(TargetRound)); Update(); }
    partial void OnTargetWidthChanged(double? value) => Update();
    partial void OnTargetLengthChanged(double? value) => Update();
    partial void OnTargetHeightChanged(double? value) => Update();
    partial void OnShowNotesChanged(bool value) => Update();

    private void Update()
    {
        double? f = Mode switch
        {
            ScaleMode.Amount => TargetAmount is { } a ? Scaling.AmountFactor(_cost.TotalAmount, a) : null,
            ScaleMode.Servings => TargetServings is { } s ? Scaling.ServingsFactor(_recipe.Servings, s) : null,
            ScaleMode.Pan => Scaling.PanFactor(Pan.Of(_recipe), new Pan(TargetShape.Value, TargetWidth, TargetLength, TargetHeight)),
            _ => FactorInput is > 0 ? FactorInput : null,
        };
        Factor = f ?? 1;
        var amount = _cost.TotalAmount * Factor;
        Result = f is null
            ? "Enter a target to scale; showing the original."
            : $"×{Factor:0.###}  →  {amount:#,##0.#} {_recipe.Unit}" +
              (_recipe.Servings is > 0 ? $", {_recipe.Servings * Factor:0.#} servings" : "") +
              $", cost {_cost.TotalCost * (decimal)Factor:C2}";

        Lines.Clear();
        foreach (var l in RecipeExpander.Expand(Book.Calculator(_recipe), _recipe, Factor, Book.UnitConverter)) Lines.Add(new PreviewLine(l));
    }

    [RelayCommand]
    private void Print()
    {
        var calc = Book.Calculator(_recipe);
        var path = Launcher.ExportPath(_recipe.Name + (Math.Abs(Factor - 1) > 0.0001 ? $" x{Factor:0.##}" : ""));
        Pdf.Recipe(path, _recipe, Book.Categories.FirstOrDefault(c => c.Id == _recipe.CategoryId)?.Name, _cost,
            RecipeExpander.Expand(calc, _recipe, Factor, Book.UnitConverter), PrintSettings.Options(Book, Factor, ShowNotes, PrintInstructions, PrintCosts));
        Launcher.Open(path);
    }

    [RelayCommand]
    private async Task AddToShopping()
    {
        if (_recipe.Id == 0)
        {
            await _owner.Dialogs.Alert("Shopping list", "Save the recipe first.");
            return;
        }
        var vm = new AddToShoppingViewModel(Book, [_recipe], Factor);
        if (await _owner.Dialogs.Show(vm, "Add to shopping list", 620, 560) is int listId)
        {
            _owner.Main.GoToShoppingList(listId);
            Close();
        }
    }

    [RelayCommand]
    private void Done() => Close();
}

public partial class ShoppingPick(Recipe recipe, double factor) : ObservableObject
{
    public Recipe Recipe { get; } = recipe;
    public string Name => Recipe.Name;
    [ObservableProperty] private double? _times = Math.Round(factor, 3);
}

/// <summary>Choose a list (new or existing) and how many batches of each recipe to shop for.</summary>
public partial class AddToShoppingViewModel : DialogViewModel
{
    private readonly RecipeBook _book;

    public AddToShoppingViewModel(RecipeBook book, IReadOnlyList<Recipe> recipes, double factor = 1)
    {
        _book = book;
        Picks = new ObservableCollection<ShoppingPick>(recipes.Select(r => new ShoppingPick(r, factor)));
        Lists = [new(0, $"A new list for {DateTime.Today:D}"), .. book.ShoppingLists.Select(l => new Option<int>(l.Id, $"{l.Date:D}{(string.IsNullOrWhiteSpace(l.Name) ? "" : " — " + l.Name)} ({l.Items.Count} items)"))];
        _list = Lists[0];
    }

    public ObservableCollection<ShoppingPick> Picks { get; }
    public IReadOnlyList<Option<int>> Lists { get; }
    [ObservableProperty] private Option<int> _list;

    [RelayCommand]
    private void Add()
    {
        var items = ShoppingBuilder.FromRecipes(_book, Picks.Where(p => p.Times is > 0).Select(p => (p.Recipe, p.Times!.Value)));
        var list = List.Value == 0 ? new ShoppingList { Date = DateTime.Today } : _book.ShoppingLists.First(l => l.Id == List.Value).Clone();
        list.Items = ShoppingBuilder.Merge(list.Items.Concat(items));
        Close(_book.SaveShoppingList(list).Id);
    }

    [RelayCommand]
    private void Cancel() => Close();
}
