using static Chatap.Core.Tests.ConnectionTests;

namespace Chatap.Core.Tests;

public class SizeLimitTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("hø")]
    [InlineData("😀 æøå")]
    [InlineData("a\"b<c>&'d\\e+f")]
    public async Task ChatMessageSize_is_never_smaller_than_the_real_frame(string text)
    {
        var stream = new MemoryStream();
        await Codec.WriteAsync(stream, new ChatMessage(text, DateTimeOffset.UtcNow));
        int realJsonBytes = (int)stream.Length - 4;

        int measured = Codec.ChatMessageSize(text);

        Assert.InRange(measured, realJsonBytes, realJsonBytes + 8); // at most the extra digits of the longest time
    }

    [Fact]
    public void Many_non_ASCII_letters_need_6_bytes_each()
    {
        // 15,000 characters: this passed the old 20,000-character check, but it is about 90 KB of JSON.
        Assert.True(Codec.FitsInOneMessage(new string('a', 15_000)));
        Assert.False(Codec.FitsInOneMessage(new string('ø', 15_000)));
    }

    [Fact]
    public async Task A_too_long_message_is_not_sent_and_the_conversation_continues()
    {
        using var a = NewNode();
        using var b = NewNode();
        var aSide = NextConversation(a);
        var bSide = NextConversation(b);
        await b.ConnectAsync("127.0.0.1", a.Port);
        var atA = await aSide.WaitAsync(Wait);
        var atB = await bSide.WaitAsync(Wait);
        await Task.WhenAll(atA.Ready, atB.Ready).WaitAsync(Wait);
        var notSent = Signal<IReadOnlyList<string>>();
        atB.Handler.NotSent += texts => notSent.TrySetResult(texts);
        var received = Signal<ChatMessage>();
        atA.Handler.MessageReceived += m => received.TrySetResult(m);

        string tooLong = new('ø', 15_000);
        Assert.False(await atB.Handler.SendAsync(tooLong)); // no crash, returns false

        Assert.Equal([tooLong], await notSent.Task.WaitAsync(Wait));
        Assert.Equal(HandlerState.Ready, atB.Handler.State); // the connection stays open
        await atB.Handler.SendAsync("still works");
        Assert.Equal("still works", (await received.Task.WaitAsync(Wait)).Text);
    }
}
