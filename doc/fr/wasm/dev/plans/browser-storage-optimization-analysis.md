> [English version](../../../../en/wasm/dev/plans/browser-storage-optimization-analysis.md)

# Analyse : optimiser le stockage dans le navigateur

Analyse faite le 2026-11-14 sur `main` (version 3.3.4). **Aucun code n'est changé** et
aucun numéro d'ADR n'est pris : ce document décrit ce que l'OS garde dans le navigateur,
ce que ça coûte, et les pistes pour faire mieux. Chaque piste retenue demandera son ADR
avant le code. Les décisions de Benoit sont au §7.

## 1. Résumé

1. **L'espace n'est jamais récupéré.** Le contenu d'un fichier supprimé ou réécrit reste
   dans IndexedDB : la passe de nettoyage existe, mais aucun hôte ne l'appelle. Sur le
   site publié, un profil neuf a déjà 3 contenus orphelins sur 25 après le premier
   démarrage, et 4 sur 26 après un rechargement.
2. **Le contenu des fichiers occupe 1,34 fois sa taille**, parce qu'il est écrit en
   base64 dans des enregistrements JSON. Le même contenu en binaire occupe sa taille.
3. **Le chemin d'écriture et de lecture peut être environ 8 fois et 5 fois plus rapide**
   (52 Mio : 3,4 s → 0,4 s en écriture, 1,9 s → 0,36 s en lecture) avec des enregistrements
   binaires, un passage d'octets sans JSON et le SHA-256 du navigateur.
4. **C'est bien la cause de la lenteur de l'issue [#520](https://github.com/Kazo-ca/Os/issues/520).**
   Le profilage dans l'OS réel ([#538](https://github.com/Kazo-ca/Os/issues/538), rendu
   le 2026-11-15) montre qu'en publication Release, un enregistrement de 52,5 Mio prend
   4,1 à 4,9 s, dont 96 % dans le dépôt de contenu. Les 43 s de l'issue venaient d'un
   hôte Debug. Le contenu binaire (piste 2) règle donc #520. La première version de
   cette analyse disait le contraire, à tort (voir §3.4).
5. **Une app installée est gardée en trois copies** : la version courante et la
   précédente dans le système de fichiers (en base64), plus une copie des fichiers
   servis dans Cache Storage. Une app de 64 Mio occupe environ 236 Mio.
6. **Le stockage n'est pas protégé contre l'éviction** : `persist()` n'est jamais
   appelé. L'[ADR 0250](https://github.com/Kazo-ca/Os/blob/feature/base/doc/fr/wasm/dev/adr/0250-storage-persistence-and-eviction.md)
   (dans `feature/base`, pas encore bâti) couvre déjà ce point.

## 2. Ce que l'OS garde dans le navigateur

| Endroit | Contenu | Remarques |
| --- | --- | --- |
| IndexedDB `kazoos` (version 4) | Métadonnées des fichiers (`fsEntries`, `fsLinks`), contenu des fichiers (`fsContent`), utilisateurs, groupes, permissions, journal d'audit, diagnostics, synchro | Toute la donnée de l'utilisateur est ici. |
| IndexedDB `kazoweb.<appId>` | Une base par site ASP.Kazo | |
| Cache Storage `kazoos-cache-<version>` | Toute la version publiée de l'OS (325 entrées, 52,2 Mo), posée par le service worker | Sert le fonctionnement hors ligne. |
| Cache Storage `kazoos-installed-apps` | Copie des fichiers `wwwroot` des apps installées, servis à `_content/<assembly>/…` (ADR [0206](../adr/0206-serve-installed-app-assets.md), [0225](../adr/0225-serve-installed-app-files-to-every-page.md)) | Recopiée du système de fichiers au premier lancement de chaque session. |
| Cache Storage `kazoos-isolation` | État de l'isolation d'origine | Négligeable. |
| OPFS | Rien | Seulement effacé par la réinitialisation d'usine. |

**Format du contenu d'un fichier** (`IndexedDbFileContentRepository`,
`FileContentStoragePolicy`) :

- blocs de 256 Kio, 64 blocs (16 Mio) par transaction, 128 Mio au plus par fichier
  (ADR [0245](../adr/0245-raise-the-maximum-file-size.md)) ;
- un enregistrement par bloc : `{contentHash, chunkIndex, dataBase64, createdUtcMs}` ;
- la clé est `[contentHash, chunkIndex]`, où `contentHash` est le SHA-256 **du fichier
  entier** : deux fichiers identiques partagent leur contenu, mais deux fichiers qui
  diffèrent d'un seul octet ne partagent rien ;
- le SHA-256 est calculé en .NET à l'écriture, et recalculé à chaque lecture ;
- les enregistrements passent de .NET à JavaScript en JSON (`IJSRuntime`).

**Paquets d'apps** : 64 Mio au plus pour l'archive, 128 Mio une fois extraite, 2000
fichiers. Les fichiers sont extraits dans `/usr/lib/kazoos/apps/<appId>/<version>/` (ou
`~/.local/share/kazoos/apps/` pour une installation pour soi). L'installeur garde la
version courante et la précédente.

**Politique d'espace** : stockage faible sous 64 Mio ou 10 % libres, réserve de 5 % pour
root (256 Mio au plus), rétention des contenus orphelins de 30 jours
(ADR [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md) pour la rétention,
[0099](../adr/0099-os-backup-and-low-storage.md) pour le stockage faible).

## 3. Mesures

Chromium 153 sans interface, 4 cœurs, profil sur disque, 52 Mio d'octets aléatoires.
Les bancs d'essai sont jetables et ne sont pas dans le dépôt.

### 3.1 En JavaScript seulement

| Méthode | Écriture | Lecture | Espace occupé |
| --- | --- | --- | --- |
| IndexedDB, forme actuelle (base64, JSON) | 1,68 s | 0,56 s | 72,8 Mo (1,34×) |
| IndexedDB, binaire, blocs de 256 Kio | 0,18 s | 0,19 s | 54,7 Mo |
| IndexedDB, binaire, blocs de 4 Mio | 0,17 s | 0,12 s | 54,5 Mo |
| OPFS, `createWritable` | 0,14 s | 0,06 s | lecture partielle de 64 Kio : 2,1 ms |
| OPFS, accès synchrone dans un worker | 0,09 s | 0,03 s | lecture partielle : 0,1 ms |
| Cache Storage | 0,17 s | 0,08 s | 54,5 Mo |

SHA-256 par WebCrypto : 0,09 s. Compression gzip (`CompressionStream`) : 0,9 à 1,2 s
pour compresser, 0,1 à 0,2 s pour décompresser ; ratio 1,0 sur de l'aléatoire, 0,10 sur
du texte répétitif.

### 3.2 Depuis .NET (Release, sans AOT, comme en production)

Projet Blazor WebAssembly qui reproduit le dépôt de contenu avec le vrai `indexedDb.js`.

| Chemin | Écriture | Lecture |
| --- | --- | --- |
| Actuel | 3,37 s | 1,86 s |
| Binaire + SHA-256 WebCrypto + octets passés sans JSON | 0,40 s | 0,36 s |

Détail du chemin actuel, écriture : hachage et découpage 1,36 s (dont 1,2 à 1,3 s pour
le hachage seul), base64 0,27 s, interop et IndexedDB 1,75 s (dont 0,34 à 0,38 s pour
sérialiser en JSON chaque lot de 16 Mio). Lecture : interop et IndexedDB 1,25 s,
décodage et hachage 0,58 s, copies du SDK 0,04 s.

### 3.3 Site publié (os.kazo.ca, profil neuf)

- Usage : 71,6 Mo, dont 52,2 Mo pour `kazoos-cache-<version>` (325 entrées).
- `persisted` : faux.
- 23 fichiers, 24 Ko de contenu utile ; 25 contenus dont 3 orphelins après le premier
  démarrage, 26 dont 4 orphelins après un rechargement. Chaque démarrage en ajoute.

### 3.4 Ce que ça dit de l'issue #520

L'issue mesure, pour un `.psd` de 52,5 Mo : 43,4 s pour enregistrer, 40,0 s pour ouvrir
(2,8 s dans PhotoCraft lui-même), et la mémoire qui passe de 1391 à 2588 Mio. La couche
de stockage, mesurée seule en Release, prend 3,4 s et 1,9 s.

**Correction du 2026-11-15.** La première version de cette analyse comparait ces deux
séries de chiffres et concluait qu'environ 90 % du temps était ailleurs que dans le
stockage. C'était faux : les 43 s de l'issue avaient été mesurées sur un hôte Debug,
et le banc d'essai en Release. Le profilage de l'issue
[#538](https://github.com/Kazo-ca/Os/issues/538), fait dans l'OS réel en publication
Release, donne :

- un enregistrement de 52,5 Mio prend 4,1 à 4,9 s, proche des 3,4 s du banc d'essai ;
- 96 % de ce temps est dans le dépôt de contenu (hachage, base64, JSON, IndexedDB) ;
- le premier enregistrement fait monter la mémoire d'environ 780 Mio.

Le stockage est donc bien la cause de #520, le contenu binaire (piste 2) la règle, et
l'écriture en flux (piste 4) reste justifiée par la mémoire. Le rapport complet est
`large-file-save-profile.md` (branche `docs/large-file-save-profile`).

Une observation reste à part : sur le site publié,
`head -c 1048576 /dev/urandom > a.bin` dans le terminal n'avait pas fini après
4 min 30. Le chemin du terminal a donc son propre problème, sans rapport avec
IndexedDB.

## 4. Constats

- **C1. Les contenus orphelins ne sont jamais ramassés.**
  `IndexedDbFileSystemMaintenance.InitializeAsync` et `CleanupAsync` n'ont aucun appelant
  hors des tests. Tout fichier réécrit laisse son ancien contenu pour toujours.
- **C2. La passe de nettoyage ne tiendrait pas la charge.** Elle commence par un
  `getAll` de tout le magasin `fsContent`, donc elle charge tout le contenu en mémoire.
- **C3. La rétention de 30 jours est longue** pour un stockage sous quota : même une
  fois la passe branchée, un fichier de 50 Mo enregistré dix fois occupe 500 Mo pendant
  un mois.
- **C4. Base64 et JSON coûtent 34 % d'espace** et la moitié du temps d'écriture.
- **C5. Le SHA-256 géré est lent** (1,2 à 1,3 s pour 52 Mio, contre 0,09 s par
  WebCrypto), et il est refait à chaque lecture.
- **C6. Tout est en mémoire en entier.** L'écriture garde la liste de tous les blocs,
  la lecture un `MemoryStream` complet, et `KazoFile` fait ses propres copies. Il n'y a
  ni lecture partielle ni écriture en flux. C'est la cause probable du pic mémoire de
  l'issue #520.
- **C7. La déduplication est par fichier entier**, pas par bloc.
  `browser-infrastructure.md` disait le contraire ; il est corrigé dans le même
  changement.
- **C8. Les apps installées sont en trois copies** (voir §1, point 5), et la copie vers
  Cache Storage est refaite à chaque session.
- **C9. `persist()` n'est jamais appelé** : `RequestPersistenceAsync` n'a aucun appelant.
  Le navigateur peut tout effacer s'il manque d'espace.
- **C10. Le journal réécrit son fichier en entier** à chaque vidage (1 s) :
  `LogFileWriter` lit le fichier, ajoute, et réécrit tout. Avec C1, chaque vidage laisse
  jusqu'à 1 Mio d'orphelin.
- **C11. La sauvegarde charge tout en mémoire** : `IndexedDbBackupService` fait un
  `getAll` de chaque magasin, puis JSON, puis gzip.

## 5. Pistes

1. **Récupérer l'espace.** Réécrire la passe pour lire seulement les clés, la brancher
   au démarrage (en arrière-plan) et quand l'espace est faible, raccourcir la rétention,
   et supprimer tout de suite l'ancien contenu quand le système réécrit ses propres
   fichiers (journaux, réglages). Petit, sans changement de format, gain immédiat.
2. **Contenu binaire.** Enregistrements `Uint8Array` au lieu de base64, octets passés
   par `[JSImport]` et `JSType.MemoryView` au lieu de JSON, SHA-256 par WebCrypto.
   Gain mesuré : 8× en écriture, 5× en lecture, 25 % d'espace en moins. Demande un ADR :
   migration des enregistrements existants (ou lecture des deux formes), format de la
   sauvegarde, lecteurs qui lisent `dataBase64` directement.
3. **Profiler l'issue #520 dans l'OS réel**, couche par couche, avant de la corriger.
   Fait le 2026-11-15 (#538, voir §3.4) : le temps est dans le dépôt de contenu.
4. **Lecture partielle et écriture en flux.** Lire une plage de blocs sans charger le
   fichier, écrire bloc par bloc sans garder la liste. Réduit le pic mémoire ; demande
   de revoir la vérification du hachage (par bloc plutôt que sur le fichier entier).
5. **Adresse de fichier servie par le service worker**
   ([#379](https://github.com/Kazo-ca/Os/issues/379)) : une app lit un gros fichier par
   `fetch` avec `Range`, sans passer par .NET. S'appuie sur la piste 4.
6. **Une seule copie des fichiers d'app.** Option A : le service worker sert
   `_content/…` directement depuis le magasin de fichiers, et la copie dans Cache
   Storage disparaît. Option B : les fichiers servis vivent seulement dans Cache Storage
   et ne sont plus extraits dans le système de fichiers. Avec la piste 2, une app de
   64 Mio passe d'environ 236 Mio à 64 Mio (128 tant que la version précédente est
   gardée).
7. **Compression par bloc** (`CompressionStream`), seulement pour les types qui se
   compressent (texte, JSON, XML, journaux) : ratio 0,10 sur du texte, mais environ 1 s
   pour 52 Mio et aucun gain sur des images ou des archives.
8. **Déduplication par bloc** : clé par hachage du bloc. Utile pour les versions
   successives d'un gros document et les versions d'une app ; change le format et le
   ramassage (compte de références par bloc).
9. **OPFS pour les gros objets.** Le plus rapide et le seul avec une vraie lecture
   partielle (0,1 ms), mais l'accès synchrone demande un worker, et ça fait un deuxième
   magasin à garder cohérent avec IndexedDB (transactions, sauvegarde, réinitialisation).
   À garder pour plus tard, si les pistes 2 et 4 ne suffisent pas.
10. **Bâtir l'ADR 0250** : `persist()` sur un geste, détection de l'éviction, page
    Réglages ▸ Stockage, API `KazoStorage`.
11. **Sauvegarde en flux** : magasin par magasin, avec un curseur, compressée au fil de
    l'eau.
12. **Alléger la mise en cache de la version** : 52 Mo sont mis en cache au premier
    chargement ; les parties rarement utilisées pourraient l'être à la demande.

## 6. Ordre proposé

| Ordre | Piste | Pourquoi là |
| --- | --- | --- |
| 1 | 1. Récupérer l'espace | Petit, sans risque de format, corrige une fuite réelle en production. |
| 2 | 3. Profiler #520 | Dit où mettre l'effort ensuite. Fait : le contenu binaire règle #520. |
| 3 | 2. Contenu binaire | Le plus gros gain mesuré ; change le format, donc tôt. |
| 4 | 10. ADR 0250 | Déjà décidé ; protège les données. |
| 5 | 6. Une copie des fichiers d'app | Gros gain d'espace pour les apps de 30 à 160 Mo. |
| 6 | 4 puis 5. Lecture partielle, adresse de fichier | Règle le pic mémoire des gros documents. |
| 7 | 7, 11. Compression, sauvegarde en flux | Gains ciblés. |
| 8 | 8, 9, 12 | Seulement si un besoin mesuré le demande. |

Les pistes 1 et 3 partent comme deux issues `TODO` indépendantes (#537 et #538). Le
reste fait une série `storage/base`, avec ses ADR avant le code.

## 7. Décisions de Benoit (2026-11-14)

Les trois questions de l'analyse ont été posées à Benoit, qui a retenu les
recommandations.

1. **Par où commencer.** Deux issues `TODO` tout de suite :
   [#537](https://github.com/Kazo-ca/Os/issues/537) (piste 1, récupérer l'espace) et
   [#538](https://github.com/Kazo-ca/Os/issues/538) (piste 3, profiler #520). Le reste
   fait une série `storage/base`, avec ses ADR avant le code.
2. **Où vivent les fichiers d'une app installée.** Option A de la piste 6 : le système
   de fichiers reste la seule source, comme sur une vraie machine ; le service worker
   sert `_content/…` depuis le magasin de fichiers, et la copie dans Cache Storage
   disparaît.
3. **Combien de temps garder un contenu orphelin.** Court, de quelques heures à un
   jour, et tout de suite quand l'espace est faible. La valeur exacte est fixée par
   l'ADR qui remplace la rétention de 30 jours de l'ADR 0094.

## 8. Limites

- Mesures sur une seule machine et un seul navigateur (Chromium). Firefox et Safari
  ont d'autres coûts pour IndexedDB et OPFS, et Safari a des quotas plus stricts.
- Les octets aléatoires sont le pire cas pour la compression et ne disent rien de la
  déduplication.
- Le banc .NET reproduit le dépôt de contenu, pas tout l'OS : pas de métadonnées, pas
  de permissions, pas de pression mémoire.
- Le temps d'un enregistrement dans l'OS réel n'a pas été mesuré par cette analyse ;
  il l'a été ensuite par l'issue #538 (voir §3.4).

## 9. Liens

- [`browser-infrastructure.md`](../browser-infrastructure.md)
- ADR [0015](../adr/0015-browser-storage-and-indexeddb-adapter.md),
  [0018](../adr/0018-indexeddb-failure-and-recovery-policy.md),
  [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md),
  [0099](../adr/0099-os-backup-and-low-storage.md),
  [0206](../adr/0206-serve-installed-app-assets.md),
  [0225](../adr/0225-serve-installed-app-files-to-every-page.md),
  [0245](../adr/0245-raise-the-maximum-file-size.md)
- [ADR 0250](https://github.com/Kazo-ca/Os/blob/feature/base/doc/fr/wasm/dev/adr/0250-storage-persistence-and-eviction.md) (branche `feature/base`)
- Issues [#520](https://github.com/Kazo-ca/Os/issues/520), [#379](https://github.com/Kazo-ca/Os/issues/379)
