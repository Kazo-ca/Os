> [English version](../../../en/wasm/framework/drive-providers.md)

# Fournisseurs de lecteurs réseau

## Objet

Une **fonctionnalité** du Store (genre `Feature`,
[ADR 0260](../dev/adr/0260-feature-app-type.md)) peut fournir un **type de
lecteur réseau** de plus, comme Dropbox, OneDrive et S3 livrés avec l'OS :
pCloud, un serveur WebDAV d'entreprise, un service maison.
L'[ADR 0282](../dev/adr/0282-third-party-drive-providers.md) fixe le modèle :
l'OS garde tout ce qui est pareil pour chaque type (le montage sous `/mnt`,
ses modes, la liste des lecteurs, le dialogue de connexion, Paramètres,
`mount -t`, la connexion au compte et le scellement des jetons et secrets) ;
le **fournisseur** ne fait que parler à son service.

Ce document décrit le **contrat public** (chantier 2 de la série
`drive-providers`) : les types du SDK `Kazo.Os.AppSdk.Drives` et la
déclaration `FeatureDefinition.DriveProviders`. Le branchement dans l'OS
(adaptateur vers le système de fichiers, dialogue de connexion, Paramètres)
vient au chantier 4 ; d'ici là, un fournisseur déclaré est validé au build et
au paquet, mais l'OS ne l'ouvre pas encore.

## Écrire un fournisseur

1. Une classe dérivée de **`KazoDriveProvider`**, concrète, dans l'assemblage
   de la fonctionnalité, avec un constructeur public qui prend un
   `IFeatureServiceContext` ou aucun paramètre (la même règle que les
   services, [`feature-services.md`](feature-services.md)). L'OS en crée une
   instance par page, au premier lecteur de ce type.
2. Sa méthode `OpenAsync(KazoDriveOpenRequest)` rend un **`KazoDriveFolder`** :
   un lecteur de l'utilisateur. Elle ne joint **jamais** le service (l'OS
   l'appelle à l'ouverture de session, qui n'attend pas le réseau) ;
   `GetStateAsync` dira plus tard si le service répond.
3. La fonctionnalité le déclare et demande la capacité `drives.provide` :

```csharp
[AppManifest]
public sealed class PCloudFeature : FeatureDefinition
{
    public override string Id => "ca.exemple.pcloud";
    public override string Name => "pCloud";
    public override string PublisherId => "ca.exemple";
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new("pcloud", "pCloud", typeof(PCloudProvider), AppDriveConnectionManifest.WithOAuth(
            new AppDriveOAuthManifest(
                "https://my.pcloud.com/oauth2/authorize",
                "https://api.pcloud.com/oauth2_token")
            {
                FullAccess = new AppDriveOAuthClientManifest("client-id-public"),
            }))
        {
            Icon = "assets/pcloud.svg",
            Hosts = ["api.pcloud.com", "*.pcloud.com"],
        },
    ];
}
```

## Le contrat (`Kazo.Os.AppSdk.Drives`)

| Type | Rôle |
| --- | --- |
| `KazoDriveProvider` | Classe abstraite : `OpenAsync(request, ct)` rend le dossier d'un lecteur. Classe et non interface, pour qu'un SDK plus récent ajoute un membre virtuel sans casser un fournisseur déjà compilé. |
| `KazoDriveFolder` | Un lecteur ouvert (`IAsyncDisposable`). **Abstraits** : `GetStateAsync`, `StatAsync`, `ListAsync`, `ReadAsync`, `WriteAsync(path, content, ifRevision)`, `CreateFolderAsync`, `DeleteAsync(path, recursive)`. **Virtuels avec défaut** : `CreateFileAsync` (`StatAsync` puis écriture vide, `Exists` si le nom est pris), `TryMoveAsync` (`false` : l'OS copie puis supprime), `GetQuotaAsync` (`null` : `df` affiche `-`), `DisposeAsync`. |
| `KazoDrivePath` | Chemin **relatif à la racine du lecteur** (jamais le chemin Kazo OS sous `/mnt`) : `Root`, `Segments`, `Name`, `Parent`, `Append`, `Parse`, `TryParse`, `FromSegments`, `IsValidName`. Chaque nom est vérifié : non vide, ni `.` ni `..`, sans `/`, `\` ni caractère de contrôle, 255 caractères au plus. Un fournisseur ne peut donc pas construire un chemin de service qui sort de la racine. Égalité ordinale, nom par nom. |
| `KazoDriveEntry` | `Name`, `Kind` (`File`, `Folder`), `Length`, `LastModifiedUtc`, et `Revision` (l'étiquette du service : ETag, `rev` Dropbox) que l'OS repasse à `WriteAsync` pour une écriture conditionnelle. |
| `KazoDriveQuota` | Taille et espace libre, pour `df` et l'Explorateur. |
| `KazoDriveState` | `Ready`, `SignInRequired`, `Unavailable`. |
| `KazoDriveException` | Dérive d'`IOException` ; `Kind` (`KazoDriveErrorKind`) : `NotFound`, `Exists`, `Unavailable`, `SignInRequired`, `RevisionConflict`, `FileTooLarge`, `QuotaExceeded`, `TypeMismatch`, `NotEmpty`, `InvalidName`, `AccessDenied`, `Other`. `Unavailable` et `SignInRequired` déconnectent le lecteur. |
| `KazoDriveLimits` | `MaxFileBytes` = 128 Mio : un fichier est lu et écrit en entier, comme pour tout lecteur réseau. |
| `KazoDriveOpenRequest` | `Name` (nom du lecteur), `Context`, `Root` (dossier du service monté comme racine, facultatif), `Options` (valeurs du formulaire qui ne sont pas des secrets). |
| `IKazoDriveProviderContext` | Ce que l'OS prête au lecteur ouvert : `Type`, `Account` (`KazoDriveAccount` : `Id`, `DisplayName`), `GetAccessTokenAsync()` (jeton d'accès **du compte de ce lecteur**, renouvelé par l'OS), `GetSecretAsync(name)` (un champ secret du formulaire), `NotifyAvailabilityChanged()` (le fournisseur signale que le service revient ou part). |

Le jeton de renouvellement et les secrets restent scellés par l'OS sur
l'appareil ([ADR 0269](../dev/adr/0269-cloud-tokens-stay-on-the-device.md)) :
le fournisseur ne reçoit que le jeton d'accès ou les secrets du lecteur qu'il
ouvre, à la demande.

## La déclaration (`FeatureDefinition.DriveProviders`)

`KazoDriveProviderDeclaration(Type, Name, Provider, Connection)` avec `Icon`
et `Hosts` :

| Membre | Règle |
| --- | --- |
| `Type` | Le nom de `mount -t` : lettres minuscules, chiffres et tirets, commence par une lettre, 32 caractères au plus. **Réservés** : les types de l'OS (`hostfs`, `dropbox`, `onedrive`, `s3`) et les types du noyau (`idbfs`, `proc`, `tmpfs`, `ext4`, `nfs`, `cifs`, `smb`, `fuse`, `none`… liste complète dans `AppDriveProviders.ReservedTypes`). Unique dans la fonctionnalité. |
| `Name` | Le nom du service affiché. |
| `Provider` | La classe du fournisseur ; le manifeste l'écrit `"Espace.Classe, Assemblage"`. |
| `Connection` | `AppDriveConnectionManifest.WithOAuth(...)` ou `WithForm(...)` (ci-dessous). |
| `Icon` | Chemin d'une ressource du paquet, facultatif. |
| `Hosts` | Les hôtes que le fournisseur joint : nom exact (`api.pcloud.com`) ou joker de sous-domaine (`*.pcloud.com`), toujours en `https`. Refusés : joker nu, nom sans point, adresse IP, majuscules. |

**OAuth** (`AppDriveOAuthManifest`) : `AuthorizationEndpoint`, `TokenEndpoint`,
`RevocationEndpoint` (facultatif), tous en `https` ; un client par portée,
`FullAccess` et/ou `AppFolder` (`AppDriveOAuthClientManifest(ClientId)` avec
`Scope`), au moins un ; `AppFolderPath`, `ExtraAuthorizationParameters` et
`Identity` (`AppDriveIdentityManifest(Address, IdPath)` avec `UsePost` et
`NamePaths` : où lire l'id et le nom du compte). Pas de secret de client :
le client est public (PKCE), comme pour Dropbox et OneDrive.

**Formulaire** (`AppDriveFormFieldManifest(Name, Label, Kind)`) : `Kind`
`Text`, `Url`, `Number` ou `Choice` (avec `Choices`), `Secret` (scellé par
l'OS, rendu par `GetSecretAsync`), `Required` (vrai par défaut), `Default`.
Noms uniques, 64 caractères au plus.

Dans le manifeste (`driveProviders`, trié par type) :

```json
"driveProviders": [{
  "type": "pcloud",
  "name": "pCloud",
  "provider": "Exemple.PCloud.PCloudProvider, Exemple.PCloud",
  "icon": "assets/pcloud.svg",
  "hosts": ["*.pcloud.com", "api.pcloud.com"],
  "connection": {
    "kind": "oauth",
    "oauth": {
      "authorizationEndpoint": "https://my.pcloud.com/oauth2/authorize",
      "tokenEndpoint": "https://api.pcloud.com/oauth2_token",
      "fullAccess": { "clientId": "client-id-public" }
    }
  }
}]
```

## Connexion menée par l'OS (chantier 5)

L'OS mène la connexion d'un type déclaré
([ADR 0282](../dev/adr/0282-third-party-drive-providers.md) §3, détail dans
[`cloud-accounts.md`](../dev/cloud-accounts.md)) :

- **OAuth** : l'OS lit la déclaration d'une fonctionnalité installée et
  activée, ouvre la fenêtre de connexion, échange le code (PKCE, sans secret
  de client), lit l'identité, scelle les jetons sur l'appareil et renouvelle
  le jeton d'accès. Le fournisseur n'appelle que
  `Context.GetAccessTokenAsync()` avant chaque requête.
- **Formulaire** : l'OS garde les valeurs non secrètes en clair avec le compte
  (elles arrivent dans `KazoDriveOpenRequest.Options`) et scelle les champs
  `Secret` ; le fournisseur les lit par `Context.GetSecretAsync(nom)`.
  `Context.Account` est `null` et `GetAccessTokenAsync` lève
  `SignInRequired`.
- **Ce qui est prêté** : le contexte d'un lecteur est lié à son compte et à son
  type ; il ne rend jamais le jeton ou les secrets d'un autre compte, rien une
  fois le compte retiré, et jamais le jeton de renouvellement.

### Adresses à inscrire chez le service

L'auteur inscrit chez son service une app **publique** (sans secret, PKCE
`S256`) avec, comme adresse de retour, la page de retour de l'OS, la même pour
tous les types :

| Où Kazo OS est servi | Adresse de retour |
| --- | --- |
| os.kazo.ca | `https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Autre site | `<adresse de base>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Développement | `http://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` et la même en `https` |

Le point de jeton (et l'adresse d'identité, la révocation) doit accepter CORS
depuis l'origine de l'OS. Un administrateur qui sert Kazo OS ailleurs inscrit
sa propre app et remplace l'identifiant dans `/etc/kazoos/cloud-drives.config`,
section au nom du type (`client-id`, `app-folder-client-id`).

## Vérifications

| Où | Code | Cas |
| --- | --- | --- |
| Validateur | `manifest.feature.only` | Une app qui n'est pas une fonctionnalité déclare `driveProviders` ou `drives.provide`. |
| Validateur | `manifest.drive-provider.capability` | Des fournisseurs sans la capacité `drives.provide`. |
| Validateur | `manifest.drive-provider.type`, `.reserved`, `.duplicate` | Type mal formé, réservé ou répété. |
| Validateur | `manifest.drive-provider.name`, `.class`, `.icon` | Nom vide, classe absente ou mal formée, icône vide. |
| Validateur | `manifest.drive-provider.host` | Hôte refusé (ci-dessus). |
| Validateur | `manifest.drive-provider.connection`, `.oauth`, `.form`, `.address` | Forme de connexion incohérente, aucun client OAuth, champs en double ou mal formés, adresse qui n'est pas `https`. |
| Générateur | `KOSAPP0033` | Une app ordinaire demande `drives.provide`. |
| Générateur | `KOSAPP0036` | La classe déclarée ne dérive pas de `KazoDriveProvider`, est abstraite, n'est pas dans l'assemblage de la fonctionnalité, ou n'a pas de constructeur public `()` ou `(IFeatureServiceContext)`. |

Le schéma `manifest.schema.v1.json` porte les mêmes règles de forme
(fixtures `feature-provides-drive-*.valid.json` et
`invalid-drive-provider-*.json`).

La capacité **`drives.provide`** est dangereuse : l'écran d'approbation la
nomme « Fournir un type de lecteur réseau ».

## Écarts avec l'esquisse de l'ADR 0282

L'ADR donnait des noms indicatifs ; voici ce qui a changé, sans toucher au
fond (aucune opération retirée, l'OS garde toujours les jetons) :

- le contexte expose une **méthode** `NotifyAvailabilityChanged()` au lieu
  d'un événement : c'est le fournisseur qui prévient l'OS ;
- **`CreateFileAsync`** virtuelle ajoutée : le système de fichiers a besoin
  de créer un fichier seulement s'il n'existe pas (`touch`, `O_EXCL`) ;
- types d'erreur ajoutés (`TypeMismatch`, `NotEmpty`, `InvalidName`,
  `AccessDenied`, `Other`) pour rendre les codes POSIX attendus ;
- `KazoDriveOpenRequest(Name, Context)` avec `Root` et `Options`, et
  `KazoDriveLimits.MaxFileBytes` ;
- les clients OAuth sont nommés par portée (`FullAccess`, `AppFolder`) comme
  pour les connecteurs livrés ;
- les types de connexion du manifeste s'appellent `AppDrive*Manifest`
  (assemblage `Kazo.Os.App.Abstractions`, qui ne voit pas le SDK) ;
- les hôtes sont validés par `AppDriveProviders.IsValidHost` ; quand le
  chantier 1 (`network.direct.access`, `DirectNetworkHosts`) rejoint la base,
  les deux règles seront unifiées.

## Liste des tâches (chantier 2)

- [x] Types du SDK `Kazo.Os.AppSdk.Drives` et leurs tests
- [x] `FeatureDefinition.DriveProviders`, `KazoDriveProviderDeclaration`
- [x] `driveProviders` dans le manifeste, sérialisation stricte, schéma, fixtures
- [x] Validateur (`manifest.feature.only`, `manifest.drive-provider.*`)
- [x] Générateur (`KOSAPP0033`, `KOSAPP0036`)
- [x] Capacité dangereuse `drives.provide`
- [x] Connexion menée par l'OS, comptes à formulaire, contexte du fournisseur (chantier 5)
- [ ] Branchement dans l'OS (chantier 4)
