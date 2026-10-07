using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SvetaRecipes.App.Services;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class MainWindow : Window
{
    private const double AssistantDefaultWidth = 420;
    private bool _confirmedClose;
    private bool _restartForUpdate;
    private double _assistantWidth = AssistantDefaultWidth;

    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += OnViewModelChanged;
        LayOutAssistant(vm.IsAssistantOpen);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAssistantOpen) && sender is MainViewModel vm) LayOutAssistant(vm.IsAssistantOpen);
    }

    /// <summary>Opens the assistant column at the width she last dragged it to, or collapses it.</summary>
    private void LayOutAssistant(bool open)
    {
        var column = Root.ColumnDefinitions[2];
        if (!open && column.Width.Value > 0) _assistantWidth = column.Width.Value;
        column.MinWidth = open ? 320 : 0;
        column.Width = new GridLength(open ? _assistantWidth : 0);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_confirmedClose || DataContext is not MainViewModel vm) return;
        e.Cancel = true;
        if (!await vm.CanClose())
        {
            _restartForUpdate = false;   // she stayed (unsaved changes): a later ordinary close shouldn't restart
            return;
        }
        _confirmedClose = true;
        await vm.Assistant.DisposeAsync();
        Updates.ApplyOnExit(restart: _restartForUpdate);
        Close();
    }

    /// <summary>Closes the normal way (unsaved edits are asked about) and comes back as the new version.</summary>
    private void RestartForUpdate(object? sender, RoutedEventArgs e)
    {
        _restartForUpdate = true;
        Close();
    }
}
