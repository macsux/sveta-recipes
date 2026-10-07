using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SvetaRecipes.App.Services;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Views;

public partial class ChatView : UserControl
{
    /// <summary>Follow the conversation to the bottom while it grows, unless she has scrolled up to read.</summary>
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && _stickToBottom) Scroller.ScrollToEnd();
            else if (e.OffsetDelta.Y != 0) _stickToBottom = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 24;
        };
    }

    /// <summary>Enter sends; Shift+Enter is a new line.</summary>
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not ChatViewModel vm) return;
        e.Handled = true;
        _stickToBottom = true;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    /// <summary>The window goes into "point at something" mode; what she clicks is attached to the next message.</summary>
    private async void PickArea(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || DataContext is not ChatViewModel vm) return;
        if (await window.PickAsync(vm.PicksFolder) is { } picked) vm.Attach(picked);
        Composer.Focus();
    }

    private void OpenPicture(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatPicture { Path: var path } && File.Exists(path)) Launcher.Open(path);
    }

    private void Close(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) main.IsAssistantOpen = false;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Composer.Focus();
    }
}
