using System.Text.Json.Serialization;

namespace Chatap.Core;

public static class Protocol
{
    // Only nodes with the same version can chat (strict version matching).
    public const int Version = 1;

    // Larger messages are rejected and the connection closes.
    public const int MaxMessageBytes = 64 * 1024;
}

// One message on the wire. The JSON field "type" tells which kind it is.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ChatMessage), "chat")]
public abstract record Message;

// First message on every connection, from both sides.
public sealed record HelloMessage(Guid Id, int Version) : Message;

// Sent is always UTC.
public sealed record ChatMessage(string Text, DateTimeOffset Sent) : Message;
