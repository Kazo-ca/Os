> [English version](../../../en/wasm/dev/rename-migration.md)

# Migration des données au renommage (HackerOS → Kazo OS)

## But

Le chantier 5 du renommage ([ADR 0228](adr/0228-rename-to-kazoos.md)) a aussi
renommé ce que l'OS écrit sur le disque : bases IndexedDB, clés de stockage,
ids d'apps, chemins de configuration. Un navigateur qui a fait tourner une
version d'avant 3.0.0, ou une sauvegarde `.kosbak` faite avant 3.0.0, contient
donc encore les anciens noms. [ADR 0230](adr/0230-rename-migration-by-previous-version.md)
décide de les migrer : au démarrage, avant que l'OS ouvre sa base, et à la
restauration d'une sauvegarde, avant de l'écrire. Après la migration, plus
aucune donnée enregistrée ne porte l'ancien nom, sauf ce que l'utilisateur a
écrit lui-même dans ses documents.

## Architecture

Tout est dans `Infrastructure/Kazo.Os.Infrastructure.Browser/Rename/` :

| Élément | Rôle |
|---|---|
| `RenamedNames` | La table des noms (règles du chantier 5, dans le même ordre) : « HackerOS » → « Kazo OS », `HackerOs.` → `Kazo.Os.`, `Hack*`/`Hacker*` → `Kazo*`, `hackeros` → `kazoos`, `hackweb` → `kazoweb`, `hack-paint` → `kazo-paint`, `hackpkg` → `kazopkg`, `.hackapp` → `.kazoapp`..., puis les noms courts du chantier 9 : `--hos-*` → `--kos-*`, `data-hos-*` → `data-kos-*`, classes `hos-*` → `kos-*`, `.hosbak` → `.kosbak` (un thème ou un réglage enregistré avec un jeton du système de design reste valable). Chaque règle est idempotente. Dit aussi si une base est ancienne (`hackeros`, `hackeros-*`, `hackweb.*`) et si une version précède 3.0.0. |
| `RenamedOsDatabaseRewriter` | Réécrit les enregistrements de la base de l'OS (fonction pure, sur du JSON) et liste les écritures à faire. |
| `RenamedStorageMigration` | Le déroulé au démarrage, sur une interface `IRenamedStorageBrowser` (un faux dans les tests). |
| `RenamedContentRepair` | Depuis 3.2.1, répare une fois les fichiers que la migration de 3.0.0 à 3.2.0 a laissés sur un contenu supprimé (voir plus bas). |
| `BrowserRenamedStorage` + `wwwroot/renamedStorage.js` | Le navigateur : liste et copie des bases, clés `localStorage`/`sessionStorage`, caches. Le JavaScript ne fait que ce que C# ne peut pas atteindre ; les décisions sont en C#. |

L'appel est fait par `LoadInstalledAppsAsync` (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs`),
que chaque hôte navigateur appelle entre `Build()` et `RunAsync()` : c'est le
premier point du démarrage où la page tourne et où rien n'a encore ouvert la
base. Un échec est journalisé et le démarrage continue ; rien n'est marqué,
donc la migration recommence au démarrage suivant.

## Version précédente

Les versions d'avant 3.0.0 n'enregistraient pas leur version dans le
navigateur. La version qui a écrit les données est maintenant gardée dans
`localStorage`, clé `kazoos.osVersion` :

- présente et ≥ 3.0.0 (une préversion de 3.0.0 compte comme 3.0.0) : rien à
  faire, sans même lister les bases ;
- absente, et une ancienne base, une clé ou un cache à l'ancien nom existe : la
  migration s'applique ;
- absente sur un navigateur neuf : la version courante est seulement
  enregistrée.

Une sauvegarde porte maintenant `osVersion` (dans la partie couverte par le
SHA-256). Une sauvegarde dont la base s'appelle `hackeros`, sans `osVersion` ou
avec une version antérieure, est migrée.

## Ce qui est réécrit

1. **Bases IndexedDB.** Chaque ancienne base est copiée sous son nouveau nom
   (`hackeros` → `kazoos`, `hackeros-host-folders` → `kazoos-host-folders`,
   `hackweb.<app>` → `kazoweb.<app>`) : même version, magasins, index et
   enregistrements, tous écrits dans la transaction de mise à niveau qui crée la
   nouvelle base (une copie interrompue ne laisse aucune base). Le clonage
   structuré d'IndexedDB garde les valeurs que JSON ne sait pas porter (les
   `FileSystemDirectoryHandle` des dossiers de l'hôte). L'ancienne base est
   ensuite supprimée. Le contenu des bases de sites n'est pas réécrit : ce sont
   les données des sites.
2. **Clés** `localStorage` et `sessionStorage` à l'ancien nom : renommées, leur
   valeur réécrite ; si la nouvelle clé existe déjà, elle gagne.
3. **Caches** (Cache Storage) à l'ancien nom : supprimés, l'OS les remplit de
   nouveau.
4. **Base de l'OS** (`RenamedOsDatabaseRewriter`), en une seule transaction :
   - **noms de fichiers** (`fsLinks`) dans une zone système, c'est-à-dire hors
     de `/home`, `/root`, `/tmp`, `/mnt` et `/media`, plus les dossiers
     cachés de chaque compte (`~/.config`, `~/.local`, `~/.hackeros`...) : `/etc/hackeros` → `/etc/kazoos`,
     `~/.config/hackeros` → `~/.config/kazoos`, `/var/log/hackweb` →
     `/var/log/kazoweb`. `~/Documents/hackeros` garde son nom. Un nom déjà pris
     n'est jamais écrasé ;
   - **fichiers de configuration** (`/etc/**`, `~/.config/**`) en texte UTF-8 :
     contenu réécrit, enregistré sous son nouveau SHA-256 (morceaux de 256 Kio),
     taille mise à jour ; les anciens morceaux sont supprimés quand plus aucun
     fichier ne les utilise. Les documents ne sont jamais réécrits ;
   - **tous les autres enregistrements** (catalogue, droits, réglages, sessions,
     journaux, métadonnées des fichiers, cibles des liens symboliques dans une
     zone système) : chaque chaîne est réécrite (ids d'apps, de thèmes, de sons,
     capacités, chemins), sauf les secrets (`credential`), l'adressage du
     contenu (`contentHash`, `dataBase64`) et les noms que les personnes ont
     choisis (`loginName`, `displayName`). Un enregistrement dont la clé change
     (l'id d'une app du catalogue) est supprimé sous l'ancienne clé.
5. **Fin.** La version courante est écrite sous `kazoos.osVersion`.

La restauration d'une sauvegarde d'avant 3.0.0 passe par la même étape 4 dans
`IndexedDbBackupService` (validation, puis réécriture avant l'écriture) : la
vérification du SHA-256 se fait sur le contenu d'origine, la restauration écrit
le contenu migré.

## Reprise

`kazoos.renameMigration` (`localStorage`) garde, pendant la migration, la liste
des bases déjà copiées. Une migration interrompue recommence au démarrage
suivant : une base notée n'est pas recopiée, seulement supprimée ; une base du
nouveau nom que la migration n'a pas créée (faite par un autre build) n'est
jamais écrasée, l'ancienne est alors gardée et un avertissement est journalisé.
Toutes les réécritures sont idempotentes.

## Réparation des fichiers abîmés par 3.0.0 à 3.2.0

Jusqu'à 3.2.0, `RenamedOsDatabaseRewriter` enregistrait bien le texte réécrit
d'un fichier de configuration sous son nouveau SHA-256 et supprimait les anciens
morceaux, mais n'écrivait pas le nouveau hachage dans la fiche du fichier
(`fsEntries`) : la fiche, changée en mémoire avant que `RewriteRecords` la
compare à elle-même, ne figurait pas dans les écritures. Le fichier pointait donc
vers un contenu supprimé. Sa lecture échouait en `ProviderFailure` (les réglages
retombaient en silence sur leurs valeurs par défaut, avec un avertissement
`settings: cannot read ...`) et toute écriture échouait, par exemple
l'approbation d'une capacité dangereuse (« The decision could not be recorded:
Settings operation on '/etc/kazoos/app-permissions.config' failed:
ProviderFailure »). Presque tous les fichiers de `/etc/kazoos` et
`~/.config/kazoos` d'avant 3.0.0 étaient touchés : leur en-tête nommait
`/etc/hackeros/...`.

Depuis 3.2.1 :

- la réécriture renvoie aussi les fiches dont elle a changé le contenu, et
  `RewriteRecords` les écrit ;
- `RenamedContentRepair` répare une fois par navigateur, juste après la
  migration, les profils déjà abîmés, comme `fsck` répare un inode dont les
  blocs ont disparu. Il cherche d'abord, avec les seules fiches et les clés des
  morceaux (aucune donnée de fichier chargée), une fiche de fichier dont le
  hachage n'a aucun morceau. S'il en trouve, il lit les morceaux que plus
  aucune fiche ne référence : le texte réécrit y est encore. Pour chaque
  fichier abîmé, il relit chaque texte candidat à l'envers
  (`RenamedNames.ReverseForms` : chaque nouvelle orthographe peut venir de
  l'ancienne ou être déjà la nouvelle), garde les choix dont la taille en
  octets égale celle que la fiche a gardée, et n'accepte qu'un texte d'origine
  dont le SHA-256 est celui de la fiche et dont la réécriture redonne le texte
  candidat. La fiche pointe alors vers ce texte (fichier récupéré). Deux
  fichiers qui avaient les mêmes octets partagent le même texte réécrit : un
  texte prouvé reste disponible pour le suivant. Sans preuve,
  le fichier est vidé (état d'un fichier créé par `touch`) : les réglages qu'il
  portait reprennent leurs valeurs par défaut, comme avant la réparation. La
  recherche est bornée (2^14 étapes par paire fichier-candidat, 2^20 en tout)
  pour ne jamais bloquer le démarrage ;
- chaque fichier réparé est journalisé dans `/var/log/syslog`
  (`fsck: <chemin>: content lost by the 3.0.0 rename migration, recovered` ou
  `..., file emptied (defaults apply)`) ; la clé `localStorage`
  `kazoos.renameContentRepair` note que la réparation a eu lieu. Un échec est
  journalisé et la réparation recommence au démarrage suivant.

Contournement sur une version 3.0.0 à 3.2.0 : supprimer le fichier abîmé
(`sudo rm /etc/kazoos/app-permissions.config`) ; l'OS le recrée à la
prochaine écriture.

## Anciennes adresses des dépôts d'apps

Avec 3.0.0 (ADR 0228), les dépôts d'apps de Kazo-ca ont été renommés
`HackerOs-AppMarketPlace` → `Os-AppMarketPlace` et `HackerOs-App-*` →
`Os-App-*`. La table des noms de la migration réécrit chaque `HackerOs` en
`KazoOs` : l'adresse du catalogue officiel qu'un profil créé avant 3.0.0 garde
dans `/etc/kazoos/app-sources.config` est donc devenue
`https://raw.githubusercontent.com/Kazo-ca/KazoOs-AppMarketPlace/packages/catalog.json`,
qui répond 404. Tant que le fichier ne se lisait pas (dommage ci-dessus), l'OS
prenait l'adresse intégrée et le Store marchait ; une fois le fichier réparé par
3.2.1, le Store affichait « The catalog "official" could not be read » et
aucune app.

Depuis 3.2.1, `RenamedAppRepositories` corrige ces noms à la lecture, sans
réécrire le fichier : une adresse de catalogue sur `raw.githubusercontent.com`
ou `github.com` (`AppSourcesConfigDocument`) et une source `github:`
(`AppPackageSource.TryParse`, par exemple la source de mise à jour d'une app
installée depuis son dépôt) dont le propriétaire est `Kazo-ca` et dont le dépôt
commence par `HackerOs-App` ou `KazoOs-App` nomment le dépôt `Os-App...`. Un
autre propriétaire, ou un autre dépôt de Kazo-ca, garde son nom. Tests :
`RenamedAppRepositoriesTests`.

Contournement en 3.2.0 : dans `/etc/kazoos/app-sources.config`, remplacer
`KazoOs-AppMarketPlace` par `Os-AppMarketPlace` (`sudo nano`).

## Serveur

Un serveur installé avant 3.0.0 (volume Docker, dossier de données) a
`hackeros.db`. Au démarrage, `LegacyDatabaseFile.MoveIfNeeded`
(`Server/Kazo.Os.Server/Data/`) renomme `hackeros.db` (et ses fichiers `-wal`,
`-shm`, `-journal`) en `kazoos.db` quand la base configurée s'appelle
`kazoos.db`, n'existe pas encore et que `hackeros.db` est à côté.

## Décisions

- La version précédente est lue dans `localStorage` et non dans la base : elle
  doit être connue avant d'ouvrir une base, et les versions d'avant 3.0.0 n'en
  écrivaient aucune.
- La copie des bases est faite en JavaScript (clonage structuré) ; la réécriture
  des enregistrements est faite en C#.
- Les fixtures `Tests/fixtures/indexeddb` restent telles qu'enregistrées (base
  `hackeros`) : elles servent de données d'une version d'avant 3.0.0.

## Tests

- `Kazo.Os.Infrastructure.Browser.Tests` : `RenamedNamesTests` (table des noms,
  idempotence, versions), `RenamedOsDatabaseRewriterTests` (fixtures v2 et v3
  sans ancien nom hors documents, configuration réécrite sous un nouveau
  hachage, documents gardés, morceaux partagés gardés, secrets et noms choisis
  intacts, seconde passe sans changement, fiche réécrite écrite avec son
  nouveau hachage, écritures qui redonnent la base réécrite),
  `RenamedContentRepairTests` (fichier abîmé récupéré, vidé sans texte réécrit,
  deux fichiers aux mêmes anciens octets tous deux récupérés, texte qui ressemble
  sans preuve jamais utilisé, base saine laissée telle
  quelle, base abîmée par l'ancienne migration entièrement récupérée, une seule
  fois par navigateur), `RenamedStorageMigrationTests`
  (bases, clés et caches, version enregistrée, arrêt immédiat après 3.0.0,
  reprise sans recopie, échec repris au démarrage suivant, base existante
  jamais écrasée, sauvegarde d'avant 3.0.0 migrée et sauvegarde falsifiée
  refusée), `IndexedDbUpgradeFixtureCatalogTests` (les fixtures nomment une base
  d'avant 3.0.0 que la migration envoie vers `kazoos`).
- `Kazo.Os.Server.Tests` : `LegacyDatabaseFileTests`.
- `Kazo.Os.UI.E2E.Tests` : `IndexedDbUpgradeFixtureTests` démarre l'OS sur les
  bases `hackeros` v2 et v3 et vérifie, en plus de la connexion, du fichier et
  du réglage, que `hackeros` a disparu et qu'aucun enregistrement de `kazoos`
  ne contient d'ancien nom.

## Liste des tâches

- [x] Table des noms `RenamedNames`.
- [x] Réécriture de la base de l'OS (`RenamedOsDatabaseRewriter`).
- [x] Migration au démarrage, reprise, version `kazoos.osVersion`.
- [x] Sauvegardes d'avant 3.0.0 migrées à la restauration ; `osVersion` dans les nouvelles sauvegardes.
- [x] `hackeros.db` renommé par le serveur.
- [x] Tests unitaires et E2E.
- [x] 3.2.1 : nouveau hachage des fichiers de configuration écrit, réparation des profils abîmés (`RenamedContentRepair`).
