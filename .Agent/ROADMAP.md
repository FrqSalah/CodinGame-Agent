# Feuille de Route du Projet (Roadmap)

> **Statut** : à jour · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## Statut Actuel : Phase 1 réalisée, build non vérifié

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
- [x] **Le contest en cours n'est pas le jeu témoin** — fenêtre de quelques jours, règles fermées sans inscription, et un contest disparaît alors qu'une mesure comparable entre cycles exige un jeu rejouable. Motifs en [CODINGAME_API.md §9.4](CODINGAME_API.md).

### Décisions encore ouvertes

- [ ] **Estimateur Elo** : mise à jour itérative à facteur K, ou ajustement par maximum de vraisemblance (Bradley-Terry) — voir [DETERMINISTIC_LAYER.md §2.C](DETERMINISTIC_LAYER.md).

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
- [ ] **Vérifier que la solution compile** (non vérifié à ce jour).
- [ ] Déclarer au moins une ressource dans l'AppHost — `Program.cs` est actuellement vide, le Dashboard n'affiche donc rien.

### Phase 2 : Couche Déterministe C#
* Objectif : disposer d'un terrain d'entraînement hermétique, fiable et sans LLM.
* **Critère d'acceptation** : une évaluation complète (SPRT jusqu'au verdict) tourne sans LLM, sous isolation CPU, et écrit son verdict dans `matches.db`.
- [ ] Implémentation de l'`ArenaScheduler` (sémaphore global des créneaux CPU) — **à faire en premier** : sans lui, toutes les mesures des composants suivants sont invalides.
- [ ] Implémentation de l'`ExperimentStore` (`matches.db`, source de vérité unique).
- [ ] Implémentation du `MatchRunner` (processus, timeouts par jeu, affinité CPU, appariement des places).
- [ ] Implémentation du `GitRepositoryManager` (branches `experiment/<id>`, abandon par suppression de branche, jamais de `reset --hard`).
- [ ] Implémentation de l'`OpponentPoolManager` (pool d'ancrage gelé + pool d'entraînement).
- [ ] Implémentation de l'`EloCalculator` (Elo d'ancrage, Elo d'entraînement, verdict SPRT).
- [ ] Implémentation du `BudgetGuard` (comptage des tokens, application des plafonds).
- [ ] Implémentation du `DiskQuotaManager` (purge automatique des replays volumineux).
- [ ] Implémentation du `CodinGameApiGate` (voie unique vers l'API : sémaphore de capacité 1, lecture des limites à chaque réponse, respect des `Retry-After`, soumission réservée aux `PROMOTE`).
- [ ] Tests unitaires sur l'`EloCalculator` et le verdict SPRT — ce sont les calculs dont une erreur silencieuse fausserait toutes les décisions du système.

### Phase 3 : Outils et Agents (Agent Framework)
* Objectif : donner aux agents les outils déterministes et configurer leurs prompts d'expertise.
* **Critère d'acceptation** : un cycle complet se déroule sans intervention, et s'arrête proprement sur chacun des plafonds de [PARAMETERS.md §5](PARAMETERS.md) quand on les provoque volontairement.
- [ ] Création des outils `AIFunction` : `GitTools`, `TournamentTools`, `MetricsTools`.
- [ ] Configuration des agents `WorkerCoderAgent`, `ReviewerAgent`, `ResearcherAgent`, `UtilsWriterAgent`, `AdminAgent` (`ChatClientAgent`).
- [ ] Implémentation du workflow en graphe pilotant la boucle d'itération.
- [ ] Implémentation des garde-fous : plafonds d'itérations, de compilations, de tokens, heartbeat, durée de tournoi.

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
- [ ] Exportation de la configuration Docker Compose prête pour la production.
