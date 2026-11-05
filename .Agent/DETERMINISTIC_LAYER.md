# Spécification de la Couche Déterministe (Le Socle C#)

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## 1. Principes Directeurs

La couche déterministe est le garant de la vérité terrain :
1. **Zéro hallucination** : tous les chiffres de performance proviennent de mesures d'exécution réelles.
2. **Reproductibilité** : chaque expérience est taguée avec son commit Git, son hash de code et ses graines aléatoires (*seeds*).
3. **Protection de l'infrastructure** : confinement strict des processus pour empêcher toute fuite mémoire, saturation CPU ou dépassement disque.
4. **Source de vérité unique** : les métriques vivent dans `matches.db` et nulle part ailleurs (voir §2.E).

---

## 2. Les Composants Déterministes

### A. `MatchRunner` (Exécuteur de Matchs)
* **Classe C#** : `CodinGame.Core.Arena.MatchRunner`
* **Rôle** : lancer un duel entre deux bots via l'arbitre du jeu (*referee*).
* **Périmètre** : duels 1v1 uniquement. Les jeux à 3 joueurs ou plus et les jeux à information cachée sont hors périmètre v1.
* **Contraintes opérationnelles** :
  * **Gestion des Timeouts** : détection asynchrone stricte du temps par coup. Les valeurs sont **lues dans la configuration du pipeline**, jamais codées en dur (voir [PARAMETERS.md §2](PARAMETERS.md)). Un pipeline sans timeout configuré refuse de démarrer. Si un bot dépasse le délai imparti, il est immédiatement déclaré forfait (*Timeout DQ*).
  * **Affinité Processeur** : le `MatchRunner` ne choisit pas ses cœurs. Il reçoit un créneau de l'`ArenaScheduler` (§2.F) et applique `Process.ProcessorAffinity` sur ce créneau. Sans créneau attribué, il attend : jamais d'exécution en mode dégradé.
  * **Capture des flux** : `stdout` pour les coups joués, `stderr` pour les logs de débogage du bot, et code de retour pour détecter les crashs (`SIGSEGV`, exceptions non gérées).
  * **Appariement** : chaque confrontation est jouée deux fois, places échangées, à seed identique. Le `MatchRunner` expose les deux résultats comme une paire, pas comme deux matchs indépendants.

---

### B. `GitRepositoryManager` (Gestionnaire de Versions Automatisé)
* **Classe C#** : `CodinGame.Core.Git.GitRepositoryManager`
* **Rôle** : chaque jeu possède son propre sous-dépôt Git local.

> **Règle de sécurité absolue** : une expérience ne vit **que** sur sa branche
> `experiment/<id>`. Aucune opération de cette classe n'exécute jamais
> `git reset --hard`, sous aucune condition. Annuler une expérience, c'est
> supprimer sa branche — rien d'autre. `main` n'est modifiée que par un merge
> de promotion.

* **Opérations supportées** :
  * `CreateExperimentBranch(string experimentId)` : crée la branche isolée `experiment/<id>` à partir de `main`.
  * `CommitExperiment(string experimentId, string message)` : commit horodaté sur la branche d'expérience. Le message est **lisible par un humain et ne contient aucune métrique** : les chiffres vont dans l'`ExperimentStore` (§2.E), qui est la seule source de vérité. Appelable plusieurs fois par cycle (itérations du Worker).
  * `PromoteExperiment(string experimentId)` : merge de `experiment/<id>` sur `main`, puis création du tag `exp/<experimentId>`. **Le tag ne contient aucune valeur de performance** : un Elo n'a de sens que relativement à un pool donné, et le pool évolue — un nombre gravé dans un nom de tag devient faux rétroactivement. La métrique se lit dans l'`ExperimentStore`, jointe par `experimentId`.
  * `AbandonExperiment(string experimentId)` : `git branch -D experiment/<id>`. C'est l'unique mécanisme de rollback. Opération idempotente, sans effet sur `main`.

---

### C. `EloCalculator` (Moteur Statistique)
* **Classe C#** : `CodinGame.Core.Metrics.EloCalculator`
* **Rôle** : estimer la force du bot face à un pool d'adversaires.

* **Deux mesures distinctes, à ne jamais confondre** :
  * **Elo d'ancrage** : calculé contre le *pool d'ancrage gelé* (§2.G). C'est la seule mesure comparable d'un cycle à l'autre, et donc la seule qui peut être tracée comme une courbe de progression.
  * **Elo d'entraînement** : calculé contre le pool d'entraînement évolutif. Utile pour la décision du cycle en cours, **non comparable** entre cycles puisque l'étalon bouge.

* **Formule de base** (espérance de score, puis mise à jour) :

  ```
  E_A    = 1 / (1 + 10^((R_B - R_A) / 400))
  R_A(t+1) = R_A(t) + K * (S_A - E_A)
  ```

* **Décision ouverte (à trancher en Phase 2)** : la mise à jour itérative à facteur K
  ci-dessus est conçue pour une population ouverte jouant séquentiellement. Appliquée à un
  pool fixe, elle rend le résultat dépendant de l'ordre des matchs et du choix de K.
  L'alternative est d'**ajuster** la cote par maximum de vraisemblance sur un modèle de
  Bradley-Terry (approche BayesElo / Ordo), cotes du pool maintenues fixes. C'est
  probablement le bon choix pour l'Elo d'ancrage ; la décision n'est pas encore prise.

* **Significativité statistique** : le test, ses bornes et les plafonds de matchs sont
  définis en [PARAMETERS.md §1](PARAMETERS.md). Le calculateur expose le verdict SPRT
  (`H0` acceptée, `H1` acceptée, ou non concluant), pas un simple booléen.

---

### D. `DiskQuotaManager` (Gestionnaire de Rétention et de Quotas)
* **Classe C#** : `CodinGame.Core.ResourceGuard.DiskQuotaManager`
* **Rôle** : éviter l'incident historique de saturation disque.
* **Règles de rétention** : définies en [PARAMETERS.md §7](PARAMETERS.md). Le composant les applique, il ne les décide pas.
* Les replays conservés (défaites notables) sont référencés depuis l'`ExperimentStore` afin de servir de cas de test au `Researcher`.

---

### E. `ExperimentStore` (Source de Vérité des Métriques)
* **Classe C#** : `CodinGame.Core.Storage.ExperimentStore`
* **Base** : SQLite léger, un fichier `matches.db` par pipeline.
* **Rôle** : détenir **toutes** les données chiffrées du système. C'est l'autorité : en cas de divergence avec un message de commit, un nom de tag ou un log, c'est `matches.db` qui a raison.
* **Contenu** :
  * Expériences : `experimentId`, branche, commit, hash du code, seeds, horodatage.
  * Résultats : paires de matchs, issues, timeouts, crashs, temps par tour.
  * Métriques : Elo d'ancrage, Elo d'entraînement, winrate, verdict SPRT.
  * Décisions : verdict du Reviewer, motif, agent émetteur.
  * Coûts : tokens et dollars consommés par cycle et par agent.
  * Composition du pool d'ancrage et du pool d'entraînement à chaque cycle.
* **Identifiant partagé** : la jointure se fait toujours par `experimentId`, qui est aussi le nom de la branche et du tag Git. C'est le seul identifiant commun entre Git et la base.
* **Reprise après incident** : le store est la base de la reprise. Un tournoi interrompu (reboot, coupure) est rejouable depuis les seeds enregistrés ; une expérience sans verdict enregistré est reprise ou abandonnée au redémarrage, jamais laissée dans un état indéterminé.

---

### F. `ArenaScheduler` (Ordonnanceur de Créneaux CPU)
* **Classe C#** : `CodinGame.Core.Arena.ArenaScheduler`
* **Rôle** : attribuer les cœurs aux matchs. C'est le composant qui rend les mesures valides, donc le plus critique de cette couche.
* **Pourquoi il existe** : l'incident 2 de [STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md) a montré qu'un tournoi non isolé ne produit pas des résultats dégradés mais des résultats **faux**. Sans ordonnanceur global, 40 pipelines sur 16 cœurs reproduisent l'incident à l'identique.
* **Fonctionnement** :
  * Détient l'unique registre des créneaux de match (nombre et composition : voir [PARAMETERS.md §3](PARAMETERS.md)).
  * Sémaphore global : un `MatchRunner` ne démarre jamais sans créneau attribué.
  * File d'attente par pipeline, équitable, avec plafond de tournois simultanés.
  * Applique le plafond de durée de tournoi et libère les créneaux d'un tournoi abandonné.
  * Refuse de sur-allouer, même sous pression : la file grandit, l'isolation ne se dégrade pas.

---

### G. `OpponentPoolManager` (Propriétaire Unique des Pools)
* **Classe C#** : `CodinGame.Core.Arena.OpponentPoolManager`
* **Rôle** : détenir la composition des pools d'adversaires. **Aucun agent ne modifie un pool directement** ; les agents émettent une demande, ce composant applique la politique.
* **Deux pools, deux usages** :
  * **Pool d'ancrage (gelé)** : constitué une fois à la création du pipeline, **jamais modifié ensuite**. C'est le référentiel absolu qui rend la courbe Elo comparable dans le temps. Le modifier invaliderait tout l'historique ; l'opération n'est pas exposée.
  * **Pool d'entraînement (évolutif)** : sert à l'optimisation du cycle courant. Enrichi des versions promues et de checkpoints historiques divers.
* **Politique du pool d'entraînement** :
  * Taille cible fixe, conservant des checkpoints **variés** (anciens et récents), pas seulement la dernière version promue — sinon le bot n'apprend qu'à battre son propre présent.
  * Tout retrait de checkpoint est journalisé dans l'`ExperimentStore` avec son motif.
  * Le Researcher peut *demander* un enrichissement (par exemple contre un profil d'adversaire qui gagne souvent) ; le Reviewer ne peut rien modifier.

---

### H. `BudgetGuard` (Comptabilité des Tokens)
* **Classe C#** : `CodinGame.Core.ResourceGuard.BudgetGuard`
* **Rôle** : compter les tokens et les dollars consommés, et appliquer la politique de budget. Le comptage ne dépend d'aucun agent : un agent ne peut ni s'auto-déclarer économe, ni dépasser son plafond.
* **Fonctionnement** : intercepte les appels LLM, écrit les coûts dans l'`ExperimentStore`, publie les métriques OTLP consommées par le dashboard, et applique les plafonds et seuils de [PARAMETERS.md §5 et §6](PARAMETERS.md).
