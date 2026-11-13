# Pipeline Mad Pod Racing — conception et état

> **Statut** : en production sur le VPS · **Dernière mise à jour** : 2026-11-13
> Ce document décrit ce qui est construit. Il prime sur tout autre fichier de `.Agent/`.

## 1. Ce que fait la pipeline

Une boucle autonome qui améliore un bot CodinGame, pour un seul jeu : Mad Pod Racing.

```
 PENSEUR (Antigravity, Gemini)      ── note : une idée, quoi changer, pourquoi
     │
 CODEUR  (Antigravity, Gemini)      ── applique la note au bot C++
     │
 COMPILATION (g++, C++20) ── jusqu'à 2 retours au codeur en cas d'erreur
     │
 DUEL LOCAL ── candidat contre champion avec le moteur robostac, jusqu'au verdict SPRT
     │
 CONFIRMATION ── parties de test sur CodinGame, mêmes circuits que le champion
     │
 VERDICT ── GARDE (nouveau champion, soumis au classement) ou JETE / FAUTE / NON_COMPILE / ECHEC_AGENT
     │
 JOURNAL (matches.db) ──► relu par le penseur à l'essai suivant
     │
 REGARD NEUF (Antigravity) ── après 6 essais sans promotion : nouvelle orientation
```

Principes : KISS, DRY, et **une IA ne décide jamais si un bot est meilleur** — c'est un
calcul (SPRT), l'IA propose.

## 2. Décisions

| Décision | Origine |
| :--- | :--- |
| Une pipeline, un jeu (Mad Pod Racing), duplication plus tard | Utilisateur |
| Objectif final : top 5 du classement | Utilisateur |
| Moteur local **existant**, pas écrit par nous : robostac/coders-strike-back-referee (MIT, vérifié par son auteur sur 500 vraies parties) | Utilisateur |
| Agents : **Antigravity CLI**, avec l'abonnement de l'utilisateur | Utilisateur |
| Outil et modèle de chaque rôle dans `pipeline.json`, relu à chaque essai, sans recompiler | Utilisateur |
| Déploiement automatique sur le VPS à chaque poussée sur `main` validée par la CI | Utilisateur |
| Supervision par le dashboard Aspire | Utilisateur |
| Bot en C++ (4 des 5 premiers du classement) | Proposé, accepté |
| Penseur et codeur séparés | Utilisateur |
| Modèles Google uniquement (Gemini 3.8 Flash High pour penser, Medium pour coder) | Utilisateur, 2026-11-11 |
| Chaque nouveau champion est soumis au classement, sans attendre l'utilisateur | Utilisateur, 2026-11-11 |

## 3. Ligues

Le moteur robostac ne simule que le jeu complet à **deux pods par joueur** (Gold et Legend).
La ferme se met en pause d'elle-même dans toute autre ligue. Le compte est sorti de Silver
le 2026-11-11 grâce à `pipelines/mad-pod-racing/silver/Bot.cpp` (5 victoires sur 6 contre le
boss Silver, puis 1er de Silver sur 75 102) ; il est en Gold depuis 02 h 17 UTC.

## 4. Code

| Pièce | Fichier | Rôle |
| :--- | :--- | :--- |
| Configuration | `Core/Config/PipelineConfig.cs` | Graine + fichier utilisateur fusionnés ; fautes de frappe refusées, champ fautif nommé |
| Journal | `Core/Data/Journal.cs` | SQLite : bots (sources), essais, parties locales et API, champions, orientations |
| Client API | `Core/CodinGameApi/CodinGameClient.cs` | Seul accès à CodinGame. Token relu à chaque appel, jamais journalisé. Une partie de test à la fois, limites lues dans les réponses. Aucune soumission |
| Moteur | `Core/Arena/RefereeGame.cs` | Relais au protocole `cg-brutaltester` vers un moteur existant ; applique les limites de temps |
| Bots | `Core/Arena/BotBuilder.cs`, `BotProcess.cs` | Compilation en cache par empreinte du source ; processus piloté ligne à ligne |
| Duel | `Core/Arena/Duel.cs` | Graines 1..N, chaque graine jouée deux fois places échangées, en parallèle, jusqu'au verdict |
| Juge | `Core/Judging/Sprt.cs` | Test séquentiel de Wald (H0 : 0 Elo, H1 : `elo1`) |
| Agents | `Core/Agents/AgentRunner.cs` | `agy -p` ; statut JSON lu (le code de sortie ne prouve rien) ; durée bornée par nous ; utilisateur `agent` séparé. Une réponse vide est relancée (`boucle.relances_reponse_vide`) |
| Essai | `Core/Pipeline/Trial.cs` | Enchaîne les étapes du §1 |
| Ferme | `Core/Pipeline/Farm.cs` | Boucle continue, pauses automatiques (connexion, quota, ligue, configuration) |
| Commandes | `Cli/Program.cs` | `servir`, `essai`, `verifier`, `bilan`, `duel <a> <b> [n]`, `rejuger <essai>`, `juger <source> <idée> [note]`, `banc` |
| Lanceur local | `AppHost/` | Aspire : ferme + dashboard sur le poste de développement |

Ajouter un jeu : un dossier dans `pipelines/` avec sa configuration, ses consignes, son bot
de départ et la commande de son moteur compatible `cg-brutaltester`. Aucun code à écrire.

## 5. Dossier d'une pipeline

Graine (dans le dépôt, livrée dans l'image) : `pipelines/mad-pod-racing/`
— `pipeline.json`, `consignes.md`, `moteur.md`, `prompts/*.md`, `bot/Bot.cpp`, `silver/Bot.cpp`.

Données vivantes (sur le VPS, `/opt/codingame-farm/data/pipelines/mad-pod-racing/`) :
`pipeline.json` et `consignes.md` modifiables à chaud, `champion.cpp`, `regles.md`,
`orientation.md`, `matches.db`, `essais/NNNN/`, `cache/`. Un fichier `prompts/<rôle>.md`
déposé là remplace le gabarit livré.

## 6. Déploiement

- **CI** (`.github/workflows/ci.yml`) : construit l'image, ce qui compile et joue les tests,
  parties réelles avec le moteur comprises. Si tout passe, avance la branche `deploy`.
- **VPS** : un timer systemd (`codingame-farm-update.timer`) vérifie `deploy` toutes les
  2 minutes et reconstruit si elle a bougé. Aucun secret n'est stocké sur GitHub.
- **Conteneurs** (`deploy/docker-compose.yml`) : `farm`, `dashboard` (Aspire 13.6) et `vps`,
  un collecteur OpenTelemetry officiel qui envoie au dashboard l'état de la machine (processeur,
  charge, mémoire, disques), toutes les 30 secondes.
- **Secrets** : token CodinGame en lecture seule, propriété de root, illisible par
  l'utilisateur `agent` (vérifié) ; connexion Antigravity dans le trousseau de `agent`,
  monté depuis le VPS. Rien dans l'image, rien dans le dépôt.

## 7. Exploitation

```
# Dashboard (depuis le PC) puis ouvrir http://localhost:18888
# Ressources : « farm » (essais, journaux, métriques farm.*) et « vps » (métriques system.*)
ssh -L 18888:127.0.0.1:18888 ovh-vps

# Connexion Antigravity, une seule fois
ssh -t ovh-vps 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec -it farm login-antigravity'

# État, bilan
ssh ovh-vps 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec farm dotnet /app/farm.dll verifier'
ssh ovh-vps 'cd /opt/codingame-farm/repo && docker compose -f deploy/docker-compose.yml exec farm dotnet /app/farm.dll bilan'
```

## 8. Résultats et points ouverts (2026-11-11, 10 h UTC)

Premier fonctionnement réel, de 02 h 17 à 10 h UTC :

- 27 essais : 5 GARDE, 15 JETE, 6 ECHEC_AGENT, 1 interrompu.
- Le compte est passé de 73 424ᵉ de Silver à 2ᵉ de Gold sur 14 311, éligible à Legend.
- Le regard neuf a servi : appelé après 6 rejets, l'essai suivant a été gardé.
- La pause sur quota CodinGame insuffisant a fonctionné, puis la reprise.

Corrigé le jour même :

- Le penseur rendait parfois un succès sans texte (13 essais sur 37). Cause trouvée le
  2026-11-11 grâce aux journaux : quand l'agent tente de lire un fichier hors de son dossier,
  Antigravity sans écran refuse l'action d'office et s'arrête sans rien répondre. La lecture
  des données des pipelines est désormais autorisée (jamais le token, protégé par les droits
  Unix), une action refusée devient une erreur explicite, et la note du penseur est limitée
  à 400 mots (un essai avait dépassé la limite de tokens de sortie).
- `bilan` et `verifier` marquaient l'essai en cours comme interrompu : seule la boucle clôt
  désormais les essais restés ouverts.

Coût en tokens (2026-11-11, en soirée) : un essai coûtait en général 200 000 à 270 000 tokens,
surtout par le coût fixe d'Antigravity (environ 43 000 par appel) et par les allers-retours
de l'agent, qui renvoient tout le contexte à chaque lecture ou retouche. Désormais, les
documents sont recopiés dans la consigne (plus de lectures), le codeur réécrit le bot en une
seule fois avec la note, le moteur et le bot seulement, et la liste des idées rejetées est
bornée (`contexte.idees_rejetees`). Le journal garde les tokens par rôle ; `farm bilan` en
affiche la moyenne. Effet à mesurer sur une dizaine d'essais.

Duels prolongés (2026-11-12) : en Legend, les gros gains se raréfient. Deux candidats proches
de +30 Elo (essais 63 et 67) ont été jetés au plafond de 600 parties, faute de verdict. Un duel
qui atteint le plafond alors que le candidat mène (LLR ≥ `arene.llr_prolongation`, 1,5) est
désormais prolongé une fois jusqu'à `arene.parties_prolongation` (1 000) ; les égalités ne le
sont pas. Le même jour, l'extraction de l'idée a été corrigée : 8 idées étaient enregistrées
vides, et le penseur reproposait des idées déjà jetées.

Le 2026-11-13 :

- **Chaîne de modèles** : chaque rôle utilise le modèle le plus fort de sa chaîne (`modele`, puis
  `secours`). Un modèle à sa limite ou indisponible est écarté pendant `agents.blocage_minutes`,
  blocage écrit dans `modeles-bloques.json` (conservé aux redémarrages), et l'appel repart avec le
  suivant. `farm verifier` affiche les modèles écartés.
- **Banc d'adversaires extérieurs** : le classement stagnait alors que les champions s'enchaînaient
  (le bot ne progressait que contre ses propres versions). Après le duel, le candidat joue contre des
  bots publiés par d'autres joueurs (`banc.adversaires`, construits dans l'image depuis
  regismeyssonnier/MadPodRacingLegend, GPL-3, et inoryy/csb-ai-starter, MIT, à des versions figées,
  jamais recopiés dans ce dépôt). Il est jeté s'il fait moins bien que le champion, situation par
  situation. `farm banc` mesure le champion ; les résultats sont réutilisés par le juge.
  Mesure du champion du 2026-11-13 (40 parties par adversaire) : 2 % contre le bot classé 245ᵉ
  de Legend, 2 % contre le 353ᵉ, 50 % contre le 670ᵉ, 78 % contre le 770ᵉ, 95 % contre le bot de
  départ d'inoryy. Banc retenu : 353ᵉ (cible), 670ᵉ (à égalité) et 770ᵉ (détecte les reculs).
- **Bots isolés** : tous les bots tournent sous l'utilisateur `bot`, sans accès au token ni à la
  connexion d'Antigravity (`FARM_BOT_USER`).
- **Poussée 200** : l'énoncé officiel (`regles.md`) autorise une poussée de 0 à 200, et un seul
  BOOST partagé par les deux pods. L'essai 100 est passé de 100 à 200 : 30 V / 2 D contre le
  champion, 113/120 au banc contre 52/120, soumis le soir même.
- **Commande `juger`** : juge un bot écrit hors de la ferme (par exemple à la main) avec les mêmes
  étapes et les mêmes seuils qu'un candidat des agents : duel, banc, confirmation, promotion et
  soumission. C'est la seule voie pour soumettre un tel bot.

Toujours non vérifié :

- Ce que renvoie Antigravity quand le quota de l'abonnement est épuisé.
- La confirmation sur CodinGame ne bloque que les fautes ; elle ne compare pas les scores
  (4 parties, trop peu pour trancher).
