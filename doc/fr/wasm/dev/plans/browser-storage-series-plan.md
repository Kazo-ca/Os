> [English version](../../../../en/wasm/dev/plans/browser-storage-series-plan.md)

# Stockage dans le navigateur : plan de la série `storage/base`

**Statut :** série ouverte le 2026-11-15, après les décisions de Benoit du
2026-11-14 sur l'[analyse du stockage](browser-storage-optimization-analysis.md)
(§7). Branche d'intégration `storage/base`, créée depuis `main`. Les ADR
[0272](../adr/0272-binary-file-content.md),
[0273](../adr/0273-short-orphan-content-retention.md),
[0274](../adr/0274-single-copy-of-app-files.md) et
[0275](../adr/0275-partial-read-and-file-address.md) sont écrites (chantier 0).
**Aucun code n'est commencé.** Deux questions sont posées à Benoit (§6).

## 1. Objet

Faire que Kazo OS occupe moins d'espace dans le navigateur et lise et écrive
ses fichiers plus vite, sans changer ce qu'une app voit :

- le contenu des fichiers en binaire au lieu de base64 (25 % d'espace en
  moins, écriture environ 8 fois plus rapide, lecture environ 5 fois) ;
- l'espace d'un fichier réécrit ou supprimé récupéré en un jour au plus, en quelques minutes quand l'espace manque ;
- une seule copie des fichiers d'une app installée (une app de 64 Mio passe
  d'environ 236 Mio à 128 Mio, version précédente comprise) ;
- lire une partie d'un gros fichier sans le charger en entier, et une adresse
  de fichier que le navigateur lit lui-même
  ([#379](https://github.com/Kazo-ca/Os/issues/379)).

## 2. Décisions de Benoit (2026-11-14)

| Sujet | Décision |
| --- | --- |
| Par où commencer | Deux issues `TODO` hors série, tout de suite : [#537](https://github.com/Kazo-ca/Os/issues/537) (récupérer l'espace) et [#538](https://github.com/Kazo-ca/Os/issues/538) (profiler [#520](https://github.com/Kazo-ca/Os/issues/520)). Le reste dans la série `storage/base`, ADR avant le code. |
| Fichiers d'une app installée | Le système de fichiers reste la seule source ; le service worker sert `_content/…` depuis le magasin de fichiers ; la copie dans Cache Storage disparaît. |
| Contenus orphelins | Rétention courte ; tout de suite quand l'espace est faible. Le 2026-11-15, dans le fil de #537 : **1 jour**, et 5 minutes quand l'espace est faible. |

## 3. Avant la série : deux issues qui ont leur propre fil

| Issue | Contenu | Lien avec la série |
| --- | --- | --- |
| [#537](https://github.com/Kazo-ca/Os/issues/537) | Brancher la passe de nettoyage, la faire lire seulement les clés, rétention courte | Applique l'[ADR 0273](../adr/0273-short-orphan-content-retention.md), écrite ici, et l'amène dans `main` avec son code. La série s'appuie sur une passe qui tourne (chantiers 2 et 4). |
| [#538](https://github.com/Kazo-ca/Os/issues/538) | Mesurer un enregistrement et une ouverture de 50 Mo dans l'OS réel, couche par couche | Le chantier 1 attend son résultat. Il confirme ou corrige le chantier 6 (écriture en flux), et peut changer l'ordre des chantiers. |

## 4. Chantiers

Une branche `storage/<n>-<sujet>` par chantier, créée depuis `storage/base`,
et une PR vers `storage/base`.

| N° | Branche | Contenu | ADR | Dépend de |
| --- | --- | --- | --- | --- |
| 0 | `storage/0-adrs-and-plan` | Ce plan et les ADR 0272 à 0275 | | |
| 1 | `storage/1-binary-file-content` | Blocs en `Uint8Array` avec leur hachage, module JavaScript du dépôt de contenu (`[JSImport]`, `MemoryView`), SHA-256 par WebCrypto, lecture des deux formes, base en version 5, sauvegarde et instantané de diagnostic | [0272](../adr/0272-binary-file-content.md) §1, 2, 4, 5, 6 | #538, réponses aux questions 1 et 2 |
| 2 | `storage/2-convert-existing-content` | Conversion en arrière-plan de l'ancien contenu par la passe de nettoyage, reprise après redémarrage | [0272](../adr/0272-binary-file-content.md) §3 | 1, #537 |
| 3 | `storage/3-partial-read` | Lecture d'une plage dans le dépôt de contenu, flux de lecture à la demande pour les fichiers de plus de 16 Mio | [0275](../adr/0275-partial-read-and-file-address.md) §1, 2 | 1 |
| 4 | `storage/4-single-copy-of-app-files` | Magasin `servedFiles`, publication sans copie, lecteur de blocs du service worker (flux, `Range`), fin du cache `kazoos-installed-apps` | [0274](../adr/0274-single-copy-of-app-files.md) | 1, #537 |
| 5 | `storage/5-file-address` | `KazoFile.CreateUrlAsync`, route `_kazo/files/`, disque `/` et lecteurs réseau, apps en Web Worker | [0275](../adr/0275-partial-read-and-file-address.md) §3 | 4 |
| 6 | `storage/6-streamed-write` | Écriture sans garder tout le fichier dans la mémoire .NET | [0275](../adr/0275-partial-read-and-file-address.md) §4, à confirmer | 1, #538 |
| 7 | `storage/7-docs-and-version` | `features.md`, `browser-infrastructure.md`, mémoire, entrées de changement vérifiées, API repliée, mesures finales dans Chromium, Firefox et Safari | | tous |

**Ordre et parallèle.** Le chantier 1 d'abord : il change la forme des blocs,
que tous les autres lisent. Ensuite 2, 3 et 4 peuvent avancer en même temps
(ils touchent la passe de nettoyage, le dépôt de contenu et le service worker).
Le chantier 5 suit le 4, dont il reprend le lecteur de blocs. Le chantier 6
part quand #538 a rendu son résultat.

```text
#537 ──────────────┬──────────────► 2
#538 ──► 1 ────────┼──► 3
         │         └──► 4 ──► 5
         └──► 6 (après #538)
                                    7 (à la fin)
```

**Ce que chaque chantier livre**, en plus du code : ses tests (unitaires, et
E2E quand le service worker ou l'OS publié est touché), sa doc dans les deux
langues, son entrée de changement
(`wasm2/KazoOs/changes/unreleased/<guid>.json`), et une mesure avant et après
quand il promet un gain.

## 5. Hors de la série

- **ADR 0250** (persistance du stockage, détection de l'éviction, page
  Réglages ▸ Stockage) : décidée, dans la série `feature/base` (branche
  `feature/8-storage-persistence-and-eviction`). Elle reste là. La page
  Réglages ▸ Stockage pourra montrer l'espace récupérable (ADR 0273).
- **Garder ou non la version précédente d'une app** : inchangé. Deux versions
  partagent déjà leurs fichiers identiques.
- Pistes gardées pour plus tard, seulement si un besoin mesuré le demande :
  compression par bloc (piste 7), déduplication par bloc (8), OPFS pour les
  gros objets (9), sauvegarde en flux (11), mise en cache allégée de la
  version (12).
- Les apps Craft qui reçoivent une adresse de fichier au lieu des octets
  (module `Kazo.Os.AppSdk.Craft`) : à voir après le chantier 5 et #538.

## 6. Questions posées à Benoit (2026-11-15)

Les deux questions sont posées dans le fil de la série, avec leur contexte.
En attendant, les ADR appliquent la recommandation.

| N° | Question | Choix | Recommandé |
| --- | --- | --- | --- |
| 1 | Comment convertir le contenu déjà enregistré en base64 ? (ADR 0272 §3) | A. en arrière-plan, par petits lots ; B. en une fois au premier démarrage, avec un écran d'attente ; C. jamais | A |
| 2 | Que vérifier à chaque lecture d'un fichier ? (ADR 0272 §4) | A. chaque bloc par son hachage ; B. le fichier entier, comme aujourd'hui ; C. rien à la lecture | A |

## 7. Risques

- **Pas de retour en arrière après le chantier 1.** La base passe en
  version 5 : une version précédente de l'OS ne l'ouvre plus (ADR 0272 §6).
- **Service worker.** Les chantiers 4 et 5 changent ce que le worker sert. Les
  pièges connus (page chargée par Ctrl+F5, worker d'une autre version, app
  dans une iframe, isolation d'origine) ont chacun un test E2E à garder vert
  (ADR 0225, 0237, 0241, 0243).
- **Mesures faites dans Chromium seulement.** Firefox et Safari sont mesurés
  par le chantier 1 avant sa fusion.
- **Le gain ne règle pas #520 à lui seul** : la couche de stockage n'y compte
  que pour environ 3,4 s sur 43 s. La suite dépend de #538.

## 8. Règles de la série

- ADR avant le code ; un chantier ne commence pas avant la fusion du
  chantier 0.
- Build et tests locaux verts avant de demander une fusion, suites E2E une à
  la fois ; `build/publish-trimmed.sh` et `TrimmedPublish.E2E` pour les
  chantiers 4 et 5 ; fixture de compatibilité du SDK pour le chantier 5.
- Un fil par chantier. Un agent fusionne ses PR dans `storage/base` quand les
  vérifications sont vertes ; seul Benoit décide quand `storage/base` va dans
  `main`.
- Aucun jeu sur ce repository.
