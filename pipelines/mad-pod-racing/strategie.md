# What wins at Mad Pod Racing

A summary of the techniques used by the best bots of this game (formerly Coders Strike Back),
from guides and post-mortems published by top players. It is a map of directions that proved
themselves, not a recipe to apply in one go: a trial should move the bot forward by **one
step** in one of them.

## 1. Simulate exactly, then search

All top bots do the same thing every turn: they **simulate the exact physics** of the game
(see ENGINE.md) several turns ahead, and **search** for the sequence of actions that gives the
best final state.

- **The simulation must be faithful** to the engine: rotation limited to 18°, thrust, elastic
  collisions with a minimum impulse, friction of 0.85, positions rounded and speeds truncated at
  the end of every turn, a checkpoint passed as soon as the center enters the circle. An
  approximate simulation optimizes the wrong problem: it is the first thing to check before
  tuning anything else.
- **What is searched**: for each pod and each turn of the horizon, an angle change (between
  -18° and +18°) and a thrust (0 to 200), plus possibly BOOST or SHIELD. The target point sent
  to the game follows from the chosen angle.
- **Horizon**: 4 to 8 turns. Beyond that, the uncertainty about the opponent dominates.
- **Methods that work**: genetic algorithm, simulated annealing, or random mutation keeping the
  best solutions. The choice matters less than the number of simulations per turn.
- **Reuse the previous turn's solution**, shifted by one turn, as a starting point: the gain is
  immediate and free.

## 2. Use all the available time

- The strength of a search grows with the number of simulations. The best bots use most of the
  allowed time (statement: first turn and following turns), with a safety margin of a few
  milliseconds.
- The first turn has much more time: use it to search longer, and to decide the starting BOOST.
- Code speed is therefore a strategy in its own right: fixed-size arrays, no allocation in the
  loop, precomputed sine and cosine tables, `#pragma GCC optimize`.

## 3. Optimize both pods together

- The two pods are not independent: they hinder or help each other through collisions. The best
  bots search the actions of **both pods in the same simulation**, scored by a single
  evaluation.
- **Roles**: a **runner** (the most advanced) and a **blocker**. The role is reassessed every
  turn from progress, with a little hysteresis to avoid constant switching.

## 4. A good evaluation function

It decides everything. The elements that come back in the best bots:

- **Runner progress**: checkpoints passed × a large constant, minus the distance to the next
  checkpoint. Adding a term for orientation or speed toward the checkpoint helps anticipate the
  next turn.
- **Gap with the opponent**: our best runner against **their** best runner. Hindering their
  runner is worth as much as advancing ours.
- **Blocker**: stand **in front of** the opponent's runner, between it and its next checkpoint,
  facing it, rather than chasing it from behind.
- **Loss and win**: race finished, or 100 turns without a checkpoint, are worth ±infinity.

## 5. Predict the opponent

- The simulation must move the opponent's pods, otherwise the bot believes they stand still.
  The simplest model, already effective: the opponent aims at its next checkpoint at full
  thrust.
- Better: spend a small share of the time searching the **opponent's** best actions with the
  same search, then search our actions against that plan.

## 6. BOOST and SHIELD

- BOOST: one per team and per race on CodinGame, shared by both pods. Most often on the first
  turn, or on the longest straight line; better left to the search than to a fixed rule.
- SHIELD: multiplies the mass by 10 for one turn, but cuts thrust for the next 3 turns. Useful
  to absorb or deliver a decisive hit; costly if triggered too often. Leave it to the
  simulation.

## Suggested order of progress

1. Exact, verified simulation.
2. Simple search for the runner, reusing the previous solution.
3. Evaluation comparing both teams, and a blocker placed in front of the opponent.
4. A joint search for both pods.
5. An opponent model, then an opponent search.
6. Code speed and use of all the available time.

Before proposing an idea, place the current champion on this list: the largest margin is
usually at the first step it does not fully cover yet.

## Public references

- Magus's guide to Coders Strike Back (simulation and genetic algorithm):
  http://files.magusgeek.com/csb/csb.html
- Post-mortems by Legend players: https://tcourreges.github.io/coders-strike-back and
  https://medium.com/@jstnlef/postmortem-of-coders-strike-back-f4d642d270ca
