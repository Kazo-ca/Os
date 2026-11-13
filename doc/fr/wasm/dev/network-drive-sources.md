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
- Une ligne dont le type n'a pas de source sur cet hôte est retirée de la liste
  à la connexion, avec un avertissement dans le journal.

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
