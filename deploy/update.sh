#!/bin/bash
# Lancé toutes les 2 minutes par codingame-farm-update.timer. Déploie la branche « deploy »,
# que la CI avance seulement quand la compilation, les tests et l'image ont réussi.
# Le corps est dans une fonction : bash la lit en entier avant de l'exécuter, ce qui
# permet à ce script de se mettre à jour lui-même sans risque.
main() {
  set -euo pipefail
  cd /opt/codingame-farm/repo
  git fetch --quiet origin deploy
  local target current
  target=$(git rev-parse FETCH_HEAD)
  current=$(git rev-parse HEAD)
  if [ "$target" = "$current" ] && [ "${1:-}" != "--force" ]; then
    exit 0
  fi
  echo "déploiement ${current:0:7} -> ${target:0:7}"
  git -c advice.detachedHead=false checkout --quiet --detach "$target"
  docker compose -f deploy/docker-compose.yml up -d --build --remove-orphans
  docker image prune -f >/dev/null
  echo "déployé ${target:0:7}"
}
main "$@"
exit
