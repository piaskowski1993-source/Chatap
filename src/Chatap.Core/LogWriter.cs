using System.Threading.Channels;

namespace Chatap.Core;

// Event log. Many producers (handlers) add lines at any moment; one background task writes them.
// Files: <data folder>/logs/<UTC date>/node.txt for lines without a peer,
//        <data folder>/logs/<UTC date>/<peer id>.txt for the lines of one conversation.
public sealed class LogWriter : IAsyncDisposable
{
    public delegate Task AppendText(string path, string text);

    readonly Channel<(DateTimeOffset Time, Guid? PeerId, string Text)> _queue =
        Channel.CreateUnbounded<(DateTimeOffset, Guid?, string)>(new UnboundedChannelOptions { SingleReader = true });
    readonly string _folder;
    readonly AppendText _append;
    readonly Func<DateTimeOffset> _now;
    readonly Task _writer;
    bool _failed;

    // append and now are for tests; the real program uses the disk and the real clock.
    public LogWriter(string dataFolder, AppendText? append = null, Func<DateTimeOffset>? now = null)
    {
        _folder = Path.Combine(dataFolder, "logs");
        _append = append ?? AppendToFileAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _writer = Task.Run(WriteLoopAsync);
    }

    // Raised once, on the writer task, when a write fails. The log is off for the rest of this run.
    public event Action<Exception>? LogFailed;

    // Producers: add a line and return immediately. Nothing here touches a file.
    public void Log(string text) => _queue.Writer.TryWrite((_now(), null, text));
    public void Log(Guid peerId, string text) => _queue.Writer.TryWrite((_now(), peerId, text));

    // Graceful shutdown: accept no new lines, write all lines still in the queue, then return.
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }

    // Consumer: the only code that writes log files, one line at a time.
    async Task WriteLoopAsync()
    {
        await foreach (var (time, peerId, text) in _queue.Reader.ReadAllAsync())
        {
            if (_failed)
                continue; // keep emptying the queue so that it does not grow

            DateTime utc = time.UtcDateTime;
            string file = peerId is Guid id ? $"{id}.txt" : "node.txt";
            string path = Path.Combine(_folder, utc.ToString("yyyy-MM-dd"), file);
            try
            {
                await _append(path, $"{utc:yyyy-MM-ddTHH:mm:ssZ}  {text}\n");
            }
            catch (Exception e)
            {
                _failed = true;
                LogFailed?.Invoke(e);
            }
        }
    }

    static async Task AppendToFileAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, text);
    }
}
