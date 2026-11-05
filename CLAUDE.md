# CodinGame Autonomous Agent Farm

Ferme multi-agents autonome pour l'expérimentation et l'optimisation de bots CodinGame.
Pile 100 % .NET 10 : .NET Aspire pour l'orchestration, Microsoft Agent Framework pour les
agents, eux-mêmes portés par des CLI externes (Claude Code, Antigravity CLI).

## À lire avant toute intervention

Le dossier [`.Agent/`](.Agent/) est la mémoire du projet. Commence par
[`.Agent/README.md`](.Agent/README.md), qui indexe les huit autres documents.

Deux comptent plus que les autres :

- [`.Agent/PARAMETERS.md`](.Agent/PARAMETERS.md) — source unique de toute valeur numérique.
- [`.Agent/DETERMINISTIC_LAYER.md`](.Agent/DETERMINISTIC_LAYER.md) — la couche sans LLM, où vit l'essentiel du code à écrire.

## Règle fondamentale

Si une tâche est déterministe, répétable et facile à spécifier, elle doit être un script,
pas un appel LLM. Compiler, lancer un match, calculer un Elo, parser des logs, gérer Git :
du code classique. Le LLM ne dépense des tokens que là où le raisonnement est indispensable —
comprendre pourquoi une stratégie a échoué, proposer une heuristique, concevoir un changement
d'architecture, écrire le code du bot.

## Règles dures

Chacune est là parce que la violer détruit des données ou fausse les mesures.

1. **Jamais `git reset --hard`**, sous aucune condition. Une expérience ne vit que sur sa
   branche `experiment/<id>` ; l'annuler, c'est `git branch -D`, rien d'autre.
2. **Aucun chiffre en dur hors de `.Agent/PARAMETERS.md`.** Un document ou un composant qui a
   besoin d'une valeur y renvoie. Les timeouts par tour sont lus dans la configuration du
   pipeline, jamais écrits comme constante.
3. **`matches.db` fait foi** pour toute métrique. Ni les messages de commit, ni les noms de
   tags ne portent de chiffres : un Elo n'a de sens que relativement à un pool, et le pool évolue.
4. **Agent Framework (`Microsoft.Agents.AI`), pas Semantic Kernel.** `AgentGroupChat`,
   `TerminationStrategy`, `ChatCompletionAgent` et `KernelPluginFactory` sont archivés — table
   de correspondance dans `.Agent/ARCHITECTURE.md §2.B`. Les agents sont des **CLI externes**
   enveloppés en sous-classes d'`AIAgent`, jamais lancés en direct : ils passent par
   l'`AgentProcessRunner`, qui porte l'affinité CPU et le wall-clock. Et **le code de sortie
   d'un CLI ne prouve rien** : Antigravity sort en `0` quand il refuse. Voir
   `.Agent/AGENT_BACKENDS.md`.
5. **Aucun match ne démarre sans créneau CPU** attribué par l'`ArenaScheduler`. L'isolation
   n'est pas une optimisation de performance : un tournoi non isolé donne des résultats
   **faux**, pas lents.
6. **Le token CodinGame ne s'affiche jamais, ne se colle jamais dans une conversation et ne
   se committe jamais.** Il vit dans `~/.config/codingame/token`, hors du dépôt, et se lit
   à chaque appel. Tout accès à l'API passe par le `CodinGameApiGate`, jamais en direct :
   les quotas sont partagés par tous les pipelines et une seule partie de test est permise
   à la fois. Jamais de jeu d'arcade. En conteneur, `~` n'est plus celui de l'hôte : le
   token se **monte** en lecture seule, il ne se met ni dans une image, ni dans un gabarit
   suivi par git, ni dans une variable d'environnement. Voir `.Agent/CODINGAME_API.md`
   §2.1.
7. **Les credentials des CLI d'agents sont des secrets au même titre que le token**, et
   suivent les mêmes règles — montage, jamais d'image, jamais de gabarit suivi. Un agent
   tourne dans un répertoire de travail restreint au seul fichier qu'il peut modifier, et
   les credentials sont hors de sa portée : un agent de code sait lire un fichier et
   l'afficher.

Pour les documents : liens relatifs uniquement (la cible inclut Linux), pas de LaTeX
(le rendu Markdown ne l'interprète pas), chaque fichier daté et doté d'un statut.

## Structure

```
src/CodinGame.AppHost/         Orchestrateur Aspire + Dashboard
src/CodinGame.ServiceDefaults/ Télémétrie OpenTelemetry
src/CodinGame.Core/            Couche déterministe (sans LLM)
src/CodinGame.Agents/          Les 3 agents (les 2 autres rôles sont des Executor)
src/CodinGame.Cli/             CLI Spectre.Console
```

## État réel du code

Avant de supposer quoi que ce soit sur l'état du code, regarde-le. Ce fichier décrit des
intentions, `.Agent/ROADMAP.md` un avancement déclaré, et seul le contenu de `src/` dit la
vérité. Relevé le 2026-11-05 :

- **La solution compile** : `dotnet build -c Release`, 0 avertissement, 0 erreur.
- `CodinGame.Core` porte l'essentiel du travail fait (`Arena/`, `Git/`, `Metrics/`,
  `ResourceGuard/`, `Storage/`), sans qu'aucun de ces composants soit terminé ni testé.
- `CodinGame.Cli` a un point d'entrée Spectre.Console qui affiche un tableau de statut
  **codé en dur** : il ne lit pas l'état réel du système.
- `AppHost/Program.cs` déclare le projet `CodinGame.Cli` comme ressource. Le Dashboard
  devrait donc l'afficher — non vérifié.
- `CodinGame.Agents` est **vide**. Rien de la couche agentique n'existe encore : ni
  `AgentProcessRunner`, ni les sous-classes d'`AIAgent`, ni le graphe.
- Quatre décisions ouvertes bloquent des phases : voir le haut de `.Agent/ROADMAP.md`.

## Commandes

Compiler la solution :

```
dotnet build CodinGame.sln
```

Lancer l'AppHost et le Dashboard Aspire :

```
dotnet run --project src/CodinGame.AppHost
```
