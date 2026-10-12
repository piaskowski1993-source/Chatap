namespace Chatap.Core;

// The peer sent something that breaks the protocol: too large, not valid JSON, or an unknown type.
// The connection must close.
public class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);
