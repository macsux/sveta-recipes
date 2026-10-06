using Avalonia.Controls;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        // Each grid saves a row when editing of it ends.
        Commit<UnitRow>(UnitGrid, (vm, r) => vm.CommitUnit(r));
        Commit<ConversionRow>(ConversionGrid, (vm, r) => vm.CommitConversion(r));
        Commit<SubstanceRow>(SubstanceGrid, (vm, r) => vm.CommitSubstance(r));
        Commit<NamedRow>(EntryGrid, (vm, r) => vm.CommitEntry(r));
    }

    private void Commit<T>(DataGrid grid, Action<ToolsViewModel, T> save) =>
        grid.RowEditEnded += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is T row && DataContext is ToolsViewModel vm) save(vm, row);
        };
}
