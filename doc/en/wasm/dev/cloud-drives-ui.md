> [Version française](../../../fr/wasm/dev/cloud-drives-ui.md)

# Cloud drives: connect dialog, Settings page and commands

Work item `cloud-drives/5-ui` of the `cloud-drives` series
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md) §11,
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). This document describes
what the user sees and how the system apps reach it through the public SDK.
Since work item 6 of the `drive-providers` series
([ADR 0282](adr/0282-third-party-drive-providers.md) §5), the interface no
longer names any service in its code: it reads the description of each type
the OS gives it (section "Types described by the OS").

## Purpose

- In File Explorer, **Connect a network drive…** opens a dialog that offers a
  folder of the computer, the accounts already connected, and the services
  the OS describes: those of Kazo OS (Dropbox, OneDrive, S3) and those a
  Feature provides, always with "Provided by <Feature>".
- **Settings ▸ Network ▸ Cloud accounts** lists the accounts: provider, name,
  state, scope, mounted drives, connection date. An account can be reconnected
  and removed there (its drives are unmounted).
- The terminal mounts the same drives: `mount -t <type>` for any declared
  type.
- A drive whose provider is missing stays in the explorer, greyed, with the
  reason and, for a missing Feature, **Install the provider**.

## Architecture

| Piece | Role |
| --- | --- |
| `KazoCloudAccounts` / `IAppCloudAccountsGateway` (SDK) | Providers, accounts, connect, reconnect, remove. References only, never a token. |
| `KazoDriveInfo.GetNetworkDriveTypesAsync` / `MountNetworkDriveAsync` (SDK) | Mountable types and mounting a network drive of any type, with `KazoNetworkDriveRequest` (account, root, options, secrets). |
| `KazoDriveInfo.DescribeNetworkDriveTypesAsync` (SDK) | The description of each type: name, icon, connection form (folder of the computer, OAuth with its scopes, form with its fields) and the Feature that provides it (`KazoNetworkDriveTypeInfo`). |
| `KazoDriveInfo.GetProviderMissingDrivesAsync` (SDK) | The drives kept without a provider, with `ProviderMissing` (removed, disabled, disabled by the administrator) and `ProviderFeatureId`. |
| `IDescribedNetworkDriveSource` (Platform.Core) | A source describes itself in the caller's language: `HostFolderDriveSource`, `CloudDriveSource` (Dropbox, OneDrive), `S3DriveSource`, `FeatureDriveSource`. |
| Capability `cloud-accounts.manage` (dangerous) | Required by the whole accounts gateway (except `CanConnectAsync`) and by mounting any type other than `hostfs`. Declared by File Explorer, Settings and `mount`. |
| `AppCloudAccountsGateway`, `CapabilityCheckedDriveGateway` (Platform.Core) | OS side: check the capability and translate refusals into `KazoCloudAccountException` or .NET exceptions. |
| Remote gateways (WorkerHost) | Commands run in a worker: `drv.types`, `drv.describe`, `drv.missing`, `drv.mountnet`, `cld.*` cross the boundary with a `ce` error code. |
| `ConnectNetworkDriveDialog` (File Explorer) | The dialog: choice step, OAuth step (scope, optional starting folder), generic form. Full screen below 600 px. |
| `NetworkDrivesSidebar` | Opens the dialog; in `Compact` mode, chips in the mobile explorer's bar. |
| `CloudAccountsPanel` (Settings) | The accounts page. |
| `MountCommand` | The `-t` and `-o` options. |

## The "Connect a network drive" dialog

1. When it opens, it reads the description of the types
   (`DescribeNetworkDriveTypesAsync`), then, if there is a cloud type, the
   accounts, and calls `PrepareAsync` (loads the callback page before the
   click).
2. **Choice**: existing accounts (one click mounts), then one button per
   described type: the folder of the computer first, the services of Kazo OS,
   then those of Features, each with its icon and, for a Feature, "Provided by
   <name> (<id>)".
3. **Provider**: the user chooses the scope (all the storage or the folder
   reserved to Kazo OS) when the provider offers both, and an optional
   starting folder. With the reserved folder, the field is preceded by where
   the provider puts that folder in the storage (`/Apps/Kazo OS Folder/` for
   Dropbox, `/Apps/Kazo OS/` for OneDrive, `KazoCloudProvider.AppFolderPath`):
   the starting folder is relative to it. "Sign in and mount" calls `ConnectAsync` then
   `MountNetworkDriveAsync` with no `await` before, so the sign-in window
   comes from the click (user gesture). While the sign-in window is open,
   the dialog says so and Back, Close and Escape stay usable: the OS cannot
   see that window close (COOP, ADR 0131) and a provider error page never
   comes back, so leaving cancels the wait (`ConnectAsync`'s token) and drops
   its late result, with no error shown.
4. **Form** (S3 and any type declared with a form): the fields come from the
   description, in the declared order: text, address, number, list of
   choices, secret (masked). An optional field carries "(optional)". Secret
   values go in `Secrets`, the others in `Options`; the OS checks them against
   the declaration before creating the account (`FeatureDriveForm.Check`) and
   keeps the secrets in the keyring.
   Under a field marked as the server address (`IsServerAddress`,
   [ADR 0284](adr/0284-drive-provider-hosts-and-typed-server-address.md) §2),
   the dialog writes the host the provider will be allowed to reach, before
   saving: "This provider will reach dav.example.com.". An address the OS
   cannot allow (`http`, a user name or password in the URL, an IP address,
   `localhost`) is refused with the reason, and **Mount** stays disabled.
5. An error shows in the dialog with the translated text of
   `CloudDrive.Error.{reason}`. The mounted drive opens in the explorer.

Without a keyring (guest, passkey without PRF), the dialog says so and offers
only the folder of the computer.

## The explorer's "Network drives" section

Each cloud drive carries the icon its type's description gives
(`KazoNetworkDriveTypeInfo.Icon`, written `library:name`, or a Feature's
`IconUrl` image): the Dropbox mark; Microsoft for OneDrive, which has no glyph
of its own in the icon fonts; a database for S3, from Bootstrap Icons (MIT)
and Lucide, already shipped by the SDK. Under the account's
name, a second line gives the service and, when there is one, the starting
folder (`KazoDriveInfo.Root`): two accounts often carry the same e-mail
address. On a phone, the chips keep the icon and their accessible name carries
the same second line.

### Drive whose provider is missing

`GetProviderMissingDrivesAsync` gives the rows the OS keeps without mounting
them (ADR 0282 §3, §5). The explorer shows them greyed, with a crossed-out
cloud icon, the reason on the second line and the eject button to forget the
drive:

| Reason (`KazoDriveProviderMissingReason`) | Text | Action |
| --- | --- | --- |
| `Removed` (Feature not installed) | "Provider not installed" | **Install the provider**: the Store opens on the Feature (`--app=<id>`); the drive comes back by itself once it is installed. |
| `Disabled` (Feature turned off) | "Provider disabled" | None (`aria-disabled`). |
| `DisabledByAdministrator` (shipped type turned off by `KazoOs:CloudDrives:Disabled` or `/etc/kazoos/cloud-drives.config`) | "Disabled by the administrator" | None. |

The drive keeps the id of the Feature that provided it: only that Feature
mounts it again (Benoit's answer A to work item 4). Another Feature declaring
the same type does not take it: the drive stays "not installed" with the
original id. On a phone, the same drives are greyed chips.

## Types described by the OS (ADR 0282 §5)

`KazoNetworkDriveTypeInfo` carries, for each type offered now:

- `Type`, `DisplayName` (in the caller's language for the OS types, as the
  Feature writes it for the others), `Icon` or `IconUrl`;
- `Connection`: `HostFolder`, `OAuth` (with `Scopes` and `AppFolderPath`) or
  `Form` (with `Fields`: name, label, kind, required, default value,
  example, choices, server address);
- `ProviderFeatureId`, `ProviderFeatureName` and `IsThirdParty` for a type a
  Feature provides.

An OAuth type for which the OS has no app id is not described (it cannot
connect). A gateway that does not know the method yet (older SDK) describes
each type by its name alone.

## Settings page "Cloud accounts"

- The service's name comes from the type's description; a service provided by
  a Feature shows "Provided by <name> (<id>)". The scope and **Reconnect**
  only show for an OAuth type.

- An account that needs a reconnection shows **Reconnect**: `ReconnectAsync`
  in the click, then `KazoDriveInfo.ReconnectAsync` on each of its drives.
- **Remove** asks for a confirmation in the row, then `RemoveAsync` (unmounts
  its drives, revokes the token when the provider allows it).
- The drives of an account come from `KazoDriveInfo.Account`.

## Commands

```
mount -t TYPE [-o account=NAME,scope=full|app,root=/Folder] [/mnt/name]   # OAuth type (dropbox, onedrive…)
mount -t TYPE -o FIELD=VALUE,…[,account=ID] [/mnt/name]                  # form type (s3, webdav…)
mount -o remount /mnt/name
umount /mnt/name
```

`mount` reads the type's description: it knows no service. For a form type,
`-o` carries the declared non-secret fields; a usage error gives the type's
syntax (`mount -t s3 -o endpoint=…,bucket=…[,region=…,prefix=…,addressing=path|virtual,account=ID]`).

- The account: `account=` (id or name), else the service's only account, else
  a new sign-in (the Enter key is the gesture). With several accounts, `mount`
  names them and asks for `account=`.
- The scope of a new sign-in: `scope=`, required when the provider offers
  both (the user's choice, ADR 0267 §8).
- Form: each secret field is asked with the echo off, by its label. A secret
  field written in `-o` is refused (code 1), since the history keeps the
  command line. `account=` reuses the values of an account. For S3, the
  access key id is now secret too.
- util-linux exit codes: 0, 1 (usage), 32 (mount failure).

## Key decisions

- System apps go through the public SDK, behind a dangerous capability
  (ADR 0270, Benoit's answer A).
- `KazoNetworkDriveRequest.ToString()` masks the secrets.
- The dialog does not keep the secret values after mounting.
- No service name in the code of File Explorer, Settings and `mount`
  (ADR 0282 §5): everything comes from the description.
- The services' marks (Dropbox, Microsoft) come from the icon fonts already
  shipped; Benoit allowed it on 2026-11-15 (the user signs in to their own
  account of that service).

## Tests

- `MountCommandTests`: `-t dropbox|onedrive|s3` and a Feature type
  (`webdav`), options, accounts, scope, secrets read with the echo off, secret
  refused on the command line, the type's syntax.
- `FeatureDriveFormTests`: description of a Feature type, values checked
  against the declaration, account created before the drive.
- `FeatureDriveMountTests`: description and reason of a drive without a
  provider (disabled, removed, another Feature of the same type).
- `S3DriveSourceTests`: description of S3 (fields, secrets, language).
- `AppCloudAccountsGatewayTests`: capability, references, refusal reasons,
  cloud mount reserved to the capability.
- `NetworkDriveSourceTests`: available types and mounting through the SDK with
  the fake source.
- `WireCodecDriveTests`: the drive's account, request with options and
  secrets, accounts and providers, type descriptions and drives without a
  provider across the worker.
- `SettingsWindowTests`: the page is in the Network group.
- E2E `CloudDrivesUiTests`: dialog (desktop and phone) and Settings page, axe
  with no serious finding, screenshots in `Tests/TestResults/cloud-drives/`. The
  test gives the OS fake Dropbox and OneDrive app ids (by rewriting
  `appsettings.json`) and answers a fake S3 address itself: Dropbox step with
  both scopes, S3 form and the service's refusal shown in the dialog. No
  request leaves the machine. A second test writes the drive list from the
  terminal (an S3 drive turned off by the administrator, a drive of a missing
  Feature), signs in again and checks the greyed drives on desktop and phone.

## Task list

- [x] SDK: cloud accounts, mountable types, mounting a network drive (ADR 0270).
- [x] Capability `cloud-accounts.manage`, OS and worker gateways.
- [x] "Connect a network drive" dialog (desktop and phone).
- [x] Settings page "Cloud accounts".
- [x] `mount -t dropbox|onedrive|s3`, `-o remount`, `umount`.
- [x] French and English texts, axe, screenshots.
- [x] Screenshots of the provider (Dropbox) and S3 steps with the connectors of work items 3, 4 and 6.
- [x] Work item 7: leaving a failed sign-in, service mark and starting folder in "Network drives", reserved folder path before the starting folder.
- [x] Work item 6 of `drive-providers`: types described by the OS, generic form (S3 included), "Provided by", `mount -t` for any type, greyed drive without a provider, screenshots in `/mnt/project-files/drive-providers/ui/`.
