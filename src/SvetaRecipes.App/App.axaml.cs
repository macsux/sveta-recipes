using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using SvetaRecipes.App.Services;
using SvetaRecipes.App.ViewModels;
using SvetaRecipes.App.Views;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths.SeedIfMissing(AppPaths.Database);
            var book = RecipeBook.Open(AppPaths.Database);
            ApplyTheme(book.GetSetting(SettingKeys.Theme));
            BackgroundBackup(book);
            Updates.CheckInBackground();

            var window = new MainWindow();
            window.DataContext = new MainViewModel(book, window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string? theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    /// <summary>Today's backup, off the UI thread so start-up is not held up by a slow disk.</summary>
    private static void BackgroundBackup(RecipeBook book)
    {
        var folder = book.GetSetting(SettingKeys.BackupFolder) is { Length: > 0 } f ? f : AppPaths.DefaultBackupFolder;
        Task.Run(() =>
        {
            try { Backups.Daily(book.DbPath, folder); }
            catch (Exception e) { BackupStatus.LastError = e.Message; }
        });
    }
}
