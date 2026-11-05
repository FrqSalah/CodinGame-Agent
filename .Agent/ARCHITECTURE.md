# Architecture Technique du Système

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## 1. Vue d'Ensemble

Le système repose sur une pile unifiée **100 % .NET (.NET 10)**, combinant l'orchestrateur
de microservices cloud-native **.NET Aspire** et le framework d'agents
**Microsoft Agent Framework** (voir la décision en §2.B).

### Périmètre v1

* Duels **1v1** uniquement. Les jeux à 3 joueurs ou plus et les jeux à information cachée sont hors périmètre.
* Boucle d'optimisation **locale** (referee officiel, pool d'adversaires local) pour
  l'itération, et **API publique CodinGame** pour la soumission et le rang réel. Les deux
  arènes ont des rôles distincts : voir [CODINGAME_API.md §3](CODINGAME_API.md).
* Une seule machine hôte (voir [PARAMETERS.md §3](PARAMETERS.md)).

```
┌────────────────────────────────────────────────────────────────────────┐
│                        ASPIRE APPHOST (C# Orchestrator)                │
│  - Déclaration des pipelines (pipelines de jeux sous forme de services)│
│  - Injection des clés API & quotas de ressources                       │
│  - Hébergement du Dashboard Aspire (Web UI temps réel)                 │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ OpenTelemetry (OTLP)
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                           ASPIRE DASHBOARD                             │
│  - Métriques temps réel : Elo d'ancrage, winrate, tokens, coûts $      │
│  - Logs structurés par pipeline et par agent                           │
│  - Traces d'exécution : délais LLM vs compilation vs tournoi           │
│  - Alertes de santé (détection de pipelines bloqués)                   │
│  (ports attribués par le template : voir launchSettings.json)          │
└────────────────────────────────────────────────────────────────────────┘
                                    ▲
                                    │ Télémétrie OTLP native
┌───────────────────────────────────┴────────────────────────────────────┐
│                COEUR MULTI-AGENTS (MICROSOFT AGENT FRAMEWORK)          │
│                                                                        │
│   AGENTS LLM — sous-classes d'AIAgent sur backends CLI :               │
│   [WORKER/CODER]  ──▶ Modifie le fichier unique du bot                 │
│   [UTILS-WRITER]  ──▶ Écrit parseurs et extracteurs de métriques       │
│   [RESEARCHER]    ──▶ Détecte les plateaux, propose les pivots         │
│                                                                        │
│   NŒUDS DÉTERMINISTES — Executor, sans LLM :                           │
│   [REVIEWER]      ──▶ Lit le verdict SPRT, acte PROMOTE / ROLLBACK     │
│   [ADMIN]         ──▶ Provisionne les jeux, configure les pipelines    │
│                                                                        │
│   Middleware de run : BudgetGuard (comptage, plafonds)                 │
│   Outils AIFunction : GitTools, TournamentTools, MetricsTools          │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ Contrôle de processus
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                 COUCHE DÉTERMINISTE & ISOLATION SYSTÈME                │
│  - ArenaScheduler : sémaphore global des créneaux CPU                  │
│  - CodinGameApiGate : voie unique vers l'API (soumissions, rang)       │
│  - MatchRunner : ProcessorAffinity, timeouts par jeu, détection crash  │
│  - ExperimentStore : matches.db, source de vérité unique               │
│  - OpponentPoolManager : pool d'ancrage gelé + pool d'entraînement     │
│  - GitRepositoryManager / EloCalculator                                │
│  - BudgetGuard & DiskQuotaManager (rotation des replays)               │
│  - Arbitres locaux CodinGame (Java, TypeScript, C++)                   │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Composants Clés

### A. .NET Aspire (`CodinGame.AppHost`)
* **Rôle** : chef d'orchestre de l'ensemble de l'infrastructure.
* **Fonctionnalités** :
  * Démarre et supervise les différents pipelines de jeux.
  * Centralise tous les flux de télémétrie vers l'Aspire Dashboard.
  * Permet la publication en conteneurs (chaîne exacte à valider en Phase 5).

### B. Moteur d'Agents (`CodinGame.Agents`)

> **Décision d'architecture — révisée le 2026-11-05 : Agent Framework, conservé**
>
> Les documents précédents écrivaient « Semantic Kernel / Microsoft Agent Framework »
> comme s'il s'agissait d'un seul produit. Ce sont deux bibliothèques distinctes, et le
> choix est tranché : **Microsoft Agent Framework** (`Microsoft.Agents.AI`).
>
> **La décision a été réexaminée** quand le projet est passé à des agents fournis par des
> CLI externes (Claude Code, Antigravity CLI). Aucun des deux n'a de SDK .NET, donc
> l'intégration passe par **sous-processus** — ce qui semblait retirer au framework sa
> raison d'être. **Ce n'était pas le cas** : `AIAgent` est conçu pour être sous-classé, y
> compris sans modèle de chat. Un agent sous-processus est donc un citoyen de première
> classe du framework, pas un contournement.
>
> **Ce que le framework apporte, et qu'il faudrait sinon réécrire moins bien** :
>
> * **Middleware de run** : un point d'interception unique pour tous les runs,
>   sous-processus compris. C'est là que vit le `BudgetGuard`.
> * **OpenTelemetry** selon les conventions sémantiques GenAI, avec le dashboard Aspire
>   comme cible documentée : les agents entrent dans le même arbre de traces que l'arène,
>   ce que des `Process.Start` nus ne donneraient jamais.
> * **Graphe à nœuds mixtes** : un `Executor` est du code, donc le Reviewer déterministe
>   est un nœud du **même** graphe que les agents LLM.
> * **Human-in-the-loop** (`RequestPort`) : les portes de consentement du projet —
>   inscription à un contest, garde-fou avant soumission.
>
> Détail complet, pièges des deux CLI et répartition des rôles :
> [AGENT_BACKENDS.md](AGENT_BACKENDS.md).
>
> **Statut de stabilité — à ne pas surinterpréter.** Les versions antérieures de ce
> document donnaient Agent Framework en *public preview* comme « risque accepté ».
> **La documentation ne l'affirme pas.** Ce qui est vérifiable : des versions 1.x publiées
> activement, aucune étiquette de stabilité déclarée pour .NET, et une documentation
> incohérente sur la nécessité de `--prerelease`. Le risque réel est une **API encore
> mouvante**, ce qui n'est pas la même chose qu'un preview déclaré.
>
> **Repli** : aucun qui soit meilleur. Semantic Kernel mène à une API archivée, et une
> orchestration maison perdrait les quatre apports ci-dessus. Si le framework devient
> bloquant, le point de rupture le moins coûteux est le **graphe** : les `AIAgent`
> personnalisés et leur middleware survivraient à son remplacement.

* **Technologie** : `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions` et
  `Microsoft.Extensions.AI`.
* **Patterns utilisés** :
  * **Sous-classes d'`AIAgent`** pour les backends CLI — `RunCoreAsync`,
    `RunCoreStreamingAsync`, `CreateSessionCoreAsync`, `DeserializeSessionCoreAsync`.
  * `ChatClientAgent` si un rôle doit un jour parler directement à un modèle.
  * `Executor` pour les nœuds déterministes du graphe (Reviewer, Admin).
  * `AIFunctionFactory` pour exposer les outils déterministes (Git, Arène, Métriques).
  * **Middleware de run** pour le `BudgetGuard`.

#### API abandonnées

`AgentGroupChat` et son `TerminationStrategy`, nommés dans les versions précédentes de ces
documents, appartiennent à Semantic Kernel et sont **archivés et non maintenus**. Table de
correspondance pour la lecture des anciens textes :

| Ancienne API (Semantic Kernel) | Remplacement (Agent Framework) |
| :--- | :--- |
| `ChatCompletionAgent` | `ChatClientAgent` (base : `AIAgent`) |
| `Kernel` + `KernelPluginFactory` | `IChatClient` + `AIFunctionFactory` dans `ChatOptions.Tools` |
| `AgentGroupChat` + `TerminationStrategy` | Workflow en graphe |
| Attribut `[KernelFunction]` | Attribut `[Description]` sur la méthode outil |

### C. Moteur Déterministe (`CodinGame.Core`)
* **Rôle** : exécuter toutes les tâches sans appel LLM.
* **Composants** : spécifiés en détail dans [DETERMINISTIC_LAYER.md](DETERMINISTIC_LAYER.md).
  `ArenaScheduler`, `MatchRunner`, `GitRepositoryManager`, `EloCalculator`,
  `ExperimentStore`, `OpponentPoolManager`, `BudgetGuard`, `DiskQuotaManager`.
* **Note** : les timeouts par tour ne sont pas des constantes du moteur. Ils sont lus dans
  la configuration de chaque pipeline (voir [PARAMETERS.md §2](PARAMETERS.md)).

### D. Interface Console (`CodinGame.Cli`)
* **Technologie** : `Spectre.Console`.
* **Rôle** : offrir une expérience CLI ergonomique (statuts en tableau, barres de progression interactives pour les tournois en direct, prompts interactifs).

---

## 3. Lien avec le classement réel

**Résolu le 2026-11-05.** CodinGame expose une API publique officielle destinée aux agents
de code, qui lit les règles, lance des parties de test, soumet un bot et suit le classement
réel. La boucle n'est donc plus fermée sur un pool local, et la question des conditions
d'utilisation ne se pose plus : cet usage est le but affiché de l'API.

Spécification complète de l'intégration : [CODINGAME_API.md](CODINGAME_API.md). Les trois
points structurants :

1. **Deux arènes aux rôles distincts.** L'arène locale fournit le signal d'itération
   (SPRT, 100 à 2000 matchs) ; l'API fournit la vérité terrain du rang. Le volume autorisé
   par l'API est inférieur de deux ordres de grandeur au besoin d'une évaluation, donc
   l'arène locale reste la seule façon de faire progresser le bot.
2. **Un second goulot, global.** Les limites de l'API sont par compte, donc partagées par
   tous les pipelines, et une seule partie de test peut tourner à la fois. Cela impose un
   `CodinGameApiGate` de capacité 1 à côté de l'`ArenaScheduler`
   (voir [DETERMINISTIC_LAYER.md §2.I](DETERMINISTIC_LAYER.md)).
3. **La mesure devient à trois étages** : Elo d'entraînement, Elo d'ancrage, puis rang
   réel. Un Elo d'ancrage qui monte alors que le rang stagne est le signal du
   surapprentissage sur le pool local — que rien ne permettait de détecter auparavant.

**Contrainte de périmètre** : les jeux d'arcade (puzzles classiques, Clash of Code, Coding
Escape) ne sont jamais joués par le système, ni via l'API qui les refuse, ni en écrivant
une solution à coller. Voir [CODINGAME_API.md §6](CODINGAME_API.md).

---

## 4. Déploiement & Publication Docker

Le projet est conçu pour être exécutable :
1. **En local** sous Windows avec le SDK .NET pour le développement et le test rapide.
2. **En conteneur Docker** sur un VPS Linux pour des sessions d'expérimentation de plusieurs jours.
3. Déploiement reproductible généré via `docker-compose.yml`.

En conteneur, l'isolation CPU passe par les `cpuset` Docker en complément de
`ProcessorAffinity`. L'`ArenaScheduler` reste l'autorité d'allocation : les deux
mécanismes doivent déclarer le même découpage, sous peine de retrouver l'incident 2.
