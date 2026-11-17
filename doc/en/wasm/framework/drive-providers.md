> [Version française](../../../fr/wasm/framework/drive-providers.md)

# Network drive providers

## Purpose

A Store **feature** (kind `Feature`,
[ADR 0260](../dev/adr/0260-feature-app-type.md)) can provide one more **type
of network drive**, like Dropbox, OneDrive and S3 shipped with the OS: a
storage service, a company WebDAV server, an in-house service.
[ADR 0282](../dev/adr/0282-third-party-drive-providers.md) sets the model: the
OS keeps everything that is the same for every type (the mount under `/mnt`,
its modes, the list of drives, the connect dialog, Settings, `mount -t`, the
sign-in to the account and the sealing of tokens and secrets); the
**provider** only talks to its service.
[ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md)
says which hosts it may reach.

This guide goes from the `dotnet new` template to publishing: write the
provider, reach the service without the Kazo server, check CORS, register the
OAuth return page, pass the conformance suite, then what the OS does with the
provider and what the Store review looks at. The examples use a fictional
service, "Exemple", on `api.example.com`.

## Step by step

1. **Create the project** from the template (package `Kazo.Os.Templates`,
   [`building-on-kazoos.md`](building-on-kazoos.md)):

   ```bash
   dotnet new install Kazo.Os.Templates
   dotnet new kazoos-drive-provider -n Contoso.ExempleDrive \
     --appId com.contoso.exemple-drive --publisherId com.contoso \
     --driveType exemple --apiHost api.example.com
   cd Contoso.ExempleDrive && dotnet test
   ```

   The template gives a `src/` + `tests/` solution:

   | File | Role |
   | --- | --- |
   | `CloudDriveFeature.cs` | The feature (`[AppManifest]`): `drives.provide`, the declaration of the type, the OAuth connection, the API host. The build writes `manifest.json` from it. |
   | `CloudDriveProvider.cs` | The provider: `OpenAsync` creates the drive without sending anything. |
   | `CloudDriveFolder.cs` | The drive: each operation becomes one call to the service. |
   | `ICloudDriveApi.cs` | The calls to the service, with the errors expected. |
   | `CloudDriveHttpApi.cs` | Those calls over a fictional JSON API, through `KazoDirectHttpClient`, JSON generated at build. |
   | `tests/…/InMemoryCloudDrive.cs` | An in-memory service (offline, refused token, large file, revisions). |
   | `tests/…/CloudDriveProviderTests.cs` | The public conformance suite against that service, and the manifest. |

   The tests pass from the start: they reach no real service.

2. **Plug in your service.** Replace the addresses, requests and JSON of
   `CloudDriveHttpApi` with your API's. Keep the mapping of the answers to
   `KazoDriveErrorKind` (table [below](#expected-errors)). A service without
   PKCE or without OAuth takes a form connection
   (`AppDriveConnectionManifest.WithForm`), like the [WebDAV sample](#the-webdav-sample).
3. **Declare the hosts** the code reaches in `Hosts`, and nothing else.
4. **Register the app with the service** with the OS's
   [return page](#addresses-to-register-with-the-service), and put its public
   id in `FullAccess` or `AppFolder`.
5. **Check CORS** ([below](#cors-what-the-service-must-accept)).
6. **Keep the suite green**: adapt the in-memory service to what your service
   answers, and let `CloudDriveConformanceTests` pass all its cases.
7. **Try it in the OS**: install the package as a feature for the machine,
   approve `drives.provide`, then "Connect a network drive…" or
   `mount -t exemple`.
8. **Publish** to the Store like any feature (publishing by a tag,
   [`app-repository.md`](app-repository.md)).

## Writing a provider

1. A class derived from **`KazoDriveProvider`**, concrete, in the feature's
   assembly, with a public constructor taking an `IFeatureServiceContext` or no
   parameter (the same rule as services,
   [`feature-services.md`](feature-services.md)). The OS creates one instance
   per page, at the first drive of that type.
2. Its `OpenAsync(KazoDriveOpenRequest)` method returns a
   **`KazoDriveFolder`**: one drive of the user. It **never** reaches the
   service (the OS calls it at login, which does not wait for the network);
   `GetStateAsync` says later whether the service answers.
3. The feature declares it and asks for the `drives.provide` capability:

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
                FullAccess = new AppDriveOAuthClientManifest("your-public-app-id"),
            }))
        {
            Icon = "assets/exemple.svg",
            Hosts = ["api.example.com", "*.files.example.com"],
        },
    ];
}
```

## The contract (`Kazo.Os.AppSdk.Drives`)

| Type | Role |
| --- | --- |
| `KazoDriveProvider` | Abstract class: `OpenAsync(request, ct)` returns the folder of one drive. A class, not an interface, so a later SDK can add a virtual member without breaking a provider already compiled. |
| `KazoDriveFolder` | An open drive (`IAsyncDisposable`). **Abstract**: `GetStateAsync`, `StatAsync`, `ListAsync`, `ReadAsync`, `WriteAsync(path, content, ifRevision)`, `CreateFolderAsync`, `DeleteAsync(path, recursive)`. **Virtual with a default**: `CreateFileAsync` (`StatAsync` then an empty write, `Exists` when the name is taken), `TryMoveAsync` (`false`: the OS copies then deletes), `GetQuotaAsync` (`null`: `df` shows `-`), `DisposeAsync`. |
| `KazoDrivePath` | A path **relative to the drive's root** (never the Kazo OS path under `/mnt`): `Root`, `Segments`, `Name`, `Parent`, `Append`, `Parse`, `TryParse`, `FromSegments`, `IsValidName`. Each name is checked: not empty, not `.` or `..`, no `/`, `\` or control character, at most 255 characters. A provider therefore cannot build a service path that escapes the root. Ordinal equality, name by name. |
| `KazoDriveEntry` | `Name`, `Kind` (`File`, `Folder`), `Length`, `LastModifiedUtc`, and `Revision` (the service's tag: ETag, Dropbox `rev`) that the OS passes back to `WriteAsync` for a conditional write. |
| `KazoDriveQuota` | Size and free space, for `df` and File Explorer. |
| `KazoDriveState` | `Ready`, `SignInRequired`, `Unavailable`. |
| `KazoDriveException` | Derives from `IOException`; `Kind` (`KazoDriveErrorKind`): `NotFound`, `Exists`, `Unavailable`, `SignInRequired`, `RevisionConflict`, `FileTooLarge`, `QuotaExceeded`, `TypeMismatch`, `NotEmpty`, `InvalidName`, `AccessDenied`, `Other`. `Unavailable` and `SignInRequired` disconnect the drive. |
| `KazoDriveLimits` | `MaxFileBytes` = 128 MiB: a file is read and written whole, as for every network drive. |
| `KazoDriveOpenRequest` | `Name` (the drive's name), `Context`, `Root` (the service folder mounted as root, optional), `Options` (the form values that are not secrets). |
| `IKazoDriveProviderContext` | What the OS lends to the open drive: `Type`, `Account` (`KazoDriveAccount`: `Id`, `DisplayName`), `GetAccessTokenAsync()` (the access token **of this drive's account**, renewed by the OS), `GetSecretAsync(name)` (a secret field of the form), `NotifyAvailabilityChanged()` (the provider says the service came back or went away). |

The refresh token and the secrets stay sealed by the OS on the device
([ADR 0269](../dev/adr/0269-cloud-tokens-stay-on-the-device.md)): the provider
only receives the access token or the secrets of the drive it opened, on
request.

## The declaration (`FeatureDefinition.DriveProviders`)

`KazoDriveProviderDeclaration(Type, Name, Provider, Connection)` with `Icon`
and `Hosts`:

| Member | Rule |
| --- | --- |
| `Type` | The `mount -t` name: lowercase letters, digits and dashes, starts with a letter, at most 32 characters. **Reserved**: the OS types (`hostfs`, `dropbox`, `onedrive`, `s3`) and the kernel types (`idbfs`, `proc`, `tmpfs`, `ext4`, `nfs`, `cifs`, `smb`, `fuse`, `none`… full list in `AppDriveProviders.ReservedTypes`). Unique in the feature. |
| `Name` | The displayed name of the service. |
| `Provider` | The provider class; the manifest writes it `"Namespace.Class, Assembly"`. |
| `Connection` | `AppDriveConnectionManifest.WithOAuth(...)` or `WithForm(...)` (below). |
| `Icon` | Path of a package asset, optional. |
| `Hosts` | The hosts the provider reaches: an exact name (`api.example.com`) or a subdomain wildcard (`*.files.example.com`), always over `https`. Refused: a bare wildcard, a name without a dot, an IP address, uppercase. `drives.provide` is enough to reach them ([below](#reaching-the-service)). |

**OAuth** (`AppDriveOAuthManifest`): `AuthorizationEndpoint`, `TokenEndpoint`,
`RevocationEndpoint` (optional), all `https`; one client per scope,
`FullAccess` and/or `AppFolder` (`AppDriveOAuthClientManifest(ClientId)` with
`Scope`), at least one; `AppFolderPath`, `ExtraAuthorizationParameters` and
`Identity` (`AppDriveIdentityManifest(Address, IdPath)` with `UsePost` and
`NamePaths`: where to read the account's id and name). No client secret: the
client is public (PKCE), as for Dropbox and OneDrive.

**Form** (`AppDriveFormFieldManifest(Name, Label, Kind)`): `Kind` `Text`,
`Url`, `Number` or `Choice` (with `Choices`), `Secret` (sealed by the OS,
returned by `GetSecretAsync`), `Required` (true by default), `Default`.
Unique names, at most 64 characters. **`ServerAddress`** (`serverAddress` in
the manifest) marks the field where the user types the address of their
server ([ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md) §2):
kind `Url`, never `Secret`, required, one per provider at most, and only in a
form connection.

In the manifest (`driveProviders`, sorted by type):

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
      "fullAccess": { "clientId": "your-public-app-id" }
    }
  }
}]
```

## Reaching the service

The provider talks to its service **straight from the browser**, with
**`KazoDirectHttpClient`** (`Kazo.Os.AppSdk.Net`,
[`sdk-network.md`](sdk-network.md)): the request leaves through the page's
`fetch`, without the Kazo server, so it works on an installation with no
server.

```csharp
using KazoDirectHttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

string token = await context.GetAccessTokenAsync(cancellationToken); // the OS renews it
KazoDirectHttpRequest request = new(HttpMethod.Get, new Uri("https://api.example.com/v1/children?path=%2F"));
request.Headers.Add(new("Authorization", "Bearer " + token));
await using KazoDirectHttpResponse response = await client.SendAsync(request, cancellationToken);
Children? children = await response.ReadFromJsonAsync(MyJson.Default.Children, cancellationToken);
```

- **Declared hosts.** `drives.provide` is enough to reach a host in the
  `Hosts` of the feature's declarations; `network.direct.access` is **not**
  needed ([ADR 0284](../dev/adr/0284-drive-provider-hosts-and-typed-server-address.md) §1).
  The gateway decides in this order: a host of `directNetworkHosts` requires
  `network.direct.access`; otherwise a provider host or a typed server
  requires `drives.provide`; otherwise the request is refused
  (`UnauthorizedAccessException` naming the host) and nothing is sent.
- **A server the user types** (WebDAV, Nextcloud, a NAS: no host known in
  advance). Mark the address field `ServerAddress = true`. When a drive of that
  type opens, the OS reads that field's value, keeps its host when valid
  (absolute `https` address, no user name or password in the address, no IP
  address, no `localhost`, a name with a dot) and allows it **to that feature
  alone, while the drive is open**. Two drives on the same server keep it
  allowed until the second one closes. The connect dialog writes it before
  saving ("This provider will reach dav.example.org"). The provider has
  nothing more to do: it reads the address in `KazoDriveOpenRequest.Options`.
- **Network errors.** A network failure, a CORS refusal and a TLS error all
  give `HttpRequestException` ("Failed to fetch" for the page): the provider
  turns them into `KazoDriveErrorKind.Unavailable`. A `Timeout` that runs out
  throws `TaskCanceledException` without the caller's token being cancelled:
  `Unavailable` as well.
- **What the browser forbids.** No cookie, no reserved header (`Host`,
  `Cookie`, `Origin`, `Referer`, `Sec-*`…), and only the response headers the
  service exposes can be read (`ETag` included).

### Expected errors

The OS turns `KazoDriveException.Kind` into a file system error; the
conformance suite checks the same kinds:

| The service answers | `KazoDriveErrorKind` | File system error |
| --- | --- | --- |
| Nothing (network, CORS, TLS), 408, 429, 5xx | `Unavailable` | `ProviderUnavailable`, drive disconnected |
| 401, token refused | `SignInRequired` | the OS renews the token once, otherwise the drive is disconnected |
| 403 | `AccessDenied` | `PermissionDenied` |
| 404 | `NotFound` | `NotFound` |
| The name is taken | `Exists` | `AlreadyExists` |
| The revision no longer matches | `RevisionConflict` | `RevisionConflict` |
| Listing a file, reading a folder | `TypeMismatch` | `NotDirectory` |
| Folder not empty, without recursion | `NotEmpty` | `DirectoryNotEmpty` |
| Name refused by the service | `InvalidName` | `InvalidName` |
| Over 128 MiB | `FileTooLarge` | `FileTooLarge` |
| Storage full | `QuotaExceeded` | `NoSpace` |
| Anything else | `Other` | `ProviderFailure` |

## CORS: what the service must accept

The request leaves from the OS's page: the browser lets it through only if the
service accepts the **OS's origin** (`https://os.kazo.ca`, or the address
where the OS is served). For a request with `Authorization`, the browser first
sends an `OPTIONS` request ("preflight"); the service must answer it `2xx`
**without** asking for authentication, with:

| Response header | Value |
| --- | --- |
| `Access-Control-Allow-Origin` | the OS's origin (or `*`) |
| `Access-Control-Allow-Methods` | the methods used (`GET, PUT, POST, DELETE`, and for WebDAV `PROPFIND, MKCOL, MOVE`) |
| `Access-Control-Allow-Headers` | `Authorization` and the other headers sent (`Content-Type`, `Depth`, `Destination`, `Overwrite`, `If-Match`, `If-None-Match`); `*` does not cover `Authorization` |
| `Access-Control-Expose-Headers` | `ETag`, `Content-Length` (otherwise the page does not see them) |

To check, from a terminal:

```bash
curl -i -X OPTIONS "https://api.example.com/v1/children?path=%2F" \
  -H "Origin: https://os.kazo.ca" \
  -H "Access-Control-Request-Method: GET" \
  -H "Access-Control-Request-Headers: authorization"
```

The answer must be `200` or `204` with the headers above. In the OS, a request
refused by CORS shows as an `Unavailable` drive, and the browser's console
(F12) names the missing header.

**A WebDAV server must accept CORS**: Nextcloud, ownCloud and most WebDAV
servers do not by default. The server's administrator adds the headers above
in the web server in front of it (Apache, nginx), and makes `OPTIONS` answer
without authentication. Without that, the drive stays disconnected, whatever
the provider.

## Sign-in run by the OS (work item 5)

The OS runs the connection of a declared type
([ADR 0282](../dev/adr/0282-third-party-drive-providers.md) §3, details in
[`cloud-accounts.md`](../dev/cloud-accounts.md)):

- **OAuth**: the OS reads the declaration of an installed and enabled feature,
  opens the sign-in window, exchanges the code (PKCE, no client secret), reads
  the identity, seals the tokens on the device and renews the access token.
  The provider only calls `Context.GetAccessTokenAsync()` before each request.
- **Form**: the OS keeps the non-secret values in clear with the account (they
  arrive in `KazoDriveOpenRequest.Options`) and seals the `Secret` fields; the
  provider reads them with `Context.GetSecretAsync(name)`. `Context.Account` is
  `null` and `GetAccessTokenAsync` throws `SignInRequired`.
- **What is lent**: a drive's context is bound to its account and its type; it
  never returns another account's token or secrets, nothing once the account
  is removed, and never the refresh token.

### Addresses to register with the service

The author registers a **public** app with their service (no secret, PKCE
`S256`) whose redirect URI is the OS's callback page, the same for every type:

| Where Kazo OS is served | Redirect URI |
| --- | --- |
| os.kazo.ca | `https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Another site | `<base address>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Development | `http://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` and the same over `https` |

The token endpoint (and the identity and revocation addresses) must accept
CORS from the OS's origin. An administrator serving Kazo OS elsewhere
registers their own app and replaces the app id in
`/etc/kazoos/cloud-drives.config`, in a section named after the type
(`client-id`, `app-folder-client-id`).

## What the OS does with a provider (work item 4)

Work item 4 wires the declared types into the file system
([`network-drive-sources.md`](../dev/network-drive-sources.md#types-features-provide)):

- **One provider per type.** `AppCatalog.GetDriveProviders()` and
  `FindDriveProvider(type)` give the feature that provides each type. The
  installer refuses a second feature that declares the same type
  (`apps.package.drive-type-already-provided`). A type of the OS always wins.
- **Hot.** A type is offered as soon as its feature is installed and enabled,
  and goes away when it is disabled or removed, with no restart. A new version
  is a new provider.
- **Loaded at the first drive.** Nothing loads at install or at the user's
  login. At the first drive of the type that opens, the OS loads the assembly,
  starts the feature's service process (account `kazoos-svc`, its permissions)
  and creates **one instance per page**, with the `(IFeatureServiceContext)`
  constructor, else `()`. Every call to the provider runs in the feature's
  context. A failure is never kept: the next drive tries again.
- **Login never waits.** The drive is mounted **disconnected** until `OpenAsync`
  answers, then reconnects by itself. Loading and opening get 20 s at most,
  `GetStateAsync` 10 s.
- **A provider that fails** (assembly or class missing, constructor that
  throws, `OpenAsync` that throws or is too slow) leaves the drive
  disconnected and writes a line `drives: <type>: <reason>` to
  `/var/log/apps/<feature>.log` and to the system log. An unexpected exception
  of an operation becomes `HostFolderErrorKind.Failure` (`EIO`), never a crash.
- **Errors.** `KazoDriveException.Kind` becomes the file system's code:
  `NotFound`, `Exists` → `AlreadyExists`, `Unavailable`, `RevisionConflict`,
  `FileTooLarge`, `QuotaExceeded` → `NoSpace`, `TypeMismatch`, `NotEmpty`,
  `InvalidName`, `AccessDenied` → `NotAllowed`, `Other` → `Failure`.
  `SignInRequired` after a token was given: the OS renews the token once and
  repeats the operation; otherwise the drive is disconnected (`NotAllowed` if
  the account was removed).
- **Account.** A drive of a provided type always belongs to an account of that
  type; the OS refuses an account of another type and lends the provider only
  that account's context (`KazoDriveState` → `Granted`, `Prompt` for
  `SignInRequired`, else `Unavailable`). A name returned by `ListAsync` that
  could leave the folder is not shown; a file larger than
  `KazoDriveLimits.MaxFileBytes` is refused both ways.
- **Testing a provider**: `InMemoryDriveProvider` and `InMemoryDriveService`
  (`Tests/Kazo.Os.Tests.Support`) are an in-memory fake provider and service
  (tree, revisions, quota, accepted token, offline, failures, slow or failing
  open).

## Testing: the conformance suite

The **`Kazo.Os.AppSdk.Testing`** package gives `KazoDriveProviderTests`
(namespace `Kazo.Os.AppSdk.Testing.Drives`): the same cases the OS expects of
every provider. A test class derives from it, opens a drive on a test service
(never a real one), and runs each case:

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

| Case | What is checked |
| --- | --- |
| `root-is-a-folder` | The root is a folder, the state is `Ready`. |
| `write-then-read`, `empty-file`, `overwrite` | Write, read back, describe, list; an empty file; a whole replacement. |
| `create-folder-and-list`, `create-existing` | Create a folder, list only the direct children; `Exists` on a taken name. |
| `missing`, `wrong-kind` | `NotFound`; `TypeMismatch`. |
| `delete` | A file, an empty folder, `NotEmpty` without recursion, a recursive delete. |
| `revision-conflict` | A conditional write of a file another device changed throws `RevisionConflict` (skipped when the service keeps no revision). |
| `move` | A move in one call, file and folder (an informative result when `TryMoveAsync` answers `false`). |
| `quota` | `0 <= free <= total` (informative when `null`). |
| `names` | Spaces, accents, `%`, `#`, `?`, `+`, `&`, brackets, an apostrophe, Japanese, an emoji, `.cache` come back as written, or are refused with `InvalidName`. |
| `offline` | `OpenAsync` does not reach the service; the state is `Unavailable`, then `Ready` when the network is back. |
| `sign-in-expired` | State `SignInRequired`, operations `SignInRequired`. |
| `file-too-large` | A file over 128 MiB stays listed with its length, but reading it throws `FileTooLarge`. |

For the last three, the test class overrides `SetServiceReachableAsync`,
`ExpireSignInAsync` and `PlaceLargeFileAsync` (otherwise the case is skipped,
with the reason); `ResetAsync` puts the service back between two cases.
`KazoDriveTestContext` plays the OS's context: `AccessToken` (without a token,
`GetAccessTokenAsync` throws `SignInRequired`), `Secrets`, and the
`TokenRequests` and `AvailabilityNotifications` counters. Each case works in a
`kazo-conformance-…` folder that it deletes afterwards.

## The WebDAV sample

`Apps/Samples/Kazo.Os.Samples.WebDavDrive` is a complete provider to copy,
with a **form connection**: server address (`ServerAddress`), user name,
app password (`Secret`, sealed by the OS). It sends WebDAV (RFC 4918) through
`KazoDirectHttpClient`, signs each request with HTTP Basic using the password
it asks the OS for each time, and takes the `ETag` as the revision (`If-Match`
for a conditional write, `If-None-Match: *` for a create). It declares no host:
the server the user types is enough. Its tests
(`Tests/Kazo.Os.Samples.WebDavDrive.Tests`) pass the whole suite against a
local in-memory WebDAV server (`LocalWebDavServer`).

## Limits and the Store review

- **128 MiB per file** (`KazoDriveLimits.MaxFileBytes`): a file is read and
  written whole. A larger file stays listed, but is neither read nor written.
- **No sandbox.** The feature's code runs in the page; its own JavaScript can
  call `fetch`. The host list bounds what goes through the SDK, not a
  malicious provider.
- **The Store review is the real barrier**: only the Store repository signs
  packages; it checks the declaration (type not reserved, hosts over `https`,
  no bare wildcard) and the review reads the code: the hosts reached must be
  the declared ones, no token or secret may leave to another host, no
  JavaScript that goes around the SDK.
- **CORS**: without the service's consent, nothing gets through
  ([above](#cors-what-the-service-must-accept)).
- **Phishing**: the OS always shows the feature's name next to the service's
  name in the connect dialog and Settings.

## Checks

| Where | Code | Case |
| --- | --- | --- |
| Validator | `manifest.feature.only` | An app that is not a feature declares `driveProviders` or `drives.provide`. |
| Validator | `manifest.drive-provider.capability` | Providers without the `drives.provide` capability. |
| Validator | `manifest.drive-provider.type`, `.reserved`, `.duplicate` | A malformed, reserved or repeated type. |
| Validator | `manifest.drive-provider.name`, `.class`, `.icon` | An empty name, a missing or malformed class, an empty icon. |
| Validator | `manifest.drive-provider.host` | A refused host (above). |
| Validator | `manifest.drive-provider.connection`, `.oauth`, `.form`, `.address` | An inconsistent connection, no OAuth client, repeated or malformed fields, an address that is not `https`. |
| Validator | `manifest.drive-provider.server-address` | A `serverAddress` field that is not a required, non-secret `Url`, or more than one per provider. |
| Generator | `KOSAPP0033` | An ordinary app asks for `drives.provide`. |
| Generator | `KOSAPP0036` | The declared class does not derive from `KazoDriveProvider`, is abstract, is not in the feature's assembly, or has no public `()` or `(IFeatureServiceContext)` constructor. |

The `manifest.schema.v1.json` schema carries the same shape rules (fixtures
`feature-provides-drive-*.valid.json` and `invalid-drive-provider-*.json`).

The **`drives.provide`** capability is dangerous: the approval screen names it
"Provide a type of network drive", with the hosts of the declarations and,
when there is one, the label of the server address field ("Each user's
server, typed in: Server address"). An update that adds a host puts it back
to pending ([approval](../dev/dangerous-capability-approval.md)).

## Deviations from the sketch of ADR 0282

The ADR gave indicative names; this is what changed, without touching the
substance (no operation removed, the OS still keeps the tokens):

- the context exposes a **method** `NotifyAvailabilityChanged()` instead of
  an event: the provider tells the OS;
- a virtual **`CreateFileAsync`** was added: the file system needs to create
  a file only when it does not exist (`touch`, `O_EXCL`);
- error kinds added (`TypeMismatch`, `NotEmpty`, `InvalidName`,
  `AccessDenied`, `Other`) to give back the expected POSIX codes;
- `KazoDriveOpenRequest(Name, Context)` with `Root` and `Options`, and
  `KazoDriveLimits.MaxFileBytes`;
- OAuth clients are named by scope (`FullAccess`, `AppFolder`) as for the
  shipped connectors;
- the connection types of the manifest are named `AppDrive*Manifest`
  (assembly `Kazo.Os.App.Abstractions`, which does not see the SDK);
- hosts are checked by `AppDriveProviders.IsValidHost`, which applies the
  rule of `DirectNetworkHosts` (work item 1, `network.direct.access`) in its
  canonical form: one rule for both.

## Task list

Work item 2 (contract):

- [x] SDK types of `Kazo.Os.AppSdk.Drives` and their tests
- [x] `FeatureDefinition.DriveProviders`, `KazoDriveProviderDeclaration`
- [x] `driveProviders` in the manifest, strict serialization, schema, fixtures
- [x] Validator (`manifest.feature.only`, `manifest.drive-provider.*`)
- [x] Generator (`KOSAPP0033`, `KOSAPP0036`)
- [x] Dangerous capability `drives.provide`
- [x] Sign-in run by the OS, form accounts, provider context (work item 5)
- [x] Wiring into the OS (work item 4, see above)

Work item 7 (developer kit):

- [x] Public conformance suite `KazoDriveProviderTests` (`Kazo.Os.AppSdk.Testing`)
- [x] `dotnet new kazoos-drive-provider` template
- [x] WebDAV sample with a form connection
- [x] ADR 0284: `drives.provide` is enough for declared hosts, a typed server address
- [x] This guide
