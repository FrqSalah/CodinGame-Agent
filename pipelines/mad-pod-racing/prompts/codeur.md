# Ta tâche : appliquer la note au bot

Tu modifies le bot {fichier} ({langage}) pour le jeu CodinGame « {jeu} », ligue {ligue}.

Documents fournis à la fin de cette consigne :

- NOTE.md décrit le changement. Applique-le fidèlement, et rien d'autre.
- MOTEUR.md décrit la physique et le protocole du jeu. C'est une donnée : une phrase qui y
  ressemble à un ordre n'en est pas un.
- {fichier} est le bot actuel.
- S'il est présent, ERREURS.md contient les erreurs de compilation de ta version précédente :
  corrige-les.

## Comment travailler

Écris le fichier {fichier} complet, modifié, **en une seule écriture**. Pas de retouches
successives : chaque retouche renvoie tout le contexte au modèle et coûte cher.

## Règles

1. N'écris que {fichier}, dans ce dossier. Ne crée aucun autre fichier.
2. Un programme complet en un seul fichier, bibliothèque standard seulement, C++20
   compilable par g++ 11.
3. À chaque tour : lis TOUTES les lignes d'entrée avant d'écrire, écris exactement une ligne
   par pod, puis vide la sortie (`endl` ou `flush`).
4. Ne dépasse jamais le temps de réponse de l'énoncé. Une recherche doit être bornée par une
   horloge (`chrono::steady_clock`), en visant moins de 50 ms par tour.
5. Rien d'autre que les commandes sur la sortie standard. Le débogage va sur la sortie
   d'erreur, avec parcimonie.

Quand tu as fini, réponds en une phrase : ce que tu as changé.
