using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvetaRecipes.App.Services;

/// <summary>
/// One line of a saved conversation. Kinds: <c>user</c> (Text as typed, Context = what was open), <c>assistant</c>,
/// <c>tool</c> (Id, Tool, Input), <c>result</c> (Id, Text, IsError), <c>error</c>, <c>session</c> (SessionId of the Claude
/// Code session, for resuming).
/// </summary>
public sealed record TranscriptEntry(
    string Kind,
    DateTime At,
    string? Text = null,
    string? Context = null,
    string? Id = null,
    string? Tool = null,
    JsonElement? Input = null,
    bool? IsError = null,
    string? SessionId = null);

/// <summary>
/// Every assistant conversation, kept as JSON lines in <c>assistant/</c> next to the database (one file per chat,
/// appended as it happens, never deleted).
/// </summary>
public sealed class Transcript
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Transcript(string path) => Path = path;

    public string Path { get; }

    public static string Folder(string dbPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath))!, "assistant");

    public static Transcript New(string folder)
    {
        Directory.CreateDirectory(folder);
        return new Transcript(System.IO.Path.Combine(folder, $"chat-{DateTime.Now:yyyy-MM-dd-HHmmss}.jsonl"));
    }

    /// <summary>The most recent conversation, if any.</summary>
    public static Transcript? Latest(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "chat-*.jsonl").Order(StringComparer.Ordinal).LastOrDefault() is { } p ? new Transcript(p) : null
            : null;

    public void Append(TranscriptEntry entry) =>
        File.AppendAllText(Path, JsonSerializer.Serialize(entry, Json) + "\n");

    public IEnumerable<TranscriptEntry> Read()
    {
        if (!File.Exists(Path)) yield break;
        foreach (var line in File.ReadLines(Path))
        {
            TranscriptEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<TranscriptEntry>(line, Json); }
            catch (JsonException) { /* a torn last line from a crash */ }
            if (entry is not null) yield return entry;
        }
    }
}
