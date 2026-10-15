// Pulls in the JSON attributes used below ([JsonPolymorphic], [JsonDerivedType]).
using System.Text.Json.Serialization;

// Everything in this file belongs to the Core project's namespace.
namespace Chatap.Core;

// The rules both nodes have to agree on. Static: no objects of it, just the values.
public static class Protocol
{
    // The version this node speaks. Goes out in the hello, and the peer's version gets compared to it.
    // Different version = no chat ("World of Warcraft law"). const = baked in at compile time, can't change at runtime.
    public const int Version = 1;

    // Biggest message allowed: 64 KB. Codec checks it when writing our own JSON,
    // and when reading the peer's length prefix, before it reserves any memory (stops the fake 3 GB length trick).
    public const int MaxMessageBytes = 64 * 1024;
}

// "type" is the metadata field in the JSON that says which kind of message this is.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
// "type":"hello" → becomes a HelloMessage.
[JsonDerivedType(typeof(HelloMessage), "hello")]
// "type":"chat" → becomes a ChatMessage. Any other value → error → ProtocolException → connection closes.
[JsonDerivedType(typeof(ChatMessage), "chat")]
// The common base: one variable / one return type that can hold either kind of message.
// abstract = can only be inherited, never made on its own (no "new Message()").
public abstract record Message;

// First thing both sides send after connecting: who I am (Id) and which protocol I speak (Version).
// record = data point: read-only values, compared by value, so two hellos with the same values are equal.
// sealed = nothing can inherit from it.
public sealed record HelloMessage(Guid Id, int Version) : Message;

// A normal chat message: the text and when it was sent.
// Sent is in UTC = one universal reference regardless of local time. The window turns it into local time only for display.
public sealed record ChatMessage(string Text, DateTimeOffset Sent) : Message;
