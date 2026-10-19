// Channel: a thread-safe queue. Many parts of the program can add items, and one part takes them out.
using System.Threading.Channels;

// This type is part of the Core project.
namespace Chatap.Core;

// The event log. It records what the node does, for example "connected", "hello received", "connection closed".
// Many producers (the handlers) add lines at any moment, on different threads. One background task writes them.
// Thus only one part of the program writes the files, and two lines cannot be written into each other.
// Files: <data folder>/logs/<UTC date>/node.txt for lines without a peer,
//        <data folder>/logs/<UTC date>/<peer id>.txt for the lines of one conversation.
public sealed class LogWriter : IAsyncDisposable
{
    // The form of a method that adds text to the end of a file. The real program uses the disk. A test can give its own method.
    public delegate Task AppendText(string path, string text);

    // The queue of lines that are not written yet. Each line has its time, the peer (or null for the node), and its text.
    // Unbounded: the queue has no size limit, so Log never waits.
    // SingleReader: only one task takes lines out. The channel can then work faster.
    readonly Channel<(DateTimeOffset Time, Guid? PeerId, string Text)> _queue =
        Channel.CreateUnbounded<(DateTimeOffset, Guid?, string)>(new UnboundedChannelOptions { SingleReader = true });
    // The "logs" folder inside the data folder.
    readonly string _folder;
    // The method that writes text to a file.
    readonly AppendText _append;
    // The clock. It gives the time for each line.
    readonly Func<DateTimeOffset> _now;
    // The background task that writes the lines (the consumer).
    readonly Task _writer;
    // True after a write fails. Then the log writes nothing more in this run.
    bool _failed;

    // append and now are for tests. The real program uses the disk and the real clock.
    public LogWriter(string dataFolder, AppendText? append = null, Func<DateTimeOffset>? now = null)
    {
        _folder = Path.Combine(dataFolder, "logs");
        _append = append ?? AppendToFileAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        // Start the consumer on a background thread. It runs until the queue is closed.
        _writer = Task.Run(WriteLoopAsync);
    }

    // Raised one time, on the writer task, when a write fails. The log is off for the rest of this run.
    public event Action<Exception>? LogFailed;

    // Producers: add a line to the queue and return immediately. This code does not touch a file.
    // The time is taken now, when the event occurs, not later when the line is written.
    public void Log(string text) => _queue.Writer.TryWrite((_now(), null, text));
    public void Log(Guid peerId, string text) => _queue.Writer.TryWrite((_now(), peerId, text));

    // Graceful shutdown: accept no new lines, write all the lines that are still in the queue, then return.
    public async ValueTask DisposeAsync()
    {
        // Close the queue for new lines. The lines that are already in it stay.
        _queue.Writer.TryComplete();
        // Wait until the consumer has written the last line and stopped.
        await _writer;
    }

    // Consumer: the only code that writes log files. It writes one line at a time, in the order of the queue.
    async Task WriteLoopAsync()
    {
        // Take the next line. If the queue is empty, wait (without blocking a thread).
        // The loop stops when the queue is closed and empty.
        await foreach (var (time, peerId, text) in _queue.Reader.ReadAllAsync())
        {
            // After a failure, take the lines out but do not write them. Thus the queue does not grow without limit.
            if (_failed)
                continue;

            // Select the file: the folder of the UTC date, then node.txt or the file of the peer.
            DateTime utc = time.UtcDateTime;
            string file = peerId is Guid id ? $"{id}.txt" : "node.txt";
            string path = Path.Combine(_folder, utc.ToString("yyyy-MM-dd"), file);
            try
            {
                // One line: the UTC time, two spaces, the text, and a line end.
                await _append(path, $"{utc:yyyy-MM-ddTHH:mm:ssZ}  {text}\n");
            }
            catch (Exception e)
            {
                // For example, the disk is full. Stop the log and tell the program one time.
                // The chat continues. A log problem must not stop a conversation.
                _failed = true;
                LogFailed?.Invoke(e);
            }
        }
    }

    // The real write: make the folder if it does not exist, then add the text to the end of the file.
    static async Task AppendToFileAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, text);
    }
}
