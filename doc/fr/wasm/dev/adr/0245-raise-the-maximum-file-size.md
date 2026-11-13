> [English version](../../../../en/wasm/dev/adr/0245-raise-the-maximum-file-size.md)

# ADR 0245 : relever la taille maximale d'un fichier à 128 Mio

## Statut

Acceptée le 2026-11-13. Benoit a choisi l'option A de la question 17 de Kazo
Word (relever la limite de l'OS plutôt que découper le `.wasm` dans l'app), puis
**128 Mio** parmi 64, 128 et 256 Mio. Branche `files/raise-max-file-size`,
version 3.4.0.

- **Remplace :** le point 2 de l'[ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md)
  pour la seule taille maximale (16 Mio). Les blocs de 256 Kio, le SHA-256, la
  déduplication et la rétention de 30 jours restent ceux de l'ADR 0094.
- **S'appuie sur :** l'[ADR 0221](0221-pack-all-static-web-assets-of-an-app.md)
  (un paquet d'app fait au plus 64 Mio compressés et 128 Mio extraits,
  `AppPackage`) et l'[ADR 0099](0099-os-backup-and-low-storage.md) (`FileTooLarge`,
  `EFBIG`).

## Contexte

- Le système de fichiers de Kazo OS refuse tout fichier de plus de 16 Mio
  (`FileContentStoragePolicy.DefaultMaxFileSizeBytes`, vérifié par
  `IndexedDbFileContentRepository.WriteAsync`).
- Un paquet d'app peut pourtant contenir 128 Mio une fois extrait. Dans l'OS
  publié 3.3.1, `sudo kazopkg install Kazo-ca/Os-App-Word` échoue avec
  `FileTooLarge` : le `.wasm` de WordCraft fait 60 Mo (22 Mo en gzip).
- L'ADR 0094 demande un nouvel ADR pour changer cette valeur, parce qu'elle
  touche le format stocké dans IndexedDB et le protocole de blocs de la synchro
  (ADR 0030).
- L'écriture gardait tout le fichier en mémoire, l'encodait en base64 et
  l'envoyait à IndexedDB en **une** transaction ; la lecture rapatriait tous les
  blocs d'une seule requête. Pour 128 Mio, cela ferait plus de 170 Mo de
  base64 (le double en chaînes .NET) dans un seul appel JavaScript.

## Décision

1. **Taille maximale.** Un fichier fait au plus **128 Mio**
   (`DefaultMaxFileSizeBytes = 128 Mio`), la taille extraite maximale d'un
   paquet : tout fichier qu'un paquet du Store peut contenir s'installe. Au-delà,
   l'écriture échoue toujours avec `FileSystemErrorCode.FileTooLarge` (`EFBIG`,
   `File too large`) sans rien écrire.

2. **Format stocké inchangé.** Les blocs restent de 256 Kio, adressés par
   `[SHA-256 du contenu, numéro]`, encodés en base64 et dédupliqués. Un fichier
   de 128 Mio fait 512 blocs. Aucune migration IndexedDB : une base existante
   reste lisible, et une version précédente lit aussi un fichier de plus de
   16 Mio écrit par 3.4.0 (elle lit tous les blocs d'un hachage, sans limite) ;
   elle refuse seulement d'en écrire un.

3. **Écriture par lots.** Le contenu est d'abord lu et haché en entier (la clé
   des blocs est le hachage de tout le fichier), puis écrit par lots de
   **64 blocs (16 Mio)** par transaction (`FileContentStoragePolicy.ChunksPerTransaction`).
   Seul le lot en cours est encodé en base64 et traverse l'interop. Un fichier
   de 16 Mio au plus garde une seule transaction, comme avant.

   Si l'écriture s'arrête entre deux lots, aucune entrée ne pointe encore vers
   ce hachage : les blocs déjà écrits sont orphelins et le nettoyage les retire
   après 30 jours, comme tout bloc non référencé. Une nouvelle écriture du même
   contenu ajoute les blocs manquants (`addIfAbsent`). L'entrée du fichier
   n'est écrite qu'après le dernier lot, comme aujourd'hui.

4. **Lecture par lots.** Un fichier d'au plus 64 blocs est lu d'une requête sur
   l'index `contentHash`, comme avant. Un fichier plus grand est lu par lots de
   64 `get` sur les clés `[hachage, numéro]`. Chaque bloc est vérifié (hachage,
   numéro qui se suit), puis la longueur et le SHA-256 de tout le contenu.

5. **Ce qui ne change pas.** Les autres limites de 16 Mio sont des limites à
   part et restent :
   - l'entrée et le résultat d'une tâche `KazoBackground` (ADR 0128 §4) ;
   - la première réponse d'une lecture depuis un worker, le reste arrive par
     blocs (ADR 0070) ;
   - les images de Kazo Paint et les fonds d'écran (ADR 0103, ADR 0202).

   La synchro avec le serveur (ADR 0030) découpe déjà le contenu en blocs de
   256 Kio et n'a pas de taille maximale de fichier : elle n'a rien à changer.

## Conséquences

- Kazo Word s'installe dans l'OS publié à partir de 3.4.0 ; son paquet demande
  cette version au minimum.
- Un fichier de 128 Mio occupe environ 171 Mo dans IndexedDB (base64). Le
  quota du navigateur et l'alerte d'espace faible (ADR 0099) s'appliquent
  toujours.
- Lire un gros fichier le charge encore en entier en mémoire (`MemoryStream`),
  et le nettoyage des blocs orphelins lit toujours tout le magasin `fsContent`
  d'une requête. L'adresse de fichier servie à la demande (issue
  [#379](https://github.com/Kazo-ca/Os/issues/379)) traitera la lecture
  partielle des gros fichiers.
- Le message du SDK pour `FileTooLarge` dit « 128 MiB at most ».
- La constante publique `FileContentStoragePolicy.DefaultMaxFileSizeBytes`
  change de valeur (`PublicAPI.Unshipped.txt`).

## Tâches

- [x] `DefaultMaxFileSizeBytes` à 128 Mio, `ChunksPerTransaction` à 64.
- [x] Écriture et lecture par lots dans `IndexedDbFileContentRepository`.
- [x] Tests : politique, refus au-delà de la limite sans écriture, écriture et
  lecture de plus d'un lot, bloc manquant refusé.
- [x] Docs (FR et EN) : SDK, commandes Linux, sauvegarde, Kazo Paint, index,
  `features.md`, mémoire.
- [ ] Version 3.4.0.
