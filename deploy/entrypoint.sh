#!/bin/bash
# Démarre la session de l'agent (bus D-Bus + trousseau, où Antigravity range sa connexion),
# puis la ferme. Le trousseau vit dans /home/agent, monté depuis le VPS : il survit aux
# redéploiements. Son mot de passe ne protège rien de plus que les droits du dossier.
set -euo pipefail

as_agent() {
  setpriv --reuid=agent --regid=agent --init-groups \
    env HOME=/home/agent USER=agent PATH=/usr/local/bin:/usr/bin:/bin \
        DBUS_SESSION_BUS_ADDRESS="$FARM_AGENT_DBUS" "$@"
}

chown agent:agent /home/agent
install -d -o agent -g agent -m 700 /run/agent-dbus
if [ ! -S /run/agent-dbus/bus ]; then
  as_agent dbus-daemon --session --address="$FARM_AGENT_DBUS" --fork --nopidfile --nosyslog
fi
as_agent bash -c 'printf farm-keyring | gnome-keyring-daemon --unlock --components=secrets >/dev/null 2>&1; gnome-keyring-daemon --start --components=secrets >/dev/null 2>&1' || true

mkdir -p "$FARM_DATA"
exec dotnet /app/farm.dll "$@"
