using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

public sealed record Option<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Something that can be added as a recipe line.</summary>
public sealed record PickItem(int Id, string Name, LineKind Kind, string DefaultUnit, string Detail)
{
    public override string ToString() => Name;
}

public sealed record ParentLink(int Id, string Name);

public partial class TagChoice(Tag tag, bool selected, Action changed) : ObservableObject
{
    public Tag Tag { get; } = tag;
    public string Name => Tag.Name;
    [ObservableProperty] private bool _isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}

public partial class LineViewModel : ObservableObject
{
    private readonly Action _changed;

    public LineViewModel(RecipeLine line, string name, Action changed)
    {
        _changed = changed;
        Kind = line.Kind;
        RefId = line.Kind switch { LineKind.Ingredient => line.IngredientId, LineKind.SubRecipe => line.SubRecipeId, _ => null };
        Name = line.Kind == LineKind.Separator ? "" : name;
        _amount = Math.Round(line.Amount, 3);
        _unit = line.Unit;
        _notes = line.Notes;
    }

    public LineKind Kind { get; }
    public int? RefId { get; }
    public string Name { get; }
    public bool IsSeparator => Kind == LineKind.Separator;
    public bool IsSubRecipe => Kind == LineKind.SubRecipe;

    [ObservableProperty] private double _amount;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private decimal _cost;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string? _warning;

    public string DisplayName => IsSeparator ? "— — —" : Name;

    /// <summary>Editable quantity text for the grid; blank for separators.</summary>
    public string AmountText
    {
        get => IsSeparator ? "" : Amount.ToString("0.###", CultureInfo.CurrentCulture);
        set
        {
            if (IsSeparator) return;
            var t = (value ?? "").Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var d)
                || double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                Amount = d;
            OnPropertyChanged();
        }
    }

    public string CostText => IsSeparator ? "" : Cost.ToString("C2", CultureInfo.CurrentCulture);
    public string PercentText => IsSeparator ? "" : Percent.ToString("0.00", CultureInfo.CurrentCulture);
    public string UnitText => IsSeparator ? "" : Unit;
    public bool HasWarning => Warning is not null;

    partial void OnAmountChanged(double value) { OnPropertyChanged(nameof(AmountText)); _changed(); }
    partial void OnUnitChanged(string value) { OnPropertyChanged(nameof(UnitText)); _changed(); }
    partial void OnNotesChanged(string? value) => _changed();
    partial void OnCostChanged(decimal value) => OnPropertyChanged(nameof(CostText));
    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(PercentText));
    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    public RecipeLine ToLine(int position) => new()
    {
        Position = position, Kind = Kind, Amount = IsSeparator ? 0 : Amount, Unit = Unit, Notes = Notes,
        IngredientId = Kind == LineKind.Ingredient ? RefId : null,
        SubRecipeId = Kind == LineKind.SubRecipe ? RefId : null,
    };
}

public partial class LayerViewModel(RecipeLayer layer, Action changed) : ObservableObject
{
    [ObservableProperty] private string _name = layer.Name;
    [ObservableProperty] private Avalonia.Media.Color _backColor = Parse(layer.BackColor, Avalonia.Media.Colors.White);
    [ObservableProperty] private Avalonia.Media.Color _foreColor = Parse(layer.ForeColor, Avalonia.Media.Colors.Black);
    partial void OnNameChanged(string value) => changed();
    partial void OnBackColorChanged(Avalonia.Media.Color value) => changed();
    partial void OnForeColorChanged(Avalonia.Media.Color value) => changed();

    private static Avalonia.Media.Color Parse(string hex, Avalonia.Media.Color fallback) =>
        Avalonia.Media.Color.TryParse(hex, out var c) ? c : fallback;

    private static string Hex(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public RecipeLayer ToLayer(int position) => new() { Position = position, Name = Name, BackColor = Hex(BackColor), ForeColor = Hex(ForeColor) };
}

/// <summary>A photo or an attached file (legacy tblRecipeImages + tblRecipeAttachments).</summary>
public sealed class PhotoViewModel
{
    public PhotoViewModel(RecipeFile file)
    {
        File = file;
        if (file.Kind == RecipeFileKind.Image)
        {
            try { using var s = new MemoryStream(file.Data); Image = Bitmap.DecodeToWidth(s, 320); }
            catch { Image = null; }
        }
    }

    public RecipeFile File { get; }
    public Bitmap? Image { get; }
    public bool IsImage => Image is not null;
    public string Caption => File.Caption ?? File.FileName;
    public string Size => File.Data.Length < 1024 * 1024 ? $"{File.Data.Length / 1024.0:0} KB" : $"{File.Data.Length / 1048576.0:0.0} MB";
}

/// <summary>
/// Edits a working copy of one recipe. Every change marks it dirty and recomputes costs live (sub-recipes included);
/// nothing reaches the database until <see cref="Save"/>.
/// </summary>
public partial class RecipeEditorViewModel : ViewModelBase
{
    private static readonly HashSet<string> DataProperties =
    [
        nameof(Name), nameof(Category), nameof(Unit), nameof(LabourHours), nameof(Servings), nameof(WeightPerServing),
        nameof(CostMethod), nameof(SellPrice), nameof(PricePerServing), nameof(Notes), nameof(Instructions),
        nameof(LabelTitle), nameof(LabelText), nameof(ChargeCode), nameof(LabelPrinted), nameof(Shape), nameof(Width),
        nameof(Length), nameof(Height), nameof(Description),
    ];

    private readonly RecipesViewModel _owner;
    private RecipeBook Book => _owner.Book;
    private bool _loading = true;
    private bool _syncingServings;

    public RecipesViewModel Owner => _owner;
    public int Id { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public bool IsNew => Id == 0;

    public RecipeEditorViewModel(RecipesViewModel owner, Recipe recipe, bool dirty = false)
    {
        _owner = owner;
        Load(recipe);
        IsDirty = dirty;
    }

    // ------------------------------------------------------------------ fields

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Option<int?>? _category;
    [ObservableProperty] private string _unit = "g";
    [ObservableProperty] private double _labourHours;
    [ObservableProperty] private int? _servings;
    [ObservableProperty] private double? _weightPerServing;
    [ObservableProperty] private Option<ServingCostMethod>? _costMethod;
    [ObservableProperty] private decimal? _sellPrice;
    [ObservableProperty] private decimal? _pricePerServing;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _instructions;
    [ObservableProperty] private string? _labelTitle;
    [ObservableProperty] private string? _labelText;
    [ObservableProperty] private string? _chargeCode;
    [ObservableProperty] private bool _labelPrinted;
    [ObservableProperty] private Option<PanShape>? _shape;
    [ObservableProperty] private double? _width;
    [ObservableProperty] private double? _length;
    [ObservableProperty] private double? _height;

    public ObservableCollection<LineViewModel> Lines { get; } = [];
    public ObservableCollection<TagChoice> Tags { get; } = [];
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<PhotoViewModel> Photos { get; } = [];
    public ObservableCollection<ParentLink> UsedIn { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private bool _isDirty;
    [ObservableProperty] private LineViewModel? _selectedLine;

    public string Title => (IsNew ? "New recipe" : Name) + (IsDirty ? " •" : "");

    // ------------------------------------------------------------------ computed

    [ObservableProperty] private double _totalAmount;
    [ObservableProperty] private decimal _componentsCost;
    [ObservableProperty] private decimal _labourCost;
    [ObservableProperty] private decimal _totalCost;
    [ObservableProperty] private decimal? _costPerServing;
    [ObservableProperty] private string? _warnings;
    [ObservableProperty] private double? _area;
    [ObservableProperty] private double? _volume;

    public bool IsRound => Shape?.Value == PanShape.Round;
    public bool HasPan => Shape?.Value is PanShape.Round or PanShape.Rectangle;
    public string DimensionsLabel => IsRound ? "Diameter × height (cm)" : "Width × length × height (cm)";
    public string LabourRateText => $"at {Book.LabourRate:C2}/h";
    public bool HasLayers => Layers.Count > 0;
    public bool NoParents => UsedIn.Count == 0;
    public string Dates => string.Join("  ·  ", new[]
    {
        IsNew ? null : $"Recipe #{Id}",
        CreatedAt is { } c ? $"Created {c:d}" : null,
        UpdatedAt is { } u ? $"Updated {u:g}" : null,
    }.Where(x => x is not null));

    // ------------------------------------------------------------------ lists for pickers

    public IReadOnlyList<Option<int?>> Categories =>
        [new(null, "(no category)"), .. Book.Categories.Select(c => new Option<int?>(c.Id, c.Name))];

    public IReadOnlyList<string> Units => Book.Units.Select(u => u.Code).ToList();

    public static IReadOnlyList<Option<ServingCostMethod>> CostMethods { get; } =
    [
        new(ServingCostMethod.ByServingCount, "÷ number of servings"),
        new(ServingCostMethod.ByServingWeight, "by weight per serving"),
    ];

    public static IReadOnlyList<Option<PanShape>> Shapes { get; } =
    [
        new(PanShape.Round, "Round"),
        new(PanShape.Rectangle, "Rectangle / square"),
        new(PanShape.None, "No pan"),
    ];

    // ------------------------------------------------------------------ load / save

    private void Load(Recipe r)
    {
        _loading = true;
        Id = r.Id;
        CreatedAt = r.CreatedAt;
        UpdatedAt = r.UpdatedAt;
        Name = r.Name;
        Category = Categories.FirstOrDefault(c => c.Value == r.CategoryId) ?? Categories[0];
        Unit = r.Unit;
        LabourHours = R3(r.LabourHours);
        Servings = r.Servings;
        WeightPerServing = R3(r.WeightPerServing);
        CostMethod = CostMethods.First(m => m.Value == r.CostMethod);
        SellPrice = R2(r.SellPrice);
        PricePerServing = R2(r.PricePerServing);
        Notes = r.Notes;
        Description = r.Description;
        Instructions = r.Instructions;
        LabelTitle = r.LabelTitle;
        LabelText = r.LabelText;
        ChargeCode = r.ChargeCode;
        LabelPrinted = r.LabelPrinted;
        Shape = Shapes.First(s => s.Value == r.Shape);
        Width = R3(r.Width);
        Length = R3(r.Length);
        Height = R3(r.Height);

        Lines.Clear();
        foreach (var l in r.Lines.OrderBy(l => l.Position)) Lines.Add(NewLine(l));
        Tags.Clear();
        var selected = r.Tags.Select(t => t.Id).ToHashSet();
        foreach (var t in Book.Tags) Tags.Add(new TagChoice(t, selected.Contains(t.Id), Changed));
        Layers.Clear();
        foreach (var l in r.Layers.OrderBy(l => l.Position)) Layers.Add(new LayerViewModel(l, Changed));
        Photos.Clear();
        foreach (var f in r.Files.OrderBy(f => f.Kind)) Photos.Add(new PhotoViewModel(f));
        UsedIn.Clear();
        foreach (var p in Book.ParentsOf(r.Id)) UsedIn.Add(new ParentLink(p.Id, p.Name));

        _loading = false;
        IsDirty = false;
        Recalculate();
        OnPropertyChanged(nameof(Dates));
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(NoParents));
    }

    // Values are shown rounded (0.### / 0.00) and a text box writes back what it shows, so load them at that
    // precision; otherwise merely displaying a recipe would change it.
    private static double R3(double v) => Math.Round(v, 3);
    private static double? R3(double? v) => v is { } d ? Math.Round(d, 3) : null;
    private static decimal? R2(decimal? v) => v is { } d ? Math.Round(d, 2) : null;

    private LineViewModel NewLine(RecipeLine l) => new(l, l.Kind switch
    {
        LineKind.Ingredient => l.IngredientId is { } i ? Book.FindIngredient(i)?.Name ?? "(missing ingredient)" : "",
        LineKind.SubRecipe => l.SubRecipeId is { } s ? Book.FindRecipe(s)?.Name ?? "(missing recipe)" : "",
        _ => "",
    }, Changed);

    /// <summary>The recipe as currently edited.</summary>
    public Recipe ToRecipe() => new()
    {
        Id = Id, Name = Name.Trim(), CategoryId = Category?.Value, Unit = Unit, LabourHours = LabourHours,
        Servings = Servings, WeightPerServing = WeightPerServing, CostMethod = CostMethod?.Value ?? ServingCostMethod.ByServingCount,
        SellPrice = SellPrice, PricePerServing = PricePerServing, Notes = Blank(Notes), Description = Blank(Description),
        Instructions = Blank(Instructions), LabelTitle = Blank(LabelTitle), LabelText = Blank(LabelText), ChargeCode = Blank(ChargeCode),
        LabelPrinted = LabelPrinted, Shape = Shape?.Value ?? PanShape.Round, Width = Width, Length = Length, Height = Height,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
        Lines = Lines.Select((l, i) => l.ToLine(i)).ToList(),
        Tags = Tags.Where(t => t.IsSelected).Select(t => t.Tag).ToList(),
        Layers = Layers.Select((l, i) => l.ToLayer(i)).ToList(),
        Files = Photos.Select(p => p.File).ToList(),
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Validates and saves. Returns false (after telling her why) when it cannot be saved.</summary>
    public async Task<bool> Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            await _owner.Dialogs.Alert("Recipe name", "Please give the recipe a name before saving.");
            return false;
        }
        var stored = Book.SaveRecipe(ToRecipe());
        Load(stored);
        return true;
    }

    public void Revert()
    {
        if (Book.FindRecipe(Id) is { } r) Load(r);
    }

    // ------------------------------------------------------------------ change tracking

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null) return;
        if (e.PropertyName == nameof(Shape))
        {
            OnPropertyChanged(nameof(IsRound));
            OnPropertyChanged(nameof(HasPan));
            OnPropertyChanged(nameof(DimensionsLabel));
        }
        if (e.PropertyName == nameof(Name)) OnPropertyChanged(nameof(Title));
        if (DataProperties.Contains(e.PropertyName)) Changed();
    }

    // As in the legacy form: entering servings fills the weight per serving and vice versa.
    partial void OnServingsChanged(int? value)
    {
        if (_loading || _syncingServings) return;
        _syncingServings = true;
        WeightPerServing = value is > 0 && TotalAmount > 0 ? Math.Round(TotalAmount / value.Value, 1) : WeightPerServing;
        _syncingServings = false;
    }

    partial void OnWeightPerServingChanged(double? value)
    {
        if (_loading || _syncingServings) return;
        _syncingServings = true;
        Servings = value is > 0 && TotalAmount > 0 ? (int)Math.Round(TotalAmount / value.Value) : Servings;
        _syncingServings = false;
    }

    private void Changed()
    {
        if (_loading) return;
        IsDirty = true;
        Recalculate();
    }

    public void Recalculate()
    {
        var snapshot = ToRecipe();
        var cost = Book.Calculator(snapshot).Calculate(snapshot);
        for (var i = 0; i < Lines.Count && i < cost.Lines.Count; i++)
        {
            Lines[i].Cost = cost.Lines[i].Cost;
            Lines[i].Percent = cost.Lines[i].Percent;
            Lines[i].Warning = cost.Lines[i].Warning;
        }
        TotalAmount = cost.TotalAmount;
        ComponentsCost = cost.ComponentsCost;
        LabourCost = cost.LabourCost;
        TotalCost = cost.TotalCost;
        CostPerServing = cost.CostPerServing;
        Warnings = cost.Warnings.Count == 0 ? null : string.Join("\n", cost.Warnings);
        var pan = Pan.Of(snapshot);
        Area = pan.Area;
        Volume = pan.Volume;
    }

    // ------------------------------------------------------------------ adding lines

    [ObservableProperty] private bool _addRecipeMode;
    [ObservableProperty] private PickItem? _addPick;
    [ObservableProperty] private string _addAmount = "";
    [ObservableProperty] private string? _addUnit;
    [ObservableProperty] private string? _addNotes;
    [ObservableProperty] private string? _addSearch;

    public IReadOnlyList<PickItem> AddCandidates => AddRecipeMode
        ? Book.Recipes.Where(r => r.Id != Id).Select(r =>
            new PickItem(r.Id, r.Name, LineKind.SubRecipe, r.Unit, Book.Categories.FirstOrDefault(c => c.Id == r.CategoryId)?.Name ?? "")).ToList()
        : Book.Ingredients.Select(i => new PickItem(i.Id, i.Name, LineKind.Ingredient, i.PerPiece ? "pc" : i.Unit,
            i.PerPiece ? $"{i.PackPrice:C2} each" : $"{i.PackQty:0.###} {i.Unit} for {i.PackPrice:C2}")).ToList();

    partial void OnAddRecipeModeChanged(bool value)
    {
        AddPick = null;
        AddSearch = null;
        OnPropertyChanged(nameof(AddCandidates));
    }

    partial void OnAddPickChanged(PickItem? value)
    {
        if (value is not null && !_loadingEdit) AddUnit = value.DefaultUnit;
        UpdateAddPreview();
    }

    partial void OnAddAmountChanged(string value) => UpdateAddPreview();
    partial void OnAddUnitChanged(string? value) => UpdateAddPreview();

    /// <summary>The line being edited through the add bar (legacy "Update Recipe Component"); null when adding.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AddButtonText))] private LineViewModel? _editingLine;
    [ObservableProperty] private string _addPreview = "";
    private bool _loadingEdit;

    public string AddButtonText => EditingLine is null ? "Add" : "Update";

    private double ParsedAddAmount() =>
        double.TryParse(AddAmount.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0;

    /// <summary>What the line would cost, before it is added (as the legacy add dialog showed).</summary>
    private void UpdateAddPreview()
    {
        if (AddPick is null || ParsedAddAmount() <= 0) { AddPreview = ""; return; }
        var probe = new Recipe
        {
            Id = -1, Unit = Unit,
            Lines = [new RecipeLine
            {
                Kind = AddPick.Kind, Amount = ParsedAddAmount(), Unit = AddUnit ?? AddPick.DefaultUnit,
                IngredientId = AddPick.Kind == LineKind.Ingredient ? AddPick.Id : null,
                SubRecipeId = AddPick.Kind == LineKind.SubRecipe ? AddPick.Id : null,
            }],
        };
        var line = Book.Calculator(ToRecipe()).Calculate(probe).Lines[0];
        AddPreview = line.Warning is null ? $"≈ {line.Cost:C2}" : $"≈ {line.Cost:C2} ⚠";
    }

    /// <summary>Loads the selected line into the add bar so its ingredient, quantity, unit or notes can be changed.</summary>
    [RelayCommand]
    private void EditLine()
    {
        if (SelectedLine is not { IsSeparator: false, RefId: { } id } line) return;
        _loadingEdit = true;
        AddRecipeMode = line.IsSubRecipe;
        AddPick = AddCandidates.FirstOrDefault(p => p.Id == id);
        AddSearch = AddPick?.Name;
        AddAmount = line.Amount.ToString("0.###", CultureInfo.InvariantCulture);
        AddUnit = line.Unit;
        AddNotes = line.Notes;
        _loadingEdit = false;
        EditingLine = line;
        UpdateAddPreview();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingLine = null;
        AddPick = null;
        AddSearch = null;
        AddAmount = "";
        AddNotes = null;
    }

    /// <summary>Appends the ingredient names to the label text (legacy "Populate").</summary>
    [RelayCommand]
    private void FillLabelFromIngredients()
    {
        var names = Lines.Where(l => !l.IsSeparator).Select(l => l.Name).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return;
        var list = string.Join(", ", names);
        LabelText = string.IsNullOrWhiteSpace(LabelText) ? list : LabelText.TrimEnd() + " " + list;
    }

    [RelayCommand]
    private void AddLine()
    {
        if (AddPick is null) return;
        var amount = ParsedAddAmount();
        var line = new RecipeLine
        {
            Kind = AddPick.Kind, Amount = amount, Unit = AddUnit ?? AddPick.DefaultUnit, Notes = Blank(AddNotes),
            IngredientId = AddPick.Kind == LineKind.Ingredient ? AddPick.Id : null,
            SubRecipeId = AddPick.Kind == LineKind.SubRecipe ? AddPick.Id : null,
        };
        var vm = NewLine(line);
        if (EditingLine is { } editing && Lines.IndexOf(editing) is var index and >= 0)
        {
            Lines[index] = vm;
            EditingLine = null;
        }
        else
        {
            var at = SelectedLine is { } sel ? Lines.IndexOf(sel) + 1 : Lines.Count;
            Lines.Insert(at, vm);
        }
        SelectedLine = vm;
        AddPick = null;
        AddSearch = null;
        AddAmount = "";
        AddNotes = null;
        Changed();
    }

    [RelayCommand]
    private void AddSeparator()
    {
        var vm = NewLine(new RecipeLine { Kind = LineKind.Separator, Unit = Unit });
        var at = SelectedLine is { } sel ? Lines.IndexOf(sel) + 1 : Lines.Count;
        Lines.Insert(at, vm);
        SelectedLine = vm;
        Changed();
    }

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine is not { } l) return;
        var i = Lines.IndexOf(l);
        Lines.Remove(l);
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(i, Lines.Count - 1)];
        Changed();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedLine is not { } l) return;
        var i = Lines.IndexOf(l);
        var j = i + delta;
        if (j < 0 || j >= Lines.Count) return;
        Lines.Move(i, j);
        SelectedLine = l;
        Changed();
    }

    /// <summary>Opens the selected line's recipe or ingredient.</summary>
    [RelayCommand]
    private void OpenLine()
    {
        if (SelectedLine is not { RefId: { } id } l) return;
        if (l.IsSubRecipe) _owner.Main.GoToRecipe(id);
        else _owner.Main.GoToIngredient(id);
    }

    [RelayCommand]
    private void OpenParent(ParentLink? p)
    {
        if (p is not null) _owner.Main.GoToRecipe(p.Id);
    }

    // ------------------------------------------------------------------ layers and photos

    [RelayCommand]
    private void AddLayer()
    {
        Layers.Add(new LayerViewModel(new RecipeLayer { Name = $"Layer {Layers.Count + 1}", BackColor = "#F3E5C8", ForeColor = "#1B1C1F" }, Changed));
        OnPropertyChanged(nameof(HasLayers));
        Changed();
    }

    [RelayCommand]
    private void RemoveLayer(LayerViewModel? layer)
    {
        if (layer is null) return;
        Layers.Remove(layer);
        OnPropertyChanged(nameof(HasLayers));
        Changed();
    }

    private static readonly HashSet<string> ImageTypes = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    [RelayCommand]
    private async Task AddPhoto()
    {
        var path = await _owner.Dialogs.PickFile("Add a photo or a file", "Photos and documents", "*.*");
        if (path is null) return;
        var kind = ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant()) ? RecipeFileKind.Image : RecipeFileKind.Attachment;
        var file = new RecipeFile { Kind = kind, FileName = Path.GetFileName(path), Data = await File.ReadAllBytesAsync(path) };
        Photos.Add(new PhotoViewModel(file));
        Changed();
    }

    [RelayCommand]
    private void RemovePhoto(PhotoViewModel? photo)
    {
        if (photo is null) return;
        Photos.Remove(photo);
        Changed();
    }

    [RelayCommand]
    private void OpenPhoto(PhotoViewModel? photo)
    {
        if (photo is null) return;
        var path = Path.Combine(Path.GetTempPath(), "SvetaRecipes-" + photo.File.FileName);
        File.WriteAllBytes(path, photo.File.Data);
        Services.Launcher.Open(path);
    }
}
