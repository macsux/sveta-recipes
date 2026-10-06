using Velopack;
using Velopack.Sources;

namespace SvetaRecipes.App.Services;

/// <summary>
/// Updates come from the GitHub releases of the public repo (they contain no data). An update is downloaded in the
/// background and applied silently when the app closes, so the next start is the new version — nothing for her to do.
/// </summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/macsux/sveta-recipes";

    public static string Status { get; private set; } = "";

    public static void CheckInBackground() => Task.Run(async () =>
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (!mgr.IsInstalled)
            {
                Status = "Updates: not installed with the installer (development copy).";
                return;
            }
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = $"Up to date (version {mgr.CurrentVersion}).";
                return;
            }
            await mgr.DownloadUpdatesAsync(update);
            mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            Status = $"Version {update.TargetFullRelease.Version} is ready and will be installed when the app is closed.";
        }
        catch (Exception e)
        {
            // Offline, GitHub unreachable, etc. — try again next start.
            Status = "Could not check for updates: " + e.Message;
        }
    });
}
