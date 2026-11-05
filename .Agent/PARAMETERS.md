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
| Cœurs réservés à l'hôte | 2 | Orchestrateur, dashboard, appels LLM, Git |
| Cœurs dédiés à l'arène | 14 | Jamais utilisés par autre chose pendant un match |
| Cœurs par créneau de match | 2 | Un par processus bot ; le referee partage le créneau |
| Créneaux de match simultanés | 7 | 14 / 2 |
| Tournois simultanés (défaut) | 4 | Évite qu'un seul tournoi monopolise les créneaux |

Ces valeurs sont une **contrainte d'exactitude, pas de performance** : l'incident 2 de
[STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md) a montré que la contention CPU ne ralentit
pas les mesures, elle les invalide. Un match qui ne peut pas obtenir son créneau attend ;
il ne démarre jamais en mode dégradé.

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
| Tokens max par cycle | 1 % du budget mensuel | Clôture du cycle, pipeline mis en pause |
| Heartbeat sans progrès | 2 h | Pipeline déclaré *bloqué*, alerte à l'Admin |
| Durée max d'un tournoi | 4 h | Abandon de l'évaluation, créneaux libérés |

Un pipeline est **bloqué** si, et seulement si, il n'a émis aucun heartbeat de progrès
depuis le délai ci-dessus. C'est la définition unique à laquelle renvoie le déclencheur
de l'Admin Agent.

---

## 6. Budget LLM

| Paramètre | Valeur |
| :--- | :--- |
| Budget mensuel | 250 USD |
| Plafond par cycle | 1 % du budget mensuel |
| Seuil d'alerte | 80 % du budget consommé |
| À 100 % du budget | Pause des pipelines, du moins performant au plus performant ; aucun nouveau cycle |

Le comptage est assuré par le `BudgetGuard` de la couche déterministe, pas par les agents.

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
