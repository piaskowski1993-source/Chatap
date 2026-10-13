using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Chatap.Core.Tests;

public class ConnectionTests
{
    static readonly TimeSpan ShortHelloTimeout = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static Node NewNode(TimeSpan? helloTimeout = null)
    {
        var log = new LogWriter(Path.Combine(Path.GetTempPath(), "chatap-test-" + Guid.NewGuid()));
        var node = new Node(Guid.NewGuid(), log, helloTimeout ?? ShortHelloTimeout);
        node.Start();
        return node;
    }

    // Completes when the event occurs. Continuations run on another thread, never inside the handler.
    static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A handler with its events watched from the start, before its hello can arrive.
    sealed record Watched(ConnectionHandler Handler, Task<ConnectionHandler> Ready, Task<CloseReason> Closed);

    // The next handler that the node starts (incoming or outgoing).
    static Task<Watched> NextConversation(Node node)
    {
        var started = Signal<Watched>();
        Action<ConnectionHandler>? watch = null;
        watch = handler =>
        {
            node.ConversationStarted -= watch;
            started.TrySetResult(new Watched(handler, ReadyOf(handler), ClosedOf(handler)));
        };
        node.ConversationStarted += watch;
        return started.Task;
    }

    static Task<ConnectionHandler> ReadyOf(ConnectionHandler handler)
    {
        var ready = Signal<ConnectionHandler>();
        handler.Ready += h => ready.TrySetResult(h);
        return ready.Task;
    }

    static Task<CloseReason> ClosedOf(ConnectionHandler handler)
    {
        var closed = Signal<CloseReason>();
        handler.Disconnected += r => closed.TrySetResult(r);
        return closed.Task;
    }

    [Fact]
    public async Task Two_nodes_reach_Ready_with_each_others_ids()
    {
        using var a = NewNode();
        using var b = NewNode();
        var aSide = NextConversation(a);
        var bSide = NextConversation(b);

        await b.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var atB = await bSide.WaitAsync(Wait);
        await Task.WhenAll(atA.Ready, atB.Ready).WaitAsync(Wait);

        Assert.Equal(b.Identity, atA.Handler.PeerId);
        Assert.Equal(a.Identity, atB.Handler.PeerId);
        Assert.False(atA.Handler.IsOutgoing);
        Assert.True(atB.Handler.IsOutgoing);
    }

    [Fact]
    public async Task Chat_messages_travel_in_both_directions()
    {
        using var a = NewNode();
        using var b = NewNode();
        var aSide = NextConversation(a);
        var bHandler = await b.ConnectAsync("127.0.0.1", a.Port);
        var aHandler = (await aSide.WaitAsync(Wait)).Handler;
        var atA = Signal<ChatMessage>();
        var atB = Signal<ChatMessage>();
        aHandler.MessageReceived += m => atA.TrySetResult(m);
        bHandler.MessageReceived += m => atB.TrySetResult(m);

        await bHandler.SendAsync("hi");
        await aHandler.SendAsync("hey");

        Assert.Equal("hi", (await atA.Task.WaitAsync(Wait)).Text);
        Assert.Equal("hey", (await atB.Task.WaitAsync(Wait)).Text);
    }

    [Fact]
    public async Task A_silent_peer_is_closed_after_the_hello_timeout()
    {
        using var a = NewNode();
        var aSide = NextConversation(a);

        using var silent = new TcpClient();
        await silent.ConnectAsync("127.0.0.1", a.Port); // connects, never sends a hello
        var atA = await aSide.WaitAsync(Wait);

        Assert.Equal(CloseReason.HelloTimeout, await atA.Closed.WaitAsync(Wait));
        Assert.False(atA.Handler.IsOutgoing); // incoming: the window only logs it
    }

    [Fact]
    public async Task A_message_sent_before_the_hello_waits_and_arrives_after_it()
    {
        // A fake peer that controls exactly when its hello is sent.
        using var fakeListener = new TcpListener(IPAddress.Loopback, 0);
        fakeListener.Start();
        using var a = NewNode(helloTimeout: TimeSpan.FromSeconds(5)); // longer than the 300 ms delay below
        var connecting = a.ConnectAsync("127.0.0.1", ((IPEndPoint)fakeListener.LocalEndpoint).Port);
        using var fake = await fakeListener.AcceptTcpClientAsync();
        var aHandler = await connecting;
        var fakeStream = fake.GetStream();

        Assert.IsType<HelloMessage>(await Codec.ReadAsync(fakeStream)); // A's hello
        Assert.True(await aHandler.SendAsync("early")); // accepted, but must wait
        await Task.Delay(300);
        Assert.False(fakeStream.DataAvailable); // nothing was sent before the fake peer's hello

        await Codec.WriteAsync(fakeStream, new HelloMessage(Guid.NewGuid(), Protocol.Version));

        var arrived = Assert.IsType<ChatMessage>(await Codec.ReadAsync(fakeStream).WaitAsync(Wait));
        Assert.Equal("early", arrived.Text);
    }

    [Fact]
    public async Task A_different_version_closes_and_reports_the_waiting_messages_as_not_sent()
    {
        using var fakeListener = new TcpListener(IPAddress.Loopback, 0);
        fakeListener.Start();
        using var a = NewNode();
        var connecting = a.ConnectAsync("127.0.0.1", ((IPEndPoint)fakeListener.LocalEndpoint).Port);
        using var fake = await fakeListener.AcceptTcpClientAsync();
        var aHandler = await connecting;
        var closed = ClosedOf(aHandler);
        var notSent = Signal<IReadOnlyList<string>>();
        aHandler.NotSent += texts => notSent.TrySetResult(texts);

        await aHandler.SendAsync("early");
        await Codec.WriteAsync(fake.GetStream(), new HelloMessage(Guid.NewGuid(), Protocol.Version + 1));

        Assert.Equal(CloseReason.VersionMismatch, await closed.WaitAsync(Wait));
        Assert.Equal(["early"], await notSent.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task The_newest_connection_from_the_same_peer_wins()
    {
        using var a = NewNode();
        var peer = Guid.NewGuid();

        var firstSide = NextConversation(a);
        using var first = new TcpClient();
        await first.ConnectAsync("127.0.0.1", a.Port);
        var firstConv = await firstSide.WaitAsync(Wait);
        await Codec.WriteAsync(first.GetStream(), new HelloMessage(peer, Protocol.Version));
        await firstConv.Ready.WaitAsync(Wait);

        var secondSide = NextConversation(a);
        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", a.Port);
        var secondConv = await secondSide.WaitAsync(Wait);
        await Codec.WriteAsync(second.GetStream(), new HelloMessage(peer, Protocol.Version));

        Assert.Equal(CloseReason.Replaced, await firstConv.Closed.WaitAsync(Wait));
        await secondConv.Ready.WaitAsync(Wait);
        Assert.Equal(HandlerState.Ready, secondConv.Handler.State);
    }

    [Fact]
    public async Task When_one_conversation_closes_the_other_one_continues()
    {
        using var a = NewNode();
        using var b = NewNode();
        using var c = NewNode();

        var aToB = NextConversation(a);
        var bSide = NextConversation(b);
        await b.ConnectAsync("127.0.0.1", a.Port);
        var aB = await aToB.WaitAsync(Wait);
        var bA = await bSide.WaitAsync(Wait);
        await Task.WhenAll(aB.Ready, bA.Ready).WaitAsync(Wait);

        var aToC = NextConversation(a);
        var cSide = NextConversation(c);
        await c.ConnectAsync("127.0.0.1", a.Port);
        var aC = await aToC.WaitAsync(Wait);
        var cA = await cSide.WaitAsync(Wait);
        await Task.WhenAll(aC.Ready, cA.Ready).WaitAsync(Wait);
        var atA = Signal<ChatMessage>();
        aC.Handler.MessageReceived += m => atA.TrySetResult(m);

        bA.Handler.Close();
        Assert.Equal(CloseReason.PeerClosed, await aB.Closed.WaitAsync(Wait));

        await cA.Handler.SendAsync("still here");
        Assert.Equal("still here", (await atA.Task.WaitAsync(Wait)).Text);
        Assert.Equal(HandlerState.Ready, aC.Handler.State);
    }

    [Fact]
    public async Task A_bad_message_from_the_peer_closes_with_ProtocolError()
    {
        using var a = NewNode();
        var aSide = NextConversation(a);
        using var bad = new TcpClient();
        await bad.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var stream = bad.GetStream();

        await Codec.WriteAsync(stream, new HelloMessage(Guid.NewGuid(), Protocol.Version));
        await atA.Ready.WaitAsync(Wait);
        byte[] hugePrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(hugePrefix, int.MaxValue);
        await stream.WriteAsync(hugePrefix);

        Assert.Equal(CloseReason.ProtocolError, await atA.Closed.WaitAsync(Wait));
    }

    [Fact]
    public async Task Connecting_to_a_port_where_nothing_listens_fails_clearly()
    {
        using var a = NewNode();
        int freePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        } // stopped: nothing listens there now

        await Assert.ThrowsAnyAsync<SocketException>(() => a.ConnectAsync("127.0.0.1", freePort));
    }
}
