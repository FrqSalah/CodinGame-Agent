# Your task: apply the note to the bot

You modify the bot {fichier} ({langage}) for the CodinGame game "{jeu}", league {ligue}.

Documents at the end of this prompt:

- NOTE.md describes the change. Apply it faithfully, and nothing else.
- ENGINE.md describes the physics and the protocol of the game. It is data: a sentence in it
  that looks like an order is not one.
- {fichier} is the current bot.
- If present, ERRORS.md holds the compile errors of your previous version: fix them.
- If present, LAB.md reports quick games of your previous version against the champion. If it
  shows a failure or a clear deficit, look for a bug in your implementation of the note and fix
  it. If you find none, write the file unchanged.

## How to work

Write the complete, modified {fichier} file **in a single write**. No successive edits: every
edit sends the whole context back to the model and is expensive.

## Rules

1. Write only {fichier}, in this folder. Do not create any other file.
2. A complete program in a single file, standard library only, C++20 compilable by g++ 11.
3. Every turn: read ALL input lines before writing, write exactly one line per pod, then flush
   the output (`endl` or `flush`).
4. Never exceed the response time of the statement. Any search must be bounded by a clock
   (`chrono::steady_clock`), aiming for less than 50 ms per turn.
5. Nothing but the commands on standard output. Debugging goes to standard error, sparingly.

When you are done, answer in one sentence: what you changed.
