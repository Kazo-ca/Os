> [Version française](../../../../fr/wasm/dev/adr/0282-third-party-drive-providers.md)

# ADR 0282: third-party drive providers (a Feature that provides a drive type) and shipped providers that can be switched off at publish

## Status

**Accepted** on 2026-11-17: Benoit chose the recommended option of the five
questions (1 A, 2 A, 3 A, 4 A, 5 A, recorded at the end). Evaluation and
decision, **no code** here. Series: `drive-providers/base`, plan
[`drive-providers-plan.md`](../plans/drive-providers-plan.md).

- **Completes:** [ADR 0267](0267-cloud-drives-as-network-drives.md) (cloud
  drives mounted as network drives, one source per type),
  [ADR 0269](0269-cloud-tokens-stay-on-the-device.md) (tokens on the device),
  [ADR 0270](0270-cloud-drives-in-the-app-sdk.md) (cloud accounts and drives in
  the SDK) and [ADR 0260](0260-feature-app-type.md) §5 (what only a Feature can
  provide: this ADR adds a row).
- **Builds on:** [ADR 0262](0262-feature-service-registry-and-async-resolution.md)
  (a Feature's code loaded at first need, run in its own context),
  [ADR 0246](0246-provider-declared-permissions.md), [ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  and [ADR 0231](0231-network-drives-count-as-personal-files.md) (rules of
  network drives), [ADR 0066](0066-optional-resource-accounting.md) (an option
  chosen at build), [ADR 0277](0277-trimmed-api-check-before-publishing.md)
  (API of the trimmed OS).
- **Does not change:** package signing, done only in Os-AppMarketPlace
  (ADR 0219, 0188).

## Context

Benoit's request (2026-11-17): a person should be able to create a file
provider "like Dropbox and OneDrive"; the existing ones stay shipped with
Kazo OS and can be switched off when publishing.

What the code does today (checked on `main`, version 3.4.2):

- A drive type is a **source**: `INetworkDriveSource`
  (`Shared/Kazo.Os.Simulation.Abstractions/FileSystem/NetworkDriveSourceContracts.cs`),
  which creates and reopens an `IHostFolder` (list, read, write, move, quota,
  conditional write). `NetworkDriveService` mounts what the source returns,
  under `/mnt`, with the rules of ADR 0181.
- The three shipped connectors (`dropbox`, `onedrive`, `s3`, about 3,500
  lines) are in `Platform/Kazo.Os.Platform.Core/CloudDrives/`. They are
  registered **by hand** in the host's dependency injection
  (`EcosystemServiceCollectionExtensions.cs:576-594`), and the list of sources
  is fixed when `NetworkDriveService` is built (`:282-289`).
- The OAuth providers are two static factories (`CloudOAuthProvider.Dropbox`,
  `.OneDrive`) and a settings record with fixed fields
  (`CloudProviderSettings`). Dropbox and OneDrive are offered only when the
  host has an app id (`KazoOs:CloudDrives`, or
  `/etc/kazoos/cloud-drives.config`); `s3` is always offered.
- The interface has names written in the code: the S3 form of the Explorer
  (`ConnectNetworkDriveDialog.razor.cs`), the service names in Settings
  (`CloudAccountsPanel.razor.cs:75-77`), `mount -t dropbox|onedrive|s3`.
- An installed app **can add nothing** to dependency injection (ADR 0262,
  context): no path leads from a Store package to `INetworkDriveSource`.
- The **Feature** app kind exists (ADR 0260, shipped in 3.4.0): installed for
  the whole machine by an administrator, it can provide services (ADR 0262),
  permissions, roles, Settings pages. Its code is loaded at first need and runs
  in its own context.
- **Network.** The OS connectors call the service through the browser's
  `fetch` (`HttpClient`, CORS). An app has no such path: `KazoHttpClient` goes
  through the simulated network, then the relay of the optional server, and the
  trimmed publish does not guarantee `System.Net.Http` to an installed app.
  Kazo AI works around it with its own JavaScript module (`ai-fetch.js`).
- **Trimming.** The `Kazo.Os.*` assemblies and those of the apps stay whole at
  publish; only the framework is trimmed. A run-time switch therefore does not
  remove a connector's code from the download: the host must not reference its
  assembly. The precedent is resource accounting (ADR 0066,
  `build/Kazo.Os.ResourceAccounting.targets`).
- **Public API.** The connector and cloud account types are public in
  `Kazo.Os.Platform.Core` (337 lines of `PublicAPI.Shipped.txt`). `IHostFolder`
  and `INetworkDriveSource` are public in `Kazo.Os.Simulation.Abstractions`.

## Approaches compared

| | A. Store Feature that provides a drive type | B. Ordinary app that provides a type | C. NuGet package composed into the host at build | D. Build option only |
| --- | --- | --- | --- | --- |
| Who can write it | any developer, with the SDK | same | whoever publishes their own Kazo OS | the OS team |
| Reaches os.kazo.ca without republishing the OS | yes (Store) | yes (Store) | no | no |
| Scope | the machine (like `mount -t`, `/proc/mounts`) | one user: two users, two versions of the same type | the machine | the machine |
| Who approves | administrator, once, at installation | each user | whoever publishes | whoever publishes |
| Existing model | ADR 0260 §5, ADR 0262 | against ADR 0260 §5 ("providing" is reserved to Features) | already possible: `services.AddScoped<INetworkDriveSource, …>()` | ADR 0066 |
| New public API | SDK (provider contract, declaration) | same | none, but the internal contracts become a commitment | none |
| Answers "a person creates a provider" | yes | yes | half | no |
| Answers "switch off the shipped ones at publish" | no | no | no | yes |

A and D do not compete: A for third parties, D for the shipped connectors. C
stays possible with no work (it is today's seam), with no promise of
stability.

## Decision

### 1. A third-party provider is a Feature that declares a drive type

- `FeatureDefinition` gains `DriveProviders`: a list of
  `KazoDriveProviderDeclaration` (type, display name, icon, provider class,
  connection, form fields, hosts reached). The manifest writes it as
  `driveProviders`. An ordinary app that declares it is refused (`KOSAPP0033`,
  `manifest.feature.only`), like the other provisions.
- **Type**: lower-case letters, digits and hyphens (`pcloud`, `webdav`); it is
  the name `mount -t`, `/proc/mounts` and `df -T` show. The OS types are
  reserved (`hostfs`, `dropbox`, `onedrive`, `s3`, and the kernel types:
  `proc`, `dev`, `tmpfs`…), even when a shipped connector is switched off (§5).
  **One provider per type**: a second Feature that declares the same type is
  refused at installation (`apps.package.drive-type-already-provided`).
- The Feature is installed for the machine, by an administrator
  (ADR 0260 §4). Each user then connects their own accounts and drives, as
  today.

### 2. The provider contract, in the public SDK

New namespace `Kazo.Os.AppSdk.Drives`. The provider derives from an **abstract
class** (a virtual member can be added later without breaking compiled
providers, which an interface does not allow):

```csharp
public abstract class KazoDriveProvider
{
    // Opens a drive: never call the service here (the user's sign-in does not wait for the network).
    public abstract ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken ct);
}

public abstract class KazoDriveFolder : IAsyncDisposable
{
    public abstract ValueTask<KazoDriveState> GetStateAsync(CancellationToken ct);      // Ready, SignInRequired, Unavailable
    public abstract ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken ct);
    public abstract ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken ct);
    public virtual ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken ct) => new(false);
    public virtual ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken ct) => new((KazoDriveQuota?)null);
}
```

- `KazoDriveOpenRequest` carries what the OS keeps about the drive: the root,
  the non-secret options, and an `IKazoDriveProviderContext` that gives
  `GetAccessTokenAsync()`, `GetSecretAsync(name)`, the account identity and an
  event "the service may be reachable again".
- Errors are `KazoDriveException` with a kind (`NotFound`, `Exists`,
  `Unavailable`, `SignInRequired`, `RevisionConflict`, `FileTooLarge`,
  `QuotaExceeded`), which the OS turns into POSIX errors as it does for the
  shipped connectors.
- The SDK **does not expose** `IHostFolder` or `INetworkDriveSource` to
  providers: they are the platform's internal contracts, which the OS must be
  free to change (the partial read of ADR 0275, for example). A platform
  adapter (`FeatureDriveSource`) bridges the two.
- The SDK ships a **test bench** (`KazoDriveProviderTests` in a test package):
  the same conformance suite as the shipped connectors (create, read, revision
  conflict, offline, file too large).

### 3. What the OS keeps, what the provider does

| The OS (unchanged for every type) | The provider |
| --- | --- |
| Mount point under `/mnt`, modes `0700`/`0600`, drive list, unmount at sign-out, app access (ADR 0231) | Talk to the service: list, read, write |
| The interface: "Connect a network drive…", Settings ▸ Cloud accounts, `mount -t <type>`; no screen from the provider | Declare its name, icon and fields |
| The OAuth sign-in (window, PKCE, return page, ADR 0267 §5), renewal, sealing of tokens and secrets (ADR 0269) | Declare its OAuth addresses and its public app id; ask for an access token |
| The 128 MiB per-file limit, timeouts, disconnecting the drive when the provider throws `Unavailable` | Answer quickly; never reach the service in `OpenAsync` |

- **Declared connection**, two forms, in the declaration:
  - `OAuth`: authorization, token and revocation addresses, public app id per
    scope (whole storage, reserved folder), extra parameters, identity address
    and fields. It is the content of `CloudOAuthProvider`, turned into manifest
    data. The OS runs the whole sign-in; the provider **never** sees the
    refresh token.
  - `Form`: typed fields, each marked secret or not (like the address, bucket
    and keys of S3). The OS draws the form, seals the secrets, and gives them
    back to the provider of that account only.
- **OAuth app id.** It is public (PKCE, no secret). The provider's author
  registers with the service the return page of each site where the provider
  must work (`https://os.kazo.ca/…`, the beta site). An administrator of
  another site overrides it in `/etc/kazoos/cloud-drives.config`, group of the
  type, key `client-id` (the settings document becomes generic, one group per
  declared type).
- **Loading.** Like a Feature service (ADR 0262 §4): the assembly is loaded at
  the first drive opened of that type, one instance per page, in the Feature's
  context (account `kazoos-svc`, its permissions). A drive whose provider does
  not load is mounted disconnected, with a line in
  `/var/log/apps/<feature>.log`; the user's sign-in goes on.
- **Hot add.** `NetworkDriveService` reads sources from a registry that changes
  (the shipped connectors, plus `AppCatalog.GetDriveProviders`): an installed
  provider is offered at once (ADR 0214).
- **Provider removed or disabled.** Its drives are unmounted but their lines
  **stay** in `network-drives.json`, marked "provider missing"; the Explorer
  shows them greyed out with "Install the provider" (Store). Today a line whose
  type has no source is removed: that rule stays only for a type that was never
  declared. Accounts and their tokens stay sealed until the user removes them.

### 4. Direct HTTP in the SDK (prerequisite)

- `KazoHttpClient` gains a **direct** mode: the request leaves through the
  browser's `fetch`, with no Kazo server, with a streamed body. It is what the
  OS connectors already do internally.
- New dangerous capability **`network.direct.access`**. The app declares its
  **hosts** (`api.pcloud.com`, `*.example.org`) in the manifest; the OS refuses
  any other host, and the approval screen names them. For a drive provider,
  the list is the one of its declaration.
- Honest limits: the service must accept CORS from an isolated page
  ([ADR 0131](0131-cross-origin-isolation.md); Nextcloud and WebDAV do not by
  default), and no cookies or headers the browser forbids.
- This mode also serves other apps (Kazo AI will be able to drop its
  JavaScript module), but that is outside this series.

### 5. The shipped connectors: absent from the build on request

- The connectors leave `Kazo.Os.Platform.Core` for their own assembly,
  `Kazo.Os.Platform.CloudDrives`, which the host references according to
  MSBuild properties, on the model of ADR 0066:

  ```xml
  <KazoOsDropboxDrives>false</KazoOsDropboxDrives>
  <KazoOsOneDriveDrives>false</KazoOsOneDriveDrives>
  <KazoOsS3Drives>false</KazoOsS3Drives>
  ```

  All default to `true`. `dotnet publish -p:KazoOsDropboxDrives=false`
  publishes an OS without Dropbox: it is no longer offered, and the assembly
  leaves the download when all three are `false`. Each property also becomes a
  run-time switch (`RuntimeHostConfigurationOption`), so a connector switched
  off is ignored even while the assembly stays for another one.
- **At run time**, without republishing: `KazoOs:CloudDrives:Disabled` in
  `appsettings.json` (a list of types) and the `disabled` key of
  `/etc/kazoos/cloud-drives.config`. A disabled type is no longer offered; its
  drives already mounted follow the "provider missing" rule of §3.
- The shipped connectors **stay OS code**: they do not go through the
  third-party contract and keep direct access to the internal contracts. They
  are the reference of the conformance suite.
- The interface stops naming services in code: the Explorer, Settings and
  `mount` read the name, icon, connection form and fields of each type (shipped
  or third-party) through `KazoDriveInfo.GetNetworkDriveTypesAsync`, which now
  returns a description per type.

## Security

- **What a provider sees.** The content of every file of the drive and an
  access token of the account: it cannot be avoided, it is its job. The guards
  are therefore at the door: installation by an administrator, package signed
  by the catalog (ADR 0219), a dangerous capability named on screen
  (`drives.provide`, which only a Feature can declare), declared hosts.
- **Tokens.** The refresh token and the secrets stay sealed by the OS on the
  device (ADR 0269). A provider receives only the access token or the secrets
  **of the account of the drive it opens**, never those of another type, and no
  ordinary app receives any (ADR 0270 unchanged).
- **No sandbox.** An app's code runs in the page; its own JavaScript can call
  `fetch`. The host list bounds what goes through the SDK, not a malicious
  provider. The real barrier stays the Store review and the signature: the
  developer guide will say so plainly.
- **Signing.** Unchanged: only Os-AppMarketPlace signs; no provider repository
  holds a key. The catalog also checks the declaration (type not reserved,
  `https` hosts, no bare `*` wildcard).
- **Phishing.** The OS always shows the Feature's name next to the service
  name ("pCloud, provided by ca.example.pcloud") in the connection dialog and
  in Settings; brand icons follow the rule of 2026-11-15 (icon fonts or the
  service's favicon).

## Trimming and public API

- The new types are in `Kazo.Os.AppSdk` and `Kazo.Os.App.Abstractions`, kept
  whole at publish: an installed provider finds them. Its package goes through
  `check-api` (ADR 0277) like any app.
- A provider does not count on `System.Net.Http`: it uses the direct mode of
  `KazoHttpClient` (§4).
- **SDK**: additions only (`PublicAPI.Unshipped.txt`, `sdk-compat` fixture), so
  a **minor** version (3.5.0).
- **`Kazo.Os.Platform.Core`**: the `CloudDrives.*` types change assembly. It is
  a break for a third-party host that names them, not for an app. No type
  forwarding is possible (it would make Platform.Core reference the new
  assembly). The change is written in the release notes; the
  `Kazo.Os.Ecosystem.Host` host follows in the same version.

## Options not chosen

- **B. An ordinary app provides a type**: per-user scope for something the
  shell shows to the whole machine, and against ADR 0260 §5.
- **C as the only way** (NuGet package composed at build): nothing reaches
  os.kazo.ca without republishing the OS, and `IHostFolder` would become a
  frozen contract.
- **The provider runs its own sign-in** (its window, `KazoKeyring`): every
  author would redo PKCE and the return page; a refresh token would live in
  third-party code; the user would see a different screen per service.
- **A provider outside the page** (worker, wire protocol): better isolation,
  but the OS file system providers run on the page and the OAuth sign-in needs
  the user's gesture; to revisit if Features gain a worker.
- **The shipped connectors become preinstalled Features**: elegant (one
  mechanism), but it needs a mechanism of preinstalled packages in the
  published image, which does not exist. Possible later, without changing the
  third-party contract.
- **Run-time switch only for the shipped connectors**: the code stays in the
  download, which does not answer "switched off when publishing" for whoever
  wants an OS without those services.

## Consequences

- SDK: `Kazo.Os.AppSdk.Drives` (`KazoDriveProvider`, `KazoDriveFolder`, models,
  exception, context), `FeatureDefinition.DriveProviders`, `driveProviders` of
  the manifest and its schema, capabilities `drives.provide` and
  `network.direct.access`, direct mode of `KazoHttpClient`, richer
  `KazoDriveInfo.GetNetworkDriveTypesAsync`.
- Platform: provider registry in `AppCatalog`, `FeatureDriveSource`, dynamic
  sources in `NetworkDriveService`, "provider missing" lines, generic
  `CloudProviderCatalog` and settings, assembly `Kazo.Os.Platform.CloudDrives`,
  `build/Kazo.Os.CloudDrives.targets`.
- System apps: Explorer, Settings and `mount` with no service name in code.
- Store: check of the declaration in `validate.yml`.
- Docs to write with the code: `framework/drive-providers.md` (developer
  guide), `dev/cloud-drives.md`, `dev/network-drive-sources.md`,
  `dev/cloud-accounts.md`, `framework/capabilities.md`,
  `framework/sdk-network.md`, `dev/trimmed-publish.md`, `features.md`,
  `changes/unreleased` entries.
- Expected tests: a fake provider installed hot then mounted; a reserved or
  duplicate type refused; an undeclared host refused; a provider that throws or
  does not load, the user's sign-in intact; Feature removed, lines kept, then
  the drive back after reinstallation; publish with the three properties
  `false` (assembly absent, types not offered); E2E on the trimmed OS with an
  installed provider.

## Questions asked to Benoit

1. **What form does a third-party provider take?**
   - A) **(Recommended)** A Store Feature, installed for the machine by an
     administrator; each user then connects their accounts.
   - B) An ordinary app, installed by each user: no administrator needed, but
     two users can have two versions of the same type, and ADR 0260 §5 needs an
     exception.
   - C) Only a NuGet package composed at build: nothing in the Store; a
     provider arrives only by republishing the OS.
2. **Who runs the sign-in to the service and keeps the tokens?**
   - A) **(Recommended)** The OS: the provider declares its OAuth addresses or
     its fields; it receives only an access token.
   - B) The provider: it opens its own window and stores its tokens; more
     flexible for an unusual service, but every author redoes the sign-in and
     keeps a refresh token.
3. **How are the shipped providers switched off?**
   - A) **(Recommended)** A build property per provider (code absent from the
     download) **and** a run-time setting (without republishing). The
     connectors change assembly: a break for a third-party host that names
     them.
   - B) The run-time setting only: nothing breaks, but the code stays in the
     download.
   - C) The shipped connectors become preinstalled Features, which the
     administrator removes in Settings: one mechanism, but preinstalled
     packages must be invented first (longer).
4. **How does a provider reach its service?**
   - A) **(Recommended)** A direct mode in `KazoHttpClient`, behind a dangerous
     capability and a list of declared hosts.
   - B) Each provider writes its own JavaScript `fetch` module (like Kazo AI
     today): no work in the OS, no host list on screen.
   - C) Through the relay of the Kazo server: works without CORS, but nothing
     works without a server any more, and file content goes through the
     server.
5. **What happens to the drives when their provider is removed?**
   - A) **(Recommended)** They stay in the list, greyed out, and come back
     after reinstallation; accounts stay until the user removes them.
   - B) They are removed from the list (today's rule for an unknown type):
     simpler, but a failed update makes the user reconnect everything.

Maintainer's answers (2026-11-17): 1 A, 2 A, 3 A, 4 A, 5 A ("I agree with your
recommendations").
