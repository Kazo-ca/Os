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
`FeatureDefinition.DriveProviders` declaration. The wiring into the OS
(adapter to the file system, connect dialog, Settings) comes in work item 4;
until then a declared provider is checked at build and package time, but the
OS does not open it yet.

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
- hosts are checked by `AppDriveProviders.IsValidHost`; when work item 1
  (`network.direct.access`, `DirectNetworkHosts`) reaches the base, the two
  rules will be unified.

## Task list (work item 2)

- [x] SDK types of `Kazo.Os.AppSdk.Drives` and their tests
- [x] `FeatureDefinition.DriveProviders`, `KazoDriveProviderDeclaration`
- [x] `driveProviders` in the manifest, strict serialization, schema, fixtures
- [x] Validator (`manifest.feature.only`, `manifest.drive-provider.*`)
- [x] Generator (`KOSAPP0033`, `KOSAPP0036`)
- [x] Dangerous capability `drives.provide`
- [ ] Wiring into the OS (work item 4)
