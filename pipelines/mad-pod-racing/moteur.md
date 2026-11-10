# Moteur de Mad Pod Racing (ligues Gold et Legend)

Les parties locales sont jouées par le moteur libre
[robostac/coders-strike-back-referee](https://github.com/robostac/coders-strike-back-referee)
(licence MIT), que son auteur a vérifié contre plus de 500 vraies parties CodinGame.
Les valeurs ci-dessous viennent de ce moteur ; l'énoncé officiel (REGLES.md) prime
en cas de désaccord.

Coordonnées entières, carte 16000 × 9000, y vers le bas. Angles en degrés,
0 = vers la droite, sens horaire à l'écran.

## Un tour, dans l'ordre

1. **Rotation** vers le point visé, d'au plus 18° par tour (aucune limite au premier tour).
2. **Poussée** : `v += (cos a, sin a) × poussée`. BOOST = 650, une fois par pod et par
   course. SHIELD : masse 10 pendant ce tour, et aucune poussée pendant les 3 tours
   suivants.
3. **Déplacement** avec détection continue des chocs : deux pods (rayon 400) se touchent
   à 800 d'écart. Choc élastique : l'impulsion est appliquée, portée à au moins 120,
   puis appliquée une seconde fois. Un checkpoint (rayon 600) est franchi quand le centre
   du pod y entre.
4. **Fin de tour** : position arrondie, vitesse `× 0,85` puis tronquée vers zéro.

## Course

- 3 tours de circuit. On part du checkpoint 0, on vise d'abord le checkpoint 1.
- Un joueur dont aucun pod ne franchit de checkpoint pendant 100 tours perd.
- Les circuits sont tirés parmi une douzaine de modèles, décalés de quelques unités.
- Vitesse limite en ligne droite à poussée 100 : environ 560 par tour.

## Entrées et sorties (deux pods par joueur)

- Au début : nombre de tours, nombre de checkpoints, puis une ligne `x y` par checkpoint.
- À chaque tour : 4 lignes `x y vx vy angle prochainCheckpoint` — vos deux pods, puis
  les deux pods adverses. Au premier tour, l'angle vaut -1.
- Réponse : une ligne par pod, `x y poussée`, `x y BOOST` ou `x y SHIELD`.
  Poussée de 0 à 100.
