# NodeWorld

A small browser game about how [Chatap](../) works inside.

You drive a little robot across two Chatap nodes (A and B). Each machine on a
board is a real part of Chatap: the window, the Node, the ConnectionHandler,
the Codec, the ConversationStore and the LogWriter. Packets move along the
wires in the same order as in the real app, and the TCP cable joins the two
boards. Drive to a machine's terminal and talk to it to learn what it does.

## Goal

Explore the machines one by one. Each machine's terminal has a quiz about
that machine: 10 multiple-choice questions, and you need all 10. You see
the result only at the end, and only the numbers of the wrong questions.
Pass the quiz to get the password of the next machine. At the next
machine's terminal, choose "Input the password" to open its door.

Earned passwords also appear on the password board in the top-left corner
of Node A. Progress is not saved when you close the page.

After the last machine, the antibodies catch the robot and demand proof that
it is part of the machine: 10 correct answers to general questions about the
whole system. Every mistake pushes the robot closer to the edge of the board;
5 mistakes throw it into cyberspace. It comes back after 30 seconds, and is
tested again the next time it leaves a machine. Pass, and the robot wears the
**node_watcher** tag.

## How to play

Play online: https://piaskowski1993-source.github.io/Chatap/NodeWorld/

Or open `index.html` in a browser. No install or server is necessary.

| Key | Action |
| --- | --- |
| Arrow keys / WASD, or click | Drive |
| Shift (while driving) | Short speed boost, then 5 s to recharge |
| E / Enter / Space | Talk at a terminal, go through a door, ride the cable |
| Space / Enter / click | Next line of dialogue |
| 1, 2, 3 … | Choose a question in a dialogue, or an answer in a quiz |
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
- **Doors, quizzes and passwords.** Chatap machines have no doors. The
  passwords are game passwords, not part of Chatap.
- **Package view and the guard.** The package-view ride is a replay of one
  message's route through a room, not a live packet. The guard antibody that
  throws the robot out of a factory is a game rule.
- **The traffic cop.** Boost into a machine and an antibody in a police cap
  tells you off. Pure game rule.
- **The identity check and cyberspace.** The capture, the push to the edge and
  the fall are the game's ending, not anything Chatap does.
- **Sound.** Music, modem noise and packet sounds are decoration.

## Development

Open `index.html?test` (or `index.html#test`) to play in test mode: every quiz question gets an extra
answer **F** (or press the F key) that always counts as correct. The toolbar
also gets an **Ending (test)** button that starts the ending at once.

`dev/harness.js` runs the page script in Node.js with fake browser objects and
checks the main behaviours:

```sh
sed -n "/<script>/,/<\/script>/p" index.html | sed "1d;\$d" > dev/cw.js
node dev/harness.js
```

## Credits

Game design by Piotr. Code written with Claude Code.
