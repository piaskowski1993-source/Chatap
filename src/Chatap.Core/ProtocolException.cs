// This type is part of the Core project.
namespace Chatap.Core;

// Use this error when the peer sends data that breaks the Chatap protocol.
// The causes are: a message that is too large, text that is not valid JSON, or an unknown message type.
// The codec makes this error. The connection handler catches it and closes the connection.
// The close reason is ProtocolError. The log keeps the error text.
// This error is not for a lost connection: the codec uses EndOfStreamException for that.
// "message" is the text that tells what is wrong. "inner" keeps the original error from the JSON library, if there is one.
public class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);
