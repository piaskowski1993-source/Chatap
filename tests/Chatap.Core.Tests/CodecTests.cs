using System.Buffers.Binary;
using System.Text;

namespace Chatap.Core.Tests;

public class CodecTests
{
    static async Task<byte[]> Encode(params Message[] messages)
    {
        var stream = new MemoryStream();
        foreach (var message in messages)
            await Codec.WriteAsync(stream, message);
        return stream.ToArray();
    }

    // Builds one frame by hand: 4-byte big-endian length, then the JSON text.
    static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task Hello_carries_the_protocol_version()
    {
        var hello = new HelloMessage(Guid.NewGuid(), Protocol.Version);

        byte[] bytes = await Encode(hello);
        var decoded = await Codec.ReadAsync(new MemoryStream(bytes));

        Assert.Equal(hello, decoded);
        string json = Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);
        Assert.Contains("\"type\":\"hello\"", json);
        Assert.Contains($"\"version\":{Protocol.Version}", json);
    }

    [Fact]
    public async Task Chat_message_round_trip_keeps_text_and_time()
    {
        var chat = new ChatMessage("hi", DateTimeOffset.UtcNow);

        var decoded = await Codec.ReadAsync(new MemoryStream(await Encode(chat)));

        Assert.Equal(chat, decoded);
    }

    [Fact]
    public async Task Line_breaks_in_the_text_stay_the_same()
    {
        var chat = new ChatMessage("point one\npoint two\npoint three", DateTimeOffset.UtcNow);

        var decoded = await Codec.ReadAsync(new MemoryStream(await Encode(chat)));

        Assert.Equal(chat, decoded);
    }

    [Fact]
    public async Task Two_messages_in_one_block_decode_in_order()
    {
        var first = new ChatMessage("line one\nline two", DateTimeOffset.UtcNow);
        var second = new ChatMessage("ok", DateTimeOffset.UtcNow);
        var stream = new MemoryStream(await Encode(first, second));

        Assert.Equal(first, await Codec.ReadAsync(stream));
        Assert.Equal(second, await Codec.ReadAsync(stream));
    }

    [Fact]
    public async Task Bytes_that_arrive_one_at_a_time_still_decode()
    {
        var first = new HelloMessage(Guid.NewGuid(), Protocol.Version);
        var second = new ChatMessage("arrives in small pieces", DateTimeOffset.UtcNow);
        var stream = new OneByteStream(await Encode(first, second));

        Assert.Equal(first, await Codec.ReadAsync(stream));
        Assert.Equal(second, await Codec.ReadAsync(stream));
    }

    [Fact]
    public async Task Length_prefix_is_big_endian()
    {
        byte[] bytes = await Encode(new ChatMessage("hi", DateTimeOffset.UtcNow));

        int bodyLength = bytes.Length - 4;
        Assert.Equal(bodyLength, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(0, bytes[0]); // a small number: the most significant byte comes first and is zero
    }

    [Fact]
    public async Task Too_large_length_is_rejected()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, int.MaxValue); // a false length of about 2 GB, no body

        var error = await Assert.ThrowsAsync<ProtocolException>(() => Codec.ReadAsync(new MemoryStream(prefix)));

        Assert.Contains("not allowed", error.Message);
    }

    [Fact]
    public async Task Close_between_messages_means_no_more_messages()
    {
        var stream = new MemoryStream(await Encode(new ChatMessage("last one", DateTimeOffset.UtcNow)));

        Assert.NotNull(await Codec.ReadAsync(stream));
        Assert.Null(await Codec.ReadAsync(stream));
    }

    [Fact]
    public async Task Close_in_the_middle_of_a_message_is_an_error()
    {
        byte[] whole = await Encode(new ChatMessage("cut off", DateTimeOffset.UtcNow));

        // only 2 of the 4 length bytes
        await Assert.ThrowsAsync<EndOfStreamException>(() => Codec.ReadAsync(new MemoryStream(whole[..2])));
        // the full length, but only half of the body
        await Assert.ThrowsAsync<EndOfStreamException>(() => Codec.ReadAsync(new MemoryStream(whole[..(whole.Length / 2)])));
    }

    [Fact]
    public async Task Unknown_message_type_is_a_protocol_error()
    {
        byte[] frame = Frame("{\"type\":\"toggle\"}");

        await Assert.ThrowsAsync<ProtocolException>(() => Codec.ReadAsync(new MemoryStream(frame)));
    }
}
