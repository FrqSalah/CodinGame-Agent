# Paramètres du Système — Source Unique

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05

> **Ce fichier est la seule source de vérité pour toute valeur numérique du projet.**
> Les autres documents ne recopient jamais un chiffre : ils renvoient ici.
> Une valeur écrite en dur ailleurs est un bug de documentation.

---

## 1. Évaluation d'une expérience (arène locale)

Le nombre de matchs **n'est pas une constante**. Il est déterminé par un test séquentiel
(SPRT, *Sequential Probability Ratio Test* — standard du test de moteurs d'échecs) :
on joue jusqu'à ce que le résultat soit concluant, puis on s'arrête.

| Paramètre | Valeur | Justification |
| :--- | :--- | :--- |
| Test statistique | SPRT sur bornes Elo | S'arrête tôt quand l'écart est net, ne dépense beaucoup de matchs que sur les cas serrés |
| Borne `H0` | 0 Elo | « aucun gain » |
| Borne `H1` | +25 Elo | Seuil d'effet minimal : en dessous, le gain ne justifie pas un `PROMOTE` |
| `alpha` / `beta` | 0.05 / 0.05 | Satisfait l'exigence `p < 0.05` du Reviewer |
| Matchs minimum | 100 | Plancher avant toute décision ; détecte les grosses régressions en quelques dizaines de matchs |
| Matchs maximum | 2000 | Plafond de coût. SPRT non concluant à 2000 → verdict `TUNE_PARAMS`, jamais `PROMOTE` |
| Appariement | **obligatoire** | Chaque confrontation est jouée deux fois, places échangées, même seed |
| Seeds | identiques entre versions | Comparaison par paires : réduit fortement la variance, à coût nul |

### Pourquoi ces bornes, et ce qu'elles impliquent

Les ordres de grandeur ne sont pas négociables, ils sortent du calcul :

* +25 Elo correspond à un winrate de **53,6 %**. Le distinguer de 50 % avec 80 % de
  puissance demande environ **1500 matchs** non appariés (moins avec appariement).
* +10 Elo correspond à **51,4 %**. Le distinguer demanderait environ **9500 matchs** —
  hors budget.
* À 100 matchs, l'intervalle de confiance à 95 % sur le winrate est de **±10 points**.
  À 2000 matchs, **±2,2 points**.

**Conséquence stratégique** : en dessous de +25 Elo, le projet ne sait pas mesurer.
Un SPRT non concluant répété n'est donc pas forcément un plateau de la stratégie, c'est
d'abord le **plancher de mesure**. C'est précisément le signal qui doit déclencher le
Researcher — un changement d'architecture a un effet attendu large, donc mesurable,
alors que poursuivre l'affinage produit des gains sous le seuil de détection.

### Déclenchement du Researcher

| Paramètre | Valeur |
| :--- | :--- |
| Cycles consécutifs sans `PROMOTE` avant escalade | 3 |

---

## 2. Contraintes de temps par tour

Il n'existe **aucune** valeur globale : le timeout dépend du jeu. L'autorité est le
referee officiel du jeu, et la valeur est stockée dans la configuration du pipeline.

| Champ de configuration | Rôle |
| :--- | :--- |
| `firstTurnTimeoutMs` | Temps alloué au premier tour (initialisation) |
| `turnTimeoutMs` | Temps alloué à chaque tour suivant |

Valeurs courantes sur CodinGame, **à titre indicatif uniquement** : premier tour
1000 ms ; tours suivants 50 ms ou 100 ms selon le jeu. Le `MatchRunner` lit la
configuration et ne suppose jamais de valeur par défaut : un pipeline sans timeout
configuré refuse de démarrer.

---

## 3. Ressources machine et ordonnancement

Référence : une seule machine **16 cœurs**.

| Paramètre | Valeur | Rôle |
| :--- | :--- | :--- |
| Cœurs réservés à l'hôte | 2 | Orchestrateur, dashboard, Git, appels réseau |
| Cœurs réservés aux agents CLI | 2 | Processus Claude Code / `agy`. **À calibrer en Phase 3** |
| Cœurs dédiés à l'arène | 12 | Jamais utilisés par autre chose pendant un match |
| Cœurs par créneau de match | 2 | Un par processus bot ; le referee partage le créneau |
| Créneaux de match simultanés | 6 | 12 / 2 |
| Tournois simultanés (défaut) | 4 | Évite qu'un seul tournoi monopolise les créneaux |

Ces valeurs sont une **contrainte d'exactitude, pas de performance** : l'incident 2 de
[STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md) a montré que la contention CPU ne ralentit
pas les mesures, elle les invalide. Un match qui ne peut pas obtenir son créneau attend ;
il ne démarre jamais en mode dégradé.

### Pourquoi ce découpage a changé le 2026-11-05

Le découpage précédent donnait 2 cœurs à l'hôte, « appels LLM » compris, et 14 à
l'arène. Il était dimensionné pour des **appels réseau**, qui ne consomment presque pas
de CPU. Les agents sont désormais des **CLI externes** : des processus locaux qui lisent
des fichiers, grepent et lancent `dotnet build`. Voir
[AGENT_BACKENDS.md §7](AGENT_BACKENDS.md).

Un agent qui s'exécute sur un cœur d'arène pendant un match ramène l'incident 2 : les
mesures ne ralentissent pas, elles deviennent **fausses**.

Deux façons de tenir l'invariant, et il faut en choisir une :

1. **Partition dédiée** (valeurs du tableau) : les processus d'agents sont épinglés aux
   cœurs réservés, jamais sur ceux de l'arène. Coût : un créneau de match perdu.
2. **Emprunt de créneau** : un run d'agent demande un créneau à l'`ArenaScheduler` comme
   le ferait un match. Coût : un agent long bloque des créneaux ; avantage : aucune
   partition à maintenir cohérente avec les `cpuset` Docker.

La première est recommandée : elle découple le débit de l'arène de la durée
imprévisible d'un run d'agent. Le nombre exact de cœurs d'agents dépend du nombre de
pipelines autorisés à raisonner en même temps : à **mesurer** en Phase 3, pas à deviner
ici.

---

## 4. Pipelines et cadence

| Paramètre | Valeur |
| :--- | :--- |
| Pipelines provisionnés (cible) | 40+ |
| Pipelines en évaluation simultanée | 4 tournois max (voir §3) |
| Durée d'un cycle d'itération | 24 h à 48 h |
| Temps humain attendu | 1 à 2 h par semaine |

**Les deux lignes ne se contredisent pas** : « 40+ pipelines » décrit le nombre de jeux
*provisionnés*, pas le nombre de tournois simultanés. Sur un cycle de 24-48 h, un pipeline
passe l'essentiel de son temps à attendre (raisonnement LLM, délai de cycle, file d'attente
de l'arène). Seuls ceux arrivés à l'étape d'évaluation se disputent les créneaux CPU du §3,
sous le contrôle de l'`ArenaScheduler`.

---

## 5. Garde-fous de boucle

Sans ces plafonds, un cycle peut consommer un budget illimité sans produire de décision.

| Paramètre | Valeur | Déclenche |
| :--- | :--- | :--- |
| Itérations max par cycle | 10 | Clôture forcée du cycle en `ROLLBACK` |
| Tentatives de compilation max (Worker) | 5 | Escalade vers le Researcher |
| Tours max par run d'agent | 30 | Fin du run ; le cycle continue avec ce qui a été produit |
| Durée max d'un run d'agent | 20 min | Kill du processus **par l'orchestrateur** |
| Tokens max par cycle | 1 % du budget mensuel | Clôture du cycle, pipeline mis en pause |
| Heartbeat sans progrès | 2 h | Pipeline déclaré *bloqué*, alerte à l'Admin |
| Durée max d'un tournoi | 4 h | Abandon de l'évaluation, créneaux libérés |

Un pipeline est **bloqué** si, et seulement si, il n'a émis aucun heartbeat de progrès
depuis le délai ci-dessus. C'est la définition unique à laquelle renvoie le déclencheur
de l'Admin Agent.

### Ce que les agents CLI ont changé à ces plafonds

Deux de ces lignes ne s'appliquent plus comme avant, parce qu'un agent CLI porte sa propre
boucle d'outils. Voir [AGENT_BACKENDS.md §4 et §6](AGENT_BACKENDS.md).

* **Les tentatives de compilation ne sont plus comptables par l'orchestrateur.** L'agent
  lance `dotnet build` lui-même, à l'intérieur de son run. Le plafond ci-dessus devient
  une **consigne de prompt**, et le garde-fou qui tient réellement est le plafond de tours.
* **Le plafond de tokens par cycle n'est plus un garde-fou préventif.** La consommation
  d'un run agentique varie d'un ordre de grandeur et n'est pas prévisible avant le run. Il
  reste une **limite de comptabilité** appliquée après coup. Ce qui borne un run avant
  qu'il parte, ce sont les tours, le wall-clock et le plafond de dépense de §6.

> **La durée max d'un run est appliquée par l'orchestrateur, jamais par le CLI.** Le
> `--print-timeout` d'Antigravity vaut `0s` par défaut, soit aucune limite : un CLI n'est
> jamais l'autorité sur sa propre durée.

---

## 6. Budget LLM

| Paramètre | Valeur |
| :--- | :--- |
| Budget mensuel | 250 USD |
| Plafond par cycle | 1 % du budget mensuel |
| Seuil d'alerte | 80 % du budget consommé |
| À 100 % du budget | Pause des pipelines, du moins performant au plus performant ; aucun nouveau cycle |
| Plafond par run d'agent | à définir | Passé à `--max-budget-usd` (Claude Code uniquement) |
| Tarif des tokens Antigravity | à renseigner | Antigravity ne rend **aucun coût** : ses tokens doivent être tarifés ici |

Le comptage est assuré par le `BudgetGuard` de la couche déterministe, pas par les agents.
Depuis le passage aux agents CLI, il s'applique dans le **middleware de run** d'Agent
Framework : un point d'interception unique pour tous les runs, sous-processus compris.

> **Un plafond n'est pas une garantie de facture.** Les plafonds de dépense des CLI sont
> des *estimations côté client* et la dépense réelle peut les dépasser. Claude Code rend
> `total_cost_usd` ; Antigravity ne rend que des tokens, d'où la ligne de tarif ci-dessus.
> Voir [AGENT_BACKENDS.md §6](AGENT_BACKENDS.md).

---

## 7. Rétention disque

| Donnée | Rétention |
| :--- | :--- |
| Statistiques agrégées, historique Elo, décisions | Indéfinie (`matches.db`) |
| Replays de matchs ordinaires | Supprimés à la fin de l'évaluation |
| Replays de défaites notables | 5 derniers par pipeline |
| Logs texte | Rotation à 50 Mo par pipeline |

---

## 8. Ports réseau

**Aucun port n'est documenté ici ni ailleurs en prose.** Les ports du dashboard Aspire et
des endpoints OTLP sont attribués par le template et vivent dans
`src/CodinGame.AppHost/Properties/launchSettings.json`. C'est la seule référence.

---

## 9. Limites imposées par l'API CodinGame

> **Ces valeurs ne sont pas les nôtres.** Contrairement à tout le reste de ce fichier,
> elles sont imposées de l'extérieur, peuvent changer sans préavis, et **ne doivent jamais
> être codées en dur**. Les chiffres ci-dessous sont les ordres de grandeur annoncés par la
> documentation CodinGame, donnés pour dimensionner l'architecture — pas pour être lus par
> le code.

| Limite | Ordre de grandeur annoncé | Portée |
| :--- | :--- | :--- |
| Requêtes API | ~120 / minute avec token (~60 / minute sans, par IP) | par utilisateur |
| Soumissions | ~5 / 5 minutes et ~100 / 5 heures | par utilisateur |
| Parties de test | ~10 / minute et ~600 / jour | par utilisateur |
| Parties de test simultanées | **1** | par utilisateur |

### Autorité à l'exécution

Le code lit les limites, il ne les suppose pas :

* Chaque réponse JSON porte `limits.requests` (et, pour les tests et soumissions,
  `limits.submits` et `limits.test_games`) avec `limit`, `used`, `remaining` et
  `resets_in_seconds`. C'est la source à lire après **chaque** appel.
* L'en-tête `RateLimit` liste les mêmes fenêtres.
* `GET /limits?challenge=<public_id>` (ou `?puzzle=<pretty_id>`) liste toutes les fenêtres.
* Sur `429 rate-limited` : attendre `Retry-After` (aussi `retry_after_seconds` dans le
  corps). **Ne jamais retenter plus tôt.**
* `409 test-game-running` : une partie de test est déjà en cours ; attendre sa fin sur le
  `poll_url` fourni.

### Conséquences de dimensionnement

* **Toutes les limites sont par compte, donc partagées par les 40+ pipelines.** Ce n'est
  pas une ressource par pipeline : c'est un goulot global, distinct des créneaux CPU du §3.
* **« Une seule partie de test à la fois » fait de l'API une voie unique** pour toute la
  ferme. Elle exige son propre sémaphore global, de capacité 1, en plus de
  l'`ArenaScheduler` local. Voir [DETERMINISTIC_LAYER.md §2.I](DETERMINISTIC_LAYER.md).
* **Le budget de soumissions est la ressource la plus rare du système.** Une soumission ne
  part que sur un verdict `PROMOTE` ; le SPRT local est le filtre qui protège ce budget.
* Attendre avec `wait=30` coûte **une** requête là où interroger chaque seconde en coûte
  trente : toujours préférer l'attente longue au sondage.
* Le volume de l'API est inférieur de deux ordres de grandeur au besoin d'une évaluation
  SPRT (§1). L'arène locale n'est donc pas optionnelle — voir
  [CODINGAME_API.md §3](CODINGAME_API.md).
