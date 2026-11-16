# Mad Pod Racing engine (Gold and Legend leagues)

Local games are played by the open-source engine
[robostac/coders-strike-back-referee](https://github.com/robostac/coders-strike-back-referee)
(MIT license), which its author checked against more than 500 real CodinGame games. The values
below come from that engine; the official statement (RULES.md) takes precedence in case of
disagreement.

Integer coordinates, 16000 × 9000 map, y pointing down. Angles in degrees, 0 = facing right,
clockwise on screen.

## One turn, in order

1. **Rotation** toward the target point, at most 18° per turn (no limit on the first turn).
2. **Thrust**: `v += (cos a, sin a) × thrust`, thrust from 0 to 200. BOOST = 650; on
   CodinGame a team has a single BOOST shared by its two pods (a second BOOST gives the maximum
   thrust). SHIELD: mass × 10 during that turn, and no thrust during the next 3 turns.
3. **Movement** with continuous collision detection: two pods (radius 400) touch at a distance
   of 800. Elastic collision: the impulse is applied, raised to at least 120, then applied a
   second time. A checkpoint (radius 600) is passed when the pod's center enters it.
4. **End of turn**: position rounded, speed `× 0.85` then truncated toward zero.

## Race

- 3 laps. Pods start at checkpoint 0 and first aim at checkpoint 1.
- A player whose pods pass no checkpoint for 100 turns loses.
- Maps are drawn from about a dozen layouts, shifted by a few units.
- Top speed in a straight line: about 1130 per turn at thrust 200 (about 560 at thrust 100).

## Input and output (two pods per player)

- At start: number of laps, number of checkpoints, then one line `x y` per checkpoint.
- Every turn: 4 lines `x y vx vy angle nextCheckpointId`: your two pods, then the two opponent
  pods. On the first turn, the angle is -1.
- Answer: one line per pod, `x y thrust`, `x y BOOST` or `x y SHIELD`. Thrust from 0 to 200.
