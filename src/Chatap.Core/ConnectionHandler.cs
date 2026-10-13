using System.Net.Sockets;

namespace Chatap.Core;

public enum HandlerState { WaitingForHello, Ready, Closed }

public enum CloseReason { PeerClosed, HelloTimeout, VersionMismatch, ProtocolError, Replaced, ConnectionLost, LocalClose }

// Owns one conversation: its socket, the hello handshake, the receive loop and sending.
// States: WaitingForHello → Ready → Closed (or WaitingForHello → Closed when the handshake fails).
public sealed class ConnectionHandler
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly Guid _myId;
    readonly LogWriter _log;
    readonly TimeSpan _helloTimeout;
    readonly Action<ConnectionHandler> _onHello;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly object _stateLock = new();
    readonly List<string> _waiting = [];
    readonly CancellationTokenSource _closing = new();

    internal ConnectionHandler(TcpClient client, bool isOutgoing, Guid myId, LogWriter log,
        TimeSpan helloTimeout, Action<ConnectionHandler> onHello)
    {
        _client = client;
        _stream = client.GetStream();
        _myId = myId;
        _log = log;
        _helloTimeout = helloTimeout;
        _onHello = onHello;
        IsOutgoing = isOutgoing;
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    // True when this node started the connection (the user typed the address).
    public bool IsOutgoing { get; }
    public string RemoteEndPoint { get; }
    public Guid? PeerId { get; private set; }
    public HandlerState State { get; private set; } = HandlerState.WaitingForHello;
    public CloseReason? ClosedBecause { get; private set; }

    // All events are raised on a background thread.
    public event Action<ConnectionHandler>? Ready;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<CloseReason>? Disconnected;
    public event Action<IReadOnlyList<string>>? NotSent;

    // Before Ready the text waits in a queue; it is sent right after the peer's hello.
    // Returns false when the conversation is closed and the text was not sent.
    public async Task<bool> SendAsync(string text)
    {
        lock (_stateLock)
        {
            if (State == HandlerState.Closed)
                return false;
            if (State == HandlerState.WaitingForHello)
            {
                _waiting.Add(text);
                return true;
            }
        }

        await _writeLock.WaitAsync();
        try
        {
            await Codec.WriteAsync(_stream, new ChatMessage(text, DateTimeOffset.UtcNow));
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Close(CloseReason.ConnectionLost, e.Message);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close() => Close(CloseReason.LocalClose, "closed by this node");

    internal void Close(CloseReason reason, string detail)
    {
        List<string> notSent;
        lock (_stateLock)
        {
            if (State == HandlerState.Closed)
                return;
            State = HandlerState.Closed;
            notSent = [.. _waiting];
            _waiting.Clear();
        }

        ClosedBecause = reason;
        _closing.Cancel();
        _client.Dispose();

        if (PeerId is Guid peer)
            _log.Log(peer, $"closed: {reason} ({detail})");
        else
            _log.Log($"connection {RemoteEndPoint} closed: {reason} ({detail})");

        if (notSent.Count > 0)
            NotSent?.Invoke(notSent);
        Disconnected?.Invoke(reason);
    }

    // Started by Node for each new connection. Runs until the conversation closes.
    internal async Task RunAsync()
    {
        try
        {
            await WriteLockedAsync(new HelloMessage(_myId, Protocol.Version));

            if (await ReadHelloAsync() is not HelloMessage hello)
                return;
            if (hello.Version != Protocol.Version)
            {
                Close(CloseReason.VersionMismatch, $"peer uses version {hello.Version}, this node uses version {Protocol.Version}");
                return;
            }

            PeerId = hello.Id;
            _onHello(this);
            await BecomeReadyAsync();

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
            Close(CloseReason.ConnectionLost, e.Message);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // This node closed the conversation; Close has already run.
        }
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
            List<string> early;
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

            foreach (string text in early)
                await Codec.WriteAsync(_stream, new ChatMessage(text, DateTimeOffset.UtcNow));
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
