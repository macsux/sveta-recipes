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
/// the app reloads after every write (<paramref name="dataChanged"/>, raised on the UI thread).
/// </summary>
public static class Assistant
{
    public const string ServerName = "recipes";

    /// <param name="resume">Claude Code session to continue (the reopened conversation), or null for a new one.</param>
    public static ClaudeAgentOptions Options(RecipeBook book, Action dataChanged, string workFolder, string? resume) => new()
    {
        Model = "sonnet",
        SystemPrompt = SystemPrompt(),
        // Claude Code's own tools (web search/fetch, files, shell) plus the database tools below, all without asking.
        PermissionMode = PermissionMode.BypassPermissions,
        McpServers = McpServersConfig.FromServers(new Dictionary<string, object> { [ServerName] = Tools(book, dataChanged) }),
        StrictMcpConfig = true,
        // Nothing from the PC's Claude Code setup (CLAUDE.md, settings, skills) leaks in.
        SettingSources = [],
        Cwd = workFolder,
        Resume = resume,
        IncludePartialMessages = true,
    };

    public static string SystemPrompt() => $"""
        You are the assistant inside "Sveta's Recipes", the Windows app Sveta uses to cost recipes for her home pastry
        business: recipes and sub-recipes, ingredients and prices, shopping lists, invoices, customers and suppliers.
        She is not technical: answer plainly and briefly, and don't show SQL or ids unless she asks. Money is CAD.
        Each message starts with what she has open in the app.

        You have the app's SQLite database:
        - sql runs any SQL, reads and writes. Before the first write of a request call backup, once. The app reloads
          itself after each write. Write rows as the app would: Position from 0, CreatedAt/UpdatedAt =
          datetime('now','localtime'), units from Units.Code. Don't touch data she didn't ask about.
        - get_costing gives a recipe's live cost. Costs are never stored; use it for anything about cost.
        - find_similar finds recipes with a similar composition (sub-recipes expanded, compared by weight share).
        Change the database only through sql, never the file directly. You also have Claude Code's usual tools: web
        search and fetch (a recipe from a link, an ingredient, a technique), and files and a shell on her PC.

        Importing a recipe (pasted, or from a link: fetch it): map every line to an existing ingredient or sub-recipe (names differ: "heavy cream" may be
        "Cream 35%"); convert amounts to grams yourself; run find_similar on the whole recipe and on each component that
        could be one of her sub-recipes; then decide. A duplicate: say which recipe and don't import. A variant: import
        it, say what differs and write that in Description. Reuse her sub-recipes where they match. Create missing
        ingredients with PackPrice 0 and list them so she can price them. Tag what you import "Imported".

        Database:
        {DbSchema.Describe()}
        """;

    private static McpSdkServerConfig Tools(RecipeBook book, Action dataChanged)
    {
        var registry = McpServers.Sdk(ServerName, b => b
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
                "coverage = share of the draft's weight made of her ingredients; when low, check candidates by name too."));
        return (McpSdkServerConfig)registry[ServerName];
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
