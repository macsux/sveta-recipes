using System.Reflection;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace SvetaRecipes.App.Services;

/// <summary>
/// Updates come from the GitHub releases of the public repo (they contain no data). The app checks on start and then
/// every <see cref="Interval"/> while it runs; an update is downloaded in the background and the window then offers to
/// restart into it (<see cref="Ready"/>). If she carries on instead, it is applied silently when the app closes, so the
/// next start is the new version either way.
/// </summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/macsux/sveta-recipes";

    /// <summary>How often a running app looks for a new release (GitHub allows 60 anonymous API calls an hour).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static UpdateManager? _manager;
    private static VelopackAsset? _pending;

    public static string Status { get; private set; } = "";

    /// <summary>The downloaded version waiting to be installed, if any.</summary>
    public static string? ReadyVersion { get; private set; }

    /// <summary>Raised on the UI thread when an update has been downloaded.</summary>
    public static event Action? Ready;

    /// <summary>The running version (from the build; 1.0.0 in a development copy).</summary>
    public static string CurrentVersion => typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// The git commit this build was made from (short), stamped by the build: dotnet's SourceLink adds it to the
    /// informational version, and release.sh / development builds pass it explicitly (-p:SourceRevisionId).
    /// </summary>
    public static string? Revision =>
        typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } v
        && v.IndexOf('+') is >= 0 and var plus && v.Length > plus + 1
            ? v[(plus + 1)..Math.Min(v.Length, plus + 8)] : null;

    /// <summary>"1.0.2 (a1b2c3d)".</summary>
    public static string VersionText => Revision is { } r ? $"{CurrentVersion} ({r})" : CurrentVersion;

    /// <summary>Checks now and then every <see cref="Interval"/> for as long as the app runs.</summary>
    public static void CheckInBackground() => Task.Run(async () =>
    {
        var mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        if (!mgr.IsInstalled)
        {
            Status = "Updates: not installed with the installer (development copy).";
            return;
        }
        using var timer = new PeriodicTimer(Interval);
        do await Check(mgr);
        while (await timer.WaitForNextTickAsync());
    });

    private static async Task Check(UpdateManager mgr)
    {
        try
        {
            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = $"Up to date (version {mgr.CurrentVersion}, checked {DateTime.Now:t}).";
                return;
            }
            // Already downloaded and waiting; a still newer release replaces it.
            if (_pending is not null && _pending.Version >= update.TargetFullRelease.Version) return;
            await mgr.DownloadUpdatesAsync(update);
            _manager = mgr;
            _pending = update.TargetFullRelease;
            MarkReady(update.TargetFullRelease.Version.ToString());
        }
        catch (Exception e)
        {
            // Offline, GitHub unreachable, etc. — the next check tries again.
            Status = "Could not check for updates: " + e.Message;
        }
    }

    /// <summary>Announces a downloaded update (public so the smoke test can show the prompt).</summary>
    public static void MarkReady(string version)
    {
        ReadyVersion = version;
        Status = $"Version {version} is ready and will be installed when the app is closed.";
        Dispatcher.UIThread.Post(() => Ready?.Invoke());
    }

    /// <summary>Called as the app closes: installs a downloaded update once it has exited, and restarts it if asked.</summary>
    public static void ApplyOnExit(bool restart)
    {
        if (_manager is null || _pending is null) return;
        try { _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: restart); }
        catch { /* the update stays downloaded; the next start tries again */ }
    }
}
