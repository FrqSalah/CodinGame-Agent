# Dossier `.Agent` — mémoire du projet

> **Statut** : à jour · **Dernière mise à jour** : 2026-11-10

## Ce que le projet construit

Une boucle automatique qui améliore un bot CodinGame, pour **un seul jeu** : Mad Pod Racing.
Une fois qu'elle tourne, elle sera dupliquée pour d'autres jeux.

Le projet a été **recadré le 2026-11-10**. La conception précédente visait 40 pipelines,
5 rôles d'agents et une arène locale ; elle est mise de côté, pas supprimée.

---

## Documents en vigueur

| Fichier | Contenu |
| :--- | :--- |
| [PIPELINE_SPEC.md](PIPELINE_SPEC.md) | **La spécification à développer.** Elle prime sur tout le reste. |
| [CODINGAME_API.md](CODINGAME_API.md) | Seulement §2 (token), §6 (conduite), §7 (code du bot), §8 (conventions de jeu) et §10 (références). Les §3, §4, §5 et §9 décrivent l'ancienne conception. |
| [AGENT_BACKENDS.md](AGENT_BACKENDS.md) | Seulement §3 à §5 : ce que Claude Code et Antigravity CLI exposent, et leurs pièges. Le reste suppose Agent Framework, mis de côté. |

## Ancienne conception, mise de côté

Ne rien construire à partir de ces fichiers. Ils restent comme réserve d'idées pour plus
tard, quand un signal le justifiera (voir [PIPELINE_SPEC.md §14](PIPELINE_SPEC.md)).

| Fichier | Ce qu'il décrivait |
| :--- | :--- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | .NET Aspire, Microsoft Agent Framework, Docker |
| [MULTI_AGENT_SPEC.md](MULTI_AGENT_SPEC.md) | Cinq rôles d'agents |
| [DETERMINISTIC_LAYER.md](DETERMINISTIC_LAYER.md) | Arène locale, ordonnanceur de cœurs, pool d'adversaires, Elo |
| [PARAMETERS.md](PARAMETERS.md) | Les valeurs de cette ancienne conception. Les réglages actuels sont au §8 de la spécification |
| [ROADMAP.md](ROADMAP.md) | Les phases de l'ancienne conception. L'ordre de livraison actuel est au §12 de la spécification |
| [STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md) | Retour d'expérience rapporté, non sourcé |

---

## Conventions de ce dossier

1. **Une seule source par valeur.** Les réglages vivent dans le `pipeline.json` de la
   pipeline, décrit au §8 de la spécification. Aucun autre document ne les recopie.
2. **Liens relatifs uniquement.**
3. **Chaque fichier porte une date et un statut** en tête.
4. **Pas de LaTeX.** Les formules sont en texte brut ou en bloc de code.
5. **Une décision non prise se déclare comme telle**, avec son origine.
