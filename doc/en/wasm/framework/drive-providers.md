> [Version française](../../../fr/wasm/framework/drive-providers.md)

# Network drive providers

## Purpose

A Store **feature** (`Feature` kind,
[ADR 0260](../dev/adr/0260-feature-app-type.md)) can provide one more **type
of network drive**, like the Dropbox, OneDrive and S3 drives shipped with the
OS: pCloud, a company WebDAV server, an in-house service.
[ADR 0282](../dev/adr/0282-third-party-drive-providers.md) sets the model:
the OS keeps everything that is the same for every type (the mount under
`/mnt`, its modes, the drive list, the connect dialog, Settings, `mount -t`,
the sign-in and the sealing of tokens and secrets); the **provider** only
talks to its service.

This document describes the **public contract** (work item 2 of the
`drive-providers` series): the SDK types of `Kazo.Os.AppSdk.Drives` and the
`FeatureDefinition.DriveProviders` declaration, then what the OS does with
it (work item 4). The connect dialog and Settings come in work item 6.

## Writing a provider

1. A class derived from **`KazoDriveProvider`**, concrete, in the feature's
   assembly, with a public constructor that takes an `IFeatureServiceContext`
   or no parameter (the same rule as services,
   [`feature-services.md`](feature-services.md)). The OS creates one instance
   per page, on the first drive of that type.
2. Its `OpenAsync(KazoDriveOpenRequest)` method returns a **`KazoDriveFolder`**:
   one drive of the user. It **never** reaches the service (the OS calls it at
   login, and a login never waits on the network); `GetStateAsync` tells later
   whether the service answers.
3. The feature declares it and asks for the `drives.provide` capability:

```csharp
[AppManifest]
public sealed class PCloudFeature : FeatureDefinition
{
    public override string Id => "ca.example.pcloud";
    public override string Name => "pCloud";
    public override string PublisherId => "ca.example";
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new("pcloud", "pCloud", typeof(PCloudProvider), AppDriveConnectionManifest.WithOAuth(
            new AppDriveOAuthManifest(
                "https://my.pcloud.com/oauth2/authorize",
                "https://api.pcloud.com/oauth2_token")
            {
                FullAccess = new AppDriveOAuthClientManifest("public-client-id"),
            }))
        {
            Icon = "assets/pcloud.svg",
            Hosts = ["api.pcloud.com", "*.pcloud.com"],
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
| `Hosts` | The hosts the provider reaches: an exact name (`api.pcloud.com`) or a subdomain wildcard (`*.pcloud.com`), always over `https`. Refused: a bare wildcard, a name without a dot, an IP address, uppercase. |

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
Unique names, at most 64 characters.

In the manifest (`driveProviders`, sorted by type):

```json
"driveProviders": [{
  "type": "pcloud",
  "name": "pCloud",
  "provider": "Example.PCloud.PCloudProvider, Example.PCloud",
  "icon": "assets/pcloud.svg",
  "hosts": ["*.pcloud.com", "api.pcloud.com"],
  "connection": {
    "kind": "oauth",
    "oauth": {
      "authorizationEndpoint": "https://my.pcloud.com/oauth2/authorize",
      "tokenEndpoint": "https://api.pcloud.com/oauth2_token",
      "fullAccess": { "clientId": "public-client-id" }
    }
  }
}]
```

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

## Checks

| Where | Code | Case |
| --- | --- | --- |
| Validator | `manifest.feature.only` | An app that is not a feature declares `driveProviders` or `drives.provide`. |
| Validator | `manifest.drive-provider.capability` | Providers without the `drives.provide` capability. |
| Validator | `manifest.drive-provider.type`, `.reserved`, `.duplicate` | A malformed, reserved or repeated type. |
| Validator | `manifest.drive-provider.name`, `.class`, `.icon` | An empty name, a missing or malformed class, an empty icon. |
| Validator | `manifest.drive-provider.host` | A refused host (above). |
| Validator | `manifest.drive-provider.connection`, `.oauth`, `.form`, `.address` | An inconsistent connection, no OAuth client, repeated or malformed fields, an address that is not `https`. |
| Generator | `KOSAPP0033` | An ordinary app asks for `drives.provide`. |
| Generator | `KOSAPP0036` | The declared class does not derive from `KazoDriveProvider`, is abstract, is not in the feature's assembly, or has no public `()` or `(IFeatureServiceContext)` constructor. |

The `manifest.schema.v1.json` schema carries the same shape rules (fixtures
`feature-provides-drive-*.valid.json` and `invalid-drive-provider-*.json`).

The **`drives.provide`** capability is dangerous: the approval screen names it
"Provide a type of network drive".

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

## Task list (work item 2)

- [x] SDK types of `Kazo.Os.AppSdk.Drives` and their tests
- [x] `FeatureDefinition.DriveProviders`, `KazoDriveProviderDeclaration`
- [x] `driveProviders` in the manifest, strict serialization, schema, fixtures
- [x] Validator (`manifest.feature.only`, `manifest.drive-provider.*`)
- [x] Generator (`KOSAPP0033`, `KOSAPP0036`)
- [x] Dangerous capability `drives.provide`
- [x] Wiring into the OS (work item 4, see above)
