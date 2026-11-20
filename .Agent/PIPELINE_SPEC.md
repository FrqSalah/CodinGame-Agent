# Mad Pod Racing pipeline — design and state

> **Status**: in production on the VPS · **Last updated**: 2026-11-20
> This document describes what is built. It takes precedence over every other file in `.Agent/`.

## 1. What the pipeline does

An autonomous loop that improves a CodinGame bot, for a single game: Mad Pod Racing.

```
 LOSS ANALYSIS           ── why the champion loses its real games (LOSSES.md)
     │
 THINKER (Antigravity)   ── note: one idea, the cause it targets, what to change, why
     │
 CODER   (Antigravity)   ── applies the note to the C++ bot
     │
 BUILD (g++, C++20)      ── up to 2 returns to the coder on compile errors
     │
 LAB                     ── quick games vs the champion; a fault or a clear deficit goes back
     │                      to the coder, up to 2 times
 LOCAL DUEL              ── candidate vs champion with the robostac engine, until the SPRT verdict
     │
 CONFIRMATION            ── CodinGame test games vs 6 real players ranked above us, same maps
     │                      as the champion
 VERDICT                 ── GARDE (new champion, submitted) or JETE / FAUTE / NON_COMPILE / ECHEC_AGENT
     │
 JOURNAL (matches.db)    ──► read by the thinker at the next trial
     │
 FRESH LOOK (Antigravity) ── after 6 trials without promotion: a new direction
     │
 RANKING LOOP            ── hourly ranking; a champion that scores clearly lower than the
                            previous one on the real leaderboard is withdrawn and the previous
                            one is resubmitted
```

Principles: KISS, DRY, and **an AI never decides whether a bot is better**: a calculation (SPRT,
real games, real score) decides; the AI proposes.

## 2. Decisions

| Decision | Origin |
| :--- | :--- |
| One pipeline, one game (Mad Pod Racing), duplication later | User |
| Final goal: top 5 of the leaderboard | User |
| **Existing** local engine, not written by us: robostac/coders-strike-back-referee (MIT, checked by its author on 500 real games) | User |
| Agents: **Antigravity CLI**, with the user's subscription | User |
| Tool and model of each role in `pipeline.json`, read again at every trial, no rebuild | User |
| Automatic deployment to the VPS on every push to `main` validated by the CI | User |
| Monitoring with the Aspire dashboard; remote access through Tailscale only | User |
| Bot in C++ (4 of the top 5 players) | Proposed, accepted |
| Separate thinker and coder | User |
| Strongest model of each role first, automatic fallback remembered on disk | User, 2026-11-13 |
| Every new champion is submitted without waiting for the user | User, 2026-11-11 |
| Confirmation against real players instead of the saturated local bench | User, 2026-11-16 |
| Coder's lab and rollback on the real leaderboard | User, 2026-11-17 |
| Everything pushed to the (public) repository is in English | User, 2026-11-16 |

## 3. Leagues

The robostac engine only simulates the full game with **two pods per player** (Gold and
Legend). The farm pauses itself in any other league. The account left Silver on 2026-11-11
thanks to `pipelines/mad-pod-racing/silver/Bot.cpp`, reached Legend the same day, and is around
110th of about 236,000 players on 2026-11-17.

## 4. Code

| Part | File | Role |
| :--- | :--- | :--- |
| Configuration | `Core/Config/PipelineConfig.cs` | Seed + live file merged; typos rejected, faulty field named |
| Journal | `Core/Data/Journal.cs` | SQLite: bot sources, trials, local and API games, champions (with submission and real score), fresh-look directions, hourly ranking |
| API client | `Core/CodinGameApi/CodinGameClient.cs` | Only access to CodinGame. Token read at every call, never logged. One test game at a time, limits read from the responses |
| Engine | `Core/Arena/RefereeGame.cs` | Relay to an existing engine with the `cg-brutaltester` protocol; enforces time limits |
| Bots | `Core/Arena/BotBuilder.cs`, `BotProcess.cs` | Build cached by source hash; process driven line by line, as the `bot` user |
| Duel | `Core/Arena/Duel.cs` | Seeds 1..N, each played from both seats, in parallel, until the verdict |
| Judge | `Core/Judging/Sprt.cs` | Wald's sequential test (H0: 0 Elo, H1: `elo1`) |
| Confirmation | `Core/Pipeline/Confirmation.cs` | Fixed rule picking real opponents above us; game-by-game comparison with the champion |
| Ranking loop | `Core/Pipeline/RankingCheck.cs` | Fixed rule withdrawing a champion that scores clearly lower than the previous one |
| Loss analysis | `Core/Analysis/RaceAnalysis.cs`, `LossReport.cs` | Turns of a game from the trace line or the engine relay; collisions, pace, cause of a loss; LOSSES.md |
| Agents | `Core/Agents/AgentRunner.cs`, `FallbackAgentRunner.cs` | `agy -p`; JSON status read (the exit code proves nothing); duration bounded by us; separate `agent` user; model chain with fallback |
| Trial | `Core/Pipeline/Trial.cs` | Chains the steps of §1 |
| Farm | `Core/Pipeline/Farm.cs` | Continuous loop, automatic pauses (login, quota, league, configuration), hourly ranking and rollback |
| Status page | `Core/Report/StatusPage.cs` | Script-free HTML: ranking over time, champion and real scores, trials, agents |
| Commands | `Cli/Program.cs` | `servir`, `essai`, `verifier`, `bilan`, `duel <a> <b> [n]`, `rejuger <trial>`, `juger <source> <idea> [note]`, `banc`, `page <file>` |
| Local launcher | `AppHost/` | Aspire: farm + dashboard on the development machine |

Adding a game: a folder in `pipelines/` with its configuration, prompts, starting bot and the
command of its `cg-brutaltester`-compatible engine. No code to write.

## 5. Pipeline folder

Seed (in the repository, shipped in the image): `pipelines/mad-pod-racing/`:
`pipeline.json`, `consignes.md` (user guidance), `moteur.md` (engine notes), `strategie.md`
(strategy sheet), `prompts/*.md`, `bot/Bot.cpp`, `silver/Bot.cpp`. Agent-facing texts are in
English.

Live data (on the VPS, under the data folder `pipelines/mad-pod-racing/`): `pipeline.json` and
`consignes.md` editable while running, `champion.cpp`, `regles.md` (official statement, in the
account's language), `orientation.md`, `matches.db`, `essais/NNNN/`, `cache/`. A
`prompts/<role>.md` file placed there replaces the shipped template.

## 6. The coder's lab (2026-11-17)

The agents used to write blind: one pass, then the judge. Now, after each build, the farm plays
`laboratoire.parties` (40) quick games against the champion (same engine and limits, never
extended, not journaled). If the candidate faults or wins less than
`laboratoire.seuil_revision` (45 %), the result goes back to the coder as `LAB.md`, which may
fix a bug in its implementation or keep its version unchanged; at most
`laboratoire.revisions_max` (2) times. A revision that does not compile is ignored. Only the
judge decides afterwards. The agent still runs no command: the farm plays the games, as the
`bot` user.

## 7. The ranking loop (2026-11-17)

Every `api.releve_classement_minutes` (60), the farm records the rank and score of the current
submission. When a submission is fully ranked (100 %), its score is stored with the champion
that produced it. If the current champion's real score is lower than the previous standing
champion's by more than `api.retour_si_baisse` (1.0 point), it is marked withdrawn, the previous
champion becomes champion again and is resubmitted. The agents see the real scores and the
withdrawals in their history. Only the current champion can be withdrawn, never twice against
the same comparison; 0 disables the rule.

## 8. Loss analysis (2026-11-19)

Every bot writes, every turn, one line to standard error: `T` then the 24 integers it read
(x y vx vy angle nextCheckPointId of the 4 pods). CodinGame keeps our standard error in test games
and in ranked games (checked on 2026-11-19: 297 complete lines out of 297 turns, at most 107
characters; it sometimes merges two turns in one frame). With the referee's lines, which give
every pod's command, the farm has the whole game. Locally, the engine relay gives the same.

- Collision: a pod whose next state is more than `analyse.seuil_choc` (25) away from its
  no-collision prediction (rotation at most 18°, thrust, rounding, `× 0.85` truncated). Checked on
  a real game: 1,040 pod-turns within 10, 144 beyond 40, none between. It is paired with the
  nearest pod that also deviated, or else the nearest pod (a shielded pod barely moves).
- Runner: the team's pod that passed more checkpoints. Pace: its turns per checkpoint on stretches
  where it hit nothing.
- Cause of a lost game, first rule that applies: fault; blocking (our runner hit at least
  `ecart_chocs` (3) times more than theirs); speed (pace at least `ecart_cadence` (0.5) worse);
  finish (lost by at most `arrivee_serree` (1) checkpoint); other.
- Sources, all in the `analyses` table: every local duel game, every confirmation game, and up to
  `parties_classees_par_releve` (30) new ranked games of the champion per hourly ranking.
- The thinker and the fresh look get LOSSES.md (real games of the champion only). The thinker
  writes `TARGET:` (stored as `essais.cause_visee`); each trial stores its duel's measures,
  candidate against champion (`essais.mesures`), shown in HISTORY.md and in LAB.md. The lab sends
  a version without the trace line back to the coder. The status page shows the causes.
- The champion is not modified to add the line (a resubmission would reset its ranking): the
  coder adds it to the next candidates, and the first kept one brings it to the leaderboard.

## 9. Deployment

- **CI** (`.github/workflows/ci.yml`): builds the image, which compiles and runs the tests, real
  engine games included. If everything passes, moves the `deploy` branch.
- **VPS**: a systemd timer (`codingame-farm-update.timer`) checks `deploy` every 2 minutes and
  rebuilds when it moved. No secret is stored on GitHub. A push interrupts the running trial:
  group pushes.
- **Containers** (`deploy/docker-compose.yml`): `farm`, `dashboard` (Aspire 13.6) and `vps`, an
  official OpenTelemetry collector sending the machine's state (CPU, load, memory, disks) every
  30 seconds.
- **Secrets**: CodinGame token read-only, owned by root, unreadable by the `agent` and `bot`
  users (checked); Antigravity login in the `agent` user's keyring, mounted from the VPS.
  Nothing in the image, nothing in the repository.
- **Remote access**: the status page (port 18890) and the dashboard (18888) only listen on the
  VPS. They are published on the user's private Tailscale network with `tailscale serve`
  (ports 8443 and 10000). Nothing is open on the Internet for the farm.

## 10. Operations

```
# Without Tailscale: SSH tunnel, then http://localhost:18890 (page) and http://localhost:18888 (dashboard)
ssh -L 18890:127.0.0.1:18890 -L 18888:127.0.0.1:18888 <vps>

# Antigravity login, once
ssh -t <vps> 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec -it farm login-antigravity'

# State, summary
ssh <vps> 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec farm dotnet /app/farm.dll verifier'
ssh <vps> 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec farm dotnet /app/farm.dll bilan'
```

## 11. History

- **2026-11-11**: first real run, 27 trials, Silver to Gold, then Legend. Fixed: silent empty
  answers (a denied file read; now an explicit error), the 400-word thinker note, `bilan`
  closing the running trial. Token cost per trial reduced (documents inlined, single-write
  coder, bounded rejected-ideas list).
- **2026-11-12**: duels that reach their cap while the candidate leads are extended once
  (`arene.parties_prolongation`); idea extraction fixed.
- **2026-11-13**: model chain with fallback; local bench of outside bots (published Legend bots,
  GPL-3, and inoryy/csb-ai-starter, MIT, pinned, never copied here); bots isolated as the `bot`
  user; thrust 200 and a team-shared BOOST from the official rules (trial 100); `juger` command;
  hand-written full-simulation bot judged and kept (trial 103).
- **2026-11-14**: an unfinished answer retries with the next model; status page and hourly
  ranking; Tailscale access.
- **2026-11-16**: confirmation against real players; local bench emptied (saturated); ranking
  loop survives HTTP timeouts; agent prompts and documents in English.
- **2026-11-17**: coder's lab; ranking loop with rollback; real scores on the page and in the
  agents' history. Fixed: since the real-player confirmation (2026-11-16, 48 test games per
  candidate), the quota check compared that need with the 10-per-minute window and paused the
  farm for about 29 hours. Only windows large enough to hold the need count now, and test games
  wait for an empty short window to refill instead of failing.
- **2026-11-19**: the test game quota is checked when a candidate reaches the confirmation
  (waiting up to 6 hours), no longer before every trial; `farm verifier` shows every window;
  the status page shows the best rank ever held (fully ranked readings only); loss analysis
  (section 8).
- **2026-11-20**: the thinker's note starts at its last `IDEA:` label. Trial 189's thinker sent
  its draft first (the prompt's format quoted, a word count) and the idea was read as « one
  sentence. »; the draft is now dropped before the idea, the target and the coder's note.

## 12. Still unverified

- What Antigravity returns when the subscription quota is exhausted.
- How often the lab really changes a candidate, and whether rollbacks happen (first real runs
  pending).
- The cause thresholds of the loss analysis: initial values, to be set from the first real
  numbers (they are in `pipeline.json`, no rebuild needed).
