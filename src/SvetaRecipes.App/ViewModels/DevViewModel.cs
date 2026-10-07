using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;

namespace SvetaRecipes.App.ViewModels;

/// <summary>
/// The Release / Development switch and the bar at the top while the app is being built or a change is ready to apply
/// (<see cref="DevMode"/> does the work). The assistant prepares a change with <see cref="PrepareForAssistant"/>; she
/// applies it with "Apply changes", which restarts into the new build.
/// </summary>
public sealed partial class DevViewModel(MainViewModel main) : ViewModelBase
{
    /// <summary>What <see cref="Restart"/> returns when she stays (Cancel on unsaved edits).</summary>
    public const string Stayed = "stayed";

    public bool IsDevMode { get; } = DevMode.IsDevBuild;
    public string? RunningBuild { get; } = DevMode.RunningBuild;
    /// <summary>The commit the running app was built from (short).</summary>
    public string? Revision { get; } = Updates.Revision;
    public string ModeLabel => "Mode: " + (IsDevMode ? $"Development{(Revision is { } r ? " · " + r : "")}" : "Release");

    /// <summary>
    /// Set by the window: closes the normal way (unsaved edits are asked about), runs the start, and exits if it returns
    /// null; otherwise comes back and returns why (or <see cref="Stayed"/>).
    /// </summary>
    public Func<Func<Task<string?>>, Task<string?>>? Restart { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string? _status;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReady))] private string? _readyBuild;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLog))] private string? _logPath;
    public bool HasStatus => Status is not null;
    public bool IsReady => ReadyBuild is not null;
    public bool HasLog => LogPath is not null;
    private string? _readySummary;
    private string? _readyRevision;
    /// <summary>A failed Apply, told to the assistant with her next message.</summary>
    private string? _lastFailure;

    private IDialogs Dialogs => main.Dialogs;

    /// <summary>For the assistant's "what's open" line; reports a failed Apply once.</summary>
    public string? Describe()
    {
        if (!IsDevMode) return $"released version {Updates.VersionText}";
        var text = $"development build {RunningBuild} from commit {Revision ?? "unknown"}";
        if (IsReady) text += $"; a built change is waiting for her to press Apply changes: {_readySummary}";
        if (_lastFailure is { } f) text += $"; {f}";
        _lastFailure = null;
        return text;
    }

    [RelayCommand]
    private async Task ToggleMode()
    {
        if (IsWorking) return;
        if (IsDevMode) await SwitchToRelease();
        else await SwitchToDevelopment();
    }

    /// <summary>
    /// The folder development mode uses for the source (shown and changed under Tools &amp; settings → Assistant): the
    /// running build's own in development mode, else the one chosen for next time.
    /// </summary>
    [ObservableProperty] private string _sourceFolder = DevMode.SourceDir;

    private async Task SwitchToDevelopment()
    {
        var source = await AskSourceFolder(
            "In development mode the assistant can change the app itself: ask it for a new button, a different layout, a new " +
            "report, and it changes the app's code, tests it, and offers to restart with the change.\n\n" +
            "The app's source code goes in the folder below. If it isn't there yet, it's downloaded, and anything needed to " +
            "build it is set up; the first time takes a few minutes, with nothing for you to do. Your data stays where it " +
            "is, and you can switch back to Release at any time.",
            "Switch to development");
        if (source is not null) await StartFrom(source, switching: true);
    }

    /// <summary>Tools &amp; settings → Assistant: another source folder; in development mode the app is rebuilt from it and restarted.</summary>
    [RelayCommand]
    private async Task ChangeSourceFolder()
    {
        if (IsWorking) return;
        var source = await AskSourceFolder(IsDevMode
                ? "The folder the app is built from. Choosing another one builds the app from the source code there (downloading it if the folder is empty) and restarts into it."
                : "Where development mode keeps the app's source code. It's used the next time you switch to development mode.",
            IsDevMode ? "Use and restart" : "Save");
        if (source is null || (IsDevMode && PathsEqual(source, DevMode.SourceDir))) return;
        if (IsDevMode) await StartFrom(source, switching: false);
    }

    /// <summary>Asks for the folder, starting from the current choice; remembers what she picks.</summary>
    private async Task<string?> AskSourceFolder(string intro, string ok)
    {
        var dialog = new SourceFolderViewModel(Dialogs, SourceFolder, intro, ok);
        if (await Dialogs.Show(dialog, "Source code folder", 620, 340) is not string folder) return null;
        var state = DevMode.Load();
        state.SourceDir = folder;
        DevMode.Save(state);
        if (!IsDevMode) SourceFolder = folder;
        return folder;
    }

    /// <summary>
    /// Sets up <paramref name="source"/> (tools, source, a first build) and restarts into it. Everything is automatic; if a
    /// step fails the assistant gets the error to fix, then it's tried again once.
    /// </summary>
    /// <param name="switching">Turning development mode on (back off if it doesn't start), rather than moving folders.</param>
    private async Task StartFrom(string source, bool switching)
    {
        IsWorking = true;
        Failed = false;
        LogPath = null;
        try
        {
            var result = await SetUp(source);
            if (result.Error is { } error)
            {
                Status = "Something needed fixing; the assistant is on it…";
                main.IsAssistantOpen = true;
                var asked = await main.Assistant.RunForApp(
                    "Setting up development mode hit a problem; the assistant is fixing it.",
                    SetUpProblem(error, source));
                result = asked ? await SetUp(source) : result;
            }
            if (result.Error is { } still) { Fail("Development mode couldn't be set up: " + still.Split('\n')[0], result.Build?.LogPath); return; }

            var build = result.Build!;
            Status = "Starting the development version…";
            var state = DevMode.Load();
            var wasEnabled = state.Enabled;
            state.Enabled = true;
            if (!IsDevMode) state.Launcher = Environment.ProcessPath;
            DevMode.Save(state);
            var failure = await RestartInto(build.Build!);
            if (failure is null) return;
            state = DevMode.Load();
            state.Enabled = wasEnabled;
            DevMode.Save(state);
            if (failure == Stayed) Status = null;
            else Fail("The development version didn't start: " + failure, build.LogPath);
        }
        catch (Exception e)
        {
            Fail("Setting up development mode failed: " + e.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Tools (installed silently if missing), the source, a first build: null error = ready to start.</summary>
    private async Task<(string? Error, DevMode.BuildResult? Build)> SetUp(string source)
    {
        Status = "Checking the tools needed to build the app…";
        var missing = await DevMode.MissingTools();
        if (missing.Count > 0)
        {
            Status = $"Installing {string.Join(" and ", missing)} (only for you, nothing to click)…";
            if (await DevMode.InstallTools(missing, s => Status = s) is { } installError) return (installError, null);
            if (await DevMode.MissingTools() is { Count: > 0 } still) return ($"Still missing after installing: {string.Join(", ", still)}.", null);
        }
        Status = $"Getting the app's source code ({source})…";
        if (await DevMode.EnsureSource(source, _ => { }) is { } sourceError) return (sourceError, null);
        var build = await DevMode.BuildAndCheck(source, "Development mode set up", s => Status = s);
        LogPath = build.LogPath;
        return build.Ok ? (null, build) : ($"The source didn't build or pass its check. Log: {build.LogPath}\n{DevMode.Tail(build.Log)}", build);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>For the assistant (still the released app's): what failed and what has to be true afterwards.</summary>
    private static string SetUpProblem(string error, string source) => $"""
        [From the app, not from Sveta] She switched the app to development mode and the automatic setup failed. Fix it yourself, silently: don't ask her anything or tell her to do anything (she isn't technical), and don't change her data.

        What failed:
        {error}

        What the app needs, after which it tries again by itself:
        - git and a .NET 10 SDK that this app's processes can run. Install per-user without administrator rights or installer windows; the app puts {DevMode.ToolsDir}\dotnet and {DevMode.ToolsDir}\git\cmd first on its PATH, so installing there works (MinGit zip from github.com/git-for-windows/git releases; .NET via https://dot.net/v1/dotnet-install.ps1 -Channel 10.0 -InstallDir {DevMode.ToolsDir}\dotnet).
        - A git clone of {Updates.RepoUrl} at {source} on a branch named local (leave an existing clone's changes alone).
        - `dotnet publish src/SvetaRecipes.App` succeeding in that folder.
        Stay inside {source} and {DevMode.ToolsDir}: don't clone other repositories, don't install things system-wide, and don't edit the source to make it build. The source is self-contained; if it doesn't build as cloned, that's a problem with the released code: stop and say so.
        Reply in one or two plain sentences for her when it's done (or what you couldn't fix).
        """;

    private async Task SwitchToRelease()
    {
        if (!await Dialogs.Confirm("Back to the released version",
                "Switch back to the released version of the app? The assistant's changes stay in the source code " +
                $"({DevMode.SourceDir}) and come back if you switch to development again, but the released version doesn't have them.",
                "Switch to release", "Cancel"))
            return;
        var state = DevMode.Load();
        state.Enabled = false;
        DevMode.Save(state);
        if (state.Launcher is { } launcher && File.Exists(launcher) && Restart is { } restart)
        {
            if (await restart(() => { DevMode.Start(launcher); return Task.FromResult<string?>(null); }) is null) return;
            state.Enabled = true;   // she stayed
            DevMode.Save(state);
            return;
        }
        await Dialogs.Alert("Back to the released version", "The next time you start Sveta's Recipes it will be the released version.");
    }

    /// <summary>The assistant's prepare_changes: build, check, and offer "Apply changes". Returns what it should know.</summary>
    public async Task<string> PrepareForAssistant(string summary)
    {
        if (!IsDevMode) return "Not in development mode: there is nothing to apply changes to.";
        if (IsWorking) return "A build is already running; wait for it and try again.";
        IsWorking = true;
        Failed = false;
        ReadyBuild = null;
        try
        {
            var result = await DevMode.BuildAndCheck(DevMode.SourceDir, summary, s => Status = s);
            LogPath = result.LogPath;
            if (!result.Ok)
            {
                Fail("The assistant's change didn't build or pass the check; it is looking into it.", result.LogPath);
                return $"FAILED, nothing to apply. Full log: {result.LogPath}\n{DevMode.Tail(result.Log)}";
            }
            _readySummary = summary;
            _readyRevision = result.Revision;
            ReadyBuild = result.Build;
            Status = "Changes ready: " + summary;
            return $"Built {result.Build} from commit {result.Revision![..7]} (not on the branch until she applies it); the headless check passed (screenshots in {DevMode.CheckDir}). She now has an " +
                   "\"Apply changes\" button at the top of the window: ask her to press it when she's ready. The app restarts " +
                   "into the new build and this conversation carries on there.";
        }
        catch (Exception e)
        {
            Fail("Building the change failed: " + e.Message);
            return "FAILED: " + e.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (ReadyBuild is not { } build || IsWorking) return;
        IsWorking = true;
        try
        {
            // What she is about to run becomes the branch's newest commit, before anything else.
            Status = "Saving the changes…";
            string commit;
            try { commit = await DevMode.CommitApplied(DevMode.SourceDir, _readyRevision!); }
            catch (Exception e)
            {
                _lastFailure = $"Apply changes stopped: committing build {build} failed ({e.Message}); she is still on the previous build";
                Fail("The changes couldn't be saved, so the app wasn't restarted. The assistant will be told with your next message.");
                return;
            }
            Status = "Restarting with the changes…";
            main.Assistant.Note($"Applied: {_readySummary} (commit {commit[..7]})");
            var failure = await RestartInto(build);
            if (failure is null) return;
            if (failure == Stayed)
            {
                Status = "Changes ready: " + _readySummary;
                return;
            }
            _lastFailure = $"Apply changes failed, she is still on the previous build: build {build} (already committed as {_readyRevision?[..7]}) {failure}";
            main.Assistant.Note("The changed app didn't start, so you're still on the previous version. The assistant will be told why with your next message.");
            ReadyBuild = null;
            Fail("The changed app didn't start; you're still on the previous version.", LogPath);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        ReadyBuild = null;
        Status = null;
        Failed = false;
    }

    [RelayCommand]
    private void ShowLog()
    {
        if (LogPath is { } p && File.Exists(p)) Launcher.Open(p);
    }

    private Task<string?> RestartInto(string build) =>
        Restart?.Invoke(() => DevMode.StartAndWait(build, TimeSpan.FromSeconds(90))) ?? Task.FromResult<string?>("The window isn't ready.");

    private void Fail(string message, string? log = null)
    {
        Failed = true;
        Status = message;
        if (log is not null) LogPath = log;
    }
}
