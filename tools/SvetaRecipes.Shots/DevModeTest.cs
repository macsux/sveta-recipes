using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SvetaRecipes.App;
using SvetaRecipes.App.Services;
using SvetaRecipes.App.ViewModels;
using SvetaRecipes.App.Views;
using SvetaRecipes.Core.Services;

/// <summary>
/// End-to-end test of development mode, isolated in a work folder: a snapshot of this working tree as the "GitHub" repo,
/// the clone, a copy of the data. Clone → build + check → start the real build and wait for its window → the installed
/// app hands over → a build that crashes falls back. With --live, Claude (Claude Code CLI, costs a little) makes a real
/// change to the app and prepares it. Opens real app windows briefly (needs a display).
/// Usage: SvetaRecipes.Shots devmode &lt;database to COPY&gt; &lt;work folder&gt; [--live]
/// </summary>
internal static class DevModeTest
{
    public static int Run(string source, string work, bool live)
    {
        var failures = new List<string>();
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {what}"); if (!ok) failures.Add(what); }

        work = Path.GetFullPath(work);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        var repo = Path.Combine(work, "repo");
        var data = Path.Combine(work, "data", "recipes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        File.Copy(source, data);
        Environment.SetEnvironmentVariable(AppPaths.DbOverrideVariable, data);
        // The started builds back up on start: into the work folder, not Documents.
        RecipeBook.Open(data).SetSetting(SettingKeys.BackupFolder, Path.Combine(work, "backups"));
        Environment.SetEnvironmentVariable(DevMode.SourceVariable, Path.Combine(work, "Source", "SvetaRecipes"));
        Environment.SetEnvironmentVariable(DevMode.RepoVariable, repo);

        // "GitHub": this working tree as it is now (tracked + new files, nothing ignored), tagged as the running version.
        var root = Root();
        Git(root, $"ls-files -co --exclude-standard -z", out var files);
        foreach (var f in files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(Path.Combine(root, f))))
        {
            var to = Path.Combine(repo, f);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(root, f), to);
        }
        Git(repo, "init -q -b main", out _);
        Git(repo, "add -A", out _);
        Git(repo, "-c user.name=test -c user.email=test@test commit -q -m snapshot", out _);
        Git(repo, $"tag v{Updates.CurrentVersion}", out _);

        Check(DevMode.MissingTools().GetAwaiter().GetResult() is { Count: 0 }, "git and a .NET 10 SDK are found");
        // The silent per-user installs (Windows) download from these; check they resolve.
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SvetaRecipes");
            var mingit = DevMode.MinGitUrl(http).GetAwaiter().GetResult();
            var script = http.GetStringAsync("https://dot.net/v1/dotnet-install.ps1").GetAwaiter().GetResult();
            Check(mingit.Contains("MinGit-") && script.Contains("-InstallDir"), $"silent install sources resolve ({Path.GetFileName(mingit)}, dotnet-install.ps1)");
        }
        // Choosing the folder: empty = clone into it; a checkout of the app = used as it is; anything else = refused.
        var other = Path.Combine(work, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.txt"), "");
        Check(DevMode.CheckSourceFolder(DevMode.SourceDir, out _) == DevMode.SourceKind.Clone
              && DevMode.CheckSourceFolder(root, out _) == DevMode.SourceKind.Existing
              && DevMode.CheckSourceFolder(other, out _) is null && DevMode.CheckSourceFolder("relative/path", out _) is null,
            "source folder choice: empty → download, a checkout → use it, other files or a relative path → refused");
        Check(DevMode.EnsureSource(DevMode.SourceDir, Console.WriteLine).GetAwaiter().GetResult() is null && Directory.Exists(Path.Combine(DevMode.SourceDir, ".git")),
            $"the source is cloned to {DevMode.SourceDir}");
        Git(DevMode.SourceDir, "branch --show-current", out var branch);
        Check(branch.Trim() == "local", $"…on a local branch for her changes ({branch.Trim()})");

        // An existing checkout (like andrew's own) is used as it is: its branch, and its owner's git identity.
        var mine = Path.Combine(work, "mine");
        Git(work, $"clone -q {repo} mine", out _);
        Git(mine, "checkout -q -b my-branch", out _);
        Git(mine, "config user.name Someone", out _);
        Git(mine, "config user.email someone@example.com", out _);
        Check(DevMode.EnsureSource(mine, _ => { }).GetAwaiter().GetResult() is null, "an existing checkout is accepted");
        Git(mine, "branch --show-current", out var myBranch);
        Git(mine, "config user.name", out var myName);
        Check(myBranch.Trim() == "my-branch" && myName.Trim() == "Someone", $"…and left on its branch with its owner's name ({myBranch.Trim()}, {myName.Trim()})");

        // What the assistant runs first; the solution must not need anything outside the clone.
        var solution = DevMode.Run("dotnet", ["build", "SvetaRecipes.slnx", "--disable-build-servers"], DevMode.SourceDir).GetAwaiter().GetResult();
        Check(solution.Code == 0, "the whole solution builds in a plain clone" + (solution.Code == 0 ? "" : "\n" + DevMode.Tail(solution.Output)));

        var sw = Stopwatch.StartNew();
        var built = DevMode.BuildAndCheck(DevMode.SourceDir, "Development mode set up", s => Console.WriteLine("    " + s)).GetAwaiter().GetResult();
        Check(built.Ok && File.Exists(DevMode.Exe(built.Build!)), $"build and headless check pass ({sw.Elapsed.TotalSeconds:0}s, log {built.LogPath})");
        if (!built.Ok) { Console.WriteLine(DevMode.Tail(built.Log)); return 1; }
        var build = built.Build!;
        Git(DevMode.SourceDir, "rev-parse HEAD", out var head);
        Check(built.Revision == head.Trim() && Stamp(build).Contains("+" + head.Trim()),
            $"an unchanged source builds from HEAD, and the build knows its commit ({Stamp(build)})");

        // Apply: start it and wait for its window.
        var failure = DevMode.StartAndWait(build, TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
        Check(failure is null && DevMode.Load().LastGood == build, $"the build starts and confirms its window ({failure ?? "ok"})");
        StopBuilds();

        // The installed app hands over to the current build.
        var state = DevMode.Load();
        state.Enabled = true;
        DevMode.Save(state);
        Check(DevMode.HandOver([]), "the installed app hands over in development mode");
        var deadline = DateTime.Now.AddSeconds(90);
        while (DevMode.Load().Tried is not null && DateTime.Now < deadline) Thread.Sleep(300);
        Check(DevMode.Load().Tried is null, "…and the build it started confirms");
        StopBuilds();

        // A build that dies on start: Apply reports it and stays on the good one; a later start skips it.
        var broken = Path.Combine(DevMode.BuildsDir, "29990101-000000");
        Directory.CreateDirectory(broken);
        File.WriteAllText(DevMode.Exe(broken), OperatingSystem.IsWindows() ? "" : "#!/bin/sh\nexit 3\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DevMode.Exe(broken), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        failure = DevMode.StartAndWait(broken, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Check(failure is not null && DevMode.Load().Current == build, $"a build that dies is reported and the good one stays current ({failure?.Split('\n')[0]})");
        state = DevMode.Load();
        state.Current = state.Tried = broken;   // as if the launcher had started it and it never opened
        DevMode.Save(state);
        Check(DevMode.HandOver([]) && DevMode.Load().Current == build, "the launcher skips a build that never opened, for the last good one");
        Thread.Sleep(5000);
        StopBuilds();
        Directory.Delete(broken, recursive: true);

        // A change: built from a commit the branch doesn't point at until Apply, which commits exactly what was built.
        File.AppendAllText(Path.Combine(DevMode.SourceDir, "CHANGELOG.md"), "\nA change.\n");
        var changed = DevMode.BuildAndCheck(DevMode.SourceDir, "Test change", _ => { }).GetAwaiter().GetResult();
        Git(DevMode.SourceDir, "rev-parse HEAD", out var headAfterBuild);
        Check(changed.Ok && changed.Revision != head.Trim() && headAfterBuild == head && Stamp(changed.Build!).Contains("+" + changed.Revision),
            "a changed source builds from its own commit, stamped in the build; the branch hasn't moved");
        File.AppendAllText(Path.Combine(DevMode.SourceDir, "CHANGELOG.md"), "Edited after the build.\n");
        var applied = DevMode.CommitApplied(DevMode.SourceDir, changed.Revision!).GetAwaiter().GetResult();
        Git(DevMode.SourceDir, "log -1 --format=%H%n%s", out var last);
        Git(DevMode.SourceDir, "diff --cached --stat", out var staged);
        Git(DevMode.SourceDir, "diff --stat", out var unstaged);
        Check(applied == changed.Revision && last.StartsWith(changed.Revision!) && last.Contains("Test change") && staged.Trim() == "" && unstaged.Contains("CHANGELOG.md"),
            "Apply makes the built commit the branch head; an edit made after the build stays uncommitted");
        Git(DevMode.SourceDir, "checkout -- CHANGELOG.md", out _);

        if (live) LiveChange(build, Check);

        Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Claude, as the app's developer, makes a small change and prepares it (the UI here is headless).</summary>
    private static void LiveChange(string build, Action<bool, string> check)
    {
        Environment.SetEnvironmentVariable(DevMode.AsBuildVariable, build);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        App.ApplyTheme("Light");
        var book = RecipeBook.Open(AppPaths.Database);
        var window = new MainWindow { Width = 1366, Height = 768, WindowState = Avalonia.Controls.WindowState.Normal };
        var vm = new MainViewModel(book, window);
        window.DataContext = vm;
        window.Show();
        check(vm.Dev.IsDevMode && vm.DescribeView().Contains("development build"), $"the assistant is told it's a development build ({vm.DescribeView()})");

        Git(DevMode.SourceDir, "rev-parse HEAD", out var before);
        check(vm.DescribeView().Contains("from commit "), "the assistant is told the commit it's running");
        vm.Assistant.Draft = "On the recipe screen, rename the \"Duplicate\" button to \"Make a copy\". Go ahead and prepare it, no need to ask.";
        var send = vm.Assistant.SendCommand.ExecuteAsync(null);
        var deadline = DateTime.Now.AddMinutes(20);
        while (!send.IsCompleted && DateTime.Now < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(50);
        }
        foreach (var item in vm.Assistant.Items)
            Console.WriteLine("    " + (item switch
            {
                AssistantChatItem a => a.Text,
                ToolChatItem t => $"[{t.Tool}{(t.Failed ? " FAILED" : "")}] {t.Input.Split('\n')[0]}",
                ErrorChatItem e => "ERROR " + e.Text,
                _ => "",
            }).Replace("\n", "\n    "));
        var tools = vm.Assistant.Items.OfType<ToolChatItem>().ToList();
        Git(DevMode.SourceDir, "rev-parse HEAD", out var after);
        Git(DevMode.SourceDir, "grep -n \"Make a copy\" -- src", out var found);
        check(found.Contains("RecipeEditorView.axaml"), "live: the button is renamed in the source");
        check(tools.Any(t => t.Input.Contains("dotnet build")) && tools.Any(t => t.Input.Contains("SvetaRecipes.Shots") && t.Input.Contains("check")),
            "live: it built the source and ran the headless check itself");
        check(tools.Any(t => t.Tool == "Read" && t.Input.Contains(".png")), "live: it looked at a screenshot");
        check(after.Trim() == before.Trim(), "live: it left committing to the app (the branch hasn't moved before Apply)");
        check(vm.Dev.IsReady && vm.Dev.ReadyBuild != build, $"live: prepare_changes built it and \"Apply changes\" is offered ({vm.Dev.Status})");
        // Apply: the commit is made first. (No window to restart here, so the restart itself reports a failure.)
        var apply = vm.Dev.ApplyCommand.ExecuteAsync(null);
        while (!apply.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
        Git(DevMode.SourceDir, "log -1 --format=%s", out var subject);
        Git(DevMode.SourceDir, "status --porcelain -- src", out var dirty);
        check(subject.Contains("Make a copy", StringComparison.OrdinalIgnoreCase) && dirty.Trim() == "",
            $"live: Apply committed the change with its summary ({subject.Trim()})");
    }

    /// <summary>The informational version of a build ("1.0.0+&lt;commit&gt;").</summary>
    private static string Stamp(string build) =>
        FileVersionInfo.GetVersionInfo(Path.Combine(build, "SvetaRecipes.dll")).ProductVersion ?? "";

    private static void StopBuilds()
    {
        foreach (var p in Process.GetProcessesByName("SvetaRecipes"))
            if (p.MainModule?.FileName?.StartsWith(DevMode.BuildsDir) == true) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
    }

    private static void Git(string cwd, string args, out string output)
    {
        var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false })!;
        output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SvetaRecipes.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
