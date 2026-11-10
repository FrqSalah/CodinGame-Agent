# CodinGame Agent — Pipeline Mad Pod Racing

Une boucle automatique qui améliore un bot CodinGame, pour **un seul jeu** : Mad Pod
Racing. Une IA propose un changement, un programme fait jouer le bot via l'API CodinGame,
un calcul décide s'il est meilleur, on garde ou on jette, et on recommence.

Pile : .NET 10, une application console, SQLite. Les IA sont des outils en ligne de
commande (Claude Code, Antigravity CLI) lancés par le programme.

## À lire avant toute intervention

**[`.Agent/PIPELINE_SPEC.md`](.Agent/PIPELINE_SPEC.md) est la spécification à développer.**
Elle prime sur tout autre document du dépôt.

[`.Agent/README.md`](.Agent/README.md) indique quels autres documents restent valables.
La plupart décrivent une conception plus vaste (40 pipelines, 5 rôles, arène locale), mise
de côté le 2026-11-10 : ne rien construire à partir d'eux.

## Principes

- **KISS.** Une pipeline, un jeu, une partie à la fois, des logs console.
- **DRY.** Ce que CodinGame, Claude Code, Antigravity ou SQLite font déjà n'est pas refait.
- **Ce qui est déterministe est un programme, pas un appel d'IA.** Lancer une partie, lire
  un résultat, juger, tenir le journal : du code classique. L'IA ne sert qu'à réfléchir et
  à écrire le bot.
- **Une IA ne décide jamais si un bot est meilleur.** Elle propose, le programme mesure.

## Règles dures

Chacune est là parce que la violer détruit des données, fausse les mesures ou expose un secret.

1. **Le token CodinGame ne s'affiche jamais, ne se colle jamais dans une conversation et ne
   se committe jamais.** Il vit dans `~/.config/codingame/token`, hors du dépôt, et se lit
   à chaque appel.
2. **Aucune soumission au classement.** Le code n'en contient aucune. Soumettre peut changer
   la ligue, donc les règles du jeu. C'est une décision de l'utilisateur, faite à la main.
3. **Jamais de jeu d'arcade**, et aucune modification du compte CodinGame (code de l'IDE,
   pseudo).
4. **Tout accès à CodinGame passe par `CodinGameClient`**, tout appel d'IA par
   `AgentRunner`. Une seule partie de test à la fois ; les limites se lisent dans les
   réponses de l'API.
5. **Aucune valeur en dur dans le code.** Tout réglage, y compris l'outil et le modèle de
   chaque rôle, vient du `pipeline.json` de la pipeline, relu à chaque essai.
6. **`matches.db` fait foi** pour tout résultat.
7. **Le code de sortie d'un outil d'IA ne prouve rien.** La réussite se vérifie sur ce qui
   a été produit. Un agent travaille dans un dossier qui ne contient que ce qu'il doit
   lire et le fichier qu'il peut modifier ; les secrets sont hors de sa portée.
8. **Jamais `git reset --hard`**, sous aucune condition.

Pour les documents : liens relatifs uniquement, pas de LaTeX, chaque fichier daté et doté
d'un statut.

## Structure visée

```
src/CodinGame.Core/          toute la logique
src/CodinGame.Cli/           les commandes
tests/CodinGame.Tests/       les tests
pipelines/mad-pod-racing/    config, champion, journal de cette pipeline
```

## État réel du code

Avant de supposer quoi que ce soit sur l'état du code, regarde-le. Relevé le 2026-11-10 :

- **Rien de la spécification n'est encore écrit.** Le code présent date de l'ancienne
  conception.
- La solution contient cinq projets (`AppHost`, `ServiceDefaults`, `Core`, `Agents`,
  `Cli`). La spécification en garde deux et ajoute un projet de tests : voir son §5.1.
- `CodinGame.Core` contient des composants de l'ancienne conception, à supprimer. Le
  stockage n'y est qu'une suite de « TODO ».
- `CodinGame.Cli` affiche un tableau de statut **codé en dur**.
- Il n'existe aucun test.
- `Dockerfile`, `docker-compose.yml` et `.github/workflows/deploy.yml` servent un
  déploiement serveur mis de côté. `deploy.yml` contient un `git reset --hard`, qui viole
  la règle 8.
- La solution compilait le 2026-11-05 (`dotnet build -c Release`, 0 avertissement) ; non
  revérifié depuis.

## Commandes

Compiler la solution :

```
dotnet build CodinGame.sln
```
