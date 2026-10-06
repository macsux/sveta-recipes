using Avalonia;
using Velopack;

namespace SvetaRecipes.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack's install / update / uninstall hooks, then returns for a normal start.
        VelopackApp.Build().Run();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
