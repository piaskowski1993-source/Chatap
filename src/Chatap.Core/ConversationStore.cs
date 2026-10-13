using System.Text.Json;
using System.Threading.Channels;

namespace Chatap.Core;

// One saved message. Id is local to this node; From is the node ID of the sender.
public sealed record SavedMessage(Guid Id, Guid From, string Text, DateTimeOffset Sent);

// One entry of the history: a saved message, and whether it was never sent.
public sealed record HistoryEntry(SavedMessage Message, bool NotSent);

// Conversation history: <data folder>/conversations/<peer id>.jsonl, one JSON object per line.
// Lines are only appended. Handlers add lines at any moment; one background task writes them.
public sealed class ConversationStore : IAsyncDisposable
{
    public delegate Task AppendText(string path, string text);

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    readonly Channel<(Guid Peer, string Line)> _queue =
        Channel.CreateUnbounded<(Guid, string)>(new UnboundedChannelOptions { SingleReader = true });
    readonly string _folder;
    readonly AppendText _append;
    readonly Task _writer;
    bool _failed;

    // append is for tests; the real program writes to the disk.
    public ConversationStore(string dataFolder, AppendText? append = null)
    {
        _folder = Path.Combine(dataFolder, "conversations");
        _append = append ?? AppendToFileAsync;
        _writer = Task.Run(WriteLoopAsync);
    }

    // Raised once, on the writer task, when a write fails. Saving is off for the rest of this run.
    public event Action<Exception>? StoreFailed;

    // Producer: adds the line to the queue and returns at once. The result carries the Id for MarkNotSent.
    public SavedMessage Save(Guid peer, Guid from, string text, DateTimeOffset sent)
    {
        var message = new SavedMessage(Guid.NewGuid(), from, text, sent);
        _queue.Writer.TryWrite((peer, JsonSerializer.Serialize(message, Options)));
        return message;
    }

    // Adds a second line that marks an earlier message as not sent. The first line stays.
    public void MarkNotSent(Guid peer, Guid messageId) =>
        _queue.Writer.TryWrite((peer, JsonSerializer.Serialize(new NotSentMark(messageId), Options)));

    // The history with one peer, in file order. Broken lines (for example after a crash) are skipped.
    // Lines still in the queue are not in the file yet.
    public IReadOnlyList<HistoryEntry> ReadHistory(Guid peer)
    {
        string path = PathFor(peer);
        if (!File.Exists(path))
            return [];

        var messages = new List<SavedMessage>();
        var notSent = new HashSet<Guid>();
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("notSent", out var id))
                    notSent.Add(id.GetGuid());
                else if (json.RootElement.Deserialize<SavedMessage>(Options) is { } message)
                    messages.Add(message);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
            {
                // A broken line: skip it and read the rest.
            }
        }
        return [.. messages.Select(m => new HistoryEntry(m, notSent.Contains(m.Id)))];
    }

    // The peers that have a history file.
    public IReadOnlyList<Guid> KnownPeers() =>
        !Directory.Exists(_folder)
            ? []
            : [.. Directory.EnumerateFiles(_folder, "*.jsonl")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => Guid.TryParse(name, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)];

    // Graceful shutdown: accept no new lines, write all lines still in the queue, then return.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }

    // Consumer: the only code that writes conversation files, one line at a time.
    async Task WriteLoopAsync()
    {
        await foreach (var (peer, line) in _queue.Reader.ReadAllAsync())
        {
            if (_failed)
                continue;
            try
            {
                await _append(PathFor(peer), line + "\n");
            }
            catch (Exception e)
            {
                _failed = true;
                StoreFailed?.Invoke(e);
            }
        }
    }

    // The peer ID is a Guid, so the file name can only contain hex digits and dashes.
    string PathFor(Guid peer) => Path.Combine(_folder, $"{peer}.jsonl");

    static async Task AppendToFileAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, text);
    }

    sealed record NotSentMark(Guid NotSent);
}
