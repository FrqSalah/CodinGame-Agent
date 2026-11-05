# Spécification du Système Multi-Agents

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## 1. Vue d'Ensemble des Rôles

> **Révisé le 2026-11-05.** Les rôles LLM sont désormais portés par des **CLI externes**
> (Claude Code, Antigravity CLI) enveloppés en sous-classes d'`AIAgent` — voir
> [AGENT_BACKENDS.md](AGENT_BACKENDS.md). Ce changement a rendu visible que **deux de ces
> cinq rôles n'étaient pas des agents** : le Reviewer et l'Admin sont des nœuds
> déterministes (`Executor`) du même graphe. Trois agents, deux exécuteurs.

Le système se compose de 5 rôles aux responsabilités strictement délimitées pour éviter la dispersion de contexte et maximiser le ratio valeur/coût de tokens.

```
       ┌────────────────────────┐
       │    ADMIN (Executor)    │
       └───────────┬────────────┘
                   │ Initialise & alloue les ressources
                   ▼
       ┌────────────────────────┐
       │   RESEARCHER (agent)   │◀──────────────┐
       └───────────┬────────────┘               │
                   │ Stratégie macro            │ Plancher de mesure
                   ▼                            │ atteint (3 cycles)
       ┌────────────────────────┐               │
       │ WORKER / CODER (agent) │               │
       └───────────┬────────────┘               │
                   │ Code & compile             │
                   ▼                            │
     [ ARÈNE LOCALE — SPRT, voir PARAMETERS ]   │
                   │ Verdict du tournoi         │
                   ▼                            │
       ┌────────────────────────┐               │
       │  REVIEWER (Executor)   │───────────────┘
       └───────────┬────────────┘
                   │
         ┌─────────┴─────────┐
         ▼                   ▼
    [ PROMOTE ]         [ ROLLBACK ]
  (Merge sur main)   (Suppression branche)
```

---

## 2. Fiche Détaillée par Agent

### 1. Admin — nœud déterministe (`Executor`)
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
* **Contrainte de format** : la soumission CodinGame est une **chaîne de code unique**, donc un **fichier unique**. Pas de projet multi-fichiers, quelle que soit la taille du bot. Le langage disponible dépend du jeu et se lit depuis l'API. Voir [CODINGAME_API.md §7](CODINGAME_API.md).
* **Priorité de conception** : un bot qui dépasse le temps imparti, plante ou produit une action invalide est **éliminé** et perd la partie, quel que soit son score antérieur. Tenir le temps compte donc plus que jouer mieux.
* **Validation impérative** : le code généré doit compiler en local avant d'être envoyé en tournoi. Au-delà du nombre de tentatives de compilation de [PARAMETERS.md §5](PARAMETERS.md), le Worker n'insiste pas : il escalade vers le Researcher avec les erreurs rencontrées.
* **Périmètre d'écriture — la frontière est le système de fichiers.** Le Worker tourne
  dans un répertoire de travail qui ne contient **que** le fichier du bot. Les drapeaux de
  permission des CLI sont une seconde couche utile, pas la frontière : l'un d'eux n'est
  que partiellement honoré en mode headless, et deux autres aux noms voisins ont des
  effets opposés. Voir [AGENT_BACKENDS.md §5](AGENT_BACKENDS.md).
* **Les tentatives de compilation ne sont plus comptables de l'extérieur** : l'agent lance
  `dotnet build` dans sa propre boucle. Le garde-fou qui tient est le plafond de **tours**,
  pas un compteur tenu par l'orchestrateur.

---

### 4. Reviewer — nœud déterministe (`Executor`)
* **Mission** : gestionnaire tactique, à la cadence de cycle définie en [PARAMETERS.md §4](PARAMETERS.md). Analyse les données chiffrées de l'arène locale et tranche.
* **Entrées** (toutes lues dans l'`ExperimentStore`, jamais recalculées par l'agent) :
  * Verdict SPRT : `H0` acceptée, `H1` acceptée, ou non concluant.
  * Elo d'ancrage et son évolution (seule mesure comparable entre cycles).
  * Winrate contre le pool d'entraînement.
  * Métriques de stabilité : nombre de timeouts, nombre de crashs.
* **Sorties (décision formelle)** :
  * `PROMOTE` : le SPRT a accepté `H1` **et** la stabilité est intacte (zéro timeout, zéro crash). Déclenche `PromoteExperiment`, émet une *demande* d'enrichissement du pool d'entraînement auprès de l'`OpponentPoolManager`, puis une **demande de soumission** au `CodinGameApiGate`. Le Reviewer ne modifie aucun pool et ne soumet pas lui-même.
    **C'est le seul verdict qui soumet.** Le budget de soumissions est la ressource la plus rare du système et il est partagé par tous les pipelines : le SPRT local est le filtre qui le protège. Voir [CODINGAME_API.md §5](CODINGAME_API.md).
  * `ROLLBACK` : le SPRT a accepté `H0`, ou la stabilité est compromise. Déclenche `AbandonExperiment` — c'est-à-dire la **suppression de la branche** `experiment/<id>`, et uniquement cela. Le motif de l'échec est journalisé dans l'`ExperimentStore`.
  * `TUNE_PARAMS` : le SPRT est non concluant au plafond de matchs. L'architecture est peut-être prometteuse, mais l'effet est sous le plancher de mesure du projet. Affinage des poids heuristiques ; au bout du nombre de cycles de [PARAMETERS.md §1](PARAMETERS.md), escalade vers le Researcher.

> **Le Reviewer n'a aucune latitude statistique** : il ne décide pas si un gain est
> significatif, il lit le verdict produit par la couche déterministe. Son raisonnement
> porte sur *quoi faire* du verdict et sur le motif de l'échec, pas sur les chiffres.

> **Et c'est pourquoi ce n'est pas un agent.** Lire un verdict SPRT et choisir entre trois
> issues est une **table de décision**, pas du raisonnement. Lui payer un run de CLI
> agentique coûterait de l'argent et ajouterait du non-déterminisme sans rien apporter.
> Il devient un `Executor` du graphe. Si l'on veut une **narration** du motif d'échec pour
> le journal, c'est un appel de modèle court et séparé, jamais la décision elle-même.

---

### 5. Researcher Agent
* **Mission** : décisions stratégiques de haut niveau lorsque l'optimisation incrémentale n'est plus mesurable.
* **Déclenchement** : nombre de cycles consécutifs sans `PROMOTE` défini en [PARAMETERS.md §1](PARAMETERS.md) ; escalade du Worker après échecs de compilation répétés ; **promotion de ligue** sur CodinGame ; ou divergence entre l'Elo d'ancrage et le rang réel.
* **Deux déclencheurs venus de l'API** :
  * **Promotion de ligue** : battre le boss fait changer de ligue, et une nouvelle ligue peut **ajouter des entrées et des actions**. Le statement doit être relu et les hypothèses du bot sont potentiellement invalidées. Ce n'est pas un gain incrémental, c'est un changement de problème.
  * **Elo d'ancrage en hausse mais rang réel stagnant** : signal de surapprentissage sur le pool local. Le Researcher demande alors un enrichissement du pool d'entraînement plutôt qu'une nouvelle heuristique.
* **Entrées** : historique des gains et échecs récents, profils des défaites récurrentes contre certains types d'adversaires, replays de défaites notables conservés.
* **Sorties** :
  * Décision de changement de paradigme algorithmique (par exemple : *« l'heuristique actuelle atteint ses limites face aux stratégies agressives : basculer sur un moteur Minimax 4 plis avec table de transposition »*).
  * *Demande* d'enrichissement du pool d'entraînement auprès de l'`OpponentPoolManager`.
  * Décision de restart complet du problème, en conservant les leçons apprises.

> **C'est le bon endroit pour le second backend.** Deux CLI adossés à des modèles
> différents, interrogés sur le même plateau, produisent une véritable diversité de
> raisonnement — là où la dédoubler sur le Worker ne ferait que doubler la facture.

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
