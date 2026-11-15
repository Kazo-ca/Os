> [English version](../../../../en/wasm/dev/adr/0272-binary-file-content.md)

# ADR 0272 : le contenu des fichiers en binaire dans IndexedDB

## Statut

Acceptée le 2026-11-15, série `storage/base`
([plan](../plans/browser-storage-series-plan.md)). Benoit a retenu la piste le
2026-11-14 (analyse du stockage, §7) et a répondu aux deux questions le
2026-11-15 : le contenu existant est converti **en une fois au premier
démarrage, avec un écran d'attente** (question 1, choix B), et une lecture
vérifie **chaque bloc par son propre hachage** (question 2, choix A). Le
chantier attend le résultat de l'issue
[#538](https://github.com/Kazo-ca/Os/issues/538) pour son ordre dans la série,
pas pour son contenu.

- **Remplace :** le point 2 de l'[ADR 0245](0245-raise-the-maximum-file-size.md)
  (« format stocké inchangé », blocs encodés en base64) et, dans le point 2 de
  l'[ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md), le
  seul encodage des blocs.
- **Garde :** les blocs de 256 Kio, la clé `[SHA-256 du fichier, numéro]`, la
  déduplication par fichier entier, les lots de 64 blocs par transaction et la
  limite de 128 Mio (ADR 0094 et 0245).
- **S'appuie sur :** l'[ADR 0015](0015-browser-storage-and-indexeddb-adapter.md)
  (adaptateur IndexedDB), l'[ADR 0018](0018-indexeddb-failure-and-recovery-policy.md)
  (pannes et reprise) et l'[ADR 0273](0273-short-orphan-content-retention.md)
  (passe de nettoyage).
- **Analyse :** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  constats C4 à C6, piste 2.

## Contexte

- Un bloc de contenu est aujourd'hui l'enregistrement
  `{contentHash, chunkIndex, dataBase64, createdUtcMs}` du magasin `fsContent`
  (`IndexedDbFileContentRepository`). Le base64 occupe 1,34 fois la taille du
  fichier.
- Les enregistrements passent de .NET à JavaScript en JSON par `IJSRuntime`, et
  le SHA-256 est calculé en .NET à l'écriture **et à chaque lecture**.
- Mesuré pour 52 Mio (Release, sans AOT) : écriture 3,37 s, lecture 1,86 s. Le
  même contenu en `Uint8Array`, passé sans JSON et haché par WebCrypto : 0,40 s
  et 0,36 s.
- D'autres codes lisent ou écrivent `dataBase64` sans passer par le dépôt :
  `IndexedDbBackupService` (sauvegarde, tout le magasin en JSON),
  `StorageSnapshotRedactor` (masque le contenu dans un instantané de
  diagnostic), `RenamedOsDatabaseRewriter` et `RenamedContentRepair`
  (migration du renommage, ADR 0230).
- La base `kazoos` est en version 4. Une page d'une version plus ancienne de
  l'OS peut être ouverte en même temps qu'une page plus récente (autre onglet).

## Décision

### 1. Forme d'un bloc

Un bloc écrit à partir de cette ADR est l'enregistrement :

```text
{ contentHash, chunkIndex, data: Uint8Array, chunkHash, createdUtcMs }
```

- `data` : les octets du bloc, sans encodage.
- `chunkHash` : le SHA-256 du bloc, en hexadécimal. Il sert à vérifier un bloc
  lu seul (§4) ; ce n'est pas une clé.
- Le magasin, la clé `[contentHash, chunkIndex]`, l'index `contentHash` et la
  taille des blocs ne changent pas. `contentHash` reste le SHA-256 du fichier
  entier : la synchro (ADR 0030), la vérification des fichiers d'app
  (ADR 0098 §5) et la déduplication continuent de s'en servir.

Les blocs de 4 Mio mesurés dans l'analyse sont un peu plus rapides en lecture
(0,12 s contre 0,19 s) mais changeraient le protocole de blocs de la synchro :
ils ne sont pas retenus.

### 2. Passage des octets et hachage

- Le dépôt de contenu a son propre module JavaScript dans
  `Kazo.Os.Infrastructure.Browser/wwwroot/`, importé par `[JSImport]`. Les
  octets passent en `JSType.MemoryView`, sans JSON et sans base64. Les autres
  magasins gardent l'adaptateur JSON.
- Le SHA-256 du fichier et celui de chaque bloc sont calculés par WebCrypto
  (`crypto.subtle.digest`). WebCrypto ne hache pas par morceaux : le fichier
  est donc présent une fois en entier du côté JavaScript pendant l'écriture
  (128 Mio au plus). L'écriture en flux, qui évite aussi la copie du côté .NET,
  est le sujet de l'[ADR 0275](0275-partial-read-and-file-address.md) §4.
- `/proc/diskstats` continue de compter les octets et le temps de chaque
  transaction (ADR 0057 §4).

### 3. Contenu existant : converti en une fois au premier démarrage

- Au premier démarrage de la version qui apporte cette ADR, **avant que l'OS
  ouvre une session**, tout le magasin `fsContent` est converti : chaque bloc
  en base64 est remplacé, à la même clé, par sa forme binaire avec son
  `chunkHash`.
- Un **écran d'attente** le dit et montre l'avancement (octets convertis sur
  octets à convertir). L'OS ne démarre qu'une fois la conversion finie.
- La conversion se fait du côté JavaScript, par lots d'au plus 16 Mio, une
  transaction par lot. Elle note sa position dans le magasin `syncMetadata` :
  si l'onglet est fermé en cours de route, le démarrage suivant reprend là.
  Quand elle a fini, elle note que la base est convertie et ne revient plus.
- Un seul onglet convertit ; un autre onglet ouvert pendant ce temps montre le
  même écran et attend.
- La passe de nettoyage (ADR 0273) tourne d'abord quand elle existe, pour ne
  pas convertir un contenu orphelin qui va partir.
- Remplacer un bloc à la même clé ne demande pas d'espace en plus au total, et
  l'espace occupé baisse d'environ 25 % à la fin.
- Un bloc qui ne se décode pas est laissé tel quel et noté dans
  `/var/log/syslog` ; son fichier est ensuite signalé comme abîmé à la lecture
  (ADR 0018), comme il l'aurait été avant.
- **Après la conversion, le code ne lit plus que la forme binaire.** Le dépôt
  de contenu, le service worker (ADR 0274) et la lecture partielle (ADR 0275)
  n'ont donc qu'une forme à connaître. L'ancienne forme ne reste lue que par
  la conversion et par la restauration d'une sauvegarde (§5).
- Durée attendue : de l'ordre de 20 secondes par Gio dans Chromium, d'après
  les mesures de l'analyse (lecture base64, hachage, écriture binaire) ; c'est
  une estimation, que le chantier mesure dans les trois navigateurs. Un profil
  neuf ou petit passe l'écran en moins d'une seconde.

### 4. Vérification à la lecture

- Une lecture vérifie chaque bloc par son `chunkHash`, le numéro qui se suit
  et la longueur totale. Le SHA-256 du fichier entier **n'est plus recalculé à
  chaque lecture** : c'est ce qui permet la lecture partielle (ADR 0275).
- La vérification du fichier entier reste disponible à la demande (passe de
  nettoyage, sauvegarde, restauration, synchro).

### 5. Sauvegarde, diagnostic, migration du renommage

- **Sauvegarde** (`IndexedDbBackupService`, ADR 0099) : le document garde
  `formatVersion: 1` et le champ `dataBase64`. L'export encode les blocs
  binaires en base64 dans le document ; la restauration les décode et écrit la
  forme binaire. Une sauvegarde faite avant cette ADR se restaure donc, et une
  sauvegarde faite après reste lisible par une version précédente de l'OS. La
  sauvegarde en flux (piste 11 de l'analyse) n'est pas dans cette ADR.
- **Instantané de diagnostic** (`StorageSnapshotRedactor`) : masque `data` et
  `dataBase64`.
- **Migration du renommage** (`RenamedOsDatabaseRewriter`) : elle ne lit que
  des bases d'avant la 3.0.0, qui n'ont que l'ancienne forme, et elle passe
  avant la conversion. Elle ne change pas. `RenamedContentRepair` lit le
  contenu par le dépôt, après la conversion.

### 6. Version de la base : 5

La base `kazoos` passe en **version 5**. L'étape de migration ne change aucun
magasin pour cette ADR (l'[ADR 0274](0274-single-copy-of-app-files.md) y
ajoute le sien). Le numéro sert de garde : une page d'une version précédente
de l'OS ne peut plus ouvrir la base, au lieu d'échouer fichier par fichier sur
un bloc qu'elle ne sait pas lire. L'onglet plus ancien reçoit `versionchange`
et ferme sa connexion (`indexedDb.js`) ; sa prochaine opération échoue avec
une erreur de version. Le chantier vérifie ce que cet onglet montre alors et,
s'il le faut, lui fait proposer le redémarrage sur la nouvelle version
(ADR 0223, 0239) plutôt qu'une erreur de fichier.

## Conséquences

- Écriture environ 8 fois plus rapide et lecture environ 5 fois plus rapide
  pour la couche de stockage (mesures de l'analyse, Chromium) ; 25 % d'espace
  en moins une fois la base convertie.
- **Pas de retour en arrière** : une fois la base en version 5, une version
  précédente de l'OS ne l'ouvre plus. Remettre en ligne une version plus
  ancienne laisse les profils déjà passés sans accès à leurs fichiers jusqu'au
  retour de la nouvelle version.
- Le premier démarrage après la mise à jour attend la fin de la conversion :
  quelques secondes pour la plupart des profils, plusieurs minutes pour un
  profil de plusieurs dizaines de Gio. L'écran d'attente le dit.
- Une seule forme à lire ensuite : pas de double lecture à garder ni à
  retirer plus tard.
- Le temps gagné ne règle pas à lui seul l'issue
  [#520](https://github.com/Kazo-ca/Os/issues/520) : la couche de stockage
  n'y compte que pour environ 3,4 s sur 43 s (issue #538).
- Firefox et Safari n'ont pas été mesurés ; le chantier mesure les trois
  navigateurs avant de fusionner.

## Options écartées

- **Conversion en arrière-plan, par petits lots, en lisant les deux formes**
  (recommandée au départ) : l'OS démarre sans attendre, mais tout code qui
  lit `fsContent` doit connaître les deux formes pendant la conversion.
  Écartée par Benoit au profit de la conversion en une fois.
- **Ne jamais convertir** (lire les deux formes pour toujours) : l'ancien
  contenu garde ses 34 % de trop et la double lecture ne peut jamais être
  retirée.
- **OPFS pour le contenu** : le plus rapide, mais un deuxième magasin à garder
  cohérent avec IndexedDB (transactions, sauvegarde, réinitialisation). Gardé
  pour plus tard (analyse, piste 9).
- **Garder le SHA-256 géré** : 1,2 à 1,3 s pour 52 Mio contre 0,09 s.

## Décisions de Benoit (2026-11-15)

| Question | Choix | Décision |
| --- | --- | --- |
| 1. Comment convertir le contenu déjà enregistré ? | A. en arrière-plan, par petits lots (recommandé) ; B. en une fois au premier démarrage, avec un écran d'attente ; C. jamais | **B** |
| 2. Que vérifier à chaque lecture ? | A. chaque bloc par son propre hachage (recommandé) ; B. le fichier entier comme aujourd'hui ; C. rien à la lecture | **A** |

## Tâches

- [ ] Module JavaScript du dépôt de contenu, `[JSImport]`, `MemoryView`.
- [ ] Écriture binaire avec `chunkHash`, SHA-256 par WebCrypto.
- [ ] Lecture de la forme binaire, vérification par bloc.
- [ ] Base en version 5, étape de migration vide, test d'une page plus ancienne.
- [ ] Sauvegarde et restauration (format 1 gardé), instantané de diagnostic.
- [ ] Conversion en une fois au premier démarrage : écran d'attente avec
  avancement, reprise après une fermeture, un seul onglet à la fois, test sur
  une base de la version 4.
- [ ] Mesures avant et après dans Chromium, Firefox et Safari.
- [ ] Docs (FR et EN) : `browser-infrastructure.md`, sauvegarde, mémoire,
  `features.md`, entrée de changement.
