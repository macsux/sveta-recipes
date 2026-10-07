using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;

namespace SvetaRecipes.App.ViewModels;

/// <summary>
/// Where development mode keeps the app's source: the default is suggested, she can type or browse to another folder,
/// and the note says what will happen with it (downloaded into it, or the source already there used as it is).
/// Closes with the folder, or null.
/// </summary>
public sealed partial class SourceFolderViewModel : DialogViewModel
{
    private readonly IDialogs _dialogs;

    public SourceFolderViewModel(IDialogs dialogs, string folder, string intro, string okText)
    {
        _dialogs = dialogs;
        Intro = intro;
        OkText = okText;
        Folder = folder;
    }

    public string Intro { get; }
    public string OkText { get; }
    public string DefaultFolder => DevMode.DefaultSourceDir;

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OkCommand))] private bool _isUsable;

    partial void OnFolderChanged(string value)
    {
        IsUsable = DevMode.CheckSourceFolder(value, out var note) is not null;
        Note = note;
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (await _dialogs.PickFolder("Folder for the app's source code") is { } picked) Folder = picked;
    }

    [RelayCommand]
    private void UseDefault() => Folder = DevMode.DefaultSourceDir;

    [RelayCommand(CanExecute = nameof(IsUsable))]
    private void Ok() => Close(Path.TrimEndingDirectorySeparator(Folder.Trim()));

    [RelayCommand]
    private void Cancel() => Close(null);
}
