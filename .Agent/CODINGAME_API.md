# Intégration de l'API publique CodinGame

> **Statut** : validé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique citée ici renvoie à [PARAMETERS.md](PARAMETERS.md).

## 1. Ce que ça change

CodinGame expose une **API publique officielle conçue pour les agents de code**, à
`https://www.codingame.com/api/v1`, documentée à `https://www.codingame.com/llms.txt`.
Elle permet de lire les règles d'un jeu, lancer des parties de test, lire leurs logs,
**soumettre** un bot et **suivre le classement réel**.

Cela **ferme la lacune** que ce dossier signalait jusqu'ici : la boucle n'est plus
condamnée à rester fermée sur un pool local, et il n'y a plus de question de conditions
d'utilisation à trancher — l'usage par un agent est le but affiché de l'API, sous les
règles de conduite du §6.

> **Correction** : les versions antérieures d'[ARCHITECTURE.md](ARCHITECTURE.md)
> affirmaient que « CodinGame n'expose pas d'API publique de soumission » et que
> l'automatiser « peut contrevenir aux conditions d'utilisation ». **C'était faux.**
> Conservé ici comme rappel : une affirmation technique non sourcée dans ce dossier a une
> durée de vie courte.

## 2. Authentification

Device flow OAuth. L'agent ne voit jamais le mot de passe de l'utilisateur :

1. `POST /auth/device` avec `client_name` → renvoie `user_code`, `verification_uri_complete`, `interval`, `expires_in`.
2. L'utilisateur ouvre l'URL, **crée son compte ou se connecte dans le navigateur**, vérifie le code et approuve. Les comptes ne sont jamais créés via l'API.
3. `POST /auth/token` en boucle (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) jusqu'à obtenir `access_token`.

Règles de manipulation du token :

* Stocké dans `~/.config/codingame/token` (dossier en `chmod 700`), **hors du dépôt**.
* **Jamais affiché, jamais collé dans une conversation, jamais committé.** Lu à chaque appel : `-H "Authorization: Bearer $(cat ~/.config/codingame/token)"`.
* Durée de vie choisie par l'utilisateur à l'approbation (2 ans par défaut), révocable sur `https://www.codingame.com/settings/tokens`.
* Portées réduites possibles (par exemple `scope="challenge:read"` pour un agent en lecture seule).
* `401` (`missing-token` / `invalid-token`) → supprimer le fichier et refaire le flow. Ne jamais réessayer avec le même token.
* `403 insufficient-scope` → nouveau token avec la portée indiquée dans le `hint`.

La skill agent officielle est installée dans `~/.claude/skills/codingame/` (voir `llms.txt`
pour la commande d'installation). Une seconde skill, *contributions*, écrit des puzzles et
Clash of Code pour l'utilisateur : **hors périmètre de ce projet**.

## 3. Deux arènes, deux rôles — ne jamais les confondre

C'est la conséquence architecturale la plus importante.

| | Arène locale | API CodinGame |
| :--- | :--- | :--- |
| Volume | illimité | plafonné, voir [PARAMETERS.md §9](PARAMETERS.md) |
| Parallélisme | plusieurs créneaux CPU | **une seule partie de test à la fois, pour tout le compte** |
| Rôle | signal d'itération rapide (SPRT) | **vérité terrain** du classement |
| Adversaires | pool d'ancrage + pool d'entraînement | vrais joueurs du ladder |

**L'ordre de grandeur interdit d'inverser les rôles.** Une seule évaluation SPRT demande
de 100 à 2000 matchs ([PARAMETERS.md §1](PARAMETERS.md)), alors que l'API plafonne autour
de 600 parties de test par jour pour *l'ensemble* du compte — soit une quinzaine par
pipeline et par jour à 40 pipelines. L'écart est de deux ordres de grandeur.

L'arène locale n'est donc pas une commodité de confort : **c'est la seule façon dont ce
projet peut fonctionner**. L'API sert à savoir si les progrès locaux se traduisent sur le
vrai classement, pas à les produire.

## 4. La mesure devient à trois étages

L'API ajoute l'étage qui manquait, et rend les deux autres secondaires :

1. **Elo d'entraînement** (local, pool évolutif) — décision du cycle courant seulement, non comparable entre cycles.
2. **Elo d'ancrage** (local, pool gelé) — comparable dans le temps, proxy local peu coûteux.
3. **Rang CodinGame réel** (API) — l'objectif réel, externe, non falsifiable par la boucle.

Le pool d'ancrage avait été introduit comme substitut à une mesure externe absente. Cette
mesure existe maintenant : l'ancrage reste un garde-fou bon marché, mais **c'est le rang
réel qui arbitre**. Un Elo d'ancrage qui monte alors que le rang stagne est le signal d'un
surapprentissage sur le pool local — précisément ce que le dossier craignait sans pouvoir
le détecter.

**Délai de stabilisation** : le score du leaderboard est une cote de compétence calculée
sur les parties classées de la soumission courante ; il n'est **définitif que lorsque les
battles atteignent 100 %**. Lire le rang trop tôt après une soumission donne un chiffre
qui bougera encore.

## 5. Couplage avec la boucle d'agents

* **Une soumission ne part que sur un `PROMOTE`.** Le budget de soumissions est la
  ressource la plus rare du système et il est partagé par tous les pipelines. Un
  `ROLLBACK` ou un `TUNE_PARAMS` ne soumet jamais. Le SPRT local est le filtre qui protège
  ce budget.
* **Une promotion de ligue est un événement pour le Researcher.** Battre le boss fait
  changer de ligue, et une nouvelle ligue **peut ajouter des entrées et des actions** : le
  statement doit être relu et les hypothèses du bot sont potentiellement invalidées. Ce
  n'est pas un gain incrémental, c'est un changement de problème. Détails dans
  `~/.claude/skills/codingame/references/leagues.md`.
* **Reproductibilité côté API** : la ligne `options` d'un log (par exemple `seed=12`) peut
  être renvoyée en `game_options` pour rejouer la même carte avec une nouvelle version du
  bot, et `replay_game_id` conserve en plus les adversaires et les places. C'est l'exact
  pendant de la discipline d'appariement par seed de
  [DETERMINISTIC_LAYER.md §2.A](DETERMINISTIC_LAYER.md).

## 6. Contraintes et conduite

Ces règles viennent de CodinGame et sont contraignantes :

* **Ne jamais jouer les jeux d'arcade** (puzzles classiques, Clash of Code, Coding Escape).
  L'API les refuse (`403 arcade-game`), et il est également interdit d'écrire une solution
  que l'utilisateur collerait sur le site. Ces jeux sont pour l'humain ; on peut seulement
  les lister et donner le lien.
* **Agir uniquement pour l'utilisateur qui a approuvé le token**, et sur les jeux qu'il a
  demandés.
* Ne modifier le code de l'IDE, le pseudo, le squad ou les équipes de l'utilisateur que
  s'il le demande.
* Respecter les limites et les `Retry-After` : voir [PARAMETERS.md §9](PARAMETERS.md).

## 7. Contraintes sur le code du bot

* La soumission est un corps `{"language": "<id>", "code": "<source>"}` : **une seule
  chaîne de code**, donc un **fichier unique**. Pas de projet multi-fichiers. C'est une
  contrainte forte sur la façon dont le Worker structure le code, et elle ferme une des
  décisions ouvertes de [ROADMAP.md](ROADMAP.md).
* La liste des langages disponibles (`languages`) est propre à chaque jeu et **n'est
  lisible qu'avec un token**. Elle n'est pas supposée ici : à lire depuis l'API avant de
  choisir le langage du bot.
* Pas de replay visuel : les logs de partie sont du texte. Seul le `stderr` du bot de
  l'utilisateur est visible, et uniquement dans les logs de parties de test.
* Les statements sont rédigés dans la langue du compte CodinGame de l'utilisateur.

## 8. Conventions de jeu CodinGame

Utiles au Worker, et cohérentes avec la couche déterministe locale :

* Un bot qui dépasse le temps, plante ou produit une action invalide est **éliminé** :
  score généralement `-1` et partie perdue, quel que soit son score antérieur. **Tenir le
  temps imparti compte plus que jouer mieux.**
* Le premier tour a généralement une limite de temps plus longue que les suivants — ce que
  le `MatchRunner` local modélise déjà avec `FirstTurnTimeoutMs` / `TurnTimeoutMs`.
* Chaque tour : lire toute l'entrée avant d'écrire, écrire exactement le nombre de lignes
  attendu, puis vider `stdout`.
* Quand le statement contredit ces conventions, **le statement gagne**.

## 9. Documentation de référence

Installée localement avec la skill, et en ligne :

| Sujet | Emplacement |
| :--- | :--- |
| Guide agent (token, boucle de jeu, limites, debug) | `~/.claude/skills/codingame/SKILL.md` · `https://www.codingame.com/api/v1/docs/skill` |
| Référence complète des endpoints | `~/.claude/skills/codingame/references/api.md` · `https://www.codingame.com/api/v1/docs/api` |
| Ligues et promotion | `~/.claude/skills/codingame/references/leagues.md` · `https://www.codingame.com/api/v1/docs/leagues` |
| Syntaxe du générateur de stub (format d'entrée) | `https://www.codingame.com/api/v1/docs/stub-generator` |
| Types d'erreurs | `https://www.codingame.com/api/v1/problems/<slug>` |

Toute erreur est un problème JSON (RFC 9457) portant un membre `hint` qui indique quoi
faire ensuite : **lire le `hint` avant de retenter**.
