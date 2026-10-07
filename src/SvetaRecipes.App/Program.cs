using Avalonia;
using SvetaRecipes.App.Services;
using Velopack;

namespace SvetaRecipes.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack's install / update / uninstall hooks, then returns for a normal start.
        VelopackApp.Build().Run();
        // Development mode's own git / .NET SDK, if it installed them, for everything this process starts.
        DevMode.UseInstalledTools();
        // In development mode the installed app only starts the current development build.
        if (DevMode.HandOver(args)) return 0;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DevMode.RecordCrash(e.ExceptionObject);
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception e)
        {
            DevMode.RecordCrash(e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
