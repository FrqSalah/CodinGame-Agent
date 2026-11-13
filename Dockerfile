# ---- Moteur du jeu : robostac/coders-strike-back-referee (MIT), version figée ----
FROM golang:1.24-bookworm AS referee
ARG CSBREF_COMMIT=f96d1960cbb861a7191760034620aaa51f057767
RUN git clone --quiet https://github.com/robostac/coders-strike-back-referee /src/csbref \
 && cd /src/csbref && git checkout --quiet "$CSBREF_COMMIT" \
 && CGO_ENABLED=0 go build -o /out/csbref csbref.go

# ---- Compilation et tests (les tests jouent de vraies parties avec le moteur) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends g++ && rm -rf /var/lib/apt/lists/*
COPY --from=referee /out/csbref /usr/local/bin/csbref
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props CodinGame.sln ./
COPY src/ src/
COPY tests/ tests/
COPY pipelines/ pipelines/
RUN dotnet test --project tests/CodinGame.Tests/CodinGame.Tests.csproj -c Release
RUN dotnet publish src/CodinGame.Cli/CodinGame.Cli.csproj -c Release -o /app

# ---- Exécution ----
FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
      g++ ca-certificates curl dbus dbus-x11 gnome-keyring libsecret-tools util-linux \
 && rm -rf /var/lib/apt/lists/*

# Antigravity CLI : script officiel, binaire vérifié par SHA-512, mises à jour automatiques coupées.
RUN curl -fsSL https://antigravity.google/cli/install.sh -o /tmp/agy-install.sh \
 && bash /tmp/agy-install.sh --dir /usr/local/bin \
 && rm -rf /tmp/agy-install.sh /root/.cache/antigravity \
 && agy --version

# L'agent et les bots tournent chacun sous leur propre utilisateur, sans accès au token
# CodinGame ni à la connexion d'Antigravity.
RUN useradd --create-home --uid 1001 agent && useradd --no-create-home --uid 1002 bot

COPY --from=referee /out/csbref /usr/local/bin/csbref
COPY --from=build /app /app
COPY deploy/entrypoint.sh /usr/local/bin/entrypoint
COPY deploy/login-antigravity.sh /usr/local/bin/login-antigravity
RUN chmod 755 /usr/local/bin/entrypoint /usr/local/bin/login-antigravity

ENV FARM_DATA=/data \
    FARM_SEED=/app/pipelines \
    CODINGAME_TOKEN_FILE=/run/secrets/codingame_token \
    FARM_AGENT_USER=agent \
    FARM_BOT_USER=bot \
    FARM_AGENT_DBUS=unix:path=/run/agent-dbus/bus \
    AGY_CLI_DISABLE_AUTO_UPDATE=true

ENTRYPOINT ["entrypoint"]
CMD ["servir"]
