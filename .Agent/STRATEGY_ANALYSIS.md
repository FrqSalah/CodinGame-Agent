# Analyse Stratégique : Du Codage Manuel à la Ferme d'Agents (#1 Mondial)

> **Statut** : analyse source, non vérifiée · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

> **Source à compléter** : ce document résume le retour d'expérience public d'un joueur,
> mais **aucun lien vers la source d'origine n'est enregistré**. Les chiffres de
> trajectoire et la citation de la §2 sont *rapportés*, pas vérifiés. Ajouter la référence,
> et distinguer explicitement ce que la source affirme de ce qui relève de notre
> interprétation, avant de traiter ce document comme une base de décision.

## 1. Contexte & Historique

Ce projet vise à répliquer la stratégie documentée par le joueur ayant atteint le
**Top 1 mondial CodinGame** (sur 5M+ développeurs) en septembre 2026.

### Trajectoire du joueur (telle que rapportée par la source)
* **2020** : #1 en Pologne, ~#30 mondial (méthode 100 % manuelle, 2 à 3 jours de dev intensif par jeu).
* **Printemps 2022** : #7 mondial (début de l'assistance LLM interactive).
* **Fin 2025** : #4 mondial (modèle « pipeline semi-automatique », cycles de 12 à 48 h).
* **Septembre 2026** : #1 mondial (40+ pipelines provisionnés, autonomie quasi-complète, intervention humaine réduite à quelques heures par semaine).

---

## 2. Le Changement de Paradigme

```
AVANT :
   Humain (Code) ──▶ Bot ──▶ Soumission manuelle
   (Goulot d'étranglement : temps et attention humaine)

APRÈS :
   Humain (Supervision macro)
      │
      ▼
   Multi-Agent Management Layer (Admin / Researcher / Reviewer / Utils-Writer)
      │
      ▼
   40+ Pipelines provisionnés (Worker + Arena locale + Git + Evaluation Elo)
   (Goulot d'étranglement : CPU et budget de tokens, PAS le temps humain)
```

### Le Principe de Progression
> **Comprendre le niveau ──▶ Scaler horizontalement ──▶ Automatiser ce qui ne scale plus ──▶ Monter d'un niveau d'abstraction.**

---

## 3. Retours d'Expérience & Pièges Réels Évités

L'auteur a documenté des échecs réels en production qui ont dicté l'architecture du système :

### Incident 1 : La Saturation Disque
* **Le problème** : en laissant tourner des dizaines de pipelines, les logs textuels et les replays de milliers de matchs ont saturé le disque dur du serveur, entraînant un crash complet du système et 24 h de maintenance.
* **La règle retenue** : nettoyage déterministe strict. On ne conserve que les statistiques agrégées (Elo, winrate, logs d'erreurs) et un échantillon réduit de défaites notables. Les règles de rétention exactes sont en [PARAMETERS.md §7](PARAMETERS.md), appliquées par le `DiskQuotaManager`.

### Incident 2 : La Saturation CPU et le Biais de Tournoi
* **Le problème** : un bot a saturé 100 % des cœurs CPU. Conséquence : les autres bots en cours de tournoi local ont dépassé leur timeout de réponse, faussant complètement les résultats des tournois locaux et détruisant la fiabilité de l'évaluation. (Les timeouts CodinGame varient selon le jeu — couramment 50 ms ou 100 ms par tour ; la valeur réelle est une donnée de configuration, voir [PARAMETERS.md §2](PARAMETERS.md).)
* **La règle retenue** : **l'isolation des ressources fait partie de l'exactitude expérimentale**. Chaque match doit être confiné (affinité CPU, cgroups Docker, quotas stricts de temps et de mémoire). Ce n'est pas une optimisation de performance : un tournoi non isolé ne donne pas des résultats dégradés, il donne des résultats **faux**.
* **Traduction architecturale** : l'`ArenaScheduler` ([DETERMINISTIC_LAYER.md §2.F](DETERMINISTIC_LAYER.md)) détient l'unique registre des créneaux CPU. Aucun match ne démarre sans créneau attribué.

### Incident 3 : « Agentic Everything » est une erreur
* L'auteur insiste : n'utiliser un agent LLM **que** là où le raisonnement est indispensable. Les parsers, lanceurs de tournois, compilateurs et calculateurs d'Elo doivent être des scripts déterministes écrits une fois pour toutes (via l'agent `Utils-Writer` ou manuellement).

---

## 4. L'Économie du Système

* **Machine hôte** : une seule machine 16 cœurs.
* **Budget Tokens LLM** : environ 250 USD / mois.
* **Cadence des cycles** : boucles d'itération de 24 à 48 h — privilégier la robustesse et la lenteur prévisible plutôt que des boucles rapides nécessitant une attention humaine fréquente. (C'est le régime **cible**. Les cycles de 12 à 48 h mentionnés en §1 décrivent le régime semi-automatique antérieur, désormais abandonné.)
* **Temps humain** : 1 à 2 heures par semaine pour consulter le dashboard agrégé et arbitrer les choix stratégiques majeurs.

Les valeurs opérationnelles de référence sont en [PARAMETERS.md §3, §4 et §6](PARAMETERS.md).

### Comment 40+ pipelines tiennent sur 16 cœurs

Les deux affirmations de ce document — « l'isolation CPU est obligatoire » (incident 2) et
« 40+ pipelines sur une machine 16 cœurs » — paraissent incompatibles. Elles ne le sont que
si l'on confond *provisionné* et *simultané* :

* « 40+ pipelines » désigne le nombre de jeux **provisionnés**, pas le nombre de tournois
  qui tournent en même temps.
* Sur un cycle de 24 à 48 h, un pipeline passe l'essentiel de son temps à **attendre** :
  raisonnement LLM, délai de cycle, file d'attente de l'arène.
* Seuls les pipelines arrivés à l'étape d'évaluation se disputent les créneaux CPU, et leur
  nombre est **plafonné** (voir [PARAMETERS.md §3](PARAMETERS.md)).
* Le débit n'est donc pas limité par le nombre de pipelines mais par le nombre de créneaux
  d'arène. Ajouter des pipelines au-delà de ce point allonge la file, il n'accélère rien.

Sans cet ordonnancement, 40 pipelines sur 16 cœurs reproduisent l'incident 2 à l'identique.
