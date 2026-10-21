# Chatap

A peer-to-peer chat app in C#, made as an exercise for a backend course.
Every copy of Chatap is a **node**: it listens for other nodes and can connect
to them. There is no central server. Each conversation is one TCP connection
between two nodes.

The repository also contains **[NodeWorld](NodeWorld/)**, a small browser game
where a robot explores the inside of Chatap, machine by machine.

▶ **Play NodeWorld in the browser:** https://piaskowski1993-source.github.io/Chatap/NodeWorld/

## Features

- Peer-to-peer over TCP: one connection per conversation, several conversations at a time
- Listens on a free port chosen by the operating system (port 0) and shows your address
- Hello handshake with node ID and protocol version; different versions do not talk
- Length-prefixed JSON messages (4-byte big-endian length, at most 64 KB)
- Live byte counter; Send is disabled when a message is too long
- Every message is saved (`conversations/<peer id>.jsonl`, append-only), with a chat history button
- Activity log per UTC day (`logs/<date>/node.txt` and one file per conversation)
- TCP keepalive (60 s / 10 s / 3 probes) finds friends that disappeared without closing
- If the log or the storage fails, the chat goes on and the window says so

## Build, run and test

You need the .NET 10 SDK. The window uses [Avalonia](https://avaloniaui.net/),
so it runs on Linux, Windows and macOS.

```sh
dotnet build Chatap.slnx
dotnet test Chatap.slnx                  # 51 tests for the Core project
dotnet run --project src/Chatap.App      # start a node
```

### Two nodes on one computer

Each node keeps its identity, messages and logs in a data folder
(`~/.local/share/Chatap` on Linux). A second copy needs its own folder:

```sh
dotnet run --project src/Chatap.App                            # node 1
dotnet run --project src/Chatap.App -- --data ~/chatap-copy2   # node 2
```

Each window shows "You are: <IP> : <port>". Type the other node's address and
port into "Connect to:" and press **Connect**. Both nodes must be on the same
computer or the same local network.

## How it is built

```
src/Chatap.Core    the chat engine (no UI)
  Node               listens, connects, makes one handler per connection
  ConnectionHandler  one conversation: hello, receive loop, sending, closing
  Codec              messages ⇄ length-prefixed JSON frames
  ConversationStore  saves messages (one queue, one writer)
  LogWriter          writes the activity log (one queue, one writer)
  IdentityStore      the node's permanent ID
src/Chatap.App     the Avalonia window (MVVM); the composition root is Program.cs
tests/             xUnit tests for Chatap.Core
NodeWorld/         the browser game about the parts above
```

A message goes: Window → ConnectionHandler (saved, then sent) → Codec → TCP →
Codec → ConnectionHandler (saved) → Window, where it is shown on the UI thread.

## Limits

- Local network only: no NAT traversal for the internet.
- No encryption, and the node ID is not verified. Use it only on a network you trust.
- Incoming conversations are accepted automatically.

## Credits

Design by Piotr. Code written with Claude Code.
