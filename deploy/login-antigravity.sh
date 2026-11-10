#!/bin/bash
# Connexion à Antigravity, à faire une fois, par l'utilisateur lui-même :
#   ssh -t ovh-vps 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec -it farm login-antigravity'
# Choisir « Google OAuth », ouvrir le lien affiché sur un autre appareil, se connecter,
# recopier le code dans ce terminal, accepter de faire confiance au dossier /data, puis /quit.
set -euo pipefail
cd /data
exec setpriv --reuid=agent --regid=agent --init-groups \
  env HOME=/home/agent USER=agent PATH=/usr/local/bin:/usr/bin:/bin \
      DBUS_SESSION_BUS_ADDRESS="$FARM_AGENT_DBUS" AGY_CLI_DISABLE_AUTO_UPDATE=true \
      TERM="${TERM:-xterm-256color}" SSH_CONNECTION="${SSH_CONNECTION:-container 0 container 22}" \
      agy
