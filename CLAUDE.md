# CodinGame Autonomous Agent Farm

Ferme multi-agents autonome pour l'expérimentation et l'optimisation de bots CodinGame.
Pile 100 % .NET 10 : .NET Aspire pour l'orchestration, Microsoft Agent Framework pour les agents.

## À lire avant toute intervention

Le dossier [`.Agent/`](.Agent/) est la mémoire du projet. Commence par
[`.Agent/README.md`](.Agent/README.md), qui indexe les six autres documents.

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
   de correspondance dans `.Agent/ARCHITECTURE.md §2.B`.
5. **Aucun match ne démarre sans créneau CPU** attribué par l'`ArenaScheduler`. L'isolation
   n'est pas une optimisation de performance : un tournoi non isolé donne des résultats
   **faux**, pas lents.

Pour les documents : liens relatifs uniquement (la cible inclut Linux), pas de LaTeX
(le rendu Markdown ne l'interprète pas), chaque fichier daté et doté d'un statut.

## Structure

```
src/CodinGame.AppHost/         Orchestrateur Aspire + Dashboard
src/CodinGame.ServiceDefaults/ Télémétrie OpenTelemetry
src/CodinGame.Core/            Couche déterministe (sans LLM)
src/CodinGame.Agents/          Les 5 agents
src/CodinGame.Cli/             CLI Spectre.Console
```

## État réel du code

L'implémentation de la Phase 2 a commencé dans `CodinGame.Core/Arena/` (`ArenaScheduler`,
`MatchRunner`, `OpponentPoolManager`, `PipelineConfig`). Le reste de ce que décrit
`.Agent/` est encore à écrire : `Agents`, `Cli` et l'AppHost sont vides ou quasi vides.

Avant de supposer quoi que ce soit sur l'état du code, regarde-le. Ce fichier décrit des
intentions, `.Agent/ROADMAP.md` décrit un avancement déclaré, et seul le contenu de `src/`
dit la vérité.

- `CodinGame.Cli` est déclaré `OutputType=Exe` mais **ne contient aucun point d'entrée** :
  tel quel, la solution ne compile pas.
- `AppHost/Program.cs` ne déclare aucune ressource, donc le Dashboard n'affiche rien.
- La compilation **n'a jamais été vérifiée**.
- Trois décisions ouvertes bloquent des phases : voir le haut de `.Agent/ROADMAP.md`.

## Commandes

Compiler la solution :

```
dotnet build CodinGame.sln
```

Lancer l'AppHost et le Dashboard Aspire :

```
dotnet run --project src/CodinGame.AppHost
```
