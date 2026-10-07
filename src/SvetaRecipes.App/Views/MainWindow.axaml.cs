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
        vm.Dev.Restart = RestartInto;
        ModeToggle.IsChecked = vm.Dev.IsDevMode;
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

    /// <summary>The mode only changes by restarting: the toggle shows the running mode, and a click asks to switch.</summary>
    private void ToggleMode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        ModeToggle.IsChecked = vm.Dev.IsDevMode;
        vm.Dev.ToggleModeCommand.Execute(null);
    }

    /// <summary>
    /// Restarts as something else (a development build, or back to the installed app): asks about unsaved edits, stops
    /// the assistant, hides, runs <paramref name="start"/>; null from it means the other one is up, so this one exits.
    /// Otherwise the window comes back and the reason is returned (<see cref="DevViewModel.Stayed"/> if she cancelled).
    /// </summary>
    private async Task<string?> RestartInto(Func<Task<string?>> start)
    {
        if (DataContext is not MainViewModel vm) return DevViewModel.Stayed;
        if (!await vm.CanClose()) return DevViewModel.Stayed;
        await vm.Assistant.DisposeAsync();
        Hide();
        string? failure;
        try { failure = await start(); }
        catch (Exception e) { failure = e.Message; }
        if (failure is null)
        {
            _confirmedClose = true;
            Close();
            return null;
        }
        Show();
        Activate();
        return failure;
    }

    /// <summary>"Point at something" mode over the whole window (<see cref="PickOverlay"/>); null if she cancels.</summary>
    public async Task<PickedArea?> PickAsync(string folder)
    {
        var overlay = new PickOverlay(this, folder);
        Grid.SetRowSpan(overlay, Root.RowDefinitions.Count);
        Grid.SetColumnSpan(overlay, Root.ColumnDefinitions.Count);
        Root.Children.Add(overlay);
        try { return await overlay.Result; }
        finally { Root.Children.Remove(overlay); }
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
