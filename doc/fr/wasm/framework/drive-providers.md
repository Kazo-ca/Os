> [English version](../../../en/wasm/framework/drive-providers.md)

# Fournisseurs de lecteurs réseau

## Objet

Une **fonctionnalité** du Store (genre `Feature`,
[ADR 0260](../dev/adr/0260-feature-app-type.md)) peut fournir un **type de
lecteur réseau** de plus, comme Dropbox, OneDrive et S3 livrés avec l'OS : un
service de stockage, un serveur WebDAV d'entreprise, un service maison.
L'[ADR 0282](../dev/adr/0282-third-party-drive-providers.md) fixe le modèle :
l'OS garde tout ce qui est pareil pour chaque type (le montage sous `/mnt`,
ses modes, la liste des lecteurs, le dialogue de connexion, Paramètres,
`mount -t`, la connexion au compte et le scellement des jetons et secrets) ;
le **fournisseur** ne fait que parler à son service.
L'[ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md)
dit quels hôtes il peut joindre.

Ce guide va du modèle `dotnet new` à la publication : écrire le fournisseur,
joindre le service sans le serveur Kazo, vérifier CORS, inscrire la page de
retour OAuth, passer la suite de conformité, puis ce que l'OS fait du
fournisseur et ce que la revue du Store regarde. Les exemples utilisent un
service fictif, « Exemple », sur `api.example.com`.

## Pas à pas

1. **Créer le projet** à partir du modèle (paquet `Kazo.Os.Templates`,
   [`building-on-kazoos.md`](building-on-kazoos.md)) :

   ```bash
   dotnet new install Kazo.Os.Templates
   dotnet new kazoos-drive-provider -n Contoso.ExempleDrive \
     --appId com.contoso.exemple-drive --publisherId com.contoso \
     --driveType exemple --apiHost api.example.com
   cd Contoso.ExempleDrive && dotnet test
   ```

   Le modèle donne une solution `src/` + `tests/` :

   | Fichier | Rôle |
   | --- | --- |
   | `CloudDriveFeature.cs` | La fonctionnalité (`[AppManifest]`) : `drives.provide`, la déclaration du type, la connexion OAuth, l'hôte de l'API. Le build en écrit `manifest.json`. |
   | `CloudDriveProvider.cs` | Le fournisseur : `OpenAsync` crée le lecteur sans rien envoyer. |
   | `CloudDriveFolder.cs` | Le lecteur : chaque opération devient un appel au service. |
   | `ICloudDriveApi.cs` | Les appels au service, avec les erreurs attendues. |
   | `CloudDriveHttpApi.cs` | Ces appels sur une API JSON fictive, par `KazoDirectHttpClient`, JSON généré au build. |
   | `tests/…/InMemoryCloudDrive.cs` | Un service en mémoire (hors ligne, jeton refusé, gros fichier, révisions). |
   | `tests/…/CloudDriveProviderTests.cs` | La suite de conformité publique contre ce service, et le manifeste. |

   Les tests passent dès la création : ils ne joignent aucun vrai service.

2. **Brancher votre service.** Remplacez les adresses, les requêtes et le JSON
   de `CloudDriveHttpApi` par ceux de votre API. Gardez la traduction des
   réponses en `KazoDriveErrorKind` (tableau [plus bas](#erreurs-attendues)).
   Un service sans PKCE ou sans OAuth prend une connexion par formulaire
   (`AppDriveConnectionManifest.WithForm`), comme l'[exemple WebDAV](#lexemple-webdav).
3. **Déclarer les hôtes** que le code joint dans `Hosts`, et rien d'autre.
4. **Inscrire l'app chez le service** avec la [page de retour](#adresses-à-inscrire-chez-le-service)
   de l'OS, et mettre son identifiant public dans `FullAccess` ou `AppFolder`.
5. **Vérifier CORS** ([plus bas](#cors--ce-que-le-service-doit-accepter)).
6. **Garder la suite verte** : adaptez le service en mémoire à ce que votre
   service répond, et laissez `CloudDriveConformanceTests` passer tous ses cas.
7. **Essayer dans l'OS** : installer le paquet comme fonctionnalité pour la
   machine, approuver `drives.provide`, puis « Connecter un lecteur réseau… »
   ou `mount -t exemple`.
8. **Publier** dans le Store comme toute fonctionnalité
   (publication par un tag, [`app-repository.md`](app-repository.md)).

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
public sealed class ExempleDriveFeature : FeatureDefinition
{
    public override string Id => "com.contoso.exemple-drive";
    public override string Name => "Exemple";
    public override string PublisherId => "com.contoso";
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new("exemple", "Exemple", typeof(ExempleDriveProvider), AppDriveConnectionManifest.WithOAuth(
            new AppDriveOAuthManifest(
                "https://api.example.com/oauth2/authorize",
                "https://api.example.com/oauth2/token")
            {
                FullAccess = new AppDriveOAuthClientManifest("id-public-de-votre-app"),
            }))
        {
            Icon = "assets/exemple.svg",
            Hosts = ["api.example.com", "*.files.example.com"],
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
| `Hosts` | Les hôtes que le fournisseur joint : nom exact (`api.example.com`) ou joker de sous-domaine (`*.files.example.com`), toujours en `https`. Refusés : joker nu, nom sans point, adresse IP, majuscules. `drives.provide` suffit pour les joindre ([plus bas](#joindre-le-service)). |

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
Noms uniques, 64 caractères au plus. **`ServerAddress`** (`serverAddress` dans
le manifeste) marque le champ où l'utilisateur tape l'adresse de son serveur
([ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md) §2) :
genre `Url`, jamais `Secret`, obligatoire, un seul par fournisseur, et
seulement dans une connexion par formulaire.

Dans le manifeste (`driveProviders`, trié par type) :

```json
"driveProviders": [{
  "type": "exemple",
  "name": "Exemple",
  "provider": "Contoso.ExempleDrive.ExempleDriveProvider, Contoso.ExempleDrive",
  "icon": "assets/exemple.svg",
  "hosts": ["*.files.example.com", "api.example.com"],
  "connection": {
    "kind": "oauth",
    "oauth": {
      "authorizationEndpoint": "https://api.example.com/oauth2/authorize",
      "tokenEndpoint": "https://api.example.com/oauth2/token",
      "fullAccess": { "clientId": "id-public-de-votre-app" }
    }
  }
}]
```

## Joindre le service

Le fournisseur parle à son service **directement depuis le navigateur**, avec
**`KazoDirectHttpClient`** (`Kazo.Os.AppSdk.Net`,
[`sdk-network.md`](sdk-network.md)) : la requête part par le `fetch` de la
page, sans serveur Kazo, et marche donc sur une installation sans serveur.

```csharp
using KazoDirectHttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

string token = await context.GetAccessTokenAsync(cancellationToken); // l'OS le renouvelle
KazoDirectHttpRequest request = new(HttpMethod.Get, new Uri("https://api.example.com/v1/children?path=%2F"));
request.Headers.Add(new("Authorization", "Bearer " + token));
await using KazoDirectHttpResponse response = await client.SendAsync(request, cancellationToken);
Children? children = await response.ReadFromJsonAsync(MyJson.Default.Children, cancellationToken);
```

- **Hôtes déclarés.** `drives.provide` suffit pour joindre un hôte des
  `Hosts` des déclarations de la fonctionnalité ; **pas besoin** de
  `network.direct.access` ([ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md) §1).
  La passerelle décide dans cet ordre : un hôte de `directNetworkHosts` exige
  `network.direct.access` ; sinon un hôte des fournisseurs ou un serveur tapé
  exige `drives.provide` ; sinon la requête est refusée
  (`UnauthorizedAccessException` qui nomme l'hôte) et rien ne part.
- **Serveur tapé par l'utilisateur** (WebDAV, Nextcloud, un NAS : aucun hôte
  connu d'avance). Marquez le champ d'adresse `ServerAddress = true`. Quand un
  lecteur de ce type s'ouvre, l'OS lit la valeur de ce champ, garde son hôte
  s'il est valide (adresse `https` absolue, sans nom ni mot de passe dans
  l'adresse, ni adresse IP, ni `localhost`, un nom avec un point) et le permet
  **à cette fonctionnalité seule, tant que le lecteur est ouvert**. Deux
  lecteurs sur le même serveur le gardent permis jusqu'à la fermeture du
  second. Le dialogue de connexion l'écrit avant d'enregistrer (« Ce
  fournisseur joindra dav.example.org »). Le fournisseur n'a rien à faire de
  plus : il lit l'adresse dans `KazoDriveOpenRequest.Options`.
- **Erreurs réseau.** Une panne, un refus CORS et une erreur TLS donnent tous
  `HttpRequestException` (« Failed to fetch » pour la page) : le fournisseur
  les rend en `KazoDriveErrorKind.Unavailable`. Un `Timeout` dépassé lève
  `TaskCanceledException` sans que le jeton de l'appelant soit annulé :
  `Unavailable` aussi.
- **Ce que le navigateur interdit.** Pas de cookie, pas d'en-tête réservé
  (`Host`, `Cookie`, `Origin`, `Referer`, `Sec-*`…), et seuls les en-têtes de
  réponse que le service expose sont lisibles (`ETag` compris).

### Erreurs attendues

L'OS traduit `KazoDriveException.Kind` en erreur du système de fichiers ; la
suite de conformité vérifie les mêmes genres :

| Le service répond | `KazoDriveErrorKind` | Erreur du système de fichiers |
| --- | --- | --- |
| Rien (réseau, CORS, TLS), 408, 429, 5xx | `Unavailable` | `ProviderUnavailable`, lecteur déconnecté |
| 401, jeton refusé | `SignInRequired` | l'OS renouvelle le jeton une fois, sinon lecteur déconnecté |
| 403 | `AccessDenied` | `PermissionDenied` |
| 404 | `NotFound` | `NotFound` |
| Le nom est pris | `Exists` | `AlreadyExists` |
| La révision ne correspond plus | `RevisionConflict` | `RevisionConflict` |
| Lister un fichier, lire un dossier | `TypeMismatch` | `NotDirectory` |
| Dossier non vide sans récursion | `NotEmpty` | `DirectoryNotEmpty` |
| Nom refusé par le service | `InvalidName` | `InvalidName` |
| Plus de 128 Mio | `FileTooLarge` | `FileTooLarge` |
| Espace plein | `QuotaExceeded` | `NoSpace` |
| Autre | `Other` | `ProviderFailure` |

## CORS : ce que le service doit accepter

La requête part de la page de l'OS : le navigateur ne la laisse passer que si
le service accepte l'**origine de l'OS** (`https://os.kazo.ca`, ou l'adresse
où l'OS est servi). Pour une requête avec `Authorization`, le navigateur envoie
d'abord une requête `OPTIONS` (« preflight ») ; le service doit y répondre `2xx`
**sans** demander d'authentification, avec :

| En-tête de réponse | Valeur |
| --- | --- |
| `Access-Control-Allow-Origin` | l'origine de l'OS (ou `*`) |
| `Access-Control-Allow-Methods` | les méthodes utilisées (`GET, PUT, POST, DELETE`, et pour WebDAV `PROPFIND, MKCOL, MOVE`) |
| `Access-Control-Allow-Headers` | `Authorization` et les autres en-têtes envoyés (`Content-Type`, `Depth`, `Destination`, `Overwrite`, `If-Match`, `If-None-Match`) ; `*` ne couvre pas `Authorization` |
| `Access-Control-Expose-Headers` | `ETag`, `Content-Length` (sinon la page ne les voit pas) |

Pour vérifier, depuis un terminal :

```bash
curl -i -X OPTIONS "https://api.example.com/v1/children?path=%2F" \
  -H "Origin: https://os.kazo.ca" \
  -H "Access-Control-Request-Method: GET" \
  -H "Access-Control-Request-Headers: authorization"
```

La réponse doit être `200` ou `204` avec les en-têtes ci-dessus. Dans l'OS,
une requête refusée par CORS apparaît comme un lecteur `Unavailable`, et la
console du navigateur (F12) nomme l'en-tête qui manque.

**Un serveur WebDAV doit accepter CORS** : Nextcloud, ownCloud et la plupart
des serveurs WebDAV ne le font pas par défaut. L'administrateur du serveur
ajoute les en-têtes ci-dessus dans le serveur web devant lui (Apache, nginx),
et fait répondre `OPTIONS` sans authentification. Sans cela, le lecteur reste
déconnecté, quel que soit le fournisseur.

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

## Ce que fait l'OS d'un fournisseur (chantier 4)

Le chantier 4 branche les types déclarés dans le système de fichiers
([`network-drive-sources.md`](../dev/network-drive-sources.md#types-fournis-par-une-fonctionnalité)) :

- **Un fournisseur par type.** `AppCatalog.GetDriveProviders()` et
  `FindDriveProvider(type)` donnent la fonctionnalité qui fournit chaque type.
  L'installeur refuse une seconde fonctionnalité qui déclare le même type
  (`apps.package.drive-type-already-provided`). Un type de l'OS gagne toujours.
- **À chaud.** Un type est proposé dès que sa fonctionnalité est installée et
  activée, et disparaît quand elle est désactivée ou retirée, sans redémarrer.
  Une nouvelle version est un nouveau fournisseur.
- **Chargé au premier lecteur.** Rien ne se charge à l'installation ni à la
  connexion de l'utilisateur. Au premier lecteur du type ouvert, l'OS charge
  l'assemblage, démarre le processus de service de la fonctionnalité (compte
  `kazoos-svc`, ses permissions) et crée **une instance par page**, avec le
  constructeur `(IFeatureServiceContext)` sinon `()`. Chaque appel au
  fournisseur s'exécute dans le contexte de la fonctionnalité. Un échec n'est
  jamais gardé : le lecteur suivant réessaie.
- **La connexion n'attend jamais.** Le lecteur est monté **déconnecté** tant que
  `OpenAsync` n'a pas répondu, puis se reconnecte seul. Charger et ouvrir ont
  20 s au plus, `GetStateAsync` 10 s.
- **Un fournisseur qui échoue** (assemblage ou classe absent, constructeur qui
  lève, `OpenAsync` qui lève ou trop lent) laisse le lecteur déconnecté et écrit
  une ligne `drives: <type>: <raison>` dans `/var/log/apps/<fonctionnalité>.log`
  et dans le journal du système. Une exception inattendue d'une opération
  devient `HostFolderErrorKind.Failure` (`EIO`), jamais un plantage.
- **Erreurs.** `KazoDriveException.Kind` devient le code du système de fichiers :
  `NotFound`, `Exists` → `AlreadyExists`, `Unavailable`, `RevisionConflict`,
  `FileTooLarge`, `QuotaExceeded` → `NoSpace`, `TypeMismatch`, `NotEmpty`,
  `InvalidName`, `AccessDenied` → `NotAllowed`, `Other` → `Failure`.
  `SignInRequired` après un jeton donné : l'OS renouvelle le jeton une fois et
  refait l'opération ; sinon le lecteur est déconnecté (`NotAllowed` si le
  compte a été retiré).
- **Compte.** Un lecteur d'un type fourni appartient toujours à un compte de ce
  type et à la fonctionnalité qui l'a ajouté ; l'OS refuse un compte d'un autre
  type ou d'une autre fonctionnalité qui sert le même type, et ne prête au fournisseur que
  le contexte de ce compte (`KazoDriveState` → `Granted`, `Prompt` pour
  `SignInRequired`, sinon `Unavailable`). Un nom renvoyé par `ListAsync` qui
  pourrait sortir du dossier n'est pas affiché ; un fichier de plus de
  `KazoDriveLimits.MaxFileBytes` est refusé dans les deux sens.
- **Fonctionnalité retirée ou désactivée.** Les lecteurs de son type sont
  démontés mais gardés dans la liste de l'utilisateur, grisés dans
  l'Explorateur ; ils reviennent seuls quand la **même** fonctionnalité est
  réactivée ou réinstallée. Une autre fonctionnalité qui déclare le même type
  ne reprend pas ces lecteurs.
- **Tester un fournisseur** : `InMemoryDriveProvider` et `InMemoryDriveService`
  (`Tests/Kazo.Os.Tests.Support`) sont un fournisseur et un service factices en
  mémoire (arborescence, révisions, quota, jeton accepté, hors ligne, pannes,
  ouverture lente ou en échec).

## Tester : la suite de conformité

Le paquet **`Kazo.Os.AppSdk.Testing`** donne `KazoDriveProviderTests`
(espace `Kazo.Os.AppSdk.Testing.Drives`) : les mêmes cas que l'OS attend de
tout fournisseur. Une classe de test en dérive, ouvre un lecteur sur un
service de test (jamais un vrai), et lance chaque cas :

```csharp
public sealed class ExempleConformanceTests : KazoDriveProviderTests
{
    private readonly InMemoryCloudDrive _service = new();

    public static TheoryData<string> Cases => new(CaseNames);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_provider_conforms(string name) =>
        Assert.Equal(KazoDriveConformanceOutcome.Passed, (await RunAsync(name)).Outcome);

    protected override ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken) =>
        new ExempleDriveProvider(_service.Connect).OpenAsync(
            new KazoDriveOpenRequest("Exemple", new KazoDriveTestContext("exemple") { AccessToken = "token-1" }),
            cancellationToken);
}
```

| Cas | Ce qui est vérifié |
| --- | --- |
| `root-is-a-folder` | La racine est un dossier, l'état est `Ready`. |
| `write-then-read`, `empty-file`, `overwrite` | Écrire, relire, décrire, lister ; fichier vide ; remplacement complet. |
| `create-folder-and-list`, `create-existing` | Créer un dossier, lister seulement les enfants directs ; `Exists` sur un nom pris. |
| `missing`, `wrong-kind` | `NotFound` ; `TypeMismatch`. |
| `delete` | Fichier, dossier vide, `NotEmpty` sans récursion, suppression récursive. |
| `revision-conflict` | Une écriture conditionnelle d'un fichier changé par un autre appareil lève `RevisionConflict` (sauté si le service ne garde pas de révision). |
| `move` | Un déplacement en un appel, fichier et dossier (résultat informatif si `TryMoveAsync` répond `false`). |
| `quota` | `0 <= libre <= total` (informatif si `null`). |
| `names` | Espaces, accents, `%`, `#`, `?`, `+`, `&`, crochets, apostrophe, japonais, émoji, `.cache` reviennent tels quels, ou sont refusés par `InvalidName`. |
| `offline` | `OpenAsync` ne joint pas le service ; l'état est `Unavailable` puis `Ready` au retour du réseau. |
| `sign-in-expired` | État `SignInRequired`, opérations `SignInRequired`. |
| `file-too-large` | Un fichier de plus de 128 Mio reste listé avec sa taille, mais sa lecture lève `FileTooLarge`. |

Pour les trois derniers, la classe de test redéfinit `SetServiceReachableAsync`,
`ExpireSignInAsync` et `PlaceLargeFileAsync` (sinon le cas est sauté, avec la
raison) ; `ResetAsync` remet le service en état entre deux cas.
`KazoDriveTestContext` joue le contexte de l'OS : `AccessToken` (sans jeton,
`GetAccessTokenAsync` lève `SignInRequired`), `Secrets`, et les compteurs
`TokenRequests` et `AvailabilityNotifications`. Chaque cas travaille dans un
dossier `kazo-conformance-…` qu'il supprime ensuite.

## L'exemple WebDAV

`Apps/Samples/Kazo.Os.Samples.WebDavDrive` est un fournisseur complet à
recopier, avec une **connexion par formulaire** : adresse du serveur
(`ServerAddress`), nom d'utilisateur, mot de passe d'application (`Secret`,
scellé par l'OS). Il envoie du WebDAV (RFC 4918) par `KazoDirectHttpClient`,
signe chaque requête en HTTP Basic avec le mot de passe demandé à l'OS à
chaque fois, et prend l'`ETag` comme révision (`If-Match` pour une écriture
conditionnelle, `If-None-Match: *` pour une création). Il ne déclare aucun hôte :
le serveur tapé par l'utilisateur suffit. Ses tests
(`Tests/Kazo.Os.Samples.WebDavDrive.Tests`) passent toute la suite contre un
serveur WebDAV local en mémoire (`LocalWebDavServer`). Sur l'OS publié et rogné,
`build/publish-trimmed.sh` installe l'exemple comme Feature, le connecte par son
formulaire à un serveur WebDAV de test que Playwright répond (`RoutedWebDavServer`,
en-têtes CORS compris), puis lit et écrit un fichier depuis le terminal.

## Limites et revue du Store

- **128 Mio par fichier** (`KazoDriveLimits.MaxFileBytes`) : un fichier est lu
  et écrit en entier. Un fichier plus gros reste listé, mais n'est ni lu ni
  écrit.
- **Pas de bac à sable.** Le code de la fonctionnalité tourne dans la page ;
  son propre JavaScript peut appeler `fetch`. La liste d'hôtes borne ce qui
  passe par le SDK, pas un fournisseur malveillant.
- **La revue du Store est la vraie barrière** : seul le dépôt du Store signe
  les paquets ; il vérifie la déclaration (type non réservé, hôtes en `https`,
  pas de joker nu) et la revue lit le code : les hôtes joints doivent être
  ceux déclarés, aucun jeton ni secret ne doit sortir vers un autre hôte, pas
  de JavaScript qui contourne le SDK.
- **CORS** : sans l'accord du service, rien ne passe ([plus haut](#cors--ce-que-le-service-doit-accepter)).
- **Hameçonnage** : l'OS montre toujours le nom de la fonctionnalité à côté du
  nom du service dans le dialogue de connexion et Paramètres.

## Dans l'interface et dans `mount` (chantier 6)

Un type déclaré apparaît partout comme les types de l'OS, sans code à écrire
([ADR 0282](../dev/adr/0282-third-party-drive-providers.md) §5,
[`cloud-drives-ui.md`](../dev/cloud-drives-ui.md)) :

- **Description.** `KazoDriveInfo.DescribeNetworkDriveTypesAsync` rend un
  `KazoNetworkDriveTypeInfo` par type : `Name` de la déclaration, `Icon`
  (image du paquet, `IconUrl`), forme de connexion, champs du formulaire et
  `ProviderFeatureId` / `ProviderFeatureName`. L'Explorateur et Paramètres
  écrivent « Fourni par <nom de la fonctionnalité> (<id>) ».
- **Formulaire.** Les champs s'affichent dans l'ordre déclaré, avec leur
  libellé, leur exemple et leurs choix ; `Secret` est masqué et
  `Required = false` ajoute « (facultatif) ». L'OS vérifie les valeurs contre
  la déclaration (type, choix, longueur, pas de secret dans les valeurs en
  clair) avant de créer le compte, nommé d'après l'hôte de la première
  adresse et la valeur suivante (`dav.exemple.com (alice)`).
- **Terminal.** `mount -t <type> -o champ=valeur,…` monte un lecteur ; les
  champs secrets sont demandés sans écho et refusés dans `-o`. Une erreur
  d'usage écrit la syntaxe du type.
- **Fournisseur absent.** Un lecteur dont la fonctionnalité est retirée reste
  grisé avec **Installer le fournisseur**, qui ouvre le Store sur son id ;
  désactivée, il porte « Fournisseur désactivé ».

## Vérifications

| Où | Code | Cas |
| --- | --- | --- |
| Validateur | `manifest.feature.only` | Une app qui n'est pas une fonctionnalité déclare `driveProviders` ou `drives.provide`. |
| Validateur | `manifest.drive-provider.capability` | Des fournisseurs sans la capacité `drives.provide`. |
| Validateur | `manifest.drive-provider.type`, `.reserved`, `.duplicate` | Type mal formé, réservé ou répété. |
| Validateur | `manifest.drive-provider.name`, `.class`, `.icon` | Nom vide, classe absente ou mal formée, icône vide. |
| Validateur | `manifest.drive-provider.host` | Hôte refusé (ci-dessus). |
| Validateur | `manifest.drive-provider.connection`, `.oauth`, `.form`, `.address` | Forme de connexion incohérente, aucun client OAuth, champs en double ou mal formés, adresse qui n'est pas `https`. |
| Validateur | `manifest.drive-provider.server-address` | Champ `serverAddress` qui n'est pas une `Url` obligatoire et non secrète, ou plus d'un par fournisseur. |
| Générateur | `KOSAPP0033` | Une app ordinaire demande `drives.provide`. |
| Générateur | `KOSAPP0036` | La classe déclarée ne dérive pas de `KazoDriveProvider`, est abstraite, n'est pas dans l'assemblage de la fonctionnalité, ou n'a pas de constructeur public `()` ou `(IFeatureServiceContext)`. |

Le schéma `manifest.schema.v1.json` porte les mêmes règles de forme
(fixtures `feature-provides-drive-*.valid.json` et
`invalid-drive-provider-*.json`).

La capacité **`drives.provide`** est dangereuse : l'écran d'approbation la
nomme « Fournir un type de lecteur réseau », avec les hôtes des déclarations
et, s'il y en a, le libellé du champ d'adresse du serveur (« Le serveur que
chaque utilisateur tape dans : Adresse du serveur »). Une mise à jour qui
ajoute un hôte la remet en attente
([approbation](../dev/dangerous-capability-approval.md)).

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
- les hôtes sont validés par `AppDriveProviders.IsValidHost`, qui applique
  la règle de `DirectNetworkHosts` (chantier 1, `network.direct.access`) sous
  sa forme canonique : une seule règle pour les deux.

## Liste des tâches

Chantier 2 (contrat) :

- [x] Types du SDK `Kazo.Os.AppSdk.Drives` et leurs tests
- [x] `FeatureDefinition.DriveProviders`, `KazoDriveProviderDeclaration`
- [x] `driveProviders` dans le manifeste, sérialisation stricte, schéma, fixtures
- [x] Validateur (`manifest.feature.only`, `manifest.drive-provider.*`)
- [x] Générateur (`KOSAPP0033`, `KOSAPP0036`)
- [x] Capacité dangereuse `drives.provide`
- [x] Connexion menée par l'OS, comptes à formulaire, contexte du fournisseur (chantier 5)
- [x] Branchement dans l'OS (chantier 4, voir ci-dessus)
- [x] Interface et `mount` pour tout type déclaré (chantier 6)

Chantier 7 (kit du développeur) :

- [x] Suite de conformité publique `KazoDriveProviderTests` (`Kazo.Os.AppSdk.Testing`)
- [x] Modèle `dotnet new kazoos-drive-provider`
- [x] Exemple WebDAV avec connexion par formulaire
- [x] ADR 0284 : `drives.provide` suffit pour les hôtes déclarés, adresse de serveur tapée
- [x] Ce guide

Chantier 9 (fin de série) :

- [x] SDK 3.5.0 : API publiée, journal des changements
- [x] Fixture de compatibilité `Contoso.Sdk35DriveProvider` : un fournisseur compilé contre 3.5.0 passe la suite courante
- [x] Parcours complet sur l'OS rogné : connexion par formulaire, lecture et écriture contre un serveur WebDAV de test
