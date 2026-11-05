# CodinGame Autonomous Agent Farm (.Agent Context)

> **Statut** : à jour · **Dernière mise à jour** : 2026-11-05

> Ce dossier contient la mémoire collective, les spécifications d'architecture et les analyses stratégiques du projet. Tout agent (humain ou IA) arrivant sur ce projet doit lire ces documents pour comprendre l'état de l'art, les règles opérationnelles et les objectifs du système.

---

## 🎯 Mission

Construire un système multi-agents autonome capable de gérer en continu une ferme de
**pipelines d'expérimentation et d'optimisation de bots CodinGame**, en répliquant la
méthodologie qui a permis d'atteindre le **top 1 mondial sur CodinGame** (septembre 2026).

La cible de pipelines provisionnés, la cadence des cycles et les plafonds de ressources
sont définis dans [PARAMETERS.md](PARAMETERS.md). « Provisionnés » n'est pas « simultanés » :
le nombre de tournois qui tournent en même temps est plafonné par les créneaux CPU
disponibles — voir [STRATEGY_ANALYSIS.md §4](STRATEGY_ANALYSIS.md).

---

## 🧭 Règle Fondamentale du Projet

> **"If a task is deterministic, repeatable and easy to specify, I strongly prefer a script. Launching versions, transforming data, parsing results, preparing statistics and maintaining infrastructure are generally cheaper and more reliable when implemented conventionally. The LLM must spend tokens only on where reasoning actually matters."**
>
> *(Citation rapportée, source à référencer — voir l'avertissement en tête de
> [STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md).)*

1. **Zéro token gaspillé** sur ce que le code classique sait faire (compiler, exécuter un match, calculer l'Elo, nettoyer les logs, gérer les commits Git).
2. **Le LLM est réservé au raisonnement** : comprendre pourquoi une stratégie a échoué, proposer une heuristique nouvelle, concevoir un changement d'architecture (par exemple Minimax vers MCTS), ou générer le code du bot.

---

## 📚 Sommaire des Fichiers

| Fichier | Description |
| :--- | :--- |
| [PARAMETERS.md](PARAMETERS.md) | **Source unique de toute valeur numérique** : matchs, SPRT, timeouts, cœurs, cadence, budget, rétention. Aucun autre document ne recopie un chiffre. |
| [STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md) | Retour d'expérience du n°1 mondial CodinGame : pièges réels évités, économie du projet, et pourquoi 40+ pipelines tiennent sur 16 cœurs. |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Choix techniques : 100 % .NET, .NET Aspire, **Microsoft Agent Framework**, Docker. Contient la décision SK → Agent Framework. |
| [CODINGAME_API.md](CODINGAME_API.md) | Intégration de l'API publique CodinGame : token par device flow, **deux arènes aux rôles distincts**, mesure à trois étages, limites externes, règles de conduite, contraintes sur le code du bot. |
| [MULTI_AGENT_SPEC.md](MULTI_AGENT_SPEC.md) | Les 5 rôles d'agents (Admin, Utils-Writer, Reviewer, Researcher, Worker/Coder) et les garde-fous de boucle. |
| [DETERMINISTIC_LAYER.md](DETERMINISTIC_LAYER.md) | Couche non-LLM : ArenaScheduler, MatchRunner, ExperimentStore, OpponentPoolManager, Git, Elo, BudgetGuard, quotas disque. |
| [ROADMAP.md](ROADMAP.md) | Feuille de route par phase, critères d'acceptation, et décisions encore ouvertes. |

---

## ⚠️ Conventions de ce dossier

1. **Aucun chiffre en dur hors de [PARAMETERS.md](PARAMETERS.md).** Un document qui a besoin d'une valeur y renvoie. Une valeur recopiée ailleurs est un bug de documentation : les deux copies finiront par diverger.
2. **Liens relatifs uniquement.** Pas de chemin absolu type `file:///c:/...` : le projet est destiné à tourner aussi sur un VPS Linux.
3. **Chaque fichier porte une date et un statut** en tête, afin qu'un agent puisse juger de sa fraîcheur.
4. **Pas de LaTeX.** Les formules sont en texte brut ou en bloc de code : le rendu Markdown des outils utilisés ici n'interprète pas `$...$`.
5. **Une décision non prise se déclare comme telle.** Les points ouverts sont listés en tête de [ROADMAP.md](ROADMAP.md) plutôt que masqués derrière une formulation ambiguë.
