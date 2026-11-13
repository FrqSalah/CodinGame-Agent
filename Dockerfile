# ---- Moteur du jeu : robostac/coders-strike-back-referee (MIT), version figée ----
FROM golang:1.24-bookworm AS referee
ARG CSBREF_COMMIT=f96d1960cbb861a7191760034620aaa51f057767
RUN git clone --quiet https://github.com/robostac/coders-strike-back-referee /src/csbref \
 && cd /src/csbref && git checkout --quiet "$CSBREF_COMMIT" \
 && CGO_ENABLED=0 go build -o /out/csbref csbref.go

# ---- Adversaires extérieurs du banc : bots publiés par d'autres joueurs, versions figées ----
# regismeyssonnier/MadPodRacingLegend (GPL-3.0) et inoryy/csb-ai-starter (MIT). Utilisés seulement
# comme adversaires des parties locales, jamais recopiés dans ce dépôt ni distribués.
FROM golang:1.24-bookworm AS sparring
ARG REGIS_COMMIT=8cd3355ad408f1e1aa4fd83d3090e117a6dc5b70
ARG INORYY_COMMIT=6d1132b302374c208aa56c4832ce8214dd22dc5b
RUN git clone --quiet https://github.com/regismeyssonnier/MadPodRacingLegend /src/regis \
 && git -C /src/regis checkout --quiet "$REGIS_COMMIT" \
 && git clone --quiet https://github.com/inoryy/csb-ai-starter /src/inoryy \
 && git -C /src/inoryy checkout --quiet "$INORYY_COMMIT" \
 && mkdir -p /out/sparring \
 && cp /src/regis/madpod_best245thlegendbeam.cpp /src/regis/madpod353thlegend.cpp \
       /src/regis/MadPopdLegend670th.cpp /src/regis/maspodlegend770thbestalltime.cpp /out/sparring/ \
 && cp /src/inoryy/main.cpp /out/sparring/inoryy-starter.cpp \
 && cp /src/regis/LICENSE /out/sparring/LICENSE-regismeyssonnier \
 && cp /src/inoryy/LICENSE /out/sparring/LICENSE-inoryy

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
COPY --from=sparring /out/sparring /app/sparring
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
