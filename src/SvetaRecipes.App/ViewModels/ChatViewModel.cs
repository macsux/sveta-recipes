using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Claude.AgentSdk;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

public abstract partial class ChatItem : ObservableObject
{
    /// <summary>False hides it (tool calls while "Tool calls" is off).</summary>
    [ObservableProperty] private bool _isShown = true;
}

public sealed class UserChatItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed partial class AssistantChatItem : ChatItem
{
    [ObservableProperty] private string _text = "";
}

/// <summary>A tool call: a plain label for her ("Looked in your data"); expanded, the complete input and result.</summary>
public sealed partial class ToolChatItem(string id, string tool, string label, string input) : ChatItem
{
    public string Id { get; } = id;
    /// <summary>The tool's own name (sql, find_similar, …).</summary>
    public string Tool { get; } = tool;
    public string Label { get; } = label;
    public string Input { get; } = input;
    public bool HasInput => Input.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResult))] private string? _result;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _isExpanded;
    public bool HasResult => Result is not null;
}

public sealed class ErrorChatItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

/// <summary>
/// The assistant panel: one conversation with Claude (a Claude Code CLI process, started on the first message and kept
/// for follow-ups). Replies stream in. Every conversation is saved (<see cref="Transcript"/>); on start the last one is
/// shown again and its Claude session resumed, until "New chat".
/// </summary>
public sealed partial class ChatViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly string _folder;
    private Transcript? _transcript;
    private string? _sessionId;
    private ClaudeSDKClient? _client;
    private AssistantChatItem? _streaming;
    private bool _streamedText;

    public ChatViewModel(MainViewModel main)
    {
        _main = main;
        _folder = Transcript.Folder(main.Book.DbPath);
        _showToolCalls = main.Book.GetSetting(SettingKeys.AssistantShowTools) != "0";
        if (Transcript.Latest(_folder) is { } last)
        {
            _transcript = last;
            foreach (var entry in last.Read()) Apply(entry, live: false);
        }
    }

    public ObservableCollection<ChatItem> Items { get; } = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isBusy;
    [ObservableProperty] private bool _showToolCalls;

    public bool IsEmpty => Items.Count == 0;

    partial void OnShowToolCallsChanged(bool value)
    {
        foreach (var t in Items.OfType<ToolChatItem>()) t.IsShown = value;
        _main.Book.SetSetting(SettingKeys.AssistantShowTools, value ? "1" : "0");
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Draft.Trim();
        var context = _main.DescribeView();
        Draft = "";
        Record(new TranscriptEntry("user", DateTime.Now, Text: text, Context: context));
        IsBusy = true;
        try
        {
            _client ??= await Connect();
            await _client.QueryAsync($"[Open in the app: {context}]\n\n{text}");
            await foreach (var message in _client.ReceiveResponseAsync())
                Handle(message);
        }
        catch (Exception e)
        {
            Record(new TranscriptEntry("error", DateTime.Now, Text: Explain(e)));
            await Reset();
        }
        finally
        {
            _streaming = null;
            IsBusy = false;
        }
    }

    /// <summary>Starts Claude Code, continuing the shown conversation's session; a new one if that can't be resumed.</summary>
    private async Task<ClaudeSDKClient> Connect()
    {
        async Task<ClaudeSDKClient> Start(string? resume)
        {
            var client = new ClaudeSDKClient(Assistant.Options(_main.Book, _main.DataChangedOutside, _folder, resume));
            await Task.Run(() => client.ConnectAsync());
            return client;
        }

        Directory.CreateDirectory(_folder);
        if (_sessionId is null) return await Start(null);
        try { return await Start(_sessionId); }
        catch (Exception e) when (e is not CliNotFoundException)
        {
            _sessionId = null;
            return await Start(null);
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        if (_client is null) return;
        try { await _client.InterruptAsync(); }
        catch { /* already finished */ }
    }

    [RelayCommand]
    private async Task NewChat()
    {
        await Reset();
        _transcript = null;
        _sessionId = null;
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task Reset()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
    }

    // ------------------------------------------------------------------ messages → transcript → items

    private void Handle(Message message)
    {
        switch (message)
        {
            case StreamEvent { Event: var e }:
                OnStreamEvent(e);
                break;
            case SystemMessage { Subtype: "init", Data: var d } when d.ValueKind == JsonValueKind.Object
                                                                   && d.TryGetProperty("session_id", out var sid):
                SetSession(sid.GetString());
                break;
            case AssistantMessage a:
                foreach (var block in a.Content)
                {
                    if (block is TextBlock t && t.Text.Trim().Length > 0) Record(new TranscriptEntry("assistant", DateTime.Now, Text: t.Text));
                    else if (block is ToolUseBlock u) Record(new TranscriptEntry("tool", DateTime.Now, Id: u.Id, Tool: u.Name, Input: u.Input.Clone()));
                }
                _streaming = null;
                _streamedText = false;
                break;
            case UserMessage u when u.Content.ValueKind == JsonValueKind.Array:
                foreach (var block in u.Content.EnumerateArray())
                    if (Str(block, "type") == "tool_result")
                        Record(new TranscriptEntry("result", DateTime.Now, Id: Str(block, "tool_use_id"), Text: ResultText(block),
                            IsError: block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True));
                break;
            case ResultMessage r:
                SetSession(r.SessionId);
                if (r.IsError)
                    Record(new TranscriptEntry("error", DateTime.Now, Text: r.Errors is { Count: > 0 } errs ? string.Join("\n", errs) : r.Result ?? "Something went wrong."));
                break;
        }
    }

    private void SetSession(string? id)
    {
        if (id is null || id == _sessionId) return;
        Record(new TranscriptEntry("session", DateTime.Now, SessionId: id));
    }

    private void OnStreamEvent(JsonElement e)
    {
        var type = Str(e, "type");
        if (type == "content_block_start" && e.TryGetProperty("content_block", out var block) && Str(block, "type") == "text")
        {
            _streaming = new AssistantChatItem();
            _streamedText = true;
            Add(_streaming);
        }
        else if (type == "content_block_delta" && _streaming is not null && e.TryGetProperty("delta", out var d)
                 && d.TryGetProperty("text", out var text))
        {
            _streaming.Text += text.GetString();
        }
    }

    /// <summary>Saves an entry and shows it.</summary>
    private void Record(TranscriptEntry entry)
    {
        _transcript ??= Transcript.New(_folder);
        try { _transcript.Append(entry); }
        catch (IOException) { /* the chat goes on; only the saved copy misses this line */ }
        Apply(entry, live: true);
    }

    /// <summary>Turns a transcript entry into what the panel shows (live, or replaying a saved chat).</summary>
    private void Apply(TranscriptEntry entry, bool live)
    {
        switch (entry.Kind)
        {
            case "user":
                Add(new UserChatItem(entry.Text ?? ""));
                break;
            case "assistant":
                // Live text was already shown as it streamed in.
                if (!(live && _streamedText)) Add(new AssistantChatItem { Text = entry.Text ?? "" });
                break;
            case "tool":
                Add(Describe(entry.Id ?? "", entry.Tool ?? "", entry.Input));
                break;
            case "result":
                if (Items.OfType<ToolChatItem>().LastOrDefault(i => i.Id == entry.Id) is { } tool)
                {
                    tool.Result = entry.Text ?? "";
                    tool.Failed = entry.IsError == true;
                }
                break;
            case "error":
                Add(new ErrorChatItem(entry.Text ?? ""));
                break;
            case "session":
                _sessionId = entry.SessionId;
                break;
        }
    }

    private ToolChatItem Describe(string id, string name, JsonElement? input)
    {
        var tool = name.StartsWith($"mcp__{Assistant.ServerName}__") ? name[$"mcp__{Assistant.ServerName}__".Length..] : name;
        var query = input is { ValueKind: JsonValueKind.Object } i && i.TryGetProperty("query", out var q) ? q.GetString() ?? "" : null;
        var label = tool switch
        {
            "sql" => IsWrite(query ?? "") ? "Changed your data" : "Looked in your data",
            "backup" => "Made a backup",
            "get_costing" => "Worked out the cost",
            "find_similar" => "Compared with your recipes",
            _ => tool,
        };
        var text = query?.Trim() ?? (input is { ValueKind: JsonValueKind.Object } o && o.EnumerateObject().Any()
            ? JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            : "");
        return new ToolChatItem(id, tool, label, text) { IsShown = ShowToolCalls };
    }

    /// <summary>A tool result's text: a string, or the text parts of a content array.</summary>
    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return c.ToString();
        var text = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (Str(part, "text") is { } t) text.AppendLine(t);
        return text.ToString().TrimEnd();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsWrite(string sql) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql, @"\b(insert|update|delete|replace|create|drop|alter)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Explain(Exception e) => e switch
    {
        CliNotFoundException => "The assistant needs Claude Code, which isn't installed on this computer.",
        _ => "The assistant stopped: " + e.Message,
    };

    private void Add(ChatItem item)
    {
        Items.Add(item);
        if (Items.Count == 1) OnPropertyChanged(nameof(IsEmpty));
    }

    public async ValueTask DisposeAsync() => await Reset();
}
