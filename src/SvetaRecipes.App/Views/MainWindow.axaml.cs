using Avalonia.Controls;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class MainWindow : Window
{
    private bool _confirmedClose;

    public MainWindow() => InitializeComponent();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_confirmedClose || DataContext is not MainViewModel vm) return;
        e.Cancel = true;
        if (!await vm.CanClose()) return;
        _confirmedClose = true;
        Close();
    }
}
