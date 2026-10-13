using System.Net;
using System.Net.Sockets;

namespace Chatap.Core;

// One running copy of Chatap. The server part accepts connections on a free port;
// the client part connects to an address the user typed. Each connection gets its own handler.
public sealed class Node : IDisposable
{
    public static readonly TimeSpan DefaultHelloTimeout = TimeSpan.FromSeconds(10);

    readonly TcpListener _listener = new(IPAddress.Any, 0);
    readonly LogWriter _log;
    readonly TimeSpan _helloTimeout;
    readonly KeepAliveSettings _keepAlive;
    readonly CancellationTokenSource _stopping = new();
    readonly object _conversationsLock = new();
    readonly Dictionary<Guid, ConnectionHandler> _conversations = [];

    // helloTimeout and keepAlive are for tests; the real program uses 10 s and 60/10/3.
    public Node(Guid identity, LogWriter log, TimeSpan? helloTimeout = null, KeepAliveSettings? keepAlive = null)
    {
        Identity = identity;
        _log = log;
        _helloTimeout = helloTimeout ?? DefaultHelloTimeout;
        _keepAlive = keepAlive ?? KeepAliveSettings.Default;
    }

    public Guid Identity { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // Raised for incoming and outgoing connections, before the handler sends its hello.
    // Subscribe to the handler's events here.
    public event Action<ConnectionHandler>? ConversationStarted;

    public void Start()
    {
        _listener.Start();
        _log.Log($"listening on port {Port}");
        _ = AcceptLoopAsync();
    }

    // Client part. Throws SocketException when nothing listens at that address.
    public async Task<ConnectionHandler> ConnectAsync(string host, int port)
    {
        _log.Log($"outgoing connection to {host}:{port}");
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port);
        }
        catch (SocketException e)
        {
            client.Dispose();
            _log.Log($"connection to {host}:{port} failed: {e.Message}");
            throw;
        }
        return Begin(client, isOutgoing: true);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        ConnectionHandler[] open;
        lock (_conversationsLock)
            open = [.. _conversations.Values];
        foreach (var handler in open)
            handler.Close();
    }

    // Server part: accept, give the connection to a new handler, wait for the next one.
    async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                _log.Log($"accept failed: {e.Message}");
                continue;
            }

            _log.Log($"incoming connection from {client.Client.RemoteEndPoint}");
            Begin(client, isOutgoing: false);
        }
    }

    ConnectionHandler Begin(TcpClient client, bool isOutgoing)
    {
        var handler = new ConnectionHandler(client, isOutgoing, Identity, _log, _helloTimeout, _keepAlive, OnHello);
        ConversationStarted?.Invoke(handler);
        _ = handler.RunAsync();
        return handler;
    }

    // Called by a handler when its peer's hello is accepted. The newest connection wins.
    void OnHello(ConnectionHandler handler)
    {
        Guid peer = handler.PeerId!.Value;
        ConnectionHandler? old;
        lock (_conversationsLock)
        {
            _conversations.TryGetValue(peer, out old);
            _conversations[peer] = handler;
        }

        handler.Disconnected += _ =>
        {
            lock (_conversationsLock)
            {
                if (_conversations.TryGetValue(peer, out var current) && current == handler)
                    _conversations.Remove(peer);
            }
        };

        old?.Close(CloseReason.Replaced, "the peer connected again");
    }
}
