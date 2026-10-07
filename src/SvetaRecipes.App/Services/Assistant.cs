using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Claude.AgentSdk;
using Claude.AgentSdk.Mcp;
using SvetaRecipes.Core.Assistant;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.Services;

/// <summary>
/// The assistant panel's agent, on the .NET Claude Agent SDK as in aibolt: the Claude Code CLI as a subprocess with its
/// own tools (web, files, shell), and an in-process MCP server ("recipes") giving it the database. It writes with SQL directly;
/// the app reloads after every write (<paramref name="dataChanged"/>, raised on the UI thread). In development mode
/// (<see cref="DevContext"/>) it is also the app's developer: it works in the source clone and has prepare_changes.
/// </summary>
/// <summary>Development mode, for the assistant: where the source is, which build runs, and how to offer a change.</summary>
/// <param name="Revision">The git commit the running build was made from (short).</param>
public sealed record DevContext(string SourceDir, string RunningBuild, string? Revision, string Database, string CheckDir, Func<string, Task<string>> Prepare);

public static class Assistant
{
    public const string ServerName = "recipes";

    /// <param name="resume">Claude Code session to continue (the reopened conversation), or null for a new one.</param>
    /// <param name="dev">Development mode: the assistant also changes the app (null in the released app).</param>
    public static ClaudeAgentOptions Options(RecipeBook book, Action dataChanged, string workFolder, string? resume, DevContext? dev = null) => new()
    {
        // Changing the app is real software work: the stronger model.
        Model = dev is null ? "sonnet" : "opus",
        SystemPrompt = SystemPrompt(book, dev),
        // Claude Code's own tools (web search/fetch, files, shell) plus the database tools below, all without asking.
        PermissionMode = PermissionMode.BypassPermissions,
        McpServers = McpServersConfig.FromServers(new Dictionary<string, object> { [ServerName] = Tools(book, dataChanged, dev) }),
        StrictMcpConfig = true,
        // Nothing from the PC's Claude Code setup (CLAUDE.md, settings, skills) leaks in; in development mode the
        // source's own CLAUDE.md (how the code is built, its rules) does.
        SettingSources = dev is null ? [] : [SettingSource.Project],
        Cwd = dev?.SourceDir ?? workFolder,
        Resume = resume,
        IncludePartialMessages = true,
    };

    /// <summary>The instructions part of the system prompt (editable under Tools & settings → Assistant).</summary>
    public const string DefaultInstructions = """
        You are the assistant inside "Sveta's Recipes", the Windows app Sveta uses to cost recipes for her home pastry business: recipes and sub-recipes, ingredients and prices, shopping lists, invoices, customers and suppliers. She is not technical: answer plainly and briefly, and don't show SQL or ids unless she asks. Money is CAD. Each message starts with what she has open in the app.

        You have the app's SQLite database:
        - sql runs any SQL, reads and writes. Before the first write of a request call backup, once. The app reloads itself after each write. Write rows as the app would: Position from 0, CreatedAt/UpdatedAt = datetime('now','localtime'), units from Units.Code. Don't touch data she didn't ask about.
        - get_costing gives a recipe's live cost. Costs are never stored; use it for anything about cost.
        - find_similar finds recipes with a similar composition (sub-recipes expanded, compared by weight share).
        Change the database only through sql, never the file directly. You also have Claude Code's usual tools: web search and fetch (a recipe from a link, an ingredient, a technique), and files and a shell on her PC.

        Add a recipe only when she explicitly asks you to add or import it. If she just pastes one or sends a link, say whether she already has it or something close, and ask if she wants it added.

        Checking or importing a recipe (pasted, or from a link: fetch it): map every line to an existing ingredient or sub-recipe (names differ: "heavy cream" may be "Cream 35%"); convert amounts to grams yourself; run find_similar on the whole recipe and on each component that could be one of her sub-recipes; then decide. A duplicate: say which recipe and don't import. A variant: import it, say what differs and write that in Description. Reuse her sub-recipes where they match. Create missing ingredients with PackPrice 0 and list them so she can price them. Tag what you import "Imported".
        """;

    /// <summary>Her edited instructions, or the default.</summary>
    public static string Instructions(RecipeBook book) =>
        book.GetSetting(SettingKeys.AssistantPrompt) is { Length: > 0 } custom ? custom : DefaultInstructions;

    /// <summary>The instructions, the development part when on, then the database schema (always generated, not editable).</summary>
    public static string SystemPrompt(RecipeBook book, DevContext? dev = null) =>
        $"{Instructions(book)}\n\n{(dev is null ? "" : DevInstructions(dev) + "\n\n")}Database:\n{DbSchema.Describe()}";

    /// <summary>
    /// Development mode's second role. Generated, not editable: it states this PC's paths and the build-and-apply
    /// workflow the app implements (<see cref="DevMode"/>).
    /// </summary>
    public static string DevInstructions(DevContext dev) => $$"""
        # Development mode: you are also this app's developer

        Sveta has switched the app to development mode, so you have two roles:
        1. Her assistant for her recipes and business data, exactly as above.
        2. The developer of the app itself. She can ask you to change how it looks or works (a button, a column, a screen, a printout, a calculation, something that's wrong or missing) and you change its source code, test it, and offer her the new version.

        Decide which one she means. Her recipes, prices, invoices and customers are data: use sql. How the app looks or behaves is code. If it could be either ("add allergens to my recipes"), ask. She isn't technical: talk about what she'll see, not about code, unless she asks.

        ## Where things are
        - Source: a git clone at {{dev.SourceDir}}, your working folder. Its CLAUDE.md describes the code and its rules; follow them (styling only through Bolt.Theme's classes, the costing rules, edit clones then save, the UI gotchas). Ignore what it says about releasing, the Mac, and the legacy Access files: none of that applies on this PC.
        - The app she is using right now runs from build {{dev.RunningBuild}}, made from commit {{dev.Revision ?? "(unknown)"}} (a published copy of the source under .builds/). Every message starts with the build and commit she is on. Nothing you do to the source affects it until she presses Apply changes.
        - git history is what she has applied: each Apply makes a commit whose message is your prepare_changes summary. `git log --oneline` = her changes so far; `git diff HEAD` = what's changed but not applied yet.
        - Her database: {{dev.Database}}. Every version of the app shares it. Never edit, move or replace the file; data changes go through sql.
        - When she points at part of the app, the message names the view and element; views are src/SvetaRecipes.App/Views/<View>.axaml (+ .axaml.cs), their view models are in ViewModels/.
        - The look (colours, fonts, control styles, icons) is src/Bolt.Theme, part of this app: views only use its classes, so a styling change goes there.

        ## Making a change
        1. Say in a sentence or two, in her words, what you'll change. For anything big, or anything that changes how costs are worked out or what's stored, ask her first.
        2. Change the code, small and in the style of the code around it.
        3. Test it yourself before offering it. Never start the app itself (dotnet run on the app opens a second window on her real data):
           - `dotnet build SvetaRecipes.slnx` must succeed with no errors.
           - `dotnet run --project tools/SvetaRecipes.Shots -- check "{{dev.Database}}" "{{dev.CheckDir}}"` opens a COPY of her data without a window, shows every screen, saves a PNG of each into that folder and fails if anything throws. Look at (Read) the screenshots of what you changed and fix what looks wrong. If your change is somewhere it doesn't show, add that to tools/SvetaRecipes.Shots/ScreenCheck.cs.
           - Changed costing or other logic in src/SvetaRecipes.Core: `dotnet test --project tests/SvetaRecipes.Core.Tests` (tests that need the original Access files are skipped on this PC).
        4. Call prepare_changes with a one-line summary in her words. It records the source exactly as it is now as a commit (with your summary as its message), publishes a new build from it and runs the same check; if it fails, read the log, fix, and call it again. When it succeeds she gets an "Apply changes" button at the top of the window: tell her to press it when she's ready. Pressing it makes that commit the newest on the branch, then the app restarts into the new build and this conversation carries on there.
        Don't commit yourself (the app does, on Apply), and never push or run release.sh.

        Database schema changes: add an EF Core migration (in src/SvetaRecipes.Core: `dotnet ef migrations add <Name> -o Data/Migrations`; if `dotnet ef` is missing, `dotnet tool install --global dotnet-ef`). The new build migrates her database on start, after snapshotting it. Only change the schema when she needs it: older builds, and the released app, can't use a migrated database.

        If a new build doesn't start, the app stays on the one she had and her next message tells you why. To undo a change she doesn't like: `git revert --no-commit <commit>`, test, then prepare_changes. To bring in the latest released version from GitHub: `git fetch origin --tags`, merge the newest v* tag (the merge commit is fine), resolve conflicts keeping her changes, then test and prepare_changes as usual.
        """;

    private static McpSdkServerConfig Tools(RecipeBook book, Action dataChanged, DevContext? dev)
    {
        var registry = McpServers.Sdk(ServerName, b =>
        {
            b
            .Tool("sql", Schema("""{"type":"object","properties":{"query":{"type":"string","description":"One or more SQL statements"}},"required":["query"]}"""),
                (args, ct) => Run(() =>
                {
                    var result = SqlRunner.Run(book.DbPath, args.GetProperty("query").GetString() ?? "");
                    if (result.RowsChanged > 0) Dispatcher.UIThread.Post(() => { book.Reload(); dataChanged(); });
                    return result.Text;
                }), "Run SQL against the app's database (SQLite). Returns rows as a table, or the number of rows changed.")
            .Tool("backup", Schema("""{"type":"object","properties":{}}"""),
                (args, ct) => Run(() =>
                {
                    var folder = book.GetSetting(SettingKeys.BackupFolder) is { Length: > 0 } f ? f : AppPaths.DefaultBackupFolder;
                    return "Backed up to " + Backups.Snapshot(book.DbPath, folder);
                }), "Snapshot the database (she can restore it from Tools & settings). Call before writing.")
            .Tool("get_costing", Schema("""{"type":"object","properties":{"recipeId":{"type":"integer"}},"required":["recipeId"]}"""),
                (args, ct) => OnUi(() => Costing(book, args.GetProperty("recipeId").GetInt32())),
                "A recipe's live cost: each line's amount, cost and share of the weight, totals, labour, cost per serving, and problems.")
            .Tool("find_similar", Schema("""
                {"type":"object","properties":{
                  "recipeId":{"type":"integer","description":"Compare an existing recipe with all others (instead of lines)"},
                  "name":{"type":"string","description":"Name of the recipe being compared (adds a small name-similarity bonus)"},
                  "lines":{"type":"array","items":{"type":"object","properties":{
                    "ingredientId":{"type":"integer"},"subRecipeId":{"type":"integer"},
                    "name":{"type":"string","description":"For an ingredient she doesn't have (no id)"},
                    "grams":{"type":"number"}},"required":["grams"]}},
                  "top":{"type":"integer","description":"How many matches (default 10)"}}}
                """),
                (args, ct) => OnUi(() => Similar(book, args)),
                "Recipes whose ingredient proportions are closest. overlap 1 = same ingredients in the same proportions. " +
                "coverage = share of the draft's weight made of her ingredients; when low, check candidates by name too.");
            if (dev is not null)
                b.Tool("prepare_changes", Schema("""{"type":"object","properties":{"summary":{"type":"string","description":"One line, in her words: what the change does"}},"required":["summary"]}"""),
                    async (args, ct) => McpToolResults.Text(await Dispatcher.UIThread.InvokeAsync(() => dev.Prepare(args.GetProperty("summary").GetString() ?? ""))),
                    "Development mode: publish the source as a new build, check it headlessly, and offer her \"Apply changes\" " +
                    "(restarts the app into it). Takes a few minutes. Returns the errors if it fails.");
        });
        // A build and check take minutes.
        return (McpSdkServerConfig)registry[ServerName] with { Timeout = 30 * 60_000 };
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Database work off the UI thread; errors go back to the model as tool errors.</summary>
    private static Task<McpToolResult> Run(Func<string> work) => Task.Run(() => Result(work));

    /// <summary>Reads of the in-memory book happen on the UI thread, which owns it.</summary>
    private static async Task<McpToolResult> OnUi(Func<string> work) => await Dispatcher.UIThread.InvokeAsync(() => Result(work));

    private static McpToolResult Result(Func<string> work)
    {
        try { return McpToolResults.Text(work()); }
        catch (Exception e) { return McpToolResults.Text("Error: " + e.Message, isError: true); }
    }

    private static string Costing(RecipeBook book, int recipeId)
    {
        if (book.Calculator().Calculate(recipeId) is not { } c) return $"No recipe {recipeId}.";
        var r = c.Recipe;
        var t = new StringBuilder($"{r.Name} — total {Num(c.TotalAmount)} {r.Unit}, ingredients ${c.ComponentsCost:0.00}, " +
                                  $"labour ${c.LabourCost:0.00} ({Num(r.LabourHours)} h × ${book.LabourRate:0.00}), total ${c.TotalCost:0.00}");
        if (c.CostPerServing is { } s) t.Append($", per serving ${s:0.00}");
        t.AppendLine().AppendLine($"line | amount ({r.Unit}) | cost | % of weight");
        foreach (var l in c.Lines.Where(l => !l.IsSeparator))
            t.AppendLine($"{l.Name} | {Num(l.AmountInRecipeUnit)} | ${l.Cost:0.00} | {l.Percent:0.#}%{(l.Warning is { } w ? " | ⚠ " + w : "")}");
        return t.ToString();
    }

    private static string Similar(RecipeBook book, JsonElement args)
    {
        var top = args.TryGetProperty("top", out var tp) && tp.TryGetInt32(out var n) ? n : 10;
        var engine = book.Similarity();
        SimilarityResult result;
        if (args.TryGetProperty("recipeId", out var rid) && rid.TryGetInt32(out var id))
        {
            if (book.FindRecipe(id) is not { } recipe) return $"No recipe {id}.";
            result = engine.Find(recipe, top);
        }
        else
        {
            var lines = args.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array
                ? ls.EnumerateArray().Select(l => new DraftLine(Int(l, "ingredientId"), Int(l, "subRecipeId"),
                    l.TryGetProperty("name", out var nm) ? nm.GetString() : null, l.GetProperty("grams").GetDouble())).ToList()
                : [];
            if (lines.Count == 0) return "Give recipeId or lines.";
            result = engine.Find(lines, top, draftName: args.TryGetProperty("name", out var name) ? name.GetString() : null);
        }
        var t = new StringBuilder($"coverage {result.Coverage:0.###}\nid | name | overlap | name similarity | only in draft | only in candidate\n");
        foreach (var m in result.Matches)
            t.AppendLine($"{m.Id} | {m.Name} | {m.Overlap:0.###} | {m.NameSimilarity:0.##} | {string.Join(", ", m.OnlyInDraft)} | {string.Join(", ", m.OnlyInCandidate)}");
        return t.ToString();

        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
