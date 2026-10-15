using System.Net.Sockets;

namespace Chatap.Core;

public enum HandlerState { WaitingForHello, Ready, Closed }

public enum CloseReason { PeerClosed, HelloTimeout, VersionMismatch, ProtocolError, Replaced, ConnectionLost, LocalClose }

// TCP keepalive: after Idle without traffic the OS sends a probe, then one every Interval;
// after Probes unanswered probes the connection counts as lost (about 90 s with the defaults).
public sealed record KeepAliveSettings(TimeSpan Idle, TimeSpan Interval, int Probes)
{
    public static readonly KeepAliveSettings Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 3);
}

// Owns one conversation: its socket, the hello handshake, the receive loop and sending.
// States: WaitingForHello → Ready → Closed (or WaitingForHello → Closed when the handshake fails).
public sealed class ConnectionHandler
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly Guid _myId;
    readonly LogWriter _log;
    readonly ConversationStore _store;
    readonly TimeSpan _helloTimeout;
    readonly Action<ConnectionHandler> _onHello;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly object _stateLock = new();
    readonly List<(string Text, DateTimeOffset Clicked)> _waiting = [];
    readonly CancellationTokenSource _closing = new();

    // How long a graceful close waits for the peer's FIN before the socket closes anyway.
    static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(1);

    internal ConnectionHandler(TcpClient client, bool isOutgoing, Guid myId, LogWriter log, ConversationStore store,
        TimeSpan helloTimeout, KeepAliveSettings keepAlive, Action<ConnectionHandler> onHello)
    {
        EnableKeepAlive(client.Client, keepAlive);
        _client = client;
        _stream = client.GetStream();
        _myId = myId;
        _log = log;
        _store = store;
        _helloTimeout = helloTimeout;
        _onHello = onHello;
        IsOutgoing = isOutgoing;
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    // True when this node started the connection (the user typed the address).
    public bool IsOutgoing { get; }
    public string RemoteEndPoint { get; }
    public Guid? PeerId { get; private set; }
    public int? PeerVersion { get; private set; }
    public HandlerState State { get; private set; } = HandlerState.WaitingForHello;
    public CloseReason? ClosedBecause { get; private set; }
    public string? ClosedDetail { get; private set; }

    // All events are raised on a background thread.
    public event Action<ConnectionHandler>? Ready;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<CloseReason>? Disconnected;
    public event Action<IReadOnlyList<string>>? NotSent;

    // Ready: the text is saved at once (the "click"), then sent.
    // Before Ready it waits in a queue, keeps its click time, and is saved and sent right after the peer's hello.
    // Returns false when the text was not sent: the conversation is closed, or the text is too long.
    public async Task<bool> SendAsync(string text)
    {
        // A text that is too long is never saved or sent. The connection stays open: the peer did nothing wrong.
        if (!Codec.FitsInOneMessage(text))
        {
            LogLine($"not sent: message of {Codec.ChatMessageSize(text)} bytes is above the limit of {Protocol.MaxMessageBytes}");
            NotSent?.Invoke([text]);
            return false;
        }

        var clicked = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            if (State == HandlerState.Closed)
                return false;
            if (State == HandlerState.WaitingForHello)
            {
                _waiting.Add((text, clicked));
                return true;
            }
        }

        var saved = _store.Save(PeerId!.Value, _myId, text, clicked);
        await _writeLock.WaitAsync();
        try
        {
            await Codec.WriteAsync(_stream, new ChatMessage(text, clicked));
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _store.MarkNotSent(PeerId.Value, saved.Id);
            NotSent?.Invoke([text]);
            Close(CloseReason.ConnectionLost, Describe(e));
            return false;
        }
        catch (ProtocolException e)
        {
            // Safety net: our own message broke the protocol. Do not crash, and keep the connection open.
            _store.MarkNotSent(PeerId.Value, saved.Id);
            LogLine($"not sent: {e.Message}");
            NotSent?.Invoke([text]);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // A log line for this conversation: in the peer's file when the peer is known, else in node.txt.
    void LogLine(string text)
    {
        if (PeerId is Guid peer)
            _log.Log(peer, text);
        else
            _log.Log($"connection {RemoteEndPoint}: {text}");
    }

    public void Close() => Close(CloseReason.LocalClose, "closed by this node");

    internal void Close(CloseReason reason, string detail)
    {
        List<(string Text, DateTimeOffset Clicked)> notSent;
        lock (_stateLock)
        {
            if (State == HandlerState.Closed)
                return;
            State = HandlerState.Closed;
            notSent = [.. _waiting];
            _waiting.Clear();
        }

        ClosedBecause = reason;
        ClosedDetail = detail;
        _closing.Cancel();
        if (reason == CloseReason.PeerClosed)
            _client.Dispose(); // the peer's FIN has already arrived
        else
            BeginGracefulClose();

        if (PeerId is Guid peer)
            _log.Log(peer, $"closed: {reason} ({detail})");
        else
            _log.Log($"connection {RemoteEndPoint} closed: {reason} ({detail})");

        if (notSent.Count > 0)
            ReportNotSent(notSent);
        Disconnected?.Invoke(reason);
    }

    // Texts from the queue that will never be sent. With a known peer they are saved and marked
    // as not sent; without a hello there is no conversation file, so only the node log keeps a note.
    void ReportNotSent(List<(string Text, DateTimeOffset Clicked)> texts)
    {
        if (texts.Count == 0)
            return;
        if (PeerId is Guid peer)
        {
            foreach (var (text, clicked) in texts)
                _store.MarkNotSent(peer, _store.Save(peer, _myId, text, clicked).Id);
        }
        else
        {
            _log.Log($"connection {RemoteEndPoint}: {texts.Count} message(s) not sent; no hello, so no conversation file");
        }
        NotSent?.Invoke([.. texts.Select(t => t.Text)]);
    }

    // Started by Node for each new connection. Runs until the conversation closes.
    internal async Task RunAsync()
    {
        try
        {
            await WriteLockedAsync(new HelloMessage(_myId, Protocol.Version));

            if (await ReadHelloAsync() is not HelloMessage hello)
                return;
            PeerVersion = hello.Version;
            if (hello.Version != Protocol.Version)
            {
                Close(CloseReason.VersionMismatch, $"peer uses version {hello.Version}, this node uses version {Protocol.Version}");
                return;
            }

            PeerId = hello.Id;
            _onHello(this);
            await BecomeReadyAsync();
            if (State == HandlerState.Closed)
                return;

            while (true)
            {
                var message = await Codec.ReadAsync(_stream, _closing.Token);
                if (message is null)
                {
                    Close(CloseReason.PeerClosed, "the peer closed the connection");
                    return;
                }
                if (message is not ChatMessage chat)
                {
                    Close(CloseReason.ProtocolError, "a second hello after the handshake");
                    return;
                }
                _store.Save(PeerId.Value, PeerId.Value, chat.Text, chat.Sent);
                MessageReceived?.Invoke(chat);
            }
        }
        catch (ProtocolException e)
        {
            Close(CloseReason.ProtocolError, e.Message);
        }
        catch (EndOfStreamException)
        {
            Close(CloseReason.ConnectionLost, "the stream ended in the middle of a message");
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            Close(CloseReason.ConnectionLost, Describe(e));
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // This node closed the conversation; Close has already run.
        }
        finally
        {
            await DrainAsync();
        }
    }

    // Half-close: send our FIN now. DrainAsync discards what still arrives until the peer's FIN.
    // After at most DrainTime the socket closes anyway.
    // A plain close with unread bytes in the receive buffer would send RST instead of FIN.
    void BeginGracefulClose()
    {
        try
        {
            _client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The connection is already broken.
        }
        _ = Task.Delay(DrainTime).ContinueWith(_ => _client.Dispose());
    }

    // Reads and discards bytes until the peer closes or the socket is closed.
    async Task DrainAsync()
    {
        byte[] discard = new byte[4096];
        try
        {
            while (await _stream.ReadAsync(discard) > 0) { }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }
        _client.Dispose();
    }

    static string Describe(Exception e) =>
        (e as SocketException ?? e.InnerException as SocketException)?.SocketErrorCode switch
        {
            SocketError.ConnectionReset => "connection reset (RST)",
            SocketError.TimedOut => "no answer to keepalive",
            _ => e.Message,
        };

    static void EnableKeepAlive(Socket socket, KeepAliveSettings keepAlive)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)keepAlive.Idle.TotalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)keepAlive.Interval.TotalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, keepAlive.Probes);
    }

    // For tests: the keepalive values as the OS reports them for this socket.
    internal (bool On, KeepAliveSettings Values) ReadKeepAlive()
    {
        Socket socket = _client.Client;
        int Get(SocketOptionLevel level, SocketOptionName name) => (int)socket.GetSocketOption(level, name)!;
        return (Get(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) != 0,
            new KeepAliveSettings(
                TimeSpan.FromSeconds(Get(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)),
                TimeSpan.FromSeconds(Get(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)),
                Get(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)));
    }

    // Waits for the peer's hello, at most _helloTimeout. Closes the conversation and returns null on failure.
    async Task<HelloMessage?> ReadHelloAsync()
    {
        using var helloWait = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        helloWait.CancelAfter(_helloTimeout);

        Message? first;
        try
        {
            first = await Codec.ReadAsync(_stream, helloWait.Token);
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            Close(CloseReason.HelloTimeout, $"no hello within {_helloTimeout.TotalSeconds} s");
            return null;
        }

        if (first is null)
        {
            Close(CloseReason.PeerClosed, "the peer closed the connection before its hello");
            return null;
        }
        if (first is not HelloMessage hello)
        {
            Close(CloseReason.ProtocolError, "the first message was not a hello");
            return null;
        }
        return hello;
    }

    // Holds the write lock while the waiting texts are sent, so that a new SendAsync cannot overtake them.
    async Task BecomeReadyAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            List<(string Text, DateTimeOffset Clicked)> early;
            lock (_stateLock)
            {
                if (State == HandlerState.Closed)
                    return;
                State = HandlerState.Ready;
                early = [.. _waiting];
                _waiting.Clear();
            }

            _log.Log(PeerId!.Value, $"hello from {RemoteEndPoint}: ready");
            Ready?.Invoke(this);

            for (int i = 0; i < early.Count; i++)
            {
                var (text, clicked) = early[i];
                var saved = _store.Save(PeerId.Value, _myId, text, clicked);
                try
                {
                    await Codec.WriteAsync(_stream, new ChatMessage(text, clicked));
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // This text and all later ones in the queue were not sent.
                    _store.MarkNotSent(PeerId.Value, saved.Id);
                    ReportNotSent(early[(i + 1)..]);
                    NotSent?.Invoke([text]);
                    Close(CloseReason.ConnectionLost, Describe(e));
                    return;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task WriteLockedAsync(Message message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await Codec.WriteAsync(_stream, message);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
