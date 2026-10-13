using System;
using Chatap.Core;

namespace Chatap.App;

// The Core parts that Program.cs (the composition root) makes and gives to the window.
public sealed record AppServices(Guid Identity, Node Node, ConversationStore Store, LogWriter Log);
