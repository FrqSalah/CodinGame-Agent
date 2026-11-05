# Spécification du Système Multi-Agents

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## 1. Vue d'Ensemble des Rôles

Le système se compose de 5 agents aux responsabilités strictement délimitées pour éviter la dispersion de contexte et maximiser le ratio valeur/coût de tokens.

```
       ┌────────────────────────┐
       │      ADMIN AGENT       │
       └───────────┬────────────┘
                   │ Initialise & alloue les ressources
                   ▼
       ┌────────────────────────┐
       │    RESEARCHER AGENT    │◀──────────────┐
       └───────────┬────────────┘               │
                   │ Stratégie macro            │ Plancher de mesure
                   ▼                            │ atteint (3 cycles)
       ┌────────────────────────┐               │
       │   WORKER / CODER AGENT │               │
       └───────────┬────────────┘               │
                   │ Code & compile             │
                   ▼                            │
     [ ARÈNE LOCALE — SPRT, voir PARAMETERS ]   │
                   │ Verdict du tournoi         │
                   ▼                            │
       ┌────────────────────────┐               │
       │     REVIEWER AGENT     │───────────────┘
       └───────────┬────────────┘
                   │
         ┌─────────┴─────────┐
         ▼                   ▼
    [ PROMOTE ]         [ ROLLBACK ]
  (Merge sur main)   (Suppression branche)
```

---

## 2. Fiche Détaillée par Agent

### 1. Admin Agent
* **Mission** : interface entre l'humain et la ferme. Provisionne de nouveaux pipelines de jeu et gère la santé globale.
* **Déclenchement** : commande CLI utilisateur, ou alerte d'infrastructure sur un pipeline **bloqué** — au sens unique défini en [PARAMETERS.md §5](PARAMETERS.md) : aucun heartbeat de progrès depuis le délai imparti.
* **Entrées** : nom du jeu CodinGame, règles du jeu, timeouts du jeu, composition initiale du pool d'ancrage.
* **Sorties** : dépôt Git initialisé, pool d'ancrage gelé constitué via l'`OpponentPoolManager`, pipeline rattaché à l'AppHost Aspire, dashboard configuré.

---

### 2. Utils-Writer Agent
* **Mission** : construire les utilitaires de données déterministes pour que les autres agents ne gaspillent pas de tokens.
* **Déclenchement** : au démarrage d'un nouveau jeu, ou lorsqu'une analyse de log spécifique est nécessaire.
* **Entrées** : spécification des entrées/sorties du jeu CodinGame, format des replays.
* **Sorties** :
  * Script de simulation rapide ou parseur de tours.
  * Extracteur de métriques : temps de calcul moyen par tour, nombre de nœuds explorés, taux de collisions, etc.

---

### 3. Worker / Coder Agent
* **Mission** : implémenter une modification ciblée sur le code du bot (nouvelle heuristique, élagage Alpha-Beta, table de transposition, etc.).
* **Déclenchement** : reçoit une directive précise du `Reviewer` (amélioration incrémentale) ou du `Researcher` (changement d'architecture).
* **Entrées** : code source actuel du bot, directive d'amélioration, erreurs de compilation éventuelles.
* **Sorties** : code source modifié, committé sur `experiment/<id>`.
* **Validation impérative** : le code généré doit compiler en local avant d'être envoyé en tournoi. Au-delà du nombre de tentatives de compilation de [PARAMETERS.md §5](PARAMETERS.md), le Worker n'insiste pas : il escalade vers le Researcher avec les erreurs rencontrées.

---

### 4. Reviewer Agent
* **Mission** : gestionnaire tactique, à la cadence de cycle définie en [PARAMETERS.md §4](PARAMETERS.md). Analyse les données chiffrées de l'arène locale et tranche.
* **Entrées** (toutes lues dans l'`ExperimentStore`, jamais recalculées par l'agent) :
  * Verdict SPRT : `H0` acceptée, `H1` acceptée, ou non concluant.
  * Elo d'ancrage et son évolution (seule mesure comparable entre cycles).
  * Winrate contre le pool d'entraînement.
  * Métriques de stabilité : nombre de timeouts, nombre de crashs.
* **Sorties (décision formelle)** :
  * `PROMOTE` : le SPRT a accepté `H1` **et** la stabilité est intacte (zéro timeout, zéro crash). Déclenche `PromoteExperiment`, puis émet une *demande* d'enrichissement du pool d'entraînement auprès de l'`OpponentPoolManager`. Le Reviewer ne modifie aucun pool lui-même.
  * `ROLLBACK` : le SPRT a accepté `H0`, ou la stabilité est compromise. Déclenche `AbandonExperiment` — c'est-à-dire la **suppression de la branche** `experiment/<id>`, et uniquement cela. Le motif de l'échec est journalisé dans l'`ExperimentStore`.
  * `TUNE_PARAMS` : le SPRT est non concluant au plafond de matchs. L'architecture est peut-être prometteuse, mais l'effet est sous le plancher de mesure du projet. Affinage des poids heuristiques ; au bout du nombre de cycles de [PARAMETERS.md §1](PARAMETERS.md), escalade vers le Researcher.

> **Le Reviewer n'a aucune latitude statistique** : il ne décide pas si un gain est
> significatif, il lit le verdict produit par la couche déterministe. Son raisonnement
> porte sur *quoi faire* du verdict et sur le motif de l'échec, pas sur les chiffres.

---

### 5. Researcher Agent
* **Mission** : décisions stratégiques de haut niveau lorsque l'optimisation incrémentale n'est plus mesurable.
* **Déclenchement** : nombre de cycles consécutifs sans `PROMOTE` défini en [PARAMETERS.md §1](PARAMETERS.md) ; ou escalade du Worker après échecs de compilation répétés.
* **Entrées** : historique des gains et échecs récents, profils des défaites récurrentes contre certains types d'adversaires, replays de défaites notables conservés.
* **Sorties** :
  * Décision de changement de paradigme algorithmique (par exemple : *« l'heuristique actuelle atteint ses limites face aux stratégies agressives : basculer sur un moteur Minimax 4 plis avec table de transposition »*).
  * *Demande* d'enrichissement du pool d'entraînement auprès de l'`OpponentPoolManager`.
  * Décision de restart complet du problème, en conservant les leçons apprises.

> **Pourquoi c'est le Researcher qui est appelé sur un plateau** : un SPRT non concluant
> signifie que l'effet recherché est sous le plancher de mesure (voir
> [PARAMETERS.md §1](PARAMETERS.md)). Continuer à affiner produit des gains que le
> système ne saura pas distinguer du bruit. Seul un changement d'architecture a un effet
> attendu suffisamment large pour être mesurable dans le budget de matchs disponible.

---

## 3. Protocole d'Interaction

* **Orchestration** : workflow Agent Framework (graphe d'exécution explicite), voir la décision d'architecture en [ARCHITECTURE.md §2.B](ARCHITECTURE.md). L'ancien motif `AgentGroupChat` de Semantic Kernel n'est plus maintenu et n'est pas utilisé ici.
* **Communication typée** : les agents échangent des messages structurés (JSON ou objets C# sérialisés) pour éviter les ambiguïtés.
* **Journalisation** : chaque échange et décision est tracé dans OpenTelemetry et dans l'`ExperimentStore`, consultable en direct sur l'Aspire Dashboard.

### Garde-fous d'exécution

Le graphe du workflow garantit qu'une action concrète a été prise (code écrit, test lancé,
décision actée) avant de clore un cycle. Il applique en plus les plafonds de
[PARAMETERS.md §5](PARAMETERS.md), qui sont la seule protection contre une boucle
infinie :

* Plafond d'itérations par cycle, au-delà duquel le cycle est clos en `ROLLBACK`.
* Plafond de tentatives de compilation du Worker, au-delà duquel il escalade.
* Plafond de tokens par cycle, appliqué par le `BudgetGuard` et non par les agents.
* Heartbeat de progrès : sans signal pendant le délai imparti, le pipeline est déclaré
  bloqué et l'Admin est alerté.
* Plafond de durée de tournoi, au-delà duquel l'évaluation est abandonnée et les créneaux
  CPU libérés.

Un ping-pong Reviewer / Worker ne peut donc pas se prolonger : il est borné par le plafond
d'itérations du cycle, pas par le jugement d'un agent.
