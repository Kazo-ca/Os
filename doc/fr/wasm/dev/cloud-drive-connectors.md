> [English version](../../../en/wasm/dev/cloud-drive-connectors.md)

# Connecteurs cloud : base commune, suite de contrat et Dropbox

Chantier `cloud-drives/3-dropbox` de la série
[`cloud-drives/base`](plans/cloud-drives-plan.md), selon
l'[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), sections 4, 6, 8, 9
et 10. Il s'appuie sur les [sources de lecteurs](network-drive-sources.md)
(chantier 1) et les [comptes cloud](cloud-accounts.md) (chantier 2). Ce
document décrit le code livré ; l'ADR reste la référence des décisions.

## But

- Monter le stockage Dropbox de l'utilisateur comme lecteur réseau sous
  `/mnt/Dropbox`, tout le stockage ou le dossier `Apps/Kazo OS` selon
  l'étendue choisie à la connexion du compte.
- Donner aux connecteurs suivants (OneDrive, S3) une base commune et une
  **suite de tests de contrat** qu'ils doivent tous passer.

## Architecture

| Pièce | Projet | Rôle |
| --- | --- | --- |
| `ICloudAccountTokens` | Platform.Core (`CloudDrives/`) | Ce qu'un connecteur voit des comptes : le compte, un jeton d'accès, son renouvellement après un refus, la fenêtre de reconnexion. `CloudAccountService` l'implémente. |
| `CloudDriveConnection` | Platform.Core (`CloudDrives/`) | Lien d'un lecteur à son compte : ajoute le jeton, le renouvelle une fois après un 401, traduit réseau, délai, 502/503/504 et 429 ; état de la permission. |
| `CloudDriveSource` | Platform.Core (`CloudDrives/`) | Base des sources cloud (`INetworkDriveSource`) : lecteur d'un compte déjà connecté, aucune fenêtre, réouverture sans réseau. |
| `DropboxDriveSource`, `DropboxFolder` | Platform.Core (`CloudDrives/Dropbox/`) | Le connecteur Dropbox (API v2, `HttpClient`). |
| `DropboxApi`, `DropboxPaths` | Platform.Core (`CloudDrives/Dropbox/`) | Requêtes, métadonnées, erreurs et chemins Dropbox (internes). |
| `CloudHostFolderContractTests` | Tests Platform.Core (`CloudDrives/`) | Suite de contrat commune, abstraite. |
| `FakeCloudAccountTokens`, `FakeDropboxServer` | Tests Platform.Core (`CloudDrives/`) | Comptes factices ; faux Dropbox derrière un `HttpMessageHandler`. |

La source Dropbox est inscrite comme `INetworkDriveSource` dans
`EcosystemServiceCollectionExtensions` ; `NetworkDriveService` la reçoit avec
les autres sources.

## Base commune

### Comptes et jetons

`ICloudAccountTokens` donne `FindAccountAsync`, `GetAccessTokenAsync`,
`RenewAccessTokenAsync` et `ReconnectAsync`, plus l'événement `Changed`. Un
connecteur ne voit jamais de jeton de rafraîchissement.
`CloudAccountService.RenewAccessTokenAsync(compte, jetonRefusé)` renouvelle un
jeton que le fournisseur a refusé avant l'expiration connue de l'OS ; si une
autre requête l'a déjà renouvelé, le jeton récent est rendu sans nouvel appel.

### Requêtes

`CloudDriveConnection.SendAsync` construit la requête par une fonction (une
requête HTTP ne s'envoie qu'une fois) et :

| Réponse | Résultat |
| --- | --- |
| 401 | Renouvellement du jeton, une seule nouvelle tentative ; un second 401 donne `Unavailable`. |
| Échec réseau, délai dépassé, 502, 503, 504 | `HostFolderErrorKind.Unavailable` : le lecteur se déconnecte (§9). |
| 429 | `HostFolderErrorKind.Failure`, avec le délai demandé dans le message. |
| Autre | Rendue au connecteur, qui lit le format d'erreur de son fournisseur. |

Un compte qui demande une reconnexion, un trousseau verrouillé ou un
renouvellement hors ligne donnent `Unavailable` ; un compte retiré donne
`NotAllowed`.

### Permission et reconnexion

`QueryPermissionAsync` n'appelle jamais le service, pour qu'une connexion de
session ne soit pas ralentie par un lecteur cloud :

| État du compte | Permission |
| --- | --- |
| Retiré | `Denied` |
| « À reconnecter » | `Prompt` |
| Pas de jeton possible maintenant (hors ligne) | `Unavailable` |
| Sinon | `Granted` |

« Reconnecter » (`RequestPermissionAsync`) ouvre la fenêtre de connexion du
fournisseur quand le compte la demande ; une fenêtre bloquée par le navigateur
donne `InvalidOperationException` (geste de l'utilisateur requis), une fenêtre
fermée laisse le lecteur tel quel.

La source lève `AvailabilityChanged` quand un compte change (reconnecté) et
quand la page revient : `IHostActivitySignal.Resumed`, que `hostFolder.js` lève
désormais aussi sur l'événement `online` du navigateur. Les lecteurs
déconnectés se reconnectent alors seuls (§9).

### Création d'un lecteur

L'interface connecte d'abord le compte (fenêtre ouverte dans le clic), puis
appelle `NetworkDriveService.MountAsync("dropbox", point, new
NetworkDriveRequest(compte, racine))`. `CreateAsync` n'ouvre aucune fenêtre et
refuse (`ArgumentException`) un compte absent, inconnu ou d'un autre
fournisseur. La référence gardée dans la liste des lecteurs est le nom du
compte, son identifiant et la racine ; jamais un jeton. Oublier un lecteur
laisse le compte connecté.

## Connecteur Dropbox

| Opération | Appel Dropbox |
| --- | --- |
| Décrire | `files/get_metadata` (la racine du compte n'a pas de métadonnées : décrite par l'OS) |
| Lister | `files/list_folder`, puis `files/list_folder/continue` tant que `has_more` |
| Lire | `files/download` |
| Créer un fichier | `files/upload`, mode `add` |
| Créer un dossier | `files/create_folder_v2` |
| Écrire | `files/upload`, mode `overwrite` |
| Écrire sous condition | `files/upload`, mode `update` avec le `rev` attendu |
| Supprimer | `files/delete_v2` |
| Déplacer, renommer | `files/move_v2` |
| Quota | `users/get_space_usage` (`allocation.allocated`, `used`) |

- **Révision** : le `rev` du fichier est `HostFolderEntry.Revision`. Une
  écriture qui attend une révision part en mode `update` avec
  `autorename=false` et `strict_conflict=true` ; le refus de Dropbox devient
  le conflit de révision de l'OS.
- **Étendue** (§8) : Dropbox fixe l'étendue par app, donc le connecteur n'a
  rien à faire ; un compte « dossier réservé » voit `Apps/Kazo OS` comme
  racine. Une **racine** choisie (`/Travail/Docs`) préfixe tous les chemins.
- **Casse** : Dropbox ne distingue pas la casse et garde celle de la
  création ; l'OS la distingue. Un chemin dont le dernier nom ne diffère que
  par la casse est « introuvable » (l'OS voit l'entrée sous son vrai nom),
  créer `a.txt` à côté de `A.txt` est « existe déjà », et un renommage qui ne
  change que la casse passe par un nom temporaire (deux `move_v2`), que
  Dropbox refuse en un seul appel.
- **Parents** : Dropbox crée les dossiers parents manquants ; le connecteur
  vérifie le parent avant une création, comme un dossier de l'ordinateur.
- **Suppression non récursive** d'un dossier non vide : refusée par le
  connecteur (`NotEmpty`), Dropbox supprimant toujours le contenu.
- **Taille** : envoi simple, 150 Mo au plus chez Dropbox ; l'OS s'arrête à
  128 Mio avant ([ADR 0245](adr/0245-raise-the-maximum-file-size.md)).
- **En-têtes** : l'argument des points d'accès de contenu va dans
  `Dropbox-API-Arg`, en ASCII (les autres caractères sont écrits `\uXXXX`) ;
  le corps JSON des appels RPC a le type `application/json` sans `charset`.
  Le JSON est écrit avec `Utf8JsonWriter` et lu avec `JsonDocument`, que
  l'élagage garde.
- **Erreurs** : les 409 de Dropbox portent un `error_summary`
  (`path/not_found/…`) traduit en `NotFound`, `TypeMismatch`, `AlreadyExists`
  (ou `RevisionConflict` pour une écriture sous condition), `NoSpace`,
  `InvalidName`, `NotAllowed` ; 403 donne `NotAllowed`.

## Suite de contrat

`CloudHostFolderContractTests` est une classe xUnit abstraite. Un connecteur la
dérive et fournit un `ICloudDriveContractService` : son faux service (chemins
`/a/b`), le quota attendu, le réseau coupé, le refus du jeton courant (401) et
des pannes HTTP (`FailNext(nombre, statut)`). Elle vérifie : liste, révisions,
entrées absentes et mauvais types, lecture entière (noms hors ASCII), création
(nom pris, parent absent), écriture, écriture sous condition et conflit,
suppression (non récursive refusée), déplacement (nom pris), quota, hors ligne
puis retour du réseau, 503 et 429, renouvellement après 401, renouvellement
impossible hors ligne, compte à reconnecter, fenêtre bloquée, compte retiré.
Exemple : `CloudDrives/Dropbox/DropboxContractTests.cs`.

## Tests

- `DropboxContractTests` : la suite de contrat contre `FakeDropboxServer`.
- `DropboxDriveTests` : casse, renommage de casse, formats de requête, listes
  paginées, racine, source (comptes refusés, aucune fenêtre, réouverture),
  étendue « dossier réservé », lecteur monté par `NetworkDriveService`
  (`/proc/mounts`, quota, écriture périmée refusée), déconnexion hors ligne et
  reconnexion seule.
- `CloudAccountServiceTests` : renouvellement d'un jeton refusé avant son
  expiration.

Aucun test n'appelle Dropbox. Un essai réel avec les App keys de Benoit est
prévu avant la fusion de la série dans `main` : il confirmera l'échange PKCE,
la durée du jeton d'accès et les en-têtes CORS de Dropbox depuis os.kazo.ca.

## Tâches

- [x] Base commune : `ICloudAccountTokens`, `CloudDriveConnection`, `CloudDriveSource`.
- [x] Renouvellement d'un jeton refusé (`RenewAccessTokenAsync`).
- [x] Connecteur Dropbox : opérations, révision `rev`, mode `update`, quota.
- [x] Casse de Dropbox, parents, suppression non récursive.
- [x] Reconnexion seule au retour du réseau (événement `online`).
- [x] Suite de contrat commune et faux Dropbox.
- [x] Inscription de la source dans l'hôte.
- [ ] Essai réel par Benoit avec ses deux App keys (série, avant `main`).
