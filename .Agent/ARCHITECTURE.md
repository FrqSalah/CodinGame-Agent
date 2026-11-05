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
│   [ADMIN] ──▶ Provisionne les jeux et configure les pipelines          │
│   [RESEARCHER] ──▶ Détecte les plateaux & propose les pivots d'archi   │
│   [UTILS-WRITER] ──▶ Écrit les simulateurs & parseurs de données       │
│   [WORKER/CODER] ──▶ Modifie le code source du bot                     │
│   [REVIEWER] ──▶ Analyse les tournois et acte PROMOTE / ROLLBACK       │
│                                                                        │
│   Outils exposés comme AIFunction (tools) :                            │
│   - GitTools (branches, commits, promotion, abandon)                   │
│   - TournamentTools (soumission d'une évaluation à l'arène)            │
│   - MetricsTools (lecture de l'ExperimentStore)                        │
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

> **Décision d'architecture — 2026-11-05 : Microsoft Agent Framework**
>
> Les documents précédents écrivaient « Semantic Kernel / Microsoft Agent Framework »,
> comme s'il s'agissait d'un seul produit. Ce sont deux bibliothèques distinctes, et le
> choix est désormais tranché : **Microsoft Agent Framework** (`Microsoft.Agents.AI`).
>
> **Pourquoi** :
> * Agent Framework est le **successeur déclaré** de Semantic Kernel et d'AutoGen, écrit par les mêmes équipes.
> * Il apporte des **workflows en graphe** donnant un contrôle explicite des chemins d'exécution multi-agents — exactement ce dont la boucle Worker → Arène → Reviewer a besoin.
> * Sa gestion d'état pour les scénarios longs avec intervention humaine correspond aux cycles de 24-48 h du projet.
> * Il est bâti sur `Microsoft.Extensions.AI`, déjà référencé par la solution.
>
> **Risque accepté** : Agent Framework est en *public preview*. Pour un système destiné à
> tourner sans surveillance plusieurs jours, c'est un risque réel, assumé ici parce que
> l'alternative mène à une API archivée (voir ci-dessous).
>
> **Repli** : si l'instabilité du preview bloque, se rabattre sur l'orchestration
> Semantic Kernel `GroupChatOrchestration`, elle-même marquée expérimentale.

* **Technologie** : `Microsoft.Agents.AI` et `Microsoft.Extensions.AI`.
* **Patterns utilisés** :
  * `ChatClientAgent` (dérivé de `AIAgent`) pour chaque rôle spécialisé.
  * `AIFunctionFactory` pour exposer les outils déterministes (Git, Arène, Métriques).
  * Workflow en graphe pour la collaboration structurée et les conditions de clôture de cycle.

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
