using System.Net.Sockets;
using static Chatap.Core.Tests.ConnectionTests;

namespace Chatap.Core.Tests;

public class EndingTests
{
    // Two nodes, connected and Ready. Returns A's and B's side of the conversation.
    static async Task<(Watched AtA, Watched AtB)> Connect(Node a, Node b)
    {
        var aSide = NextConversation(a);
        var bSide = NextConversation(b);
        await b.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var atB = await bSide.WaitAsync(Wait);
        await Task.WhenAll(atA.Ready, atB.Ready).WaitAsync(Wait);
        return (atA, atB);
    }

    // A raw peer that has done the hello handshake with node a.
    static async Task<(TcpClient Raw, Watched AtA)> ConnectRaw(Node a)
    {
        var aSide = NextConversation(a);
        var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var stream = raw.GetStream();
        await Codec.ReadAsync(stream); // read A's hello, so no unread bytes stay behind
        await Codec.WriteAsync(stream, new HelloMessage(Guid.NewGuid(), Protocol.Version));
        await atA.Ready.WaitAsync(Wait);
        return (raw, atA);
    }

    [Fact]
    public async Task Keepalive_is_on_with_60_s_idle_10_s_interval_and_3_probes()
    {
        using var a = NewNode();
        using var b = NewNode();
        var (atA, atB) = await Connect(a, b);

        foreach (var handler in new[] { atA.Handler, atB.Handler })
        {
            var (on, values) = handler.ReadKeepAlive();
            Assert.True(on);
            Assert.Equal(KeepAliveSettings.Default, values);
        }
    }

    [Fact]
    public async Task Closing_one_side_gives_PeerClosed_on_the_other_side()
    {
        using var a = NewNode();
        using var b = NewNode();
        var (atA, atB) = await Connect(a, b);

        atB.Handler.Close();

        Assert.Equal(CloseReason.LocalClose, await atB.Closed.WaitAsync(Wait));
        Assert.Equal(CloseReason.PeerClosed, await atA.Closed.WaitAsync(Wait));
    }

    [Fact]
    public async Task Closing_the_app_gives_PeerClosed_to_every_peer()
    {
        var a = NewNode();
        using var b = NewNode();
        using var c = NewNode();
        var (_, bSide) = await Connect(a, b);
        var (_, cSide) = await Connect(a, c);

        a.Dispose(); // what Program.cs does when the window closes

        Assert.Equal(CloseReason.PeerClosed, await bSide.Closed.WaitAsync(Wait));
        Assert.Equal(CloseReason.PeerClosed, await cSide.Closed.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_peer_that_stops_in_the_middle_of_a_message_gives_ConnectionLost()
    {
        using var a = NewNode();
        var (raw, atA) = await ConnectRaw(a);

        await raw.GetStream().WriteAsync(new byte[] { 0, 0 }); // 2 of the 4 length bytes
        raw.Close(); // FIN in the middle of a message

        Assert.Equal(CloseReason.ConnectionLost, await atA.Closed.WaitAsync(Wait));
        Assert.Contains("middle of a message", atA.Handler.ClosedDetail);
    }

    [Fact]
    public async Task A_reset_from_the_peer_gives_ConnectionLost()
    {
        using var a = NewNode();
        var (raw, atA) = await ConnectRaw(a);

        // Close(0) sends RST. TcpClient.Close would not: its stream sends a FIN first.
        raw.Client.Close(0);

        Assert.Equal(CloseReason.ConnectionLost, await atA.Closed.WaitAsync(Wait));
        Assert.Contains("reset", atA.Handler.ClosedDetail);
    }

    [Fact]
    public async Task Send_after_the_close_returns_false()
    {
        using var a = NewNode();
        using var b = NewNode();
        var (_, atB) = await Connect(a, b);

        atB.Handler.Close();

        Assert.False(await atB.Handler.SendAsync("too late"));
    }
}
