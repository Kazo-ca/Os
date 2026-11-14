> [English version](../../../en/wasm/dev/s3-drives.md)

# Lecteurs S3 et règle CORS du compartiment

Chantier `cloud-drives/6-s3` de la série
[`cloud-drives/base`](plans/cloud-drives-plan.md), selon
l'[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), sections 3, 4 et 6.
Le connecteur s'appuie sur les [sources de lecteurs](network-drive-sources.md)
(chantier 1) et sur les [comptes cloud](cloud-accounts.md) (chantier 2).

## But

Un compartiment d'un service compatible S3 (Cloudflare R2, Backblaze B2, MinIO,
AWS S3), ou un préfixe de ce compartiment, se monte comme un lecteur réseau
sous `/mnt/<compartiment>`. Aucune app n'est à enregistrer et aucun serveur de
Kazo OS n'intervient : l'utilisateur donne l'adresse du service, ses clés et le
compartiment, les requêtes sont signées dans l'OS (SigV4) et partent du
navigateur vers le service. La seule chose à faire chez le fournisseur est une
**règle CORS** sur le compartiment (voir plus bas).

## Architecture

Tout est dans `Platform/Kazo.Os.Platform.Core/CloudDrives/S3/`, sans
dépendance au navigateur : `HttpClient` devient `fetch` sous WebAssembly.

| Pièce | Rôle |
| --- | --- |
| `S3SignatureV4` | Signature AWS Signature Version 4 (en-tête `Authorization`), encodage des chemins et des requêtes. SHA-256 et HMAC-SHA-256 en .NET, identiques dans le navigateur et dans les tests. |
| `S3Connection` | Adresse, région, adressage (chemin ou hôte virtuel), clé d'accès et clé secrète. Vérifie ce que l'utilisateur a saisi ; ne s'imprime jamais avec le secret. |
| `S3Client` | `ListObjectsV2`, `HEAD`, `GET`, `PUT` (avec `If-Match` ou `If-None-Match: *`), `DELETE`, `CopyObject`. Les réponses XML sont lues sans bibliothèque XML, pour ne pas alourdir le téléchargement. Toute panne devient une `HostFolderException`. |
| `S3Folder` | `IHostFolder` et `IConditionalHostFolder` d'un compartiment ou d'un préfixe. |
| `S3DriveSource` | La source `s3` (`INetworkDriveSource`), enregistrée dans l'hôte Ecosystem. |

### Dossiers simulés

S3 ne connaît que des clés. Un dossier est un préfixe commun :
`2026/plage.jpg` fait apparaître le dossier `2026`.

- Créer un dossier écrit un objet vide `2026/` (le marqueur des consoles S3).
- Supprimer le dernier fichier d'un dossier écrit le marqueur du dossier parent,
  pour qu'il ne disparaisse pas.
- Supprimer un dossier supprime chaque clé de son préfixe, les plus profondes
  d'abord et le marqueur en dernier ; refusé (`DirectoryNotEmpty`) sans
  `recursive` s'il contient quelque chose.
- Renommer ou déplacer un fichier : `CopyObject` sur le service puis `DELETE`.
  Un dossier se déplace fichier par fichier (l'OS le fait déjà quand
  `TryMoveAsync` répond non).
- Une clé `a` à côté de clés `a/…` : le dossier l'emporte, Kazo OS ne peut pas
  montrer les deux.

### Révision et écriture conditionnelle

La révision d'un fichier est son `ETag`. Une écriture qui attend une révision
envoie `If-Match: <ETag>` ; le refus du service (`412`, ou `409
ConditionalRequestConflict`) devient le conflit de révision de l'OS. Créer un
fichier envoie `If-None-Match: *`. Un service qui ignore ces en-têtes écrit
quand même : le contrôle fait d'abord dans l'OS reste.

Pas de quota : S3 n'en a pas, `df` affiche `-`.

### Où vont les clés

Le service et les clés forment un **compte cloud** de fournisseur `s3`
(`CloudAccountService`), comme un compte Dropbox ou OneDrive :

- la clé secrète est scellée avec l'adresse, la région et la clé d'accès, sur
  l'appareil, sous une clé du trousseau
  ([ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md)) ; la ligne du compte
  porte `"credential": "keys"` ;
- le compte s'affiche « `<clé d'accès> — <hôte>` » ; la même clé d'accès sur le
  même service met à jour le compte au lieu d'en ajouter un ;
- la liste des lecteurs ne garde que la référence du compte et
  `compartiment/préfixe` comme racine ;
- retirer le compte démonte ses lecteurs et efface sa clé du trousseau ;
- le compte invité, sans trousseau, ne peut pas monter de lecteur S3 ;
- `GetAccessTokenAsync` et `ReconnectAsync` refusent un compte à clés : on le
  reconnecte en saisissant de nouveau ses clés ;
- S3 n'apparaît pas dans `CloudAccountService.GetProvidersAsync` (aucun
  identifiant d'app) : l'interface demande
  `NetworkDriveService.IsAvailableAsync("s3")`, toujours vrai.

## Usage

```csharp
NetworkDrive? drive = await drives.MountAsync("s3", null, new NetworkDriveRequest
{
    Options = new Dictionary<string, string>
    {
        ["endpoint"] = "https://<id>.r2.cloudflarestorage.com",
        ["region"] = "auto",
        ["bucket"] = "photos",
        ["prefix"] = "famille/2026",   // facultatif
        ["addressing"] = "path"        // path | virtual ; absent : virtual pour *.amazonaws.com, path sinon
    },
    Secrets = new Dictionary<string, string>
    {
        ["access-key-id"] = "…",
        ["secret-access-key"] = "…"
    }
});

// Un autre compartiment avec les clés d'un compte s3 existant :
await drives.MountAsync("s3", null, new NetworkDriveRequest(accountId)
{
    Options = new Dictionary<string, string> { ["bucket"] = "archives" }
});
```

- `endpoint` : `https://hôte[:port]`, sans chemin ; `http` seulement pour
  `localhost` (un MinIO sur l'ordinateur de l'utilisateur), car une page
  `https` ne peut pas appeler `http`.
- `region` vide : `us-east-1`. Cloudflare R2 : `auto`.
- Point de montage par défaut : `/mnt/<compartiment>`, puis `-2`, `-3`…
- Avant de monter, la source liste la première clé du compartiment. Un refus
  devient `NetworkDriveError.InvalidRequest` avec un message qui dit quoi
  corriger : clés refusées (ou mauvaise région), compartiment absent, droit de
  liste manquant, service injoignable (réseau, adresse ou **règle CORS**).

### Connexion et réseau

- À la connexion de l'utilisateur, le lecteur est rouvert sans aucune requête :
  la connexion n'attend jamais le réseau.
- Une requête qui ne passe pas (hors ligne, service en panne, `5xx`) déconnecte
  le lecteur (`ProviderUnavailable`). La source réessaie toutes les 30 secondes
  et quand la page redevient visible ; le lecteur revient seul dès que le
  service répond.
- Des clés que le service refuse désormais (`InvalidAccessKeyId`,
  `SignatureDoesNotMatch`) déconnectent aussi le lecteur, qui attend de
  nouvelles clés : remonter le même compartiment avec les nouvelles clés met à
  jour le compte et reconnecte le lecteur.
- Le cache HTTP du navigateur est contourné (`cache: no-store`) : le lecteur
  voit toujours l'état du service.
- Un objet de plus de 128 Mio apparaît dans la liste ; le lire échoue avec
  `FileTooLarge` ([ADR 0245](adr/0245-raise-the-maximum-file-size.md)).

## Règle CORS à poser sur le compartiment

Le navigateur n'envoie une requête de Kazo OS vers le service que si le
compartiment l'autorise. Sans règle, le montage échoue avec « cannot be
reached … CORS rule ». La règle doit :

- autoriser l'origine de Kazo OS : `https://os.kazo.ca` (ajouter
  `http://localhost:5000` ou le port de développement si besoin) ;
- autoriser les méthodes `GET`, `HEAD`, `PUT`, `DELETE` ;
- autoriser les en-têtes `authorization`, `content-type`,
  `x-amz-content-sha256`, `x-amz-date`, `x-amz-copy-source`, `if-match`,
  `if-none-match` (ou `*` quand le service l'accepte) ;
- **exposer l'en-tête `ETag`** : sans lui, Kazo OS ne voit pas la révision du
  service et ne peut pas écrire sous condition.

Conseil : créer pour Kazo OS une clé limitée à ce compartiment, en lecture et
écriture, plutôt qu'une clé de tout le compte.

### Cloudflare R2

Tableau de bord → R2 → le compartiment → **Settings** → **CORS Policy** →
**Add CORS policy**, en JSON :

```json
[
  {
    "AllowedOrigins": ["https://os.kazo.ca"],
    "AllowedMethods": ["GET", "HEAD", "PUT", "DELETE"],
    "AllowedHeaders": ["authorization", "content-type", "x-amz-content-sha256", "x-amz-date", "x-amz-copy-source", "if-match", "if-none-match"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

Dans Kazo OS : adresse `https://<id du compte>.r2.cloudflarestorage.com`, région
`auto`, adressage `path`. Les clés viennent de **R2 → Manage R2 API Tokens**
(« Object Read & Write », limitée au compartiment).

### Backblaze B2

L'interface web de B2 ne propose que des règles toutes faites ; la règle
complète se pose avec l'outil `b2` :

```sh
b2 bucket update --cors-rules '[
  {
    "corsRuleName": "kazoos",
    "allowedOrigins": ["https://os.kazo.ca"],
    "allowedOperations": ["s3_get", "s3_head", "s3_put", "s3_delete"],
    "allowedHeaders": ["authorization", "content-type", "x-amz-content-sha256", "x-amz-date", "x-amz-copy-source", "if-match", "if-none-match"],
    "exposeHeaders": ["ETag"],
    "maxAgeSeconds": 3600
  }
]' <compartiment> allPrivate
```

Dans Kazo OS : adresse `https://s3.<région>.backblazeb2.com` (par exemple
`s3.us-west-004.backblazeb2.com`), région `us-west-004`, adressage `path`.
Clé d'application limitée au compartiment (**Application Keys**).

### MinIO

MinIO répond aux requêtes CORS de toutes les origines par défaut. Pour limiter
à Kazo OS, démarrer le serveur avec :

```sh
MINIO_API_CORS_ALLOW_ORIGIN=https://os.kazo.ca minio server /data
```

Dans Kazo OS : adresse du serveur (`http://localhost:9000` est accepté pour un
MinIO sur l'ordinateur), région `us-east-1` sauf réglage contraire, adressage
`path`. Si la révision n'apparaît pas, vérifier que l'en-tête `ETag` est
exposé par la version utilisée.

### AWS S3

Console → S3 → le compartiment → **Permissions** → **Cross-origin resource
sharing (CORS)** → **Edit**, la même règle JSON que pour R2. Ou en ligne de
commande :

```sh
aws s3api put-bucket-cors --bucket <compartiment> --cors-configuration '{
  "CORSRules": [{
    "AllowedOrigins": ["https://os.kazo.ca"],
    "AllowedMethods": ["GET", "HEAD", "PUT", "DELETE"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }]
}'
```

Dans Kazo OS : adresse `https://s3.<région>.amazonaws.com` (par exemple
`s3.eu-west-3.amazonaws.com`), la région du compartiment, adressage laissé vide
(hôte virtuel). Un compartiment dont le nom contient des points demande
l'adressage `path` : son certificat ne couvre pas `a.b.s3.…`.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/S3/S3SignatureV4Tests.cs` :
  les quatre exemples de signature de la documentation publique d'AWS (GET avec
  `Range`, `?lifecycle`, liste avec `max-keys` et `prefix`, PUT avec corps),
  encodage, adresses par chemin et par hôte virtuel, adresses refusées, secret
  jamais imprimé.
- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/S3/S3DriveSourceTests.cs`,
  contre `FakeS3Server` (service S3 en mémoire qui **vérifie la signature de
  chaque requête**) : montage avec compte `s3` et secret absent des deux
  listes, dossiers par préfixes et marqueurs, renommage par copie, `ETag` et
  `If-Match`, refus du service en conflit de révision, préfixe et second
  compartiment avec le même compte, hôte virtuel, montages refusés avec leur
  raison, valeurs invalides refusées sans requête, invité refusé, perte et
  retour du réseau, connexion sans requête, clés refusées puis saisies de
  nouveau, clés illisibles, retrait du compte, service sans écriture
  conditionnelle, listes en plusieurs pages, objet de plus de 128 Mio.
- Non fait : un essai contre un vrai MinIO local (le téléchargement de MinIO
  n'a pas été autorisé dans le conteneur de développement). Les essais réels
  sont regroupés au chantier 7.

## Décisions

- **Compte cloud `s3` plutôt qu'un stockage de clés à part** : la page des
  comptes (chantier 5) liste S3 avec les autres, et « Retirer » démonte ses
  lecteurs.
- **Un compte par clé d'accès et par service, un lecteur par compartiment** :
  une même clé sert plusieurs compartiments sans être saisie de nouveau.
- **Pas de bibliothèque XML** : les trois réponses XML utiles se lisent en
  quelques lignes ; `System.Private.Xml` pèserait sur le téléchargement de
  l'OS.
- **La connexion n'atteint pas le service** ; après une panne réseau, le lecteur
  interroge le service avant de se dire reconnecté.
- **`HostFolderErrorKind.FileTooLarge` (valeur 10)**, partagé avec le
  connecteur OneDrive (chantier 4), devient `FileSystemErrorCode.FileTooLarge`.

## Tâches

- [x] Signature SigV4 vérifiée sur les exemples publiés.
- [x] Client S3 : liste, `HEAD`, `GET`, `PUT` conditionnel, `DELETE`, copie.
- [x] Dossiers simulés par les préfixes, marqueurs.
- [x] Révision `ETag` et `If-Match`.
- [x] Clés scellées dans un compte cloud `s3`.
- [x] Source `s3`, enregistrée dans l'hôte Ecosystem.
- [x] Panne réseau, retour seul, clés refusées.
- [x] Guide de la règle CORS (R2, B2, MinIO, AWS).
- [x] Tests contre un faux service S3 qui vérifie les signatures.
- [ ] Essai contre un vrai service (chantier 7).
