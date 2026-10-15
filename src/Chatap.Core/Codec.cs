// BinaryPrimitives: writes and reads a number as 4 bytes in big-endian order.
using System.Buffers.Binary;
// JsonSerializer: changes a message into JSON text and back.
using System.Text.Json;

// This type is part of the Core project.
namespace Chatap.Core;

// The codec changes a message into bytes, and bytes back into a complete message.
// One message on the network is one "frame": 4 bytes with the length, then that number of bytes of JSON text (UTF-8).
// The codec does not own a socket. It works on any stream: a network stream, or a test stream.
// The codec does not close connections and does not write the log. It reports problems with errors. The handler decides what to do.
public static class Codec
{
    // The JSON settings. "Web" writes the names with a small first letter, for example "text" and "sent".
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // A time with the longest possible text form (7 digits after the seconds). Used only to measure.
    static readonly DateTimeOffset LongestTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

    // Gives the number of JSON bytes for a chat message with this text, as WriteAsync would send it.
    // This includes the 6-byte \uXXXX form that the JSON library uses for letters like ø and for emojis.
    // The result is never smaller than the real size, because it uses the longest possible time text.
    public static int ChatMessageSize(string text) =>
        JsonSerializer.SerializeToUtf8Bytes<Message>(new ChatMessage(text, LongestTime), Options).Length;

    // True when a chat message with this text is inside the protocol limit.
    public static bool FitsInOneMessage(string text) => ChatMessageSize(text) <= Protocol.MaxMessageBytes;

    // Sends one message.
    public static async Task WriteAsync(Stream stream, Message message, CancellationToken cancel = default)
    {
        // Change the message into JSON bytes. The JSON library adds the "type" field first.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        // Do not send a message that is larger than the limit. The peer would reject it.
        if (json.Length > Protocol.MaxMessageBytes)
            throw new ProtocolException($"Message is {json.Length} bytes. The limit is {Protocol.MaxMessageBytes} bytes.");

        // Make one block of bytes: 4 bytes for the length, then the JSON.
        byte[] frame = new byte[4 + json.Length];
        // Put the length into the first 4 bytes, the most significant byte first (big-endian).
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        // Put the JSON after the 4 length bytes.
        json.CopyTo(frame, 4);
        // Send the complete block with one write.
        await stream.WriteAsync(frame, cancel);
    }

    // Receives one complete message. There are three results that are not a message:
    // - null: the peer closed the connection between two messages (a normal end).
    // - EndOfStreamException: the peer closed the connection in the middle of a message.
    // - ProtocolException: the message breaks the protocol (too large, bad JSON, unknown type).
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        // Step 1: get exactly 4 length bytes.
        byte[] prefix = new byte[4];
        // The first read gives 1 to 4 bytes, or 0 bytes if the peer closed the connection.
        int first = await stream.ReadAsync(prefix, cancel);
        // 0 bytes at the start of a message: a normal end. There are no more messages.
        if (first == 0)
            return null;
        // Read the remaining length bytes. One read can give fewer bytes than requested, so this reads again until all 4 bytes are there.
        // If the connection closes before that, it stops with EndOfStreamException.
        await stream.ReadExactlyAsync(prefix.AsMemory(first), cancel);

        // Step 2: check the length before the codec reserves memory for the message.
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        // A negative number (a value that is too large for an int) or a value above the limit stops here.
        // Thus a false length cannot make the codec reserve gigabytes of memory.
        if (length < 0 || length > Protocol.MaxMessageBytes)
            throw new ProtocolException($"Message length {length} is not allowed. The limit is {Protocol.MaxMessageBytes} bytes.");

        // Step 3: reserve memory for the JSON, then get exactly that number of bytes, however many reads that takes.
        byte[] json = new byte[length];
        await stream.ReadExactlyAsync(json, cancel);

        // Step 4: change the JSON into a message object.
        try
        {
            // The "type" field selects HelloMessage or ChatMessage. The JSON text "null" is not a message.
            return JsonSerializer.Deserialize<Message>(json, Options)
                ?? throw new ProtocolException("Message is empty (JSON null).");
        }
        catch (JsonException e)
        {
            // The bytes are not valid JSON, or the "type" value is unknown.
            throw new ProtocolException($"Message is not valid: {e.Message}", e);
        }
        catch (NotSupportedException e)
        {
            // The JSON has no "type" field, so the library cannot select a message type.
            throw new ProtocolException($"Message has no known type: {e.Message}", e);
        }
    }
}
