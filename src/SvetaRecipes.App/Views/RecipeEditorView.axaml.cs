using Avalonia.Controls;
using Avalonia.VisualTree;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class RecipeEditorView : UserControl
{
    public RecipeEditorView()
    {
        InitializeComponent();
        // Double-click a line's name to change it (legacy: double-click opened "Update Recipe Component").
        // Only the name column: double-clicking Qty / Unit / Notes edits that cell in place.
        LineGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control c && c.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Tag is "line-name")
                && DataContext is RecipeEditorViewModel vm)
                vm.EditLineCommand.Execute(null);
        };
    }
}
