> [Version française](../../../fr/wasm/dev/cloud-drives-ui.md)

# Cloud drives: connect dialog, Settings page and commands

Work item `cloud-drives/5-ui` of the `cloud-drives` series
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md) §11,
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). This document describes
what the user sees and how the system apps reach it through the public SDK.

## Purpose

- In File Explorer, **Connect a network drive…** opens a dialog that offers a
  folder of the computer, the accounts already connected, the configured
  providers (Dropbox, OneDrive) and an S3 compatible storage.
- **Settings ▸ Network ▸ Cloud accounts** lists the accounts: provider, name,
  state, scope, mounted drives, connection date. An account can be reconnected
  and removed there (its drives are unmounted).
- The terminal mounts the same drives: `mount -t dropbox|onedrive|s3`.

## Architecture

| Piece | Role |
| --- | --- |
| `KazoCloudAccounts` / `IAppCloudAccountsGateway` (SDK) | Providers, accounts, connect, reconnect, remove. References only, never a token. |
| `KazoDriveInfo.GetNetworkDriveTypesAsync` / `MountNetworkDriveAsync` (SDK) | Mountable types and mounting a network drive of any type, with `KazoNetworkDriveRequest` (account, root, options, secrets). |
| Capability `cloud-accounts.manage` (dangerous) | Required by the whole accounts gateway (except `CanConnectAsync`) and by mounting any type other than `hostfs`. Declared by File Explorer, Settings and `mount`. |
| `AppCloudAccountsGateway`, `CapabilityCheckedDriveGateway` (Platform.Core) | OS side: check the capability and translate refusals into `KazoCloudAccountException` or .NET exceptions. |
| Remote gateways (WorkerHost) | Commands run in a worker: `drv.types`, `drv.mountnet`, `cld.*` cross the boundary with a `ce` error code. |
| `ConnectNetworkDriveDialog` (File Explorer) | The dialog: choice step, provider step (scope, optional starting folder), S3 form. Full screen below 600 px. |
| `NetworkDrivesSidebar` | Opens the dialog; in `Compact` mode, chips in the mobile explorer's bar. |
| `CloudAccountsPanel` (Settings) | The accounts page. |
| `MountCommand` | The `-t` and `-o` options. |

## The "Connect a network drive" dialog

1. When it opens, it reads the mountable types, then, if there is a cloud
   type, the providers and accounts, and calls `PrepareAsync` (loads the
   callback page before the click).
2. **Choice**: existing accounts (one click mounts), folder of the computer,
   one button per configured provider whose source is present, S3.
3. **Provider**: the user chooses the scope (all the storage or the folder
   reserved to Kazo OS) when the provider offers both, and an optional
   starting folder. "Sign in and mount" calls `ConnectAsync` then
   `MountNetworkDriveAsync` with no `await` before, so the sign-in window
   comes from the click (user gesture).
4. **S3**: endpoint, bucket, region, prefix, addressing (automatic, path,
   virtual host), access key and secret key (masked field). The keys go in
   `Secrets`; the source keeps them in the keyring.
5. An error shows in the dialog with the translated text of
   `CloudDrive.Error.{reason}`. The mounted drive opens in the explorer.

Without a keyring (guest, passkey without PRF), the dialog says so and offers
only the folder of the computer.

## Settings page "Cloud accounts"

- An account that needs a reconnection shows **Reconnect**: `ReconnectAsync`
  in the click, then `KazoDriveInfo.ReconnectAsync` on each of its drives.
- **Remove** asks for a confirmation in the row, then `RemoveAsync` (unmounts
  its drives, revokes the token when the provider allows it).
- The drives of an account come from `KazoDriveInfo.Account`.

## Commands

```
mount -t dropbox|onedrive [-o account=NAME,scope=full|app,root=/Folder] [/mnt/name]
mount -t s3 -o endpoint=URL,bucket=NAME[,region=R,prefix=P,addressing=path|virtual,account=ID] [/mnt/name]
mount -o remount /mnt/name
umount /mnt/name
```

- The account: `account=` (id or name), else the service's only account, else
  a new sign-in (the Enter key is the gesture). With several accounts, `mount`
  names them and asks for `account=`.
- The scope of a new sign-in: `scope=`, required when the provider offers
  both (the user's choice, ADR 0267 §8).
- S3: the keys are asked with the echo off, never on the command line (the
  history keeps it). `account=` reuses the keys of an S3 account.
- util-linux exit codes: 0, 1 (usage), 32 (mount failure).

## Key decisions

- System apps go through the public SDK, behind a dangerous capability
  (ADR 0270, Benoit's answer A).
- `KazoNetworkDriveRequest.ToString()` masks the secrets.
- The dialog does not keep the S3 keys after mounting.

## Tests

- `MountCommandTests`: `-t dropbox|onedrive|s3`, options, accounts, scope,
  keys read with the echo off, refusals.
- `AppCloudAccountsGatewayTests`: capability, references, refusal reasons,
  cloud mount reserved to the capability.
- `NetworkDriveSourceTests`: available types and mounting through the SDK with
  the fake source.
- `WireCodecDriveTests`: the drive's account, request with options and
  secrets, accounts and providers across the worker.
- `SettingsWindowTests`: the page is in the Network group.
- E2E `CloudDrivesUiTests`: dialog (desktop and phone) and Settings page, axe
  with no serious finding, screenshots in `Tests/TestResults/cloud-drives/`. The
  test gives the OS fake Dropbox and OneDrive app ids (by rewriting
  `appsettings.json`) and answers a fake S3 address itself: Dropbox step with
  both scopes, S3 form and the service's refusal shown in the dialog. No
  request leaves the machine.

## Task list

- [x] SDK: cloud accounts, mountable types, mounting a network drive (ADR 0270).
- [x] Capability `cloud-accounts.manage`, OS and worker gateways.
- [x] "Connect a network drive" dialog (desktop and phone).
- [x] Settings page "Cloud accounts".
- [x] `mount -t dropbox|onedrive|s3`, `-o remount`, `umount`.
- [x] French and English texts, axe, screenshots.
- [x] Screenshots of the provider (Dropbox) and S3 steps with the connectors of work items 3, 4 and 6.
