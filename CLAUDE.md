# CodinGame Agent — Mad Pod Racing pipeline

> **Status**: in production · **Last updated**: 2026-11-22

An autonomous loop that improves a CodinGame bot (Mad Pod Racing): Antigravity proposes and
codes, an existing game engine plays candidate against champion, a statistical test decides,
real CodinGame games confirm, and the real leaderboard score has the last word. Continuously
deployed to a VPS, monitored with the Aspire dashboard and a status page.

Stack: .NET 10, SQLite, Docker, Aspire 13.6. Bot in C++20. Local engine:
robostac/coders-strike-back-referee (MIT, Go), pinned to a commit in the `Dockerfile`.

## Read before any change

**[`.Agent/PIPELINE_SPEC.md`](.Agent/PIPELINE_SPEC.md)** describes what is built, the decisions
and what remains to be checked. It takes precedence over the other documents in `.Agent/`, most
of which describe a larger design that was set aside. [`README.md`](README.md) is the public
overview.

## Principles

- **KISS, DRY.** Write nothing that CodinGame, Antigravity, the robostac engine or SQLite already do.
- **No game engine written by us**: an existing engine, with the `cg-brutaltester` protocol.
- **An AI never decides whether a bot is better.** It proposes; the SPRT, real games and the
  real score decide.

## Hard rules

1. **The CodinGame token is never printed, never pasted in a conversation, never committed.**
   On the PC: `~/.config/codingame/token`. On the VPS: `/opt/codingame-farm/secrets/codingame_token`,
   root, read-only, unreadable by the agent.
2. **Automatic submission of champions**, authorized by the user on 2026-11-11
   (`api.soumettre_champions`). Only a candidate kept by the SPRT, then without fault and not
   clearly worse than the champion against real players on CodinGame (sign test,
   `api.confirmation_alpha`, approved on 2026-11-22), is submitted; and a former
   champion restored by the ranking loop (`api.retour_si_baisse`, authorized on 2026-11-17).
   Nothing else submits.
3. **Never an arcade game**, no change to the CodinGame account.
4. **Every access to CodinGame goes through `CodinGameClient`**, every AI call through `AgentRunner`.
5. **No hard-coded values**: settings in `pipeline.json` (read at every trial), limits read from
   the API responses.
6. **`matches.db` is the source of truth** for every result.
7. **Antigravity's exit code proves nothing**: it exits with 0 when it fails. Read the JSON status
   and check what was produced. The agent runs as the `agent` user, in a folder holding only what
   it must read and the file it may modify, and runs no command. Bots (the agents' or other
   players') run as the `bot` user, without access to secrets. Code from elsewhere only runs with
   the user's approval.
8. **Never `git reset --hard`**, in code or in deployment.
9. **Pushing to `main` deploys** to the VPS (after the CI) and interrupts the running trial. Only
   push tested code, and group pushes.
10. **Everything pushed is in English** (code comments, documents, commit messages). The
    repository is public: never push the VPS address, the Tailscale network name, domain names,
    the user's nickname or e-mail.

Documents: relative links, no LaTeX, every file dated and with a status.

## Layout

```
src/CodinGame.Core/            logic: config, journal, API, engine, duel, judge, agents, trial, farm, loss analysis, status page
src/CodinGame.Cli/             commands (servir, essai, verifier, bilan, duel, rejuger, juger, banc, page);
                               bilan and verifier do not modify the journal
src/CodinGame.ServiceDefaults/ OTLP telemetry to the Aspire dashboard
src/CodinGame.AppHost/         local run with the Aspire dashboard
tests/CodinGame.Tests/         tests; real games run inside the Docker image
pipelines/mad-pod-racing/      configuration, agent prompts, starting bots
deploy/                        compose, container entrypoint, deploy timer
```

## Actual state (2026-11-17)

- The farm **runs on its own** on the VPS, Antigravity logged in.
- Account in **Legend**, around 110th of about 236,000 players. `farm bilan` and the status
  page give the up-to-date state.
- About 150 trials, 25 champions kept and submitted.

## Commands

```
dotnet build CodinGame.sln
dotnet test --solution CodinGame.sln
dotnet run --project src/CodinGame.AppHost
```
