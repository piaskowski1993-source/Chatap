# NodeWorld

A small browser game about how [Chatap](../) works inside.

You drive a little robot across two Chatap nodes (A and B). Each machine on a
board is a real part of Chatap: the window, the Node, the ConnectionHandler,
the Codec, the ConversationStore and the LogWriter. Packets move along the
wires in the same order as in the real app, and the TCP cable joins the two
boards. Drive to a machine's terminal and talk to it to learn what it does.

> Work in progress. Parts of this file are placeholders until the game is done.

## How to play

Open `index.html` in a browser. No install or server is necessary.

| Key | Action |
| --- | --- |
| Arrow keys / WASD, or click | Drive |
| E / Enter / Space | Talk at a terminal, go through a door, ride the cable |
| Space / Enter / click | Next line of dialogue |
| 1, 2, 3 … | Choose a question in a dialogue |
| Esc | Close the dialogue |

## Fiction

Most of the world matches the Chatap code. These things are game rules only,
not Chatap behaviour:

- **The robot.** Nothing in Chatap walks on wires or blocks packets.
- **Bumps and the DEFENSIVE PROTOCOL.** A stuck signal "bumps" every 1.5 s, and
  more than 3 bumps turn the board red. Chatap has no such rule. Only TCP on the
  cable really retransmits lost data.
- **Antibodies.** The `!` bots that push the robot off a wire do not exist in
  Chatap.
- **Talking machines.** Terminals, faces and personalities are a way to explain
  the code. The facts they say are true to the code; the voices are not.
- **Sound.** Music, modem noise and packet sounds are decoration.

## Development

`dev/harness.js` runs the page script in Node.js with fake browser objects and
checks the main behaviours:

```sh
sed -n "/<script>/,/<\/script>/p" index.html | sed "1d;\$d" > dev/cw.js
node dev/harness.js
```

## Credits

Game design by Piotr. Code written with Claude Code.
