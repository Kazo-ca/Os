> [English version](../../../en/wasm/dev/onedrive-drives.md)

# Lecteurs OneDrive

Chantier `cloud-drives/4-onedrive` de la série
[`cloud-drives/base`](plans/cloud-drives-plan.md),
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md) sections 4, 6, 8, 9 et 10.
S'appuie sur les [sources de lecteurs](network-drive-sources.md) (chantier 1) et
les [comptes cloud](cloud-accounts.md) (chantier 2).

## But

L'utilisateur monte le OneDrive d'un compte Microsoft qu'il a connecté, dans
`/mnt/OneDrive`. Tout passe par Microsoft Graph depuis le navigateur, sans
serveur et sans secret. Selon l'étendue choisie à la connexion, le lecteur
commence à la racine du OneDrive (`Files.ReadWrite`) ou au dossier réservé à
Kazo OS (`Files.ReadWrite.AppFolder`, `Apps/Kazo OS`), et
éventuellement sous un dossier choisi.

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `OneDriveDriveSource` | Platform.Core (`CloudDrives/OneDrive/`) | La source `onedrive`, sur la base commune `CloudDriveSource` : aucune fenêtre à la création, aucun réseau à la réouverture, `AvailabilityChanged` quand un compte change ou que le réseau revient. |
| `OneDriveFolder` | idem | Le `IHostFolder` du lecteur, avec `IHostFolderQuota` et `IConditionalHostFolder`. |
| `OneDriveGraphClient` (interne) | idem | Les requêtes Graph par `CloudDriveConnection` (jeton, renouvellement après un 401, réseau et 5xx en `Unavailable`) ; les adresses pré-authentifiées par `HttpClient` seul, **sans** jeton ; les refus de Graph en erreurs de l'OS. |
| `CloudDriveConnection`, `CloudDriveSource`, `ICloudAccountTokens` | Platform.Core (`CloudDrives/`) | Base commune des connecteurs, écrite au chantier 3 (Dropbox). |
| `HostFolderErrorKind.FileTooLarge` | Simulation.Abstractions | Un fichier de plus de 128 Mio (ADR 0245, ADR 0267 §10) : `EFBIG`. |

L'hôte Ecosystem enregistre la source :
`services.AddScoped<INetworkDriveSource>(… new OneDriveDriveSource(CloudAccountService, ICloudProviderCatalog, new HttpClient(), IHostActivitySignal))`.

## Correspondance avec Graph

| Opération | Requête Graph |
| --- | --- |
| Élément par chemin | `GET me/drive/root:/a/b:` ou `me/drive/special/approot:/a/b:` ; la racine : `me/drive/root` ou `me/drive/special/approot` |
| Lister | `GET …/children`, pages suivies par `@odata.nextLink` (1 000 pages au plus) |
| Lire | `GET` de l'élément, puis `GET @microsoft.graph.downloadUrl` **sans** jeton. Jamais `/content` : sa redirection casse CORS. |
| Créer un fichier | Vérifier que le parent est un dossier, puis `PUT …:/content?@microsoft.graph.conflictBehavior=fail` vide |
| Créer un dossier | `POST …/children` `{ name, folder: {}, @microsoft.graph.conflictBehavior: "fail" }` |
| Écrire, 4 Mio au plus | `PUT …:/content`, avec `If-Match: <eTag>` pour une écriture conditionnelle |
| Écrire, au-delà | `POST …:/createUploadSession` (avec `If-Match`), puis `PUT` des morceaux de 10 Mio (multiples de 320 Kio) sur `uploadUrl` **sans** jeton ; une session interrompue est supprimée |
| Supprimer | `DELETE` de l'élément (corbeille de OneDrive) ; non récursif : refusé si `folder.childCount > 0` |
| Déplacer, renommer | `PATCH` `{ name, parentReference: { id } }` en une requête |
| Quota | `GET me/drive?$select=quota` : `total`, `remaining` ; absent si l'accès ne le permet pas |

| Réponse de Graph | Erreur de l'OS |
| --- | --- |
| 404 | `NotFound` |
| 409 | `AlreadyExists` |
| 412 | `RevisionConflict` |
| 403 `quotaLimitReached`, 507 | `NoSpace` |
| 413, taille > 128 Mio | `FileTooLarge` |
| 401 après renouvellement, 403 | `Unavailable` (jeton), `NotAllowed` |
| 400 sur un nom | `InvalidName` |
| 429 | `Failure` (« réessayer plus tard ») |
| Réseau, délai, 5xx | `Unavailable` : le lecteur se déconnecte et revient seul |

## Comportement

- **Révision** : l'`eTag` de Graph ; l'écriture conditionnelle l'envoie en
  `If-Match`, et un fichier changé ailleurs donne `RevisionConflict`.
- **Casse** : OneDrive trouve un nom quelle que soit sa casse, Kazo OS non.
  Un élément dont le nom diffère de celui demandé est « introuvable » ; créer
  `Rapport.TXT` à côté de `rapport.txt` donne `AlreadyExists`.
- **Noms refusés** sans requête : `" * : < > ? / \ |`, espace au début ou à la
  fin, point final. Les autres refus de Graph (`CON`, `.lock`…) donnent
  `InvalidName`.
- **OneNote** : un bloc-notes (`package`) n'est ni fichier ni dossier ; il n'est
  pas listé.
- **Après 24 heures** : le jeton de rafraîchissement d'une app « monopage »
  expire. Le compte passe `NeedsReconnect`, le lecteur se déconnecte et répond
  `Prompt` ; « Reconnecter » ouvre la fenêtre de Microsoft
  (`CloudAccountService.ReconnectAsync`), puis le lecteur revient sans autre
  geste.
- **Racine choisie** : `Travail/2026` (séparateurs `/` ou `\`, ni `.` ni `..`).
  Elle n'est pas vérifiée au montage ; un dossier absent donne `NotFound` aux
  opérations.
- **Suppression** : OneDrive garde l'élément dans sa corbeille. Kazo OS
  demande toujours la confirmation de l'ADR 0181.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/OneDrive/OneDriveContractTests.cs` :
  la suite de contrat commune (`CloudHostFolderContractTests`, 16 tests) sur
  `FakeGraphServer`.
- `OneDriveFolderTests.cs` : pages, `eTag`, téléchargement sans jeton, dossier
  réservé et racine avec noms échappés, casse, noms refusés, écriture
  conditionnelle, session d'envoi en morceaux sans jeton, fichier de plus de
  128 Mio, suppression, déplacement, quota, refus de Graph.
- `OneDriveDriveSourceTests.cs` : disponibilité selon l'identifiant d'app,
  création sans fenêtre, réouverture sans réseau, montage par
  `NetworkDriveService` (lecture, écriture, quota, `/proc/mounts`), lecteur
  déconnecté après 24 heures puis reconnecté.
- `FakeGraphServer` : un faux Graph en mémoire. Aucun test n'appelle Microsoft.

## Reste à faire

- Essai réel par Benoit, avec l'inscription Microsoft Entra (« Single-page
  application », adresses de retour de [`cloud-accounts.md`](cloud-accounts.md)) :
  connexion, les deux étendues, envoi d'un gros fichier, reconnexion après
  24 heures. Non fait : l'inscription n'existe pas encore.
- L'interface (chantier 5).

## Tâches

- [x] Source `onedrive` sur la base commune, deux étendues, racine choisie.
- [x] Dossier Graph : liste, lecture par `downloadUrl`, création, écriture
  simple et par session, écriture conditionnelle, suppression, déplacement,
  quota.
- [x] Erreurs de Graph vers les erreurs de l'OS, `FileTooLarge`.
- [x] Inscription dans l'hôte Ecosystem.
- [x] Suite de contrat, tests du dossier et de la source, faux Graph.
- [ ] Essai réel avec un compte Microsoft.
