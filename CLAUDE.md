# CodinGame Agent — Pipeline Mad Pod Racing

Une boucle autonome qui améliore un bot CodinGame (Mad Pod Racing) : Antigravity propose et
code, un moteur de jeu existant fait jouer candidat et champion, un test statistique tranche,
CodinGame confirme. Déployée en continu sur un VPS, supervisée par le dashboard Aspire.

Pile : .NET 10, SQLite, Docker, Aspire 13.6. Bot en C++20. Moteur local :
robostac/coders-strike-back-referee (MIT, Go), figé à un commit dans le `Dockerfile`.

## À lire avant toute intervention

**[`.Agent/PIPELINE_SPEC.md`](.Agent/PIPELINE_SPEC.md)** décrit ce qui est construit, les
décisions et ce qui reste à vérifier. Il prime sur les autres documents de `.Agent/`, dont la
plupart décrivent une conception plus vaste, mise de côté.

## Principes

- **KISS, DRY.** Ne rien écrire que CodinGame, Antigravity, le moteur robostac ou SQLite font déjà.
- **Pas de moteur de jeu écrit par nous** : un moteur existant, au protocole `cg-brutaltester`.
- **Une IA ne décide jamais si un bot est meilleur.** Elle propose ; le SPRT tranche.

## Règles dures

1. **Le token CodinGame ne s'affiche jamais, ne se colle jamais dans une conversation, ne se
   committe jamais.** Sur le PC : `~/.config/codingame/token`. Sur le VPS :
   `/opt/codingame-farm/secrets/codingame_token`, root, lecture seule, illisible par l'agent.
2. **Aucune soumission automatique** au classement : c'est la décision de l'utilisateur.
3. **Jamais de jeu d'arcade**, aucune modification du compte CodinGame.
4. **Tout accès à CodinGame passe par `CodinGameClient`**, tout appel d'IA par `AgentRunner`.
5. **Aucune valeur en dur** : réglages dans `pipeline.json` (relu à chaque essai), limites
   lues dans les réponses de l'API.
6. **`matches.db` fait foi** pour tout résultat.
7. **Le code de sortie d'Antigravity ne prouve rien** : il sort en 0 quand il échoue. Lire le
   statut JSON et vérifier ce qui a été produit. L'agent tourne sous l'utilisateur `agent`,
   dans un dossier qui ne contient que ce qu'il doit lire et le fichier qu'il peut modifier.
8. **Jamais `git reset --hard`**, ni dans le code, ni dans le déploiement.
9. **Pousser sur `main` déploie** sur le VPS (après la CI). Ne pousser que du code testé.

Documents : liens relatifs, pas de LaTeX, chaque fichier daté et doté d'un statut.

## Structure

```
src/CodinGame.Core/          logique : config, journal, API, moteur, duel, juge, agents, boucle
src/CodinGame.Cli/           commandes (servir, essai, verifier, bilan, duel)
src/CodinGame.ServiceDefaults/ télémétrie OTLP vers le dashboard Aspire
src/CodinGame.AppHost/       lancement local avec le dashboard Aspire
tests/CodinGame.Tests/       tests ; les parties réelles tournent dans l'image Docker
pipelines/mad-pod-racing/    configuration, consignes des agents, bots de départ
deploy/                      compose, entrée du conteneur, timer de déploiement du VPS
```

## État réel (relevé le 2026-11-11)

- Déployé et en marche sur le VPS ; 14 tests verts dans l'image, dont 3 parties réelles.
- La ferme est **en pause** : le compte est en Silver, le moteur ne couvre que Gold et Legend.
- Antigravity n'est **pas encore connecté** : connexion interactive à faire par l'utilisateur.
- Aucun essai complet n'a encore tourné.

## Commandes

```
dotnet build CodinGame.sln
dotnet test --solution CodinGame.sln
dotnet run --project src/CodinGame.AppHost
```
