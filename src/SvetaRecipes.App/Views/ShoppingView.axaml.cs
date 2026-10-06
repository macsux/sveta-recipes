using Avalonia.Controls;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class ShoppingView : UserControl
{
    public ShoppingView()
    {
        InitializeComponent();
        Grid.RowEditEnded += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && DataContext is ShoppingViewModel vm) vm.SaveSoon();
        };
    }
}
