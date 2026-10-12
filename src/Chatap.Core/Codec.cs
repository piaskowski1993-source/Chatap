using System.Buffers.Binary;
using System.Text.Json;

namespace Chatap.Core;

// Turns messages into bytes and bytes back into whole messages.
// Frame on the wire: 4-byte big-endian length, then that many bytes of UTF-8 JSON.
public static class Codec
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, Message message, CancellationToken cancel = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (json.Length > Protocol.MaxMessageBytes)
            throw new ProtocolException($"Message is {json.Length} bytes. The limit is {Protocol.MaxMessageBytes} bytes.");

        byte[] frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancel);
    }

    // Returns null when the peer closed the connection between two messages.
    // Throws EndOfStreamException when the peer closed it in the middle of a message,
    // and ProtocolException when the message breaks the protocol.
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        // 1. Exactly 4 length bytes. A first read of 0 bytes is a normal close.
        byte[] prefix = new byte[4];
        int first = await stream.ReadAsync(prefix, cancel);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(first), cancel);

        // 2. Check the size before any memory is reserved for the message.
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > Protocol.MaxMessageBytes)
            throw new ProtocolException($"Message length {length} is not allowed. The limit is {Protocol.MaxMessageBytes} bytes.");

        // 3. Exactly that many bytes, however many reads it takes.
        byte[] json = new byte[length];
        await stream.ReadExactlyAsync(json, cancel);

        // 4. JSON into a message object.
        try
        {
            return JsonSerializer.Deserialize<Message>(json, Options)
                ?? throw new ProtocolException("Message is empty (JSON null).");
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"Message is not valid: {e.Message}", e);
        }
        catch (NotSupportedException e)
        {
            throw new ProtocolException($"Message has no known type: {e.Message}", e);
        }
    }
}
