using Avalonia.Controls;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class IngredientsView : UserControl
{
    public IngredientsView()
    {
        InitializeComponent();
        // Selecting from elsewhere ("Open" on a recipe line) must show the row.
        Grid.SelectionChanged += (_, _) => { if (Grid.SelectedItem is { } item) Grid.ScrollIntoView(item, null); };
        // Like the Access datasheet: a row is saved as soon as you leave it.
        Grid.RowEditEnded += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is IngredientRow row && DataContext is IngredientsViewModel vm)
                vm.Commit(row);
        };
        PriceGrid.RowEditEnded += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is SupplierPriceRow row && DataContext is IngredientsViewModel vm)
                vm.CommitPrice(row);
        };
    }
}
