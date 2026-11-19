> [Version française](../../../fr/wasm/framework/system-administration.md)

# System administration in the SDK

## Purpose

Gives system apps the administration operations that used to go through internal
services (ADR 0056 §8 to §11, adjusted by ADR 0060):

- read and write the OS settings documents;
- administer dangerous capabilities (policy, machine decisions);
- connect to the optional server and synchronize;
- restart or reset the machine;
- read the raw storage snapshot;
- back up and restore the whole machine (ADR 0099 §1).

Settings and Diagnostics use these APIs and no longer reference Platform
Core or Infrastructure.Browser.

## Architecture

| Layer | Files |
|---|---|
| SDK (contracts, facades) | `Shared/Kazo.Os.AppSdk/Administration/` (`KazoPermissionAdministration`, `KazoComponentProviders`, `KazoServerConnection`, `KazoSystem`, `KazoStorageDiagnostics`, `KazoScreens` and their contracts), `Shared/Kazo.Os.AppSdk/Settings/IAppSystemSettingsGateway.cs`, `SettingsDocumentStores.cs` |
| Platform Core (gateways) | `Platform/Kazo.Os.Platform.Core/Administration/`: `AppSystemSettingsGateway`, `AppPermissionAdministrationGateway`, `AppServerConnectionGateway`, `AppSystemPowerGateway`, `AppStorageDiagnosticsGateway`, `AppSystemBackupGateway`, `AppUserAccountsGateway`, `AppScreensGateway` (over `IScreenControl`, ADR 0184), `AdministratorGate`, `SystemAdministrationGatewayServices` |
| Platform Core (services) | `Power/SystemPowerService.cs` (shutdown sequence, `RestartAfterAsync`), `Backup/SystemBackupService.cs`, `Sessions/LocalPasswordService.cs` (ADR 0120), `Settings/OsSettingsDocumentRegistry.cs`, `Policy/AppPermissionAdministration.cs` (`ApplyMachineDecisionAsync`, `ApplyPolicyAsync`) |
| Ecosystem host | `Administration/BrowserSystemPowerDevice.cs` (`location.reload`, deletion of the IndexedDB database), `Administration/BrowserSystemBackupDevice.cs` (export, download, host picker), `Administration/IndexedDbStorageInspector.cs` (adapter for `IndexedDbInspectorService`) |
| Blazor SDK | `Interop/BrowserClipboard.cs` and `wwwroot/clipboard.js` (host clipboard), `[Obsolete]` since ADR 0065 then removed when SDK 1.0 was frozen (ADR 0114): Diagnostics copies through `KazoClipboard`, which the OS copies over to the host ([`clipboard.md`](../dev/clipboard.md)) |

`AppExecutionContextFactory` receives a `SystemAdministrationGatewayServices`
(lazy resolutions; `Passwords` is an `init` property added in 1.1) and creates, for each instance, the seven gateways
with the app's capabilities and principal. `AdministratorGate` calls
the OS authentication agent
([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)): the app never
sees the password, root through `sudo` goes through without a prompt, and the OS then
acts by itself.

## API

### OS documents

```csharp
await using KazoSettingsDocument appearance = await KazoSettings.OpenSystemAsync(AppContext, "appearance");
await appearance.SetAsync("accent", "cyan");                               // couche utilisateur
await appearance.SetManyAsync(values, SettingLayer.System);                // /etc : une seule approbation
```

See [`settings-files.md`](settings-files.md#os-documents-kazosettingsopensystemasync-adr-0056-8).

### Permissions (`permissions.manage`)

| Method | Effect | Administrator |
|---|---|---|
| `GetPolicyAsync`, `GetOverviewAsync` | policy and state of each dangerous capability of each app | no |
| `SetPolicyAsync(Prompt / Block)` | writes `/etc/kazoos/security.config`, log `capability.policy` | yes (`permissions.policy`) |
| `SetMachineDecisionAsync(app, capability, Allow / Deny / Ask)` | writes `/etc/kazoos/app-permissions.config`, log `capability.approve` / `capability.revoke`; `Allow` also removes the session user's own denial | yes (`permissions.machine-decision`) |
| `RemoveOwnDenialAsync(app, capability)` | removes the user's denial, the question comes back at launch | no |

A system app or a capability not declared by the app is refused **before**
any credentials request.

### Server (`server.connection.manage`)

`GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `DisconnectAsync`,
`SyncAsync(domain, direction)`, `GetConflictsAsync`. Domains: `FileSystem`
(push, pull), `Grants` (pull only: `ArgumentException` otherwise),
`FileAssociations` (push, pull). With no server configured: `NotConnected`, and
synchronization does nothing. Network errors become
`KazoServerConnectionException`.

`GetRelayPolicyAsync` returns the network relay policy of the connected server
(`KazoServerRelayPolicy`: ports, private ranges, quotas, open streams, granted
apps, warnings), or `null` without a server; Settings displays it
in its server section ([`server-network-relay.md`](../dev/server-network-relay.md)).

ADR 0101 (work item `v3/9-sync`): `GetConflictsAsync` reads the pending conflicts
recorded in `~/.local/state/kazoos/sync-conflicts.json`
(`KazoSyncConflict` carries `DetectedAt`); `ShowConflictsAsync` opens the OS
conflict dialog; `GetSyncStatusAsync` returns a `KazoSyncStatus`
(periodic sync active, paused, online, last run, sent,
received, last error, interval); `SetSyncPausedAsync` pauses or resumes
the user's sync (`~/.config/kazoos/sync.config`);
`SyncNowAsync` launches a full run. Deletions, conflicts and
periodic sync are described in
[`synchronization.md`](../dev/synchronization.md).

### Power (`system.power`)

`KazoSystem.RebootAsync` and `KazoSystem.FactoryResetAsync` always ask for
an administrator (`system.reboot`, `system.factory-reset`). Sequence of
`SystemPowerService`, like a `shutdown`:

1. audit entry;
2. log flush;
3. session closing (`ISessionService.ShutdownAsync`);
4. for the reset, storage erasure;
5. host restart.

### Storage (`system.storage.inspect`)

`KazoStorageDiagnostics.GetSnapshotAsync` returns a `KazoStorageSnapshot`
(stores, record count, JSON, resolved paths, links) and
`ToExportJson()`. The snapshot bypasses file permissions: that is
why Diagnostics, a non-system app, asks for an administrator on its first
launch.

`GetWebSiteDatabasesAsync` lists the sites' IndexedDB databases (`kazoweb.<id>`:
version, stores, record count, estimated size) and
`DeleteWebSiteDatabaseAsync(name)` deletes one, with the same capability
(ADR 0088 §4, [`asp-kazo-site-data.md`](asp-kazo-site-data.md)). Diagnostics shows them
under *Website databases*.

### Browser storage (`KazoStorage`, ADR 0250)

`KazoStorage` (`Shared/Kazo.Os.AppSdk/Storage/`) tells about the storage of
the page's origin, which Kazo's Files (IndexedDB), the OS caches and the
features' OPFS share:

- `GetEstimateAsync`: usage, quota, `IsPersisted`, `IsLowSpace` (thresholds
  of ADR 0099), the part of each kind of storage when the browser gives it
  (`IndexedDbBytes`, `CacheBytes`, `FileSystemBytes`, Chromium only), the
  space the calling feature declared and `PersistenceLost`. No capability.
- `RequestPersistenceAsync`: `navigator.storage.persist()`, **once per
  origin** (`OriginStorageService` does not ask again when `persisted()` is
  true). For features, their entry points and system apps only; an ordinary
  app gets `KazoUnauthorizedAccessException` (`manifest.feature.only`). Call
  it right after a gesture of the user.
- `RegisterMarkerAsync(path)` and `ReportUsageAsync(bytes)`: a feature
  registers a marker file of its OPFS and the space it uses, kept by the OS in
  `/var/lib/kazoos/storage/features.json`.
- `GetFeatureUsageAsync`: the features and their declared space (Settings,
  `df`).
- `Changed`: `Evicted` when the marker is gone from the OPFS, delivered to
  the feature as soon as it subscribes at its next launch.

At boot, `StorageEvictionDetector` compares, **before** the databases are
opened, the `kazoos.storage.marker` marker of `localStorage` with the
existence of the `kazoos` database: a marker without the database is an
eviction. The first-run screen says so (date of the last boot, restoring a
backup), `kern.log` and `syslog` get `storage.evicted`, then the successful
boot writes the marker again. The features' markers are checked next; a
marker gone gives the user a notification and the feature `Changed(Evicted)`.

Settings ▸ System ▸ Storage (`StoragePanel`) shows usage, quota, the parts,
the "protected" state and the "Protect the storage against erasure" button,
and each feature's space. `df` adds a `/dev/feature/<id>` line per feature
that declared its space. The 5 % reserve still counts the whole origin: the
features' disks count in the space of the Files.

### Backup (`system.backup`)

`KazoSystemBackup.CreateAsync` downloads a `.kosbak` archive of the whole
machine to the host computer; `RestoreAsync(Merge | Replace)` opens the
host's picker, validates the archive, **then** asks for an administrator,
closes the session, restores and restarts. Creation and restoration are logged in
`/var/log/auth.log`. Details: [`system-backup.md`](../dev/system-backup.md).

### Screens (`screens.manage`)

`KazoScreens` (ADR 0184) lists, opens, restores, closes and identifies the
session's secondary screens, puts the calling window's screen in full screen
and asks for the `window-management` permission. A normal capability, with no
administrator: the browser already confines each call to the user's click.
`IsSupportedFor(context)` is false on a host without screens (mobile, tests).
Host side: `ScreenControl` over `ScreenService`
([`screens-settings.md`](../dev/screens-settings.md)).

### Accounts and passwords (`users.manage`)

`KazoUserAccounts.ListAsync` and `ChangePasswordAsync` (one's own password,
after the current password) require no capability;
`ResetPasswordAsync` and `RemovePasswordAsync` require `users.manage` and an
administrator (actions `users.password-reset` and `users.password-delete`).
The target account's keyring is set aside. Details:
[`sensitive-settings.md`](../dev/sensitive-settings.md#4-passwords-passwd-and-reset)
and [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md).

`CreateAsync(new KazoNewUserAccount(login, nomAffiché, autorité, motDePasse))`
and `DeleteAsync(login, removeHome)` also require `users.manage` and an
administrator (actions `users.add` and `users.delete`, ADR 0169). Creation
gives the account its own group, and `administrators` for an administrator;
the home folder is born at first sign-in. Deletion refuses the
session's account and the last active administrator, takes with it the account's
passkeys and, with `removeHome`, its `/home/<login>` folder.
`ValidateNewAccount` checks an account before sending (login name, display
name of at most 128 characters, password). `KazoUserAccount.PasskeyCount`
gives the number of passkeys of each account.

Session passkeys (ADR 0168), without a capability: `ListPasskeysAsync`,
`AddPasskeyAsync(name)` (the browser asks for the user's presence;
`OperationCanceledException` if the request is closed, `NotSupportedException`
without WebAuthn) and `RemovePasskeyAsync(id)`. `KazoPasskey.UnlocksKeyring` says whether
the key opens the keyring. Details: [`passkeys.md`](../dev/passkeys.md).

### Roles of the features (ADR 0247, adjusted by ADR 0283)

A feature declares roles (`FeatureDefinition.Roles`, `FeatureRole(id, title)`
with `Description` and `Grants`), each `<prefix>:<name>`, the prefix being the
feature's `CapabilityPrefix`, its unique short name (`x86:vm-manager`,
[ADR 0283](../dev/adr/0283-feature-roles-isinrole-adjustments.md)); an entry point marked `RequiresRole` opens only to a user
who holds the role. The manifest carries them (`roles`, `requiresRole`); the
validator refuses a role outside a feature, under another prefix than its own,
without a title,
or a `requiresRole` no role of the feature declares.

- **Granting**: `KazoUserAccounts.ListRolesAsync()` lists the declared roles
  (`KazoFeatureRole`); `GrantRoleAsync(login, role)` and
  `RevokeRoleAsync(login, role)` need `users.manage` and an administrator
  (actions `users.role-grant` and `users.role-revoke`). `KazoUserAccount.Roles`
  gives the roles of an account. The grant is written to
  `/etc/kazoos/roles.config` (`[roles]`, `x86:vm-admin=<id>,<id>`, stable ids,
  never names). Settings ▸ Users shows one column per role (panel
  `FeatureRolesPanel`); `sudo kazorole grant|revoke <user> <role>` does the
  same, `kazorole list` lists the roles and their holders.
- **Administrator** (answer 233 B): holds every role implicitly, with no line
  in `roles.config`; the screen says "implicit (administrator)" and
  `kazorole grant` on one changes nothing (exit 0). An account demoted to
  `User` loses these implicit roles; root under `sudo` holds them all.
- **Reading**: `KazoUser.Current.IsInRole(role)` (or `KazoUser.IsInRole(role)`)
  and `IAppExecutionContext.Roles`. A role of the OS has a simple name: `admin`
  (`KazoRoles.Administrator`), true for an administrator and root; a role of a
  feature is `<Feature>:<role>`, true for its holder and for every
  administrator. A subcommand of a command open to all (`x86 admin`) checks its
  role with `IsInRole`.
  An entry point without its role is absent from the launcher, the mobile home
  screen and the shell's completion; launching it answers
  `AppLaunchStatus.RoleRequired` (`lifecycle.role.required`), the shell writes
  "role required" and exits with 126, and `feature.role-denied` is written to
  `auth.log`.
- **Sensitive operations**: the role opens the entry point; it is not an
  authorization for each operation. `KazoFeatureAuthentication.AuthenticateAsync(operation, scope)`
  opens the OS's window (never one of the feature):
  `KazoAuthenticationScope.RoleHolder` asks the session user's password again,
  and they must hold the role of the entry point marked `RequiresRole`, or, from
  another entry point of the feature, a role of the feature (the feature checks
  the exact role with `IsInRole`; refused to a service or to a user with no role
  of the feature); `Administrator` asks an
  administrator. The ticket it returns (`KazoAuthenticationTicket`) is valid
  30 s, once, for this feature, this operation and this user:
  `ConsumeAsync(ticket, operation)` refuses it for another operation or another
  user, and returns the ticket the OS issued, whose `Scope` the feature reads
  (never the copy presented). Under `sudo`, no second window. An ordinary app is
  refused. The overload `AuthenticateAsync(operation, scope, subject)` names what
  the operation touches (`KazoEventSubject`, a machine, a program): when the
  ticket is consumed, the OS writes the operation to the user's journal with
  that subject (ADR 0254 §2,
  [`app-log-files.md`](app-log-files.md#user-events-adr-0254)). Details:
  [`sensitive-settings.md`](../dev/sensitive-settings.md#5-roles-of-the-features-and-authentication-of-operations).

## Decisions

- See ADR 0060: `SystemPower` property (not `System`),
  `KazoPermissionDecision.Ask`, system layer written with the caller's
  rights, `SetManyAsync`, host clipboard for Diagnostics
  (the latter is replaced by ADR 0065: `KazoClipboard` and
  `clipboard.write`).
- `IndexedDbInspectorService` stays in Infrastructure.Browser;
  the adapter to the SDK lives in the Ecosystem host.
- The old password path of `AppPermissionAdministration` is kept
  for its tests; apps go through the agent.

## Tests

`Tests/Kazo.Os.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs`:
OS document read and written without a capability, system layer refused to a
simple user then written under root in one edit, unknown document;
required capabilities; agent refusal without a write; machine decision and
policy logged; system app refused before any request; server
absent; `Grants` pull only; restart refused; reset
in the order close, erase, restart; storage inspection;
backup (capability, host without backup, administrator refusal,
picker before administrator, restore order, failure logged at the next
startup).
`Tests/Kazo.Os.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs`:
adding and removing accounts through the gateway (`users.manage` and
administrator), session passkeys without capability or administrator.
`Tests/Kazo.Os.Platform.Core.Tests/Sessions/LocalAccountServiceTests.cs`:
groups, reserved or taken names, deletion with or without home folder, refusal for
oneself and for the last administrator.

`Tests/Kazo.Os.Platform.Core.Tests/Roles/`: roles granted, refused, implicit
for an administrator, lost by a demoted account, read back from
`roles.config`; authentication of operations (ticket bound to the operation,
single use, ordinary app refused, `sudo` without a window).
`Tests/Kazo.Os.Commands.Wave5.Tests/KazoroleCommandTests.cs`: `kazorole`.

## Task list

See [`app-sdk.md`](app-sdk.md#task-list-of-work-item-rebuild4-sdk-system-admin).
