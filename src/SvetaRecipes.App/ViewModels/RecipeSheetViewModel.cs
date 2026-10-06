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

public sealed record SheetLine(LineCost Cost)
{
    public bool IsSeparator => Cost.IsSeparator;
    public bool IsSubRecipe => Cost.Line.Kind == LineKind.SubRecipe;
    public string Name => IsSeparator ? "— — —" : Cost.Name;
    public string Amount => IsSeparator ? "" : Cost.Line.Amount.ToString("0.###", CultureInfo.CurrentCulture);
    public string Unit => IsSeparator ? "" : Cost.Line.Unit;
    public string Percent => IsSeparator ? "" : Cost.Percent.ToString("0.00", CultureInfo.CurrentCulture);
    public string Money => IsSeparator ? "" : Cost.Cost.ToString("C2", CultureInfo.CurrentCulture);
    public string Notes => Cost.Line.Notes ?? "";
}

/// <summary>A read-only recipe in its own window, to keep next to the one being edited (legacy "Open in Tab").</summary>
public partial class RecipeSheetViewModel : DialogViewModel
{
    private readonly RecipeBook _book;
    private readonly Recipe _recipe;

    public RecipeSheetViewModel(RecipeBook book, Recipe recipe)
    {
        _book = book;
        _recipe = recipe;
        var cost = book.Calculator(recipe).Calculate(recipe);
        Name = recipe.Name;
        Subtitle = string.Join("  ·  ", new[]
        {
            book.Categories.FirstOrDefault(c => c.Id == recipe.CategoryId)?.Name,
            $"{cost.TotalAmount:#,##0.#} {recipe.Unit}",
            recipe.Servings is > 0 ? $"{recipe.Servings} servings" : null,
            recipe.Notes,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Costs = $"Ingredients {cost.ComponentsCost:C2}   ·   Labour {cost.LabourCost:C2}   ·   Total {cost.TotalCost:C2}" +
                (cost.CostPerServing is { } ps ? $"   ·   per serving {ps:C2}" : "");
        foreach (var l in cost.Lines) Lines.Add(new SheetLine(l));
        Instructions = recipe.Instructions;
    }

    public string Name { get; }
    public string Subtitle { get; }
    public string Costs { get; }
    public string? Instructions { get; }
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Instructions);
    public ObservableCollection<SheetLine> Lines { get; } = [];

    [RelayCommand]
    private void Print()
    {
        var calc = _book.Calculator(_recipe);
        var path = Launcher.ExportPath(_recipe.Name);
        Pdf.Recipe(path, _recipe, _book.Categories.FirstOrDefault(c => c.Id == _recipe.CategoryId)?.Name, calc.Calculate(_recipe),
            RecipeExpander.Expand(calc, _recipe, 1, _book.UnitConverter), PrintSettings.Options(_book));
        Launcher.Open(path);
    }

    [RelayCommand]
    private void Done() => Close();
}

public partial class LabelPick(Recipe recipe) : ObservableObject
{
    public Recipe Recipe { get; } = recipe;
    public string Name => string.IsNullOrWhiteSpace(Recipe.LabelTitle) ? Recipe.Name : Recipe.LabelTitle!;
    public string Text => Recipe.LabelText ?? "(no label text)";
    [ObservableProperty] private int _copies = 1;
}

/// <summary>Legacy frmPrintLabels: how many of each label, then print; optionally mark the recipes as labelled.</summary>
public partial class PrintLabelsViewModel : DialogViewModel
{
    private readonly RecipeBook _book;

    public PrintLabelsViewModel(RecipeBook book, IReadOnlyList<Recipe> recipes)
    {
        _book = book;
        foreach (var r in recipes) Labels.Add(new LabelPick(r));
        _columns = int.TryParse(book.GetSetting(SettingKeys.LabelColumns), out var c) ? c : 2;
        _rows = int.TryParse(book.GetSetting(SettingKeys.LabelRows), out var r2) ? r2 : 5;
    }

    public ObservableCollection<LabelPick> Labels { get; } = [];
    [ObservableProperty] private bool _markPrinted = true;
    [ObservableProperty] private int _columns;
    [ObservableProperty] private int _rows;

    public string Summary => $"{Labels.Sum(l => Math.Max(0, l.Copies))} labels";

    [RelayCommand]
    private void Print()
    {
        var picks = Labels.Where(l => l.Copies > 0).Select(l => (l.Recipe, l.Copies)).ToList();
        if (picks.Count == 0) return;
        var path = Launcher.ExportPath("Labels");
        Pdf.Labels(path, picks, new LabelLayout(Math.Max(1, Columns), Math.Max(1, Rows)));
        Launcher.Open(path);
        if (MarkPrinted) _book.SetLabelPrinted(picks.Select(p => p.Recipe.Id), true);
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}

public static class PrintSettings
{
    public static RecipePrintOptions Options(RecipeBook book, double factor = 1, bool notes = true, bool instructions = true, bool costs = false) =>
        new(factor, notes, instructions, costs,
            float.TryParse(book.GetSetting(SettingKeys.PrintFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 10.5f);
}
