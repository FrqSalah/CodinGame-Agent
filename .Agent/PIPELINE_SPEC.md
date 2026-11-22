# Mad Pod Racing pipeline — design and state

> **Status**: in production on the VPS · **Last updated**: 2026-11-22
> This document describes what is built. It takes precedence over every other file in `.Agent/`.

## 1. What the pipeline does

An autonomous loop that improves a CodinGame bot, for a single game: Mad Pod Racing.

```
 LOSS ANALYSIS           ── why the champion loses its real games (LOSSES.md)
     │
 THINKER (Antigravity)   ── note: one idea, the cause it targets, what to change, why;
     │                      an unusable note is asked again once
     │
 CODER   (Antigravity)   ── applies the note to the C++ bot
     │
 BUILD (g++, C++20)      ── up to 2 returns to the coder on compile errors
     │
 LAB                     ── quick games vs the champion; a fault or a clear deficit goes back
     │                      to the coder, up to 2 times
 LOCAL DUEL              ── candidate vs champion with the robostac engine, until the SPRT verdict;
     │                      a candidate that leads at 600 games gets a second round, lower bar
     │
 CONFIRMATION            ── CodinGame test games vs 6 real players ranked above us, same maps
     │                      as the champion; rejected on a fault or a clear deficit (sign test)
 VERDICT                 ── GARDE (new champion, submitted) or JETE / FAUTE / NON_COMPILE / ECHEC_AGENT / NOTE_REFUSEE
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
| Configuration | `Core/Config/PipelineConfig.cs` | Seed + live file merged; typos rejected, faulty field named; a value never changed by hand follows the seed |
| Journal | `Core/Data/Journal.cs` | SQLite: bot sources, trials, local and API games, champions (with submission and real score), fresh-look directions, hourly ranking |
| API client | `Core/CodinGameApi/CodinGameClient.cs` | Only access to CodinGame. Token read at every call, never logged. One test game at a time, limits read from the responses |
| Engine | `Core/Arena/RefereeGame.cs` | Relay to an existing engine with the `cg-brutaltester` protocol; enforces time limits |
| Bots | `Core/Arena/BotBuilder.cs`, `BotProcess.cs` | Build cached by source hash; process driven line by line, as the `bot` user |
| Duel | `Core/Arena/Duel.cs` | Seeds 1..N, each played from both seats, in parallel, until the verdict; resumed from its journaled games after a restart |
| Judge | `Core/Judging/Sprt.cs` | Wald's sequential test (H0: 0 Elo, H1: `elo1`) |
| Confirmation | `Core/Pipeline/Confirmation.cs` | Fixed rule picking real opponents above us; game-by-game comparison with the champion |
| Ranking loop | `Core/Pipeline/RankingCheck.cs` | Fixed rule withdrawing a champion that scores clearly lower than the previous one |
| Loss analysis | `Core/Analysis/RaceAnalysis.cs`, `LossReport.cs` | Turns of a real game replayed on the engine, or of a local game from the relay; collisions, pace, cause of a loss; LOSSES.md |
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
`prompts/<role>.md` file placed there replaces the shipped template. `pipeline.defauts.json`
holds the seed last applied: a live value still equal to it follows a new default; a value
changed by hand is kept. Without that file (first start), every live value counts as set by hand.

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

A CodinGame game log (`format=json`) holds the map (`map=` in the game options) and every line
both players wrote (`stdout`), not the pods. The farm replays it on the local engine, started on
that map (robostac's `###Validate`, 3 laps as on CodinGame), with two players that write the
recorded lines again: the engine relay then gives the 4 pods of every turn, for any bot. Locally,
the engine relay of the game gives the same. The engine sends angles from -180 to 180; they are
read as CodinGame's 0 to 359.

Checked on 2026-11-20: a real test game whose bot wrote its pods to standard error, replayed:
1,187 pod states out of 1,188 identical (the last one: angle 360 against 0), same measures. On
the 20 latest ranked games, 19 replayed to their last turn with a final gap consistent with the
result; one stopped 2 turns early (our pods 100 turns without a checkpoint: the engine counts that
limit slightly differently).

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
  candidate against champion (`essais.mesures`), shown in HISTORY.md and in LAB.md. The status
  page shows the causes and the real games measured in the last 24 hours (zero shows in red).
- Until 2026-11-20 the bot wrote its pods to standard error (a « trace line »), read in the game
  logs; the current champion had none, so its real games stayed unmeasured. The replay made the
  line useless: it was removed from the coder's rules, the lab and the starting bot.

## 9. Deployment

- **CI** (`.github/workflows/ci.yml`): builds the image, which compiles and runs the tests, real
  engine games included. If everything passes, moves the `deploy` branch.
- **VPS**: a systemd timer (`codingame-farm-update.timer`) checks `deploy` every 2 minutes and
  rebuilds when it moved. No secret is stored on GitHub. The trial a restart interrupts goes on
  at the next start, under its number: if its candidate is written and the champion has not
  changed, it is judged again from its journaled duel games (in its second round if it was in
  it), without the thinker, the coder or the lab. A trial stopped earlier is closed
  `INTERROMPU`. Pushes are still grouped: each one costs the games in flight.
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
  Second round of the duel, in place of the extension: since 2026-11-18, candidates at 51 to 53 %
  of wins over 600 games were rejected (trials 176, 179, 191) since the bar is 40 Elo (about
  56 %). A duel at its cap without a verdict, whose candidate leads by a second test with a
  lower bar (`arene.elo1_prolongation`, 20 Elo, about 53 %; `llr_prolongation` 0), goes on with
  that test up to `parties_prolongation` (3,000) games. A 52.7 % candidate needs about 2,000
  games on average: with a 2,000 cap, it would be proven only half the time.
  The note is then checked: an idea label whose text is not copied from the prompt, a known
  target, at most `boucle.mots_note_max` words (also the limit the prompt states). A failing
  note is asked again once with its problems; a second failure ends the trial as
  `NOTE_REFUSEE` (no pause, not listed among the ideas tried), counted on the status page. On
  the 17 notes since the target line, only one (454 words) would have been asked again. Real
  games are replayed on the local engine for the loss analysis (section 8): every ranked game of
  any champion is measured, the trace line is gone.
- **2026-11-21**: second round checked on the VPS: trials 191 and 176 rejudged, both rejected
  (100 / 114; then 754 / 762 after a second round from 309 / 291 at 600): near ties, not small
  gains. Three games at once (`arene.parallele` 3): no timeout over 120 champion games, about
  330 games an hour instead of 220. A restart no longer loses the trial in progress: it resumes
  from its journaled duel games. A setting left at its default follows a new default (before,
  each changed default had to be edited by hand on the VPS).
- **2026-11-22**: the real-player confirmation rejects only a fault or a clear deficit (sign test
  on the games only one of the two wins, `api.confirmation_alpha` 0.05), approved by the user.
  Trial 202, better over 2,804 local games (52.4 %, second round), had been rejected at 7 / 24
  against 8 / 24 (+3 / -4): a 50 % chance result. The old rule (« more losses than the
  champion ») rejected about 4 truly better candidates in 10 at that last step. A rejudge
  against the same champion goes on from the trial's duel games instead of replaying them.
  Trial 202, rejudged as trial 209: 13 / 24 against 17 / 24 (+2 / -6, 15 % by chance), kept
  and submitted; its real score decides (rollback). A candidate is promoted only over the
  champion it was measured against: trial 208, started before 209 was kept, would otherwise
  have replaced it without facing it.

## 12. Still unverified

- What Antigravity returns when the subscription quota is exhausted.
- How often the lab really changes a candidate, and whether rollbacks happen (first real runs
  pending).
- The cause thresholds of the loss analysis: initial values, to be set from the first real
  numbers (they are in `pipeline.json`, no rebuild needed).
