using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Bolt.Theme.Controls;

/// <summary>
/// A slim custom title bar for windows that draw into the title area (<c>ExtendClientAreaToDecorationsHint</c>):
/// centered window title, optional content, drag-to-move and double-click-to-zoom. Leaves room for the macOS traffic
/// lights on the left. Put it in the first row of the window and set <see cref="Window.ExtendClientAreaTitleBarHeightHint"/>
/// to the same height.
/// </summary>
public class TitleBar : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<TitleBar, string?>(nameof(Title));

    /// <summary>The caption; defaults to the owning window's title.</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    private IDisposable? _titleBinding;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window w && !IsSet(TitleProperty))
            _titleBinding = Bind(TitleProperty, w.GetObservable(Window.TitleProperty));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _titleBinding?.Dispose();
        _titleBinding = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || TopLevel.GetTopLevel(this) is not Window w || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && w.CanResize)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            w.BeginMoveDrag(e);
        e.Handled = true;
    }
}
