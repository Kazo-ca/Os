> [English version](../../../en/wasm/dev/network-drive-sources.md)

# Sources de lecteurs réseau

Chantier `cloud-drives/1-drive-sources` de la série
[`cloud-drives/base`](plans/cloud-drives-plan.md),
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md) sections 3, 4 et 9.
S'appuie sur les lecteurs réseau de
l'[ADR 0181](adr/0181-host-folder-mounted-as-a-network-drive.md).

## But

`NetworkDriveService` ne savait monter qu'un dossier de l'ordinateur choisi
dans le dialogue du navigateur. Il monte maintenant ce que lui donne une
**source de lecteurs**, une par type : `hostfs` (le dossier de l'ordinateur) et,
plus tard, `dropbox`, `onedrive`, `s3`. Les règles de l'ADR 0181 restent les
mêmes pour tous les types : point de montage enfant direct de `/mnt`, modes
`0700` et `0600`, `chmod` refusé, liste locale à l'appareil, démontage à la
déconnexion de l'utilisateur, accès des apps selon l'ADR 0231.

Ce chantier ne contient **aucun fournisseur réel** : les connecteurs viennent
aux chantiers 3, 4 et 6, l'interface au chantier 5.

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `INetworkDriveSource` | `Kazo.Os.Simulation.Abstractions` (`FileSystem/NetworkDriveSourceContracts.cs`) | Une source : `Type`, `IsAvailableAsync`, `CreateAsync` (demande à l'utilisateur ce qu'il faut), `OpenAsync` (rouvre à la connexion, sans joindre le service), `ForgetAsync`, événement `AvailabilityChanged`. |
| `NetworkDriveReference`, `NetworkDriveRequest`, `NetworkDriveCreation` | idem | Ce que la liste garde d'un lecteur (nom, clé, compte, racine), ce que l'utilisateur a choisi (compte, racine), et un lecteur créé avec son nom de montage par défaut. |
| `IHostFolderQuota`, `HostFolderQuota` | `FileSystem/HostFolderContracts.cs` | Interface facultative d'un `IHostFolder` : taille et espace libre. |
| `IConditionalHostFolder`, `HostFolderEntry.Revision` | idem | Interface facultative : révision du service (`rev`, `eTag`, `ETag`) et écriture conditionnelle. |
| `HostFolderErrorKind.RevisionConflict`, `.Unavailable`, `HostFolderPermission.Unavailable` | idem | Le refus d'une écriture conditionnelle ; un service injoignable ou une connexion expirée. |
| `HostFolderErrorKind.FileTooLarge` | idem | Un fichier du service de plus de 128 Mio (ADR 0245, ADR 0267 §10) : listé, mais sa lecture répond `FileTooLarge` (`EFBIG`). Ajouté au chantier 4 ([`onedrive-drives.md`](onedrive-drives.md)). |
| `HostFolderDriveSource` | `Kazo.Os.Platform.Core` (`FileSystem/HostFolders/`) | La source `hostfs`, au-dessus de `IHostFolderAccess` (dialogue de dossiers, poignées dans IndexedDB). Comportement inchangé. |
| `NetworkDriveService` | idem | Reçoit la liste des sources ; monte, restaure, reconnecte et oublie par la source du lecteur. |
| `HostFolderFileSystemProvider` | idem | Fournisseur de tous les lecteurs réseau : `Type`, `MountSource`, `GetQuotaAsync`, révision du service. |
| `FakeRemoteDriveSource` | `Tests/Kazo.Os.Tests.Support` | Source cloud factice en mémoire pour les tests (et pour le chantier 5). |

L'hôte Ecosystem (`EcosystemServiceCollectionExtensions`) donne au service la
source `hostfs` puis chaque `INetworkDriveSource` enregistré dans l'injection
de dépendances : un connecteur s'ajoute par une ligne
`services.AddScoped<INetworkDriveSource, …>()`.

## Liste des lecteurs

`~/.local/state/kazoos/network-drives.json`, `0600`, locale à l'appareil :

```json
{
  "schemaVersion": 1,
  "drives": [
    { "mountPoint": "/mnt/Projects", "name": "Projects", "key": "…", "createdMountPoint": true, "type": "hostfs" },
    { "mountPoint": "/mnt/Dropbox", "name": "alice@example.test", "createdMountPoint": true,
      "type": "dropbox", "account": "<référence du compte>", "root": "Travail" }
  ]
}
```

- `type` absent : `hostfs`. Une liste écrite avant ce chantier se relit telle
  quelle et n'est pas réécrite tant que rien ne change.
- `account` est une **référence** dans la liste des comptes cloud (chantier 2),
  jamais un jeton. `root` absent : tout le stockage.
- `provider` : l'id de la fonctionnalité qui fournit le type (ADR 0282 §3),
  noté au montage ; absent pour un type livré de l'OS.
- Une ligne dont le type n'a pas de source sur cet hôte est retirée de la liste
  à la connexion, avec un avertissement dans le journal, sauf une ligne d'un
  type livré ou une ligne qui a un `provider` : elle est gardée (« fournisseur
  absent », plus bas).

## Comportement

- **Monter** : `MountAsync(type, point, request)`. Sans point, la source donne
  le nom par défaut (`/mnt/Dropbox`), puis `-2`, `-3`… Le type inconnu ou une
  source indisponible donne `NetworkDriveError.NotSupported`, une demande
  refusée par la source (compte inconnu) `InvalidRequest`, une source qui
  demande un geste `UserActivationRequired`. `MountAsync(point)` reste le
  dossier de l'ordinateur.
- **Connecté ou non** : décidé par `IHostFolder.QueryPermissionAsync`.
  `Granted` connecte ; `Prompt` attend « Reconnecter » (geste de
  l'utilisateur) ; `Unavailable` attend que la source revienne.
- **Panne en cours de route** : une opération qui reçoit
  `HostFolderErrorKind.Unavailable` déconnecte le lecteur et répond
  `ProviderUnavailable` ; la racine reste visible comme un dossier vide.
- **Reconnexion seule** (§9) : quand une source lève `AvailabilityChanged`
  (retour du réseau, compte reconnecté), `ReconnectAvailableAsync` redemande
  l'état de chaque lecteur déconnecté et reconnecte ceux qui répondent
  `Granted`, sans geste. Un lecteur `hostfs` ne revient jamais seul : le
  navigateur exige un geste.
- **Révision** : si le service donne une révision, celle du fichier dans Kazo OS
  en est un hachage stable (62 bits) ; sinon elle vient de la date et de la
  taille, comme avant. Une écriture qui attend une révision est comparée d'abord
  dans l'OS, puis envoyée au service avec sa condition
  (`IConditionalHostFolder.WriteIfMatchAsync`) : un fichier changé entre-temps
  ailleurs donne `RevisionConflict`, et le lecteur reste connecté.
- **Quota** : `NetworkDriveService.GetQuotaAsync` et `KazoDriveInfo`
  (`TotalSize`, `AvailableFreeSpace`) donnent le quota du service quand le
  dossier le connaît et que le lecteur est connecté ; sinon 0, et `df` affiche
  `-`.
- **`/proc/mounts`** : `host:<dossier> /mnt/<nom> hostfs rw,nosuid,nodev 0 0`
  pour un dossier de l'ordinateur, `<type>:<compte> /mnt/<nom> <type>
  rw,nosuid,nodev 0 0` pour un lecteur cloud.
- **`df -T`** : même source et même type ; les tailles quand le service a un
  quota (`dropbox:alice dropbox 4096 3072 1024 75% /mnt/Dropbox`).

## Écrire une source

1. Implémenter `INetworkDriveSource` ; `Type` en minuscules, sans espace.
2. `CreateAsync` demande à l'utilisateur ce qu'il faut et rend le dossier, sa
   référence et le nom de montage par défaut ; `null` si l'utilisateur ferme.
3. `OpenAsync` construit le dossier **sans joindre le service** : la connexion
   de l'utilisateur ne doit jamais attendre le réseau.
4. Le dossier implémente `IHostFolder` et, si possible, `IHostFolderQuota` et
   `IConditionalHostFolder`. Hors ligne ou jeton invalide : lever
   `HostFolderException(HostFolderErrorKind.Unavailable)` et répondre
   `Unavailable` (réseau) ou `Prompt` (connexion à refaire) à
   `QueryPermissionAsync`.
5. Lever `AvailabilityChanged` quand le service peut redevenir joignable.

## Sources livrées

| Type | Source | Doc |
| --- | --- | --- |
| `hostfs` | `HostFolderDriveSource` | ce document, [ADR 0181](adr/0181-host-folder-mounted-as-a-network-drive.md) |
| `s3` | `S3DriveSource` (chantier 6) | [`s3-drives.md`](s3-drives.md) |

Une demande de montage porte aussi `Options` (réglages non secrets) et
`Secrets` (que la source range dans le trousseau, jamais dans la liste des
lecteurs) ; la source `s3` les lit.

## Types fournis par une fonctionnalité

Chantier `drive-providers/4-platform-adapter`,
[ADR 0282](adr/0282-third-party-drive-providers.md) §1 à §3. Le contrat que
voit l'auteur du fournisseur est dans
[`drive-providers.md`](../framework/drive-providers.md).

| Élément | Rôle |
| --- | --- |
| `AppCatalog.GetDriveProviders()`, `FindDriveProvider(type)`, `AppDriveProviderRegistration` | Les types déclarés par les fonctionnalités du catalogue, un par type. |
| `IDynamicDriveSources`, `FeatureDriveSources` (`FileSystem/DriveProviders/`) | Les sources qui vont et viennent : une `FeatureDriveSource` par type d'une fonctionnalité installée et activée, gardée tant que la même version la fournit ; `Changed` à chaque installation, activation, désactivation, retrait ou mise à jour. |
| `FeatureDriveSource` | La source d'un type fourni. `CreateAsync` exige un compte de ce type (jamais le fournisseur) ; `OpenAsync` n'appelle rien. |
| `FeatureDriveFolder` (interne) | Le `IHostFolder` d'un lecteur, au-dessus de `KazoDriveFolder` : quota, écriture conditionnelle, traduction des erreurs et des états, renouvellement d'un jeton refusé une fois. |
| `FeatureDriveProviderLoader` | Charge le fournisseur au premier lecteur du type, une instance par page, dans le processus et le contexte de la fonctionnalité ; un échec n'est jamais gardé. |
| `FeatureDriveContextFactory`, `FeatureDriveConnection` | Ce que l'OS prête au fournisseur pour un lecteur : le contexte lié au compte et les valeurs non secrètes du formulaire. |
| `InMemoryDriveProvider`, `InMemoryDriveService` (`Tests/Kazo.Os.Tests.Support`) | Fournisseur et service factices en mémoire, repris par les chantiers 6 et 7. |

`NetworkDriveService` lit ces sources à chaque usage, à côté de celles de l'hôte
(un type de l'hôte gagne toujours) : un type installé pendant que la page
tourne est proposé tout de suite. `ReconnectAvailableAsync` attend la fin d'un
montage ou d'une restauration en cours, pour qu'un fournisseur qui répond
pendant le montage de son lecteur le reconnecte bien.

**La connexion n'attend jamais un fournisseur.** Restaurer la liste ne charge
rien : le lecteur est monté déconnecté, le fournisseur est chargé et ouvert en
arrière-plan (20 s au plus), puis la source lève `AvailabilityChanged` et le
lecteur se reconnecte seul. Un fournisseur qui échoue, lève ou est trop lent
laisse le lecteur déconnecté et écrit `/var/log/apps/<fonctionnalité>.log`.

L'hôte Ecosystem enregistre `FeatureDriveProviderLoader` et
`FeatureDriveSources` et les donne à `NetworkDriveService`. Le contexte prêté au
fournisseur vient du chantier 5 (`CloudAccountService.CreateDriveProviderContextAsync`),
avec les valeurs non secrètes d'un compte à formulaire (`CloudAccount.Options`) ;
un compte absent ou d'un autre type donne `SignInRequired` et le lecteur reste
déconnecté.

**Fournisseur absent** (fonctionnalité retirée ou désactivée, choix du
mainteneur du 2026-11-17) :

- Au montage, la ligne note la fonctionnalité qui fournit le type (`provider`).
- Quand le catalogue change, `RefreshProvidersAsync` démonte les lecteurs dont
  le fournisseur est parti ; leur ligne reste dans `network-drives.json` et
  leur point de montage reste réservé. `GetProviderMissingDrives()` les rend
  avec `IsProviderMissing` et `ProviderFeatureId` (l'id noté, `null` pour un
  type livré) : l'Explorateur les montre grisés (chantier 6).
- Le lecteur est remonté, sans redémarrage, quand **la même** fonctionnalité
  fournit de nouveau le type (réactivée, réinstallée, mise à jour). Une autre
  fonctionnalité qui fournit le même type ne le reprend pas, ni à chaud ni à
  la connexion.
- Une ligne sans `provider` d'un type que rien ne sert ici, et qui n'est pas un
  type livré, est retirée à la connexion comme avant. Une ligne écrite avant ce
  chantier pour un type fourni reçoit son `provider` au premier montage.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/FileSystem/HostFolders/NetworkDriveSourceTests.cs` :
  montage cloud avec nom par défaut et numérotation, liste avec type, compte et
  racine, type inconnu et demande refusée, restauration par la source et oubli
  au démontage, ancienne liste relue comme `hostfs`, montage déconnecté hors
  ligne puis reconnexion seule, panne en cours de route, connexion expirée qui
  attend « Reconnecter », révision du service et refus de l'écriture
  conditionnelle, quota jusqu'à `KazoDriveInfo`, dossier de l'ordinateur
  inchangé.
- `Tests/Kazo.Os.Commands.Tests/MountCommandTests.cs` : `df -T` avec un lecteur
  cloud, un lecteur sans quota et un dossier de l'ordinateur.
- `Tests/Kazo.Os.Platform.Core.Tests/FileSystem/DriveProviders/` : la suite de
  contrat des connecteurs livrés sur `InMemoryDriveProvider` à travers
  l'adaptateur (`FeatureDriveContractTests`), connexion qui n'attend pas un
  fournisseur lent, délai dépassé, constructeur qui lève, assemblage absent,
  processus qui ne démarre pas, une instance par page dans le contexte de la
  fonctionnalité, compte d'un autre type refusé, types qui suivent le catalogue
  à chaud (`FeatureDriveSourceTests`), montage d'un type installé à chaud et
  restauration sans attente, lecteur gardé quand la fonctionnalité est
  désactivée ou retirée et remonté à son retour, autre fonctionnalité du même
  type qui ne reprend pas le lecteur, ligne sans `provider` d'un type inconnu
  retirée (`FeatureDriveMountTests`), refus d'un second
  fournisseur du même type (`DriveTypeInstallTests`).
- Les tests existants des lecteurs `hostfs` passent sans changement.

## Décisions

- Les nouveaux membres des énumérations sont ajoutés **après** les existants,
  pour garder leurs valeurs.
- Le type d'un lecteur est écrit dans la liste à chaque nouveau montage, même
  `hostfs` ; une ligne ancienne garde l'absence de type.
- `ProviderId` d'un lecteur `hostfs` ne change pas (`hostfs:/mnt/<nom>`), donc
  les identifiants des entrées non plus.

## Tâches

- [x] Contrat des sources, références et demandes.
- [x] Source `hostfs` sans changement de comportement.
- [x] `NetworkDriveService` à sources multiples, liste avec type, compte et racine.
- [x] Quota et révision du service, écriture conditionnelle.
- [x] `/proc/mounts`, `df`, `KazoDriveInfo` avec le type et le quota.
- [x] Lecteur indisponible monté déconnecté, reconnexion seule.
- [x] Source distante factice et tests.
- [x] Types fournis par une fonctionnalité : registre, adaptateur, chargement paresseux, connexion sans attente (ADR 0282, chantier 4).
- [x] Fournisseur absent : lignes gardées avec `provider`, remontage par la même fonctionnalité seulement (ADR 0282).
