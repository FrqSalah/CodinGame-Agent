# Ce qui fait gagner à Mad Pod Racing

Synthèse des techniques employées par les meilleurs bots de ce jeu (ex-Coders Strike Back),
d'après les guides et retours d'expérience publiés par des joueurs du haut du classement.
C'est une carte des directions qui ont fait leurs preuves, pas une recette à appliquer d'un
coup : un essai ne doit faire avancer le bot que d'**un pas** dans l'une d'elles.

## 1. Simuler exactement, puis chercher

Tous les bots du haut du classement font la même chose à chaque tour : ils **simulent la
physique exacte** du jeu (voir MOTEUR.md) sur plusieurs tours à l'avance, et **cherchent** la
suite d'actions qui donne le meilleur état final.

- **La simulation doit être fidèle** au moteur : rotation limitée à 18°, poussée, chocs
  élastiques avec impulsion minimale, frottement de 0,85, arrondi des positions et troncature
  des vitesses à chaque fin de tour, passage de checkpoint dès que le centre entre dans le
  cercle. Une simulation approximative fait optimiser le mauvais problème : c'est la première
  chose à vérifier avant d'affiner quoi que ce soit d'autre.
- **Ce qu'on cherche** : pour chaque pod et chaque tour de l'horizon, un changement d'angle
  (entre -18° et +18°) et une poussée (0 à 100), plus éventuellement BOOST ou SHIELD. Le point
  visé envoyé au jeu se déduit de l'angle choisi.
- **Horizon** : 4 à 8 tours. Au-delà, l'imprécision sur l'adversaire domine.
- **Méthodes qui marchent** : algorithme génétique, recuit simulé, ou mutation aléatoire
  avec conservation des meilleures solutions. Le choix compte moins que le nombre de
  simulations par tour.
- **Réutiliser la solution du tour précédent**, décalée d'un tour, comme point de départ :
  le gain est immédiat et gratuit.

## 2. Utiliser tout le temps disponible

- La force d'une recherche croît avec le nombre de simulations. Les meilleurs bots
  consomment l'essentiel du temps permis (énoncé : premier tour et tours suivants), avec une
  marge de sécurité de quelques millisecondes.
- Le premier tour dispose de beaucoup plus de temps : il sert à chercher plus longtemps, et à
  décider le BOOST de départ.
- La vitesse du code est donc une stratégie à part entière : tableaux de taille fixe, pas
  d'allocation dans la boucle, tables de sinus et cosinus précalculées, `#pragma GCC optimize`.

## 3. Optimiser les deux pods ensemble

- Les deux pods ne sont pas indépendants : ils se gênent ou s'aident par collision. Les
  meilleurs bots cherchent les actions des **deux pods dans la même simulation**, notées par
  une seule évaluation.
- **Rôles** : un **coureur** (le plus avancé) et un **bloqueur**. Le rôle se réévalue à chaque
  tour selon l'avancement, avec un peu d'hystérésis pour éviter les bascules permanentes.

## 4. Une bonne fonction d'évaluation

Elle décide de tout. Les éléments qui reviennent chez les meilleurs :

- **Avancement du coureur** : nombre de checkpoints franchis × une grande constante, moins la
  distance au prochain checkpoint. Ajouter un terme d'orientation ou de vitesse vers le
  checkpoint aide à anticiper le virage suivant.
- **Écart avec l'adversaire** : notre meilleur coureur contre **leur** meilleur coureur. Gêner
  leur coureur vaut autant que faire avancer le nôtre.
- **Bloqueur** : se placer **devant** le coureur adverse, entre lui et son prochain checkpoint,
  orienté vers lui, plutôt que le poursuivre par derrière.
- **Défaite et victoire** : course finie, ou délai de 100 tours sans checkpoint, valent
  ±l'infini.

## 5. Prévoir l'adversaire

- La simulation doit faire bouger les pods adverses, sinon le bot croit qu'ils restent
  immobiles. Le modèle le plus simple et déjà efficace : l'adversaire vise son prochain
  checkpoint à pleine poussée.
- Mieux : consacrer une petite part du temps à chercher les meilleures actions **de
  l'adversaire** avec la même recherche, puis chercher nos actions contre ce plan.

## 6. BOOST et SHIELD

- BOOST : une fois par pod et par course. Le plus souvent au premier tour, ou sur la plus
  longue ligne droite ; à intégrer dans la recherche plutôt qu'en règle fixe.
- SHIELD : multiplie la masse par 10 pendant un tour, mais coupe la poussée pendant les 3
  tours suivants. Utile pour encaisser ou donner un choc décisif ; coûteux s'il est déclenché
  trop souvent. À laisser décider par la simulation.

## Ordre de progression conseillé

1. Simulation exacte et vérifiée.
2. Recherche simple pour le coureur, avec réutilisation de la solution précédente.
3. Évaluation qui compare les deux équipes, et bloqueur placé devant l'adversaire.
4. Recherche commune aux deux pods.
5. Modèle de l'adversaire, puis recherche de l'adversaire.
6. Vitesse du code et usage de tout le temps disponible.

Avant de proposer une idée, situe le champion actuel sur cette liste : la plus grande marge
est en général à la première étape qu'il ne remplit pas encore complètement.

## Références publiques

- Guide de Magus sur Coders Strike Back (simulation et algorithme génétique) :
  http://files.magusgeek.com/csb/csb.html
- Retours d'expérience de joueurs Legend : https://tcourreges.github.io/coders-strike-back et
  https://medium.com/@jstnlef/postmortem-of-coders-strike-back-f4d642d270ca
