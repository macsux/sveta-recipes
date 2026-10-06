using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App.Services;

public enum SaveChoice { Save, Discard, Cancel }

public interface IDialogs
{
    Task Alert(string title, string message);
    Task<bool> Confirm(string title, string message, string ok = "OK", string cancel = "Cancel");
    Task<SaveChoice> AskSave(string what);
    Task<string?> PickFile(string title, string filterName, params string[] patterns);
    Task<string?> PickFolder(string title);
    Task<string?> PickSaveFile(string title, string suggestedName, string filterName, string extension);
    /// <summary>Shows a dialog for a <see cref="DialogViewModel"/> and returns what it closed with.</summary>
    Task<object?> Show(DialogViewModel vm, string title, double width, double height);
    /// <summary>Opens a non-modal window (e.g. a recipe preview to keep beside the main window).</summary>
    void Open(ViewModelBase vm, string title, double width, double height);
    Task CopyText(string text);
}

/// <summary>A view model hosted in its own window, closed with a result.</summary>
public abstract class DialogViewModel : ViewModelBase
{
    public event Action<object?>? CloseRequested;
    protected void Close(object? result = null) => CloseRequested?.Invoke(result);
}

/// <summary>Plain message boxes built from Bolt's own classes (Avalonia has no MessageBox).</summary>
public static class MessageBox
{
    public static async Task<int> Show(Window owner, string title, string message, params (string Text, bool Accent)[] buttons)
    {
        var result = -1;
        var window = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (buttons[i].Accent) { b.Classes.Add("accent"); b.IsDefault = true; }
            if (i == buttons.Length - 1 && buttons.Length > 1) b.IsCancel = true;
            b.Click += (_, _) => { result = index; window.Close(); };
            row.Children.Add(b);
        }
        var body = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var head = new TextBlock { Text = title };
        head.Classes.Add("title");
        window.Content = new StackPanel { Margin = new(24, 20), Spacing = 14, Children = { head, body, row } };
        await window.ShowDialog(owner);
        return result;
    }
}

/// <summary>The dialog service, backed by the main window.</summary>
public sealed class WindowDialogs(Window owner) : IDialogs
{
    public Task Alert(string title, string message) => MessageBox.Show(owner, title, message, ("OK", true));

    public async Task<bool> Confirm(string title, string message, string ok = "OK", string cancel = "Cancel") =>
        await MessageBox.Show(owner, title, message, (ok, true), (cancel, false)) == 0;

    public async Task<SaveChoice> AskSave(string what) =>
        await MessageBox.Show(owner, "Unsaved changes", $"Save the changes to “{what}”?", ("Save", true), ("Don't save", false), ("Cancel", false)) switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };

    public async Task<string?> PickFile(string title, string filterName, params string[] patterns)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFile(string title, string suggestedName, string filterName, string extension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolder(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<object?> Show(DialogViewModel vm, string title, double width, double height)
    {
        object? result = null;
        var window = Host(vm, title, width, height);
        vm.CloseRequested += r => { result = r; window.Close(); };
        await window.ShowDialog(owner);
        return result;
    }

    public void Open(ViewModelBase vm, string title, double width, double height)
    {
        var window = Host(vm, title, width, height);
        if (vm is DialogViewModel d) d.CloseRequested += _ => window.Close();
        window.Show(owner);
    }

    public async Task CopyText(string text)
    {
        if (owner.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            await Alert("Copied", "The list is on the clipboard — paste it into a message or a note.");
        }
    }

    private static Window Host(ViewModelBase vm, string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = Math.Min(width, 480), MinHeight = Math.Min(height, 360),
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = vm, DataContext = vm,
    };
}
