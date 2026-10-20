// JsonSerializer and JsonDocument: change a message into JSON text and back, and read one JSON line.
using System.Text.Json;
// Channel: a thread-safe queue. Many parts of the program can add items, and one part takes them out.
using System.Threading.Channels;

// This type is part of the Core project.
namespace Chatap.Core;

// One saved message. Id is the number of this saved message, local to this node. From is the node ID of the sender.
public sealed record SavedMessage(Guid Id, Guid From, string Text, DateTimeOffset Sent);

// One entry of the history: a saved message, and true if that message was never sent.
public sealed record HistoryEntry(SavedMessage Message, bool NotSent);

// The conversation history. There is one file for each peer: <data folder>/conversations/<peer id>.jsonl.
// The file has one JSON object on each line (the "JSON Lines" format).
// Lines are only added at the end (append-only). The store never changes or deletes a line.
// The design is the same as the LogWriter: handlers add lines at any moment, and one background task writes them.
public sealed class ConversationStore : IAsyncDisposable
{
    // The form of a method that adds text to the end of a file. The real program uses the disk. A test can give its own method.
    public delegate Task AppendText(string path, string text);

    // The JSON settings. "Web" writes the names with a small first letter, for example "text" and "sent".
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // The queue of lines that are not written yet. Each item has the peer (it selects the file) and the JSON line.
    // Unbounded: the queue has no size limit, so Save never waits.
    // SingleReader: only one task takes lines out. The channel can then work faster.
    readonly Channel<(Guid Peer, string Line)> _queue =
        Channel.CreateUnbounded<(Guid, string)>(new UnboundedChannelOptions { SingleReader = true });
    // The "conversations" folder inside the data folder.
    readonly string _folder;
    // The method that writes text to a file.
    readonly AppendText _append;
    // The background task that writes the lines (the consumer).
    readonly Task _writer;
    // True after a write fails. Then the store saves nothing more in this run.
    bool _failed;

    // append is for tests. The real program writes to the disk.
    public ConversationStore(string dataFolder, AppendText? append = null)
    {
        _folder = Path.Combine(dataFolder, "conversations");
        _append = append ?? AppendToFileAsync;
        // Start the consumer on a background thread. It runs until the queue is closed.
        _writer = Task.Run(WriteLoopAsync);
    }

    // Raised one time, on the writer task, when a write fails. Saving is off for the rest of this run.
    public event Action<Exception>? StoreFailed;

    // Producer: make the saved message, add its JSON line to the queue, and return immediately.
    // The result has the new Id. The handler keeps it, so that it can call MarkNotSent later for this message.
    public SavedMessage Save(Guid peer, Guid from, string text, DateTimeOffset sent)
    {
        var message = new SavedMessage(Guid.NewGuid(), from, text, sent);
        _queue.Writer.TryWrite((peer, JsonSerializer.Serialize(message, Options)));
        return message;
    }

    // Adds a second line that marks an earlier message as not sent, for example {"notSent":"<id>"}.
    // The first line stays as it is, because the file is append-only.
    public void MarkNotSent(Guid peer, Guid messageId) =>
        _queue.Writer.TryWrite((peer, JsonSerializer.Serialize(new NotSentMark(messageId), Options)));

    // Gives the history with one peer, in the order of the file. Broken lines (for example after a crash) are skipped.
    // Lines that are still in the queue are not in the file yet, so they are not in the result.
    public IReadOnlyList<HistoryEntry> ReadHistory(Guid peer)
    {
        string path = PathFor(peer);
        // No file: there is no history with this peer.
        if (!File.Exists(path))
            return [];

        var messages = new List<SavedMessage>();
        // The Ids of the messages that have a "not sent" mark.
        var notSent = new HashSet<Guid>();
        // Read the file one line at a time.
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                // A line with a "notSent" field is a mark. Any other line is a message.
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
        // Join the two parts: each message, and true if a mark for its Id was found.
        // A mark can come after its message in the file, so this is done after all the lines are read.
        return [.. messages.Select(m => new HistoryEntry(m, notSent.Contains(m.Id)))];
    }

    // Gives the peers that have a history file. A file with a name that is not a peer ID is ignored.
    public IReadOnlyList<Guid> KnownPeers() =>
        !Directory.Exists(_folder)
            ? []
            : [.. Directory.EnumerateFiles(_folder, "*.jsonl")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => Guid.TryParse(name, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)];

    // Graceful shutdown: accept no new lines, write all the lines that are still in the queue, then return.
    public async ValueTask DisposeAsync()
    {
        // Close the queue for new lines. The lines that are already in it stay.
        _queue.Writer.TryComplete();
        // Wait until the consumer has written the last line and stopped.
        await _writer;
    }

    // Consumer: the only code that writes conversation files. It writes one line at a time, in the order of the queue.
    async Task WriteLoopAsync()
    {
        // Take the next line. If the queue is empty, wait (without blocking a thread).
        // The loop stops when the queue is closed and empty.
        await foreach (var (peer, line) in _queue.Reader.ReadAllAsync())
        {
            // After a failure, take the lines out but do not write them. Thus the queue does not grow without limit.
            if (_failed)
                continue;
            try
            {
                await _append(PathFor(peer), line + "\n");
            }
            catch (Exception e)
            {
                // For example, the disk is full. Stop saving and tell the program one time.
                // The chat continues. A storage problem must not stop a conversation.
                _failed = true;
                StoreFailed?.Invoke(e);
            }
        }
    }

    // The file of one peer. The peer ID is a Guid, so the file name can contain only hex digits and dashes.
    // Thus a peer cannot select a different folder with a name like "../../something".
    string PathFor(Guid peer) => Path.Combine(_folder, $"{peer}.jsonl");

    // The real write: make the folder if it does not exist, then add the text to the end of the file.
    static async Task AppendToFileAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, text);
    }

    // The JSON form of a "not sent" mark: {"notSent":"<message id>"}.
    sealed record NotSentMark(Guid NotSent);
}
