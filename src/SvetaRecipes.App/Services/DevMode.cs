using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.Services;

/// <summary>Development mode's state, kept next to the database (dev-mode.json) so it is read before the database is opened.</summary>
public sealed class DevState
{
    /// <summary>On: starting the installed app hands over to <see cref="Current"/>.</summary>
    public bool Enabled { get; set; }
    /// <summary>The installed (release) program, to go back to.</summary>
    public string? Launcher { get; set; }
    /// <summary>The source folder she chose (null = <see cref="DevMode.DefaultSourceDir"/>).</summary>
    public string? SourceDir { get; set; }
    /// <summary>The build to run: its folder (under a source folder's .builds).</summary>
    public string? Current { get; set; }
    /// <summary>The last build that started and showed its window.</summary>
    public string? LastGood { get; set; }
    /// <summary>A build that was started and hasn't confirmed yet; still set at the next start means it never opened.</summary>
    public string? Tried { get; set; }
}

/// <summary>
/// Development mode: the app runs from a build of its source in a folder she chose (<see cref="SourceDir"/>: cloned there,
/// or a checkout that's already there, used as it is), and the assistant
/// changes that source. Each change is published to a new folder under <see cref="BuildsDir"/> (the running build is
/// never overwritten, so the assistant can build and test freely), checked headlessly, and started; the old process
/// exits only once the new one has shown its window, otherwise it stays and says why. The installed app is just a
/// launcher while the mode is on (<see cref="HandOver"/>), and falls back to the last build that worked.
/// </summary>
public static class DevMode
{
    /// <summary>Overrides the chosen source folder (tests).</summary>
    public const string SourceVariable = "SVETA_RECIPES_SOURCE";
    /// <summary>What to clone; default the public GitHub repo.</summary>
    public const string RepoVariable = "SVETA_RECIPES_REPO";

    public static string DefaultSourceDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Source", "SvetaRecipes");

    /// <summary>The source folder chosen for development mode (Tools &amp; settings → Assistant), or the default.</summary>
    public static string ConfiguredSourceDir => Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } s
        ? Path.GetFullPath(s)
        : Load().SourceDir is { Length: > 0 } chosen ? chosen : DefaultSourceDir;

    /// <summary>The source this app works on: a development build's own (it runs from &lt;source&gt;/.builds/&lt;build&gt;), else the configured one.</summary>
    public static string SourceDir => RunningBuildDir is { } dir ? Path.GetDirectoryName(Path.GetDirectoryName(dir))! : ConfiguredSourceDir;

    public static string BuildsDirOf(string source) => Path.Combine(source, ".builds");
    public static string BuildsDir => BuildsDirOf(SourceDir);
    /// <summary>Screenshots from the headless check (the assistant's own runs and the one before applying).</summary>
    public static string CheckDir => Path.Combine(BuildsDir, "check");
    private static string RepoUrl => Environment.GetEnvironmentVariable(RepoVariable) is { Length: > 0 } r ? r : Updates.RepoUrl;
    private static string DataDir => Path.GetDirectoryName(Path.GetFullPath(AppPaths.Database))!;
    private static string StatePath => Path.Combine(DataDir, "dev-mode.json");
    private static string CrashPath => Path.Combine(DataDir, "last-crash.txt");
    private static string ExeName => OperatingSystem.IsWindows() ? "SvetaRecipes.exe" : "SvetaRecipes";
    /// <param name="buildDir">A build's folder.</param>
    public static string Exe(string buildDir) => Path.Combine(buildDir, ExeName);

    /// <summary>This process is a development build (it runs from a source folder's .builds).</summary>
    public static bool IsDevBuild => RunningBuildDir is not null;

    /// <summary>Tests only: act as if running from this build folder (a headless test can't run from one).</summary>
    public const string AsBuildVariable = "SVETA_RECIPES_AS_BUILD";

    /// <summary>The folder of the running development build: &lt;source&gt;/.builds/&lt;yyyyMMdd-HHmmss&gt;.</summary>
    public static string? RunningBuildDir
    {
        get
        {
            if (Environment.GetEnvironmentVariable(AsBuildVariable) is { Length: > 0 } pretend) return Path.GetFullPath(pretend);
            var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            return Path.GetFileName(Path.GetDirectoryName(dir)) == ".builds"
                   && File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, "SvetaRecipes.slnx"))
                ? dir : null;
        }
    }

    /// <summary>The running build's name (its folder's).</summary>
    public static string? RunningBuild => RunningBuildDir is { } d ? Path.GetFileName(d) : null;

    public static DevState Load()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<DevState>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public static void Save(DevState state)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ start-up

    /// <summary>
    /// First thing in Main: in development mode the installed app starts the current build instead and exits (true).
    /// A build that was started before and never opened its window is skipped for the last one that did.
    /// </summary>
    public static bool HandOver(string[] args)
    {
        if (IsDevBuild) return false;
        var s = Load();
        if (!s.Enabled || s.Current is null) return false;
        if (s.Tried == s.Current && s.LastGood is { } good && good != s.Current && File.Exists(Exe(good))) s.Current = good;
        if (!File.Exists(Exe(s.Current))) return false;
        s.Tried = s.Current;
        Save(s);
        Start(Exe(s.Current), args);
        return true;
    }

    /// <summary>The window is up: this build works (whoever started it is waiting for this).</summary>
    public static void ConfirmStarted()
    {
        if (RunningBuildDir is not { } build) return;
        var s = Load();
        s.Current = s.LastGood = build;
        s.Tried = null;
        Save(s);
    }

    /// <summary>Why the last start failed, for whoever started it (and the assistant).</summary>
    public static void RecordCrash(object? error)
    {
        try { File.WriteAllText(CrashPath, $"{DateTime.Now:O} {RunningBuild ?? "release"}\n{error}"); }
        catch { /* nothing more we can do */ }
    }

    public static Process Start(string exe, IEnumerable<string>? args = null) =>
        Process.Start(new ProcessStartInfo(exe, args ?? []) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })!;

    /// <summary>
    /// Starts the build in folder <paramref name="build"/> and waits for its window. Null = it's up (the caller exits); otherwise why not, and
    /// the state points back at the build that was running.
    /// </summary>
    public static async Task<string?> StartAndWait(string build, TimeSpan timeout)
    {
        var s = Load();
        var previous = s.Current;
        s.Current = s.Tried = build;
        Save(s);
        var started = DateTime.Now;
        var process = Start(Exe(build));
        string? failure = null;
        while (failure is null)
        {
            await Task.Delay(300);
            if (Load().LastGood == build) return null;
            if (process.HasExited) failure = $"It closed straight away (exit code {process.ExitCode}).";
            else if (DateTime.Now - started > timeout)
            {
                failure = $"It didn't open within {timeout.TotalSeconds:0} seconds.";
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        if (File.Exists(CrashPath) && File.GetLastWriteTime(CrashPath) >= started) failure += "\n" + File.ReadAllText(CrashPath);
        s = Load();
        s.Current = previous;
        s.Tried = null;
        Save(s);
        return failure;
    }

    // ------------------------------------------------------------------ setting up

    /// <summary>
    /// Where development mode installs its own git and .NET SDK when the PC has none: per-user, no administrator, no
    /// installer windows. Not %LOCALAPPDATA%\SvetaRecipes (Velopack's install root, deleted on uninstall).
    /// </summary>
    public static string ToolsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvetaRecipesDev", "tools");

    /// <summary>git and a .NET 10 SDK; the names of those missing.</summary>
    public static async Task<List<string>> MissingTools()
    {
        UseInstalledTools();
        var missing = new List<string>();
        if ((await Run("git", ["--version"], null)).Code != 0) missing.Add("Git");
        var sdks = await Run("dotnet", ["--list-sdks"], null);
        if (sdks.Code != 0 || !sdks.Output.Split('\n').Any(l => l.StartsWith("10."))) missing.Add(".NET 10 SDK");
        return missing;
    }

    /// <summary>
    /// Installs what's missing silently, for this user only (Windows): MinGit (git for apps, a zip) and the .NET 10 SDK
    /// through Microsoft's dotnet-install script, both into <see cref="ToolsDir"/>. Null = done, else what failed.
    /// </summary>
    public static async Task<string?> InstallTools(IEnumerable<string> missing, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return $"Can't install {string.Join(", ", missing)} automatically on this system.";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SvetaRecipes");
        foreach (var tool in missing)
        {
            try
            {
                if (tool == "Git")
                {
                    log("Downloading git…");
                    var zip = Path.Combine(Path.GetTempPath(), "mingit.zip");
                    await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(await MinGitUrl(http)));
                    var dir = Path.Combine(ToolsDir, "git");
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);
                    File.Delete(zip);
                }
                else
                {
                    log("Downloading the .NET 10 SDK…");
                    Directory.CreateDirectory(ToolsDir);
                    var script = Path.Combine(ToolsDir, "dotnet-install.ps1");
                    await File.WriteAllTextAsync(script, await http.GetStringAsync("https://dot.net/v1/dotnet-install.ps1"));
                    var r = await Run("powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                        "-Channel", "10.0", "-InstallDir", Path.Combine(ToolsDir, "dotnet"), "-NoPath"], null, log);
                    if (r.Code != 0) return $"Installing the .NET 10 SDK failed (exit code {r.Code}):\n{Tail(r.Output)}";
                }
            }
            catch (Exception e)
            {
                return $"Installing {tool} failed: {e.Message}";
            }
        }
        UseInstalledTools();
        return null;
    }

    /// <summary>The newest MinGit zip for this processor, from git-for-windows' GitHub releases.</summary>
    public static async Task<string> MinGitUrl(HttpClient http)
    {
        using var release = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/git-for-windows/git/releases/latest"));
        var suffix = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64.zip" : "-64-bit.zip";
        return release.RootElement.GetProperty("assets").EnumerateArray()
                   .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString()))
                   .FirstOrDefault(a => a.Name.StartsWith("MinGit-") && a.Name.EndsWith(suffix) && !a.Name.Contains("busybox")).Url
               ?? throw new InvalidOperationException("no MinGit download found");
    }

    /// <summary>
    /// Puts our own tools (and freshly installed system ones, not yet on this process's PATH) first on PATH; every child
    /// inherits it: the builds, the app it restarts into, and Claude Code with its shell. Called at start-up.
    /// </summary>
    public static void UseInstalledTools()
    {
        var sep = Path.PathSeparator;
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries).ToList();
        var dirs = new List<string> { Path.Combine(ToolsDir, "dotnet"), Path.Combine(ToolsDir, "git", "cmd") };
        if (OperatingSystem.IsWindows())
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            dirs.AddRange([Path.Combine(programs, "dotnet"), Path.Combine(programs, "Git", "cmd")]);
        }
        foreach (var dir in dirs.Where(Directory.Exists).Reverse())
            if (!path.Contains(dir, StringComparer.OrdinalIgnoreCase)) path.Insert(0, dir);
        Environment.SetEnvironmentVariable("PATH", string.Join(sep, path));
        // Our SDK also carries the runtime: the app and its builds started from here run on it.
        if (Directory.Exists(Path.Combine(ToolsDir, "dotnet"))) Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(ToolsDir, "dotnet"));
    }

    /// <summary>What a folder would be used as: null = can't (and why, in <paramref name="note"/>).</summary>
    public enum SourceKind { Clone, Existing }

    /// <summary>
    /// A folder she picked for the source: empty or new = the source is downloaded into it; a checkout of this app's
    /// source = used as it is (its branch and changes); anything else = refused.
    /// </summary>
    public static SourceKind? CheckSourceFolder(string? folder, out string note)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
        {
            note = "Choose a full folder path.";
            return null;
        }
        var dir = folder.Trim();
        if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            note = "The app's source code will be downloaded into this folder.";
            return SourceKind.Clone;
        }
        if (File.Exists(Path.Combine(dir, "SvetaRecipes.slnx")) && Directory.Exists(Path.Combine(dir, ".git")))
        {
            note = "This folder already has the app's source code: it will be used as it is (its current branch and changes).";
            return SourceKind.Existing;
        }
        note = "This folder has other things in it. Choose an empty folder, or one that already has the app's source code.";
        return null;
    }

    /// <summary>
    /// Clones the source if it isn't there yet, at the tag of the running version, on a local branch for her changes.
    /// An existing clone is left as it is (it has her changes).
    /// </summary>
    public static async Task<string?> EnsureSource(string source, Action<string> log)
    {
        if (CheckSourceFolder(source, out var note) is null) return note;
        var fresh = !Directory.Exists(Path.Combine(source, ".git"));
        if (fresh)
        {
            Directory.CreateDirectory(source);
            var clone = await Run("git", ["clone", RepoUrl, source], null, log);
            if (clone.Code != 0) return "Downloading the source failed:\n" + Tail(clone.Output);
            // The commit she is running, when the build says (it does for releases); else the version's tag.
            string[] wanted = [Updates.Revision ?? "", "v" + Updates.CurrentVersion];
            var start = "HEAD";
            foreach (var rev in wanted.Where(r => r.Length > 0))
                if ((await Run("git", ["rev-parse", "-q", "--verify", rev + "^{commit}"], source)).Code == 0) { start = rev; break; }
            var checkout = await Run("git", ["checkout", "-B", "local", start], source, log);
            if (checkout.Code != 0) return "Setting up the source failed:\n" + Tail(checkout.Output);
        }
        // Commits need a name: set one only where git has none (an existing checkout keeps its owner's). A fresh clone
        // also gets settings that keep git quiet and Windows-safe.
        var config = new List<string[]>();
        foreach (var key in new[] { "user.name", "user.email" })
            if ((await Run("git", ["config", key], source)).Output.Trim().Length == 0)
                config.Add([key, key == "user.name" ? "Sveta's Recipes assistant" : "assistant@sveta-recipes.local"]);
        if (fresh) config.AddRange([["core.longpaths", "true"], ["advice.detachedHead", "false"]]);
        foreach (var c in config)
            if ((await Run("git", ["config", c[0], c[1]], source)).Code is not 0 and var code) return $"git config {c[0]} failed ({code}).";
        return null;
    }

    // ------------------------------------------------------------------ commits

    /// <summary>
    /// What's about to be built, as a commit (message = what changed) that the branch doesn't point at yet: made with a
    /// separate index, so neither her branch nor git's own index changes. Applying moves the branch to it
    /// (<see cref="CommitApplied"/>). Nothing changed = HEAD itself.
    /// </summary>
    private static async Task<(string Revision, string? Error)> Snapshot(string source, string message)
    {
        var builds = BuildsDirOf(source);
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(builds, "snapshot.index") };
        File.Delete(env["GIT_INDEX_FILE"]);
        var head = (await Run("git", ["rev-parse", "HEAD"], source)).Output.Trim();
        var steps = new[] { new[] { "read-tree", "HEAD" }, ["add", "-A"], ["write-tree"] };
        var output = "";
        foreach (var step in steps)
        {
            var r = await Run("git", step, source, env: env);
            if (r.Code != 0) return ("", $"git {step[0]} failed:\n{Tail(r.Output)}");
            output = r.Output.Trim();
        }
        var tree = output;
        if (tree == (await Run("git", ["rev-parse", "HEAD^{tree}"], source)).Output.Trim()) return (head, null);
        var commit = await Run("git", ["commit-tree", tree, "-p", head, "-m", message], source);
        return commit.Code == 0 ? (commit.Output.Trim(), null) : ("", "git commit-tree failed:\n" + Tail(commit.Output));
    }

    /// <summary>
    /// She pressed Apply changes: her branch moves to the build's commit, and git's index follows (her working files are
    /// untouched, so edits made since the build stay as uncommitted changes). If the branch moved meanwhile (a merge), the
    /// same files are committed on top of it instead. Returns the commit, or throws.
    /// </summary>
    public static async Task<string> CommitApplied(string source, string revision)
    {
        var head = (await Run("git", ["rev-parse", "HEAD"], source)).Output.Trim();
        if (revision == head) return revision;
        var parent = (await Run("git", ["rev-parse", revision + "^"], source)).Output.Trim();
        if (parent != head)
        {
            var message = (await Run("git", ["log", "-1", "--format=%B", revision], source)).Output.Trim();
            var recommit = await Run("git", ["commit-tree", revision + "^{tree}", "-p", head, "-m", message], source);
            if (recommit.Code != 0) throw new InvalidOperationException("git commit-tree failed: " + Tail(recommit.Output));
            revision = recommit.Output.Trim();
        }
        var update = await Run("git", ["update-ref", "-m", "apply changes", "HEAD", revision, head], source);
        if (update.Code != 0) throw new InvalidOperationException("git update-ref failed: " + Tail(update.Output));
        await Run("git", ["reset", "-q"], source);
        return revision;
    }

    // ------------------------------------------------------------------ building

    /// <param name="Build">The build's folder.</param>
    /// <param name="Revision">The commit it was built from (see <see cref="Snapshot"/>).</param>
    public sealed record BuildResult(bool Ok, string? Build, string? Revision, string Log, string LogPath);

    /// <summary>
    /// Publishes the source to a new build folder and runs the headless check (every screen, on a copy of her data).
    /// Old builds are pruned (never the running one, the current one or the last good one).
    /// </summary>
    /// <param name="message">What changed: the commit message if she applies it.</param>
    public static async Task<BuildResult> BuildAndCheck(string source, string message, Action<string> progress, CancellationToken ct = default)
    {
        var builds = BuildsDirOf(source);
        var checkDir = Path.Combine(builds, "check");
        Directory.CreateDirectory(builds);
        var build = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var logPath = Path.Combine(builds, build + ".log");
        var log = new StringBuilder();
        void Line(string l) { lock (log) log.AppendLine(l); }

        progress("Building the app…");
        var (revision, snapshotError) = await Snapshot(source, message);
        if (snapshotError is not null) return new BuildResult(false, null, null, snapshotError, logPath);
        Line($"revision {revision}");
        var version = (await Run("git", ["describe", "--tags", "--abbrev=0"], source)).Output.Trim().TrimStart('v');
        var publish = await Run("dotnet", ["publish", "src/SvetaRecipes.App", "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier,
            "--self-contained", "false", "-o", Path.Combine(builds, build), "--disable-build-servers", "-p:PublishReadyToRun=false",
            $"-p:Version={(version.Length > 0 ? version : "1.0.0")}", $"-p:SourceRevisionId={revision}"], source, Line, ct);
        var ok = false;
        if (publish.Code == 0)
        {
            progress("Checking every screen on a copy of your data…");
            var check = await Run("dotnet", ["run", "--project", "tools/SvetaRecipes.Shots", "--disable-build-servers", "--",
                "check", AppPaths.Database, checkDir], source, Line, ct);
            ok = check.Code == 0;
            if (!ok) Line($"CHECK FAILED (exit code {check.Code})");
        }
        else Line($"BUILD FAILED (exit code {publish.Code})");
        await File.WriteAllTextAsync(logPath, log.ToString(), ct);
        if (!ok)
        {
            try { Directory.Delete(Path.Combine(builds, build), recursive: true); } catch { /* pruned later */ }
        }
        Prune(builds);
        return new BuildResult(ok, ok ? Path.Combine(builds, build) : null, revision, log.ToString(), logPath);
    }

    /// <summary>Keeps the newest few builds and every one still in use.</summary>
    private static void Prune(string buildsDir)
    {
        var s = Load();
        var keep = new HashSet<string?> { s.Current, s.LastGood, RunningBuildDir };
        var builds = Directory.GetDirectories(buildsDir).Where(d => Path.GetFileName(d) is not "check")
            .Order(StringComparer.Ordinal).ToList();
        foreach (var old in builds.SkipLast(3).Where(b => !keep.Contains(b)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
                File.Delete(old + ".log");
            }
            catch { /* in use: next time */ }
        }
    }

    // ------------------------------------------------------------------ processes

    public sealed record RunResult(int Code, string Output);

    /// <summary>Runs a command, collecting its output (and passing each line to <paramref name="line"/>).</summary>
    public static async Task<RunResult> Run(string file, string[] args, string? cwd, Action<string>? line = null, CancellationToken ct = default,
        IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // Never wait for an answer: no credential prompts, no pager, no editor.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["GIT_EDITOR"] = "true";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) info.Environment[k] = v;
        var output = new StringBuilder();
        void Got(string? l)
        {
            if (l is null) return;
            lock (output) output.AppendLine(l);
            line?.Invoke(l);
        }
        try
        {
            using var p = new Process { StartInfo = info };
            p.OutputDataReceived += (_, e) => Got(e.Data);
            p.ErrorDataReceived += (_, e) => Got(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            return new RunResult(p.ExitCode, output.ToString());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RunResult(-1, $"{file}: {e.Message}");   // not installed
        }
    }

    /// <summary>The end of a long output: where the errors are.</summary>
    public static string Tail(string text, int lines = 40)
    {
        var all = text.TrimEnd().Split('\n');
        // Build errors repeat at the end; show those lines first if there are any.
        var errors = all.Where(l => l.Contains(" error ") || l.Contains("FAIL")).Distinct().Take(20).ToList();
        return string.Join("\n", errors.Count > 0 ? errors : all.TakeLast(lines));
    }
}
