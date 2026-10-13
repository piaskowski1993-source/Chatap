using System.Net;
using System.Net.Sockets;
using static Chatap.Core.Tests.ConnectionTests;

namespace Chatap.Core.Tests;

public class StorageTests
{
    static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "chatap-test-" + Guid.NewGuid());

    [Fact]
    public async Task Sent_and_received_messages_are_saved_in_order_with_the_sender_ids()
    {
        using var a = NewNode();
        using var b = NewNode();
        var aSide = NextConversation(a);
        var bSide = NextConversation(b);
        await b.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var atB = await bSide.WaitAsync(Wait);
        await Task.WhenAll(atA.Ready, atB.Ready).WaitAsync(Wait);
        var aGotHi = Signal<ChatMessage>();
        var bGotHey = Signal<ChatMessage>();
        atA.Handler.MessageReceived += m => aGotHi.TrySetResult(m);
        atB.Handler.MessageReceived += m => bGotHey.TrySetResult(m);

        await atB.Handler.SendAsync("hi");
        await aGotHi.Task.WaitAsync(Wait);
        await atA.Handler.SendAsync("hey");
        await bGotHey.Task.WaitAsync(Wait);
        await a.Store.DisposeAsync(); // graceful shutdown: all lines are in the files now
        await b.Store.DisposeAsync();

        foreach (var (store, peer) in new[] { (a.Store, b.Identity), (b.Store, a.Identity) })
        {
            var history = store.ReadHistory(peer);
            Assert.Equal(["hi", "hey"], history.Select(e => e.Message.Text));
            Assert.Equal([b.Identity, a.Identity], history.Select(e => e.Message.From));
            Assert.All(history, e => Assert.False(e.NotSent));
        }
    }

    [Fact]
    public async Task A_message_marked_as_not_sent_stays_in_the_history_with_the_mark()
    {
        string folder = NewTempFolder();
        var store = new ConversationStore(folder);
        Guid me = Guid.NewGuid(), peer = Guid.NewGuid();

        store.Save(peer, me, "delivered", DateTimeOffset.UtcNow);
        var failed = store.Save(peer, me, "lost", DateTimeOffset.UtcNow);
        store.MarkNotSent(peer, failed.Id);
        await store.DisposeAsync();

        var history = store.ReadHistory(peer);
        Assert.Equal(["delivered", "lost"], history.Select(e => e.Message.Text));
        Assert.Equal([false, true], history.Select(e => e.NotSent));
    }

    [Fact]
    public async Task Without_a_hello_a_not_sent_message_has_no_file_and_only_a_log_line()
    {
        using var fakeListener = new TcpListener(IPAddress.Loopback, 0);
        fakeListener.Start();
        string folder = NewTempFolder();
        var log = new LogWriter(folder);
        var store = new ConversationStore(folder);
        using var a = new Node(Guid.NewGuid(), log, store, TimeSpan.FromSeconds(5));
        a.Start();
        var connecting = a.ConnectAsync("127.0.0.1", ((IPEndPoint)fakeListener.LocalEndpoint).Port);
        using var fake = await fakeListener.AcceptTcpClientAsync();
        var handler = await connecting;
        var notSent = Signal<IReadOnlyList<string>>();
        handler.NotSent += texts => notSent.TrySetResult(texts);

        await handler.SendAsync("early");
        await Codec.WriteAsync(fake.GetStream(), new HelloMessage(Guid.NewGuid(), Protocol.Version + 1));

        Assert.Equal(["early"], await notSent.Task.WaitAsync(Wait));
        await store.DisposeAsync();
        await log.DisposeAsync();
        Assert.Empty(store.KnownPeers());
        string nodeLog = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "logs"), "node.txt", SearchOption.AllDirectories).Single());
        Assert.Contains("1 message(s) not sent", nodeLog);
    }

    [Fact]
    public async Task Ten_writers_to_one_peer_file_at_the_same_time_lose_and_mix_nothing()
    {
        var store = new ConversationStore(NewTempFolder());
        Guid peer = Guid.NewGuid();

        var writers = Enumerable.Range(0, 10).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                store.Save(peer, peer, $"writer {w} message {i}", DateTimeOffset.UtcNow);
        }));
        await Task.WhenAll(writers);
        await store.DisposeAsync();

        var texts = store.ReadHistory(peer).Select(e => e.Message.Text).ToList();
        var expected = from w in Enumerable.Range(0, 10)
                       from i in Enumerable.Range(0, 100)
                       select $"writer {w} message {i}";
        Assert.Equal(1000, texts.Count);
        Assert.Equal(expected.Order(), texts.Order());
    }

    [Fact]
    public async Task A_broken_last_line_is_skipped_and_the_rest_is_read()
    {
        string folder = NewTempFolder();
        var store = new ConversationStore(folder);
        Guid peer = Guid.NewGuid();
        store.Save(peer, peer, "complete", DateTimeOffset.UtcNow);
        await store.DisposeAsync();

        // a crash during a write leaves half a line
        File.AppendAllText(Path.Combine(folder, "conversations", $"{peer}.jsonl"), "{\"id\":\"5f1c2a");

        var history = store.ReadHistory(peer);
        Assert.Equal(["complete"], history.Select(e => e.Message.Text));
    }

    [Fact]
    public async Task Graceful_shutdown_writes_every_message_still_in_the_queue()
    {
        int written = 0;
        var store = new ConversationStore("unused", append: async (path, text) =>
        {
            await Task.Delay(2); // a slow disk
            Interlocked.Increment(ref written);
        });

        for (int i = 0; i < 50; i++)
            store.Save(Guid.NewGuid(), Guid.NewGuid(), $"message {i}", DateTimeOffset.UtcNow);
        await store.DisposeAsync();

        Assert.Equal(50, written);
    }

    [Fact]
    public async Task A_write_error_informs_once_and_the_chat_continues()
    {
        var failures = new List<Exception>();
        var store = new ConversationStore("unused", append: (path, text) => throw new IOException("No space left on device"));
        store.StoreFailed += failures.Add;

        for (int i = 0; i < 3; i++)
            store.Save(Guid.NewGuid(), Guid.NewGuid(), "text", DateTimeOffset.UtcNow);
        await store.DisposeAsync(); // must not throw

        Assert.Single(failures);
    }
}
