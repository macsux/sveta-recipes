using Avalonia.Controls;
using Avalonia.Input;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class InvoicesView : UserControl
{
    public InvoicesView()
    {
        InitializeComponent();
        // Double-click opens the invoice, like Edit.
        Grid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: InvoiceRow } && DataContext is InvoicesViewModel vm) vm.EditSelectedCommand.Execute(null);
        };
    }
}
