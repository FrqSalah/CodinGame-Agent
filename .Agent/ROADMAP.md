# Feuille de Route du Projet (Roadmap)

> **Statut** : à jour · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## Statut Actuel : Phase 1 terminée, build vérifié

- [x] Analyse approfondie du retour d'expérience du n°1 mondial CodinGame.
- [x] Décision d'architecture unifiée **100 % .NET (.NET 10)**.
- [x] Validation de **.NET Aspire** (AppHost, Dashboard OTLP, orchestration de conteneurs).
- [x] Choix du framework d'agents : **Microsoft Agent Framework** (`Microsoft.Agents.AI`), successeur de Semantic Kernel. Décision et justification en [ARCHITECTURE.md §2.B](ARCHITECTURE.md).
- [x] Rédaction du socle de connaissances dans le dossier `.Agent/`.
- [x] Centralisation des paramètres numériques dans [PARAMETERS.md](PARAMETERS.md).

### Décisions tranchées le 2026-11-05

- [x] **Mesure du rang réel sur CodinGame** — l'API publique officielle le fournit. Spécification : [CODINGAME_API.md](CODINGAME_API.md). Token obtenu par device flow OAuth.
- [x] **Format de soumission du bot** — une **chaîne de code unique**, donc un fichier unique, pas de projet multi-fichiers. Voir [CODINGAME_API.md §7](CODINGAME_API.md).
- [x] **Langage du bot — C#.** Le seul bloquant était la disponibilité par jeu : relevée, elle est acquise. Les 12 duels 1v1 les mieux notés proposent tous C#, et `connect-4` en expose 27 dont C#, F# et VB.NET. Le bot reste donc sur la pile .NET du projet, sans exception à justifier. Voir [CODINGAME_API.md §9.3](CODINGAME_API.md).
- [x] **Agents portés par des CLI externes** — Claude Code et Antigravity CLI, enveloppés en sous-classes d'`AIAgent`. Aucun des deux n'a de SDK .NET, donc l'intégration passe par sous-processus : c'est la voie que leur documentation prescrit. Spécification : [AGENT_BACKENDS.md](AGENT_BACKENDS.md).
- [x] **Agent Framework conservé**, décision réexaminée et non reconduite par défaut. `AIAgent` est conçu pour être sous-classé sans modèle de chat, donc un agent sous-processus est un citoyen de première classe. Ce qu'on garde : middleware de run, OpenTelemetry vers Aspire, graphe à nœuds mixtes, human-in-the-loop. Voir [ARCHITECTURE.md §2.B](ARCHITECTURE.md).
- [x] **Reviewer et Admin ne sont pas des agents** — ce sont des `Executor` déterministes du même graphe. Trois agents, deux exécuteurs. Voir [MULTI_AGENT_SPEC.md](MULTI_AGENT_SPEC.md).
- [x] **Le contest en cours n'est pas le jeu témoin** — fenêtre de quelques jours, règles fermées sans inscription, et un contest disparaît alors qu'une mesure comparable entre cycles exige un jeu rejouable. Motifs en [CODINGAME_API.md §9.4](CODINGAME_API.md).

### Décisions encore ouvertes

- [ ] **Estimateur Elo** : mise à jour itérative à facteur K, ou ajustement par maximum de vraisemblance (Bradley-Terry) — voir [DETERMINISTIC_LAYER.md §2.C](DETERMINISTIC_LAYER.md).
- [ ] **À quoi sert le second backend ?** Répartition des rôles, comparaison A/B de la qualité des agents, ou secours mutuel. Les trois lectures ne coûtent pas la même chose : si c'est l'A/B, le backend devient une variable expérimentale à enregistrer dans `matches.db`, donc un changement de schéma. Voir [AGENT_BACKENDS.md §13](AGENT_BACKENDS.md).
- [ ] **Store de checkpoints durable en .NET** — existe-t-il ? La documentation d'Agent Framework ne démontre côté .NET qu'un store en mémoire, qui ne survit pas au crash qu'on veut couvrir. À lever avant de compter sur la reprise. Voir [AGENT_BACKENDS.md §10](AGENT_BACKENDS.md).
- [ ] **Authentification headless d'Antigravity sur un VPS** — elle repose sur des credentials mis en cache par une session interactive, et aucune voie par clé d'API n'est documentée. Bloquant pour la Phase 5. Voir [AGENT_BACKENDS.md §8](AGENT_BACKENDS.md).

---

## Phases de Développement

### Phase 0 : Preuve de déterminisme (un seul match)
* Objectif : prouver qu'un match s'exécute de bout en bout de façon reproductible. C'est l'étape qui retire le vrai risque du projet ; tout le reste en dépend.
* **Critère d'acceptation** : deux exécutions du même match, même seed, même code, produisent un résultat identique — et le relancer après un redémarrage de la machine donne encore le même résultat.
- [ ] Un referee officiel intégré, un bot témoin trivial, un adversaire fixe.
- [ ] Timeouts lus depuis la configuration du pipeline, jamais codés en dur.
- [ ] Seeds enregistrés et rejouables.

### Phase 1 : Socle de la Solution & Aspire
* Objectif : avoir une solution .NET propre qui compile et lance le Dashboard Aspire.
* **Critère d'acceptation** : `dotnet build` sans erreur, et le Dashboard Aspire s'ouvre en listant les ressources déclarées.
- [x] Création de la solution `.sln`.
- [x] Création du projet `CodinGame.AppHost` (Aspire).
- [x] Création du projet `CodinGame.ServiceDefaults` (télémétrie, métriques OpenTelemetry).
- [x] Création du projet `CodinGame.Core` (logique déterministe).
- [x] Création du projet `CodinGame.Agents` (logique agentique).
- [x] Création du projet `CodinGame.Cli` (interface Spectre.Console).
- [x] **Vérifier que la solution compile** — `dotnet build -c Release` : 0 avertissement, 0 erreur (vérifié le 2026-11-05).
- [x] Déclarer au moins une ressource dans l'AppHost — `Program.cs` déclare le projet `CodinGame.Cli`.
- [ ] Ouvrir le Dashboard Aspire et vérifier qu'il liste bien cette ressource : le critère d'acceptation porte sur les deux moitiés, et seule la compilation est faite.

### Phase 2 : Couche Déterministe C#
* Objectif : disposer d'un terrain d'entraînement hermétique, fiable et sans LLM.
* **Critère d'acceptation** : une évaluation complète (SPRT jusqu'au verdict) tourne sans LLM, sous isolation CPU, et écrit son verdict dans `matches.db`.
- [ ] Implémentation de l'`ArenaScheduler` (sémaphore global des créneaux CPU) — **à faire en premier** : sans lui, toutes les mesures des composants suivants sont invalides.
- [ ] Implémentation de l'`ExperimentStore` (`matches.db`, source de vérité unique).
- [ ] Implémentation du `MatchRunner` (processus, timeouts par jeu, affinité CPU, appariement des places).
- [ ] Implémentation du `GitRepositoryManager` (branches `experiment/<id>`, abandon par suppression de branche, jamais de `reset --hard`).
- [ ] Implémentation de l'`OpponentPoolManager` (pool d'ancrage gelé + pool d'entraînement).
- [ ] Implémentation de l'`EloCalculator` (Elo d'ancrage, Elo d'entraînement, verdict SPRT).
- [ ] Implémentation du `BudgetGuard` (comptage, application des plafonds) — à brancher en **middleware de run** et non sur les sites d'appel, voir [DETERMINISTIC_LAYER.md §2.H](DETERMINISTIC_LAYER.md).
- [ ] Implémentation du `DiskQuotaManager` (purge automatique des replays volumineux).
- [ ] Implémentation du `CodinGameApiGate` (voie unique vers l'API : sémaphore de capacité 1, lecture des limites à chaque réponse, respect des `Retry-After`, soumission réservée aux `PROMOTE`).
- [ ] Tests unitaires sur l'`EloCalculator` et le verdict SPRT — ce sont les calculs dont une erreur silencieuse fausserait toutes les décisions du système.

### Phase 3 : Backends CLI, Agents et Workflow (Agent Framework)
* Objectif : brancher les CLI d'agents, leur donner les outils déterministes, et câbler le graphe.
* **Critère d'acceptation** : un cycle complet se déroule sans intervention, et s'arrête proprement sur chacun des plafonds de [PARAMETERS.md §5](PARAMETERS.md) quand on les provoque volontairement.
- [ ] Implémentation de l'`AgentProcessRunner` — **à faire en premier** : tous les agents en dépendent, et c'est lui qui porte l'affinité CPU et le wall-clock ([DETERMINISTIC_LAYER.md §2.J](DETERMINISTIC_LAYER.md)).
- [ ] Sous-classes d'`AIAgent` : `ClaudeCodeAgent` et `AntigravityAgent`, avec **normalisation des deux sémantiques d'échec** — Antigravity sort en `0` quand il refuse ([AGENT_BACKENDS.md §4](AGENT_BACKENDS.md)).
- [ ] Création des outils `AIFunction` : `GitTools`, `TournamentTools`, `MetricsTools`.
- [ ] Configuration des trois agents : `WorkerCoderAgent`, `UtilsWriterAgent`, `ResearcherAgent`.
- [ ] Implémentation des deux `Executor` déterministes : Reviewer et Admin.
- [ ] Implémentation du workflow en graphe pilotant la boucle d'itération.
- [ ] `BudgetGuard` branché en middleware de run, avec la grille de tarif des tokens Antigravity ([PARAMETERS.md §6](PARAMETERS.md)).
- [ ] **Calibrer par la mesure** le nombre de cœurs réservés aux agents ([PARAMETERS.md §3](PARAMETERS.md)) : la valeur du tableau est une hypothèse, pas un relevé.
- [ ] Implémentation des garde-fous : plafonds d'itérations, de tours, de wall-clock, de dépense, heartbeat, durée de tournoi.

### Phase 4 : Premier Banc d'Essai Réel (Jeu Témoin)
* Objectif : valider une boucle complète autonome sur un vrai jeu CodinGame.
* **Critère d'acceptation** : le système enchaîne au moins 5 cycles sans intervention humaine, l'Elo d'ancrage progresse de façon statistiquement établie, **et le rang réel sur CodinGame progresse aussi**.
- [ ] **Confirmer le jeu témoin. Recommandation : `connect-4`** — critères, candidats mesurés et motifs en [CODINGAME_API.md §9](CODINGAME_API.md). Le choix n'est pas tracé comme décidé tant qu'il n'est pas confirmé.
- [x] Relevé des langages disponibles, et choix du langage du bot — voir les décisions tranchées ci-dessus.
- [ ] **Intégration du referee officiel pour l'arène locale** — dernière inconnue matérielle de cette phase : l'API ne fournit pas le referee. Tant qu'il manque, l'arène locale n'existe pas, et [CODINGAME_API.md §3](CODINGAME_API.md) montre que l'API seule est insuffisante d'un facteur cent. À lever avant d'engager la phase.
- [ ] Lancement d'une boucle autonome : le Worker modifie le bot ──▶ l'arène locale évalue par SPRT ──▶ le Reviewer acte la promotion ou l'abandon ──▶ un `PROMOTE` soumet via le `CodinGameApiGate`.
- [ ] Visualisation en direct de l'Elo d'ancrage **et du rang réel** sur le Dashboard Aspire, pour détecter leur divergence.
- [ ] Vérifier qu'une soumission n'est lue qu'une fois ses battles à 100 % (avant cela le score n'est pas définitif).

### Phase 5 : Conteneurisation & Publication Multi-Pipelines
* Objectif : déployer et faire tourner plusieurs pipelines en parallèle sur n'importe quelle machine ou VPS Linux. La cible de pipelines provisionnés est en [PARAMETERS.md §4](PARAMETERS.md).
* **Critère d'acceptation** : les pipelines tournent plusieurs jours sans intervention, sans saturation disque et sans dérive des timeouts mesurés.
- [ ] Configuration des conteneurs et de l'environnement Docker Compose.
- [ ] Cohérence entre les `cpuset` Docker et l'allocation de l'`ArenaScheduler` — un désaccord entre les deux ramène l'incident 2.
- [ ] **Résoudre l'authentification des agents en conteneur** — Antigravity exige des credentials mis en cache par une session interactive. Sans solution, il ne tourne pas sur le VPS. Voir [AGENT_BACKENDS.md §8](AGENT_BACKENDS.md).
- [ ] Exportation de la configuration Docker Compose prête pour la production.
