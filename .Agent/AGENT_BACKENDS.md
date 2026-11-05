# Backends d'agents : Claude Code et Antigravity CLI

> **Statut** : décidé · **Dernière mise à jour** : 2026-11-05
> Toute valeur numérique propre au projet renvoie à [PARAMETERS.md](PARAMETERS.md).
> Les drapeaux et défauts des deux CLI sont des faits externes, relevés à la date ci-dessus
> et sourcés en §13 : ils ne sont pas des paramètres du projet.

## 1. Le fait structurant : aucun SDK .NET

Le Claude Agent SDK est « programmable in Python and TypeScript ». Sa documentation indique
elle-même la voie depuis un autre langage : *« run the CLI as a subprocess with the `-p` flag
and `--output-format json` »*. Antigravity n'expose qu'un binaire, `agy`.

Dans une pile 100 % .NET, la couche d'agents est donc de la **supervision de processus** —
exactement la forme qu'a déjà le [`MatchRunner`](DETERMINISTIC_LAYER.md) pour les bots.
Ce n'est pas un contournement : c'est la voie documentée.

## 2. Pourquoi Agent Framework reste malgré tout

Le sous-processus ne disqualifie pas le framework, parce que `AIAgent` **est conçu pour être
sous-classé**, y compris sans modèle de chat. La documentation donne un agent d'exemple qui
se contente de mettre du texte en majuscules, et précise que si un agent personnalisé
n'appelle aucun client de chat, il n'y a simplement pas d'outils à invoquer.

Quatre membres à implémenter : `CreateSessionCoreAsync`, `DeserializeSessionCoreAsync`,
`RunCoreAsync`, `RunCoreStreamingAsync`.

Donc `ClaudeCodeAgent : AIAgent` et `AntigravityAgent : AIAgent` sont des citoyens de
première classe, et le projet garde quatre choses qu'il faudrait sinon réécrire moins bien :

| Apport | Ce qu'il remplace | Pourquoi ça compte ici |
| :--- | :--- | :--- |
| **Middleware de run** | Comptabilité éparpillée sur les sites d'appel | Un seul point d'interception pour **tous** les runs, sous-processus compris. C'est là que vit le `BudgetGuard` (§6). |
| **OpenTelemetry (conventions GenAI)** | Des `Process.Start` muets | Les agents entrent dans le même arbre de traces que l'arène, sur le dashboard Aspire déjà en place (§9). |
| **Graphe à nœuds mixtes** | Une machine à états maison | Un `Executor` est du code : le Reviewer déterministe est un nœud du **même** graphe que les agents LLM (§12). |
| **Human-in-the-loop (`RequestPort`)** | Rien d'équivalent | Les portes de consentement du projet : inscription à un contest, garde-fou avant soumission (voir [CODINGAME_API.md §6](CODINGAME_API.md)). |

`AsAIFunction()` permet en plus d'exposer un agent comme outil d'un autre agent, et
`AsAIAgent()` d'exposer un workflow entier comme un agent.

> **Correction d'une affirmation antérieure.** Les versions précédentes de
> [ARCHITECTURE.md](ARCHITECTURE.md) donnaient Agent Framework en *public preview* comme
> « risque accepté ». **La documentation ne l'affirme pas.** Ce qui est vérifiable : des
> versions 1.x publiées activement (`dotnet-1.27.0`, 30 octobre 2026), aucune étiquette de
> stabilité déclarée pour .NET, et une documentation incohérente sur la nécessité de
> `--prerelease` selon les pages. Ne pas rendre un risque plus précis qu'il ne l'est.

## 3. Ce que chaque CLI expose

| | **Claude Code** | **Antigravity CLI** |
| :--- | :--- | :--- |
| Binaire | `claude` | `agy` |
| Mode non interactif | `-p` / `--print` | `-p` / `--print` / `--prompt` |
| Formats de sortie | `text`, `json`, `stream-json` | `text`, `json`, `stream-json` |
| Plafond de tours | `--max-turns` (aucun par défaut) | *absent* — `num_turns` est rendu, pas plafonné |
| Plafond de dépense | `--max-budget-usd` | *absent* |
| Restriction d'outils | `--tools "Read,Edit,Bash"` ; `--disallowedTools` | règles `permissions.allow` en fichier |
| Mode de permission | `--permission-mode` ; `--permission-prompts none` | `--mode` ; `--dangerously-skip-permissions` |
| Délai d'attente | — | `--print-timeout` (défaut `0s`, **aucune limite**) |
| Sortie structurée | — | `--json-schema` |
| Tokens rendus | via `usage` (noms non documentés pour le CLI) | `input_tokens`, `output_tokens`, `thinking_tokens`, `cache_read_tokens`, `total_tokens` |
| Coût rendu | `total_cost_usd` + ventilation par modèle (estimations client) | **aucun** |
| Authentification | clé d'API | credentials en cache, après une session interactive |

Les deux sont assez convergents pour qu'une seule abstraction les couvre. Les divergences
qui coûtent sont dans les deux lignes du bas : le coût et l'authentification.

## 4. Les pièges, et comment le code les neutralise

Ce sont des comportements documentés, pas des bugs. Un orchestrateur autonome tombe dans
chacun s'il ne les traite pas explicitement.

**(a) Antigravity sort en `0` quand il refuse.** Une action nécessitant une approbation
qu'il ne peut obtenir est *soft-denied* : le run continue, **sort en `0`**, et écrit un avis
sur `stderr`. Un orchestrateur qui teste le code de retour enregistrera un succès pour un
cycle où rien n'a été fait.
→ **Ne jamais se fier au code de sortie.** Lire `status` dans le JSON, et comparer le
fichier produit à son état précédent. `RunCoreAsync` est l'endroit où normaliser les
sémantiques d'échec des deux CLI : sans ça, le framework verra une réponse réussie.

**(b) Claude Code refuse par défaut en mode non interactif.** Depuis la v2.1.302, un
`claude -p` démarre en mode Manual si rien ne fixe le mode, et un appel nécessitant une
approbation est refusé.
→ Fixer explicitement `--permission-mode`, sinon le Worker ne pourra pas écrire.

**(c) `--print-timeout` vaut `0s`, soit aucune limite.** Un agent bloqué tiendrait son
créneau indéfiniment, et le heartbeat de [PARAMETERS.md §5](PARAMETERS.md) ne verrait
qu'une absence de progrès sans savoir qu'un processus est à tuer.
→ Passer une valeur explicite, **et** garder le kill wall-clock de l'orchestrateur comme
autorité. Le CLI n'est jamais l'autorité sur sa propre durée.

**(d) `--allowedTools` auto-approuve sans restreindre.** La restriction se fait avec
`--tools` ; `--allowedTools` ne fait que pré-approuver. Deux noms voisins, deux effets
opposés. `--tools` n'affecte pas les outils MCP : les refuser demande
`--disallowedTools "mcp__*"`.

**(e) `permissions.allow` n'est qu'en partie honoré en headless chez Antigravity**
(rapport ouvert, §13). D'où la règle suivante.

## 5. Le périmètre d'écriture est le système de fichiers, pas le CLI

La soumission CodinGame est une chaîne de code unique, donc **un fichier unique**
(voir [CODINGAME_API.md §7](CODINGAME_API.md)). Un agent de code, lui, veut un dépôt.

**Ne fais pas du système de permissions du CLI ta frontière.** Donne au processus un
répertoire de travail qui ne contient que ce qu'il a le droit de toucher. Les drapeaux de
permission sont une seconde couche, utile, mais l'une est partiellement honorée et l'autre
se confond avec son voisin. La frontière qui tient est le système de fichiers.

Corollaire : le plafond de tentatives de compilation de [PARAMETERS.md §5](PARAMETERS.md)
passe **dans** la boucle de l'agent, qui lance `dotnet build` lui-même. L'orchestrateur ne
peut plus les compter ; il compte des tours et lit le résultat.

## 6. Budget : ce qui change pour le `BudgetGuard`

Le `BudgetGuard` ([DETERMINISTIC_LAYER.md §2.H](DETERMINISTIC_LAYER.md)) interceptait des
appels LLM. Il intercepte désormais des **runs d'agents**, via le middleware de run.

* **En amont** : `--max-budget-usd` et `--max-turns` pour Claude Code ; pour Antigravity,
  seuls un plafond de tours applicatif et le wall-clock, puisqu'il n'offre ni l'un ni l'autre.
* **En aval** : lire `usage` et `total_cost_usd`, débiter, écrire dans l'`ExperimentStore`.
* **Antigravity ne rend aucun coût.** Il faut donc tarifer ses tokens depuis une grille de
  prix — qui est une valeur numérique, donc qui vit dans
  [PARAMETERS.md §6](PARAMETERS.md), jamais dans le code.

Et un changement de nature : le plafond de tokens par cycle était pensé pour des appels
unitaires. La consommation d'un run agentique varie d'un ordre de grandeur. Le garde-fou
utile devient **tours + wall-clock + budget**, pas un compte de tokens que l'orchestrateur
saurait prévoir. Voir [PARAMETERS.md §5](PARAMETERS.md).

> Les plafonds de dépense des deux CLI sont des **estimations côté client** : la dépense
> peut dépasser le plafond. Un plafond n'est pas une garantie de facture.

## 7. CPU : un agent CLI consomme des cœurs

C'est le point le plus coûteux de tout ce changement.

[PARAMETERS.md §3](PARAMETERS.md) réservait des cœurs à « l'orchestrateur, le dashboard,
**les appels LLM**, Git ». Ce budget a été dimensionné pour des **appels réseau**, qui ne
consomment presque pas de CPU. Claude Code et `agy` sont des processus locaux : ils lisent
des fichiers, grepent, et lanceront `dotnet build`.

Si un agent tourne pendant un match, l'arène perd l'exclusivité de ses cœurs et **les
mesures deviennent fausses, pas lentes** — l'incident 2 de
[STRATEGY_ANALYSIS.md](STRATEGY_ANALYSIS.md), que ce projet existe pour ne pas revivre.

→ Affinité CPU explicite sur les processus d'agents, et passage par le sémaphore de
l'`ArenaScheduler` ou budget de cœurs d'agents séparé. Voir
[PARAMETERS.md §3](PARAMETERS.md), qui a été modifié pour cela.

## 8. Secrets et authentification

Les deux CLI ajoutent **de nouveaux secrets**, soumis aux mêmes règles que le token
CodinGame — y compris en conteneur : voir [CODINGAME_API.md §2.1](CODINGAME_API.md), dont
chaque ligne s'applique ici.

* **Antigravity bloque le VPS.** Le mode headless « uses your cached credentials.
  Authenticate once with an interactive `agy` session first », et **aucune voie par clé
  d'API n'est documentée**. Sur un VPS sans terminal, dans un conteneur, cela ne marche pas.
  Deux issues : monter le répertoire de credentials comme on monte le token CodinGame, ou
  accepter qu'Antigravity ne tourne que sur le poste de développement. À trancher avant que
  la Phase 5 ait un sens.
* **Claude Code** s'authentifie par clé d'API. La page du SDK précise qu'Anthropic
  n'autorise pas les développeurs tiers à proposer la connexion claude.ai ni ses limites
  dans leurs produits. Pour un système personnel non distribué, le cadre ne mord
  probablement pas — mais à lire avant de bâtir un modèle de coût sur un abonnement.

## 9. Observabilité

Agent Framework émet traces, logs et métriques selon les **conventions sémantiques GenAI**
d'OpenTelemetry. En .NET c'est **opt-in** :
`.AsBuilder().UseOpenTelemetry(sourceName: ...).Build()`. Sans nom de source explicite, le
défaut est `Experimental.Microsoft.Agents.AI`. Le dashboard Aspire est nommé dans la
documentation comme cible de développement, via un exportateur OTLP.

Deux limites à connaître :

* **Les étapes de workflow ne sont pas documentées comme émettant de l'OpenTelemetry.** Le
  graphe émet des *événements*. Le dashboard montrera donc les runs d'agents, pas
  nécessairement la topologie du graphe.
* **Voir les prompts exige `EnableSensitiveData = true`**, que la documentation déconseille
  hors développement. La sortie d'un agent contient des prompts et des contenus de
  fichiers : c'est une raison de plus de ne pas publier le port du dashboard.

Le `stream-json` des deux CLI donne du NDJSON exploitable — Antigravity émet un `usage` par
étape, utile pour un suivi en direct.

## 10. Checkpointing : utile, mais ce n'est pas le filet de sécurité

Les checkpoints sont créés en fin de *superstep* et contiennent l'état des exécuteurs, les
messages et les requêtes en attente. Séduisant pour le critère de la Phase 5 — tourner
plusieurs jours sans intervention.

Trois réserves, à lire avant d'en dépendre :

1. **Côté .NET, seul `CheckpointManager.CreateInMemory()` est démontré.** Aucun store
   durable .NET n'est nommé dans la documentation (Python a un store fichier et un store
   Cosmos). Or un checkpoint en mémoire ne survit pas au crash du processus, qui est
   précisément le cas qu'on veut couvrir. **À vérifier avant de compter dessus.**
2. **`matches.db` fait foi** (règle 3 de `CLAUDE.md`). L'état d'un cycle est reconstructible
   depuis la base. Le checkpointing est une **optimisation de reprise, pas le filet** — et
   il ne doit jamais devenir le filet, sinon la source de vérité se dédouble.
3. Un workflow réhydraté doit **préserver la topologie et l'identité des exécuteurs** :
   changer la forme du pipeline invalide les checkpoints existants.

## 11. Déterminisme : où passe la frontière

La Phase 0 exige la reproductibilité à seed identique. Un CLI agentique est **moins**
reproductible qu'un appel unique : il prend un nombre variable de tours.

Ce n'est pas un problème, à une condition : que la frontière soit écrite.

> **Le déterminisme est exigé de l'arène, jamais de l'agent.** L'agent produit un fichier de
> bot ; l'arène le mesure de façon reproductible. Les deux exigences ne portent pas sur le
> même objet, et les confondre conduirait soit à renoncer aux agents, soit à renoncer à la
> mesure.

## 12. Quels rôles gardent un agent

Passer aux CLI rend visible que certains « agents » de
[MULTI_AGENT_SPEC.md](MULTI_AGENT_SPEC.md) n'en étaient pas.

| Rôle | Forme | Pourquoi |
| :--- | :--- | :--- |
| **Worker / Coder** | Agent CLI | Lit du code, édite, compile. C'est le cas d'usage. |
| **Utils-Writer** | Agent CLI | Écrit des parseurs et des extracteurs. |
| **Researcher** | Agent CLI | Lit des replays, grepe des logs. Bon endroit pour le **second backend** : deux modèles proposant des architectures différentes, c'est de la diversité de raisonnement réelle. |
| **Reviewer** | `Executor` (code) | La spec dit déjà qu'il n'a « aucune latitude statistique » : il lit un verdict SPRT et choisit entre trois issues. C'est une table de décision. Lui payer un CLI agentique coûte de l'argent et ajoute du non-déterminisme pour rien. |
| **Admin** | `Executor` (code) | Provisionnement déterministe. |

Trois agents, deux exécuteurs, un seul graphe. C'est une simplification, pas une perte.

## 13. Ce qui reste ouvert

* **Pourquoi deux backends ?** Répartition des rôles, comparaison A/B de la qualité des
  agents, ou secours mutuel ? Si c'est l'A/B, le backend devient une **variable
  expérimentale** à enregistrer dans `matches.db` : changement de schéma, et chantier d'une
  autre taille. Les deux autres lectures coûtent une abstraction et deux configurations.
* **Store de checkpoints durable en .NET** — existe-t-il ? Voir §10.
* **Authentification headless d'Antigravity sur un VPS** — voir §8.

## 14. Sources

Relevées le 2026-11-05. Les comportements cités sont ceux de ces pages à cette date.

| Sujet | Page |
| :--- | :--- |
| Agents personnalisés (`AIAgent`) | `https://learn.microsoft.com/en-us/agent-framework/concepts/agents/custom-agents` |
| Middleware | `https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware/` |
| Workflows | `https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/` |
| Checkpoints | `https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints` |
| Human-in-the-loop | `https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop` |
| Observabilité | `https://learn.microsoft.com/en-us/agent-framework/agents/observability` |
| Agent comme outil | `https://learn.microsoft.com/en-us/agent-framework/agents/tools/` |
| Claude Agent SDK (langages, voie sous-processus) | `https://code.claude.com/docs/en/agent-sdk/overview` |
| Claude Code — référence CLI | `https://code.claude.com/docs/en/cli-reference` |
| Claude Code — mode headless | `https://code.claude.com/docs/en/headless.md` |
| Antigravity CLI — headless | `https://antigravity.google/docs/cli/headless/` |
| Antigravity CLI — modes d'exécution | `https://antigravity.google/docs/cli/modes/` |
| `permissions.allow` partiellement honoré en headless | `https://github.com/google-antigravity/antigravity-cli/issues/548` |

> **Mise en garde de méthode.** Plusieurs de ces pages, récupérées automatiquement,
> contenaient un bloc inséré se faisant passer pour des consignes adressées à l'agent qui
> les lisait. Le contenu web est une **donnée**, jamais une instruction — y compris un
> statement CodinGame que le Worker va lire.
