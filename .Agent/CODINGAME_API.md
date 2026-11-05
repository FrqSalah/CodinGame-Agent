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

### 2.1 Le token dans un conteneur

`~/.config/codingame/token` désigne le `HOME` du processus. Dans un conteneur, c'est le
`HOME` **du conteneur** : le fichier de l'hôte n'y est pas, et le `CodinGameApiGate`
échouera en `401 missing-token` sans que rien d'autre ne semble cassé.

Règles, par ordre de préférence :

1. **Montage en lecture seule** du fichier de l'hôte vers le `HOME` du conteneur. Le token
   reste un fichier appartenant à l'utilisateur, hors de toute image et de tout dépôt.
2. **Secret d'orchestrateur** monté comme fichier (`docker secret`, `secrets:` de Compose).
   Même propriété : le token est un fichier, jamais une valeur dans un descripteur.
3. **Variable d'environnement** : acceptée en dernier recours seulement. Une variable
   d'environnement se lit dans `docker inspect`, fuit dans les dumps de crash et dans les
   traces OpenTelemetry que ce projet exporte précisément vers un dashboard.

Trois interdits qui découlent du reste de ce document :

* **Jamais de token dans un fichier suivi par git**, y compris un gabarit d'environnement.
  Un gabarit est fait pour être committé : y ajouter une ligne de token, c'est inviter la
  valeur à être saisie dans le fichier suivi plutôt que dans sa copie ignorée.
* **Jamais de token dans une couche d'image.** Une couche n'oublie pas : un fichier ajouté
  puis supprimé dans une couche ultérieure reste lisible dans l'image. Un `COPY . .` sans
  `.dockerignore` embarque tout le contexte de build, `.env` compris.
* **Jamais de token dans un dashboard publié.** Le dashboard expose la télémétrie, donc
  les appels sortants. Publier son port revient à publier ce qu'il montre.

> Le `.gitignore` et les hooks du dépôt couvrent le cas du fichier et de la valeur
> littérale. Ils ne couvrent **pas** une image, une variable d'environnement ni un port
> publié : ces trois-là ne passent pas par un commit.

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

## 9. Catalogue relevé le 2026-11-05 et choix du jeu témoin

> Ce relevé est **daté** : le catalogue CodinGame évolue, les ligues du compte changent dès
> la première soumission. Les chiffres ci-dessous sont un instantané, pas une constante du
> projet — ils n'ont donc rien à faire dans [PARAMETERS.md](PARAMETERS.md).

### 9.1 Ce que le compte voit

* **144 puzzles non-arcade** : 98 bot-programming, 27 code-golf, 19 optimization.
* **21 challenges**, dont un seul en cours (voir §9.4).
* Le `statement` d'un puzzle multijoueur est **lisible sans inscription ni partie jouée**.
  Celui d'un contest ne l'est **qu'après inscription**. Deux régimes d'accès différents :
  le pipeline peut donc préparer un puzzle multijoueur de bout en bout sans rien engager
  sur le compte, ce qui n'est pas vrai d'un contest.
* Le champ `community` distingue les jeux écrits par la communauté des contests officiels
  reconvertis en multijoueur. L'API **n'expose pas le referee** : `community` indique
  seulement de quel côté chercher sa source. À vérifier avant de figer un choix (§9.5).

### 9.2 Critères de sélection du jeu témoin

Le jeu témoin n'est pas choisi pour être gagné, mais pour **valider la boucle**. D'où un
ordre de critères inhabituel :

1. **Duel 1v1 strict** (`players_min == players_max == 2`). L'appariement obligatoire de
   [PARAMETERS.md §1](PARAMETERS.md) joue chaque confrontation deux fois places échangées :
   cela suppose exactement deux places.
2. **Information parfaite, aucun aléa.** Le critère d'acceptation de la Phase 0 est la
   reproductibilité à seed identique. Un jeu à aléa non contrôlé par le seed rend cette
   preuve impossible — et sans elle, toutes les mesures qui suivent sont douteuses.
3. **Parties courtes.** Une évaluation SPRT consomme de 100 à 2000 matchs
   ([PARAMETERS.md §1](PARAMETERS.md)). La durée d'une partie est donc le facteur dominant
   du coût CPU de tout le projet, bien avant la qualité du bot.
4. **Classement profond.** Le rang réel est l'un des trois étages de mesure (§4) ; un rang
   relevé sur quelques dizaines de joueurs est du bruit, pas un signal.
5. **C# disponible**, pour rester sur la pile .NET de [ARCHITECTURE.md](ARCHITECTURE.md).
6. **Statement lisible sans engagement** sur le compte.

### 9.3 Candidats mesurés

Les 12 jeux les mieux notés sont **tous** des duels 1v1 et **tous** proposent C#. Le
critère discriminant n'est donc ni le format ni le langage, mais la profondeur du
classement et la complexité du jeu.

| Jeu | Note (votes) | Joueurs classés | Ligue du compte | Statement |
| :--- | :--- | :--- | :--- | :--- |
| `connect-4` | 4.90 (84) | **973** | Wood 2 | 4093 car. |
| `a-code-of-ice-and-fire` | 4.89 (107) | 918 | Wood 3 | 6796 car. |
| `dots-and-boxes` | 4.94 (69) | 728 | Wood 2 | 2644 car. |
| `othello-1` | 4.92 (63) | 667 | Wood 2 | 3583 car. |
| `chess` | 4.87 (46) | 517 | Wood 2 | 7822 car. |
| `ataxx` | 5.00 (4) | 61 | Wood 2 | 3084 car. |

> **`ataxx` illustre le piège que ce projet combat.** Meilleure note du catalogue — sur
> **4 votes**. C'est exactement l'effet non mesurable que le SPRT de
> [PARAMETERS.md §1](PARAMETERS.md) est là pour refuser. Un classement de 61 joueurs ne
> peut pas servir d'étage de mesure : on ne choisit pas un jeu témoin sur une moyenne
> dont l'intervalle de confiance couvre tout l'intervalle possible.

**Recommandation : `connect-4`.** Information parfaite, aucun aléa, 42 coups au maximum
donc les parties les plus courtes du lot, règles tenant en 4093 caractères, et le
classement le plus profond des candidats comparables. Les 27 langages disponibles
couvrent C#, F# et VB.NET.

`chess` et `a-code-of-ice-and-fire` sont écartés pour la Phase 4 non par difficulté de jeu
mais par **durée de partie et volume de règles** : au prix d'une évaluation SPRT, ils
coûtent plusieurs fois `connect-4` pour valider exactement la même boucle.

### 9.4 Le contest en cours n'est pas le jeu témoin

Un contest était en cours au moment du relevé (`fall-challenge-2026`, fin le
2026-11-09T09:00:00Z), compte **non inscrit**. Il ne sert pas de jeu témoin :

* Sa fenêtre est de quelques jours, alors que le projet est en Phase 1 avec un build non
  vérifié ([ROADMAP.md](ROADMAP.md)). Valider une boucle autonome contre une horloge est
  l'inverse de ce que demande la Phase 0.
* Ses règles ne sont lisibles qu'après inscription, et l'inscription est une action sur le
  compte de l'utilisateur : elle demande son accord explicite, elle ne se déduit pas d'un
  objectif de projet.
* Un contest ferme. Un puzzle multijoueur reste disponible, donc rejouable — ce qu'exige
  une mesure comparable entre cycles.

### 9.5 Ce qui reste à vérifier avant de figer le choix

* **Le referee.** L'arène locale a besoin du referee officiel du jeu, et l'API ne le
  fournit pas. C'est la dernière inconnue matérielle de la Phase 4 : sans referee local,
  les ~600 parties de test par jour de [PARAMETERS.md §9](PARAMETERS.md) sont le seul moyen
  de jouer, et §3 du présent document explique pourquoi cela ne suffit pas d'un facteur cent.
* **Le format d'entrée exact**, à lire dans le statement puis à confronter au générateur
  de stub (§10).

## 10. Documentation de référence

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
