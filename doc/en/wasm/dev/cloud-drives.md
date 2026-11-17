> [Version française](../../../fr/wasm/dev/cloud-drives.md)

# Cloud drives: overview of the series

Entry document of the [`cloud-drives/base`](plans/cloud-drives-plan.md) series
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md),
[ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md),
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). It says what the series
delivers, where the details of each piece are, what was checked, and the real
trials still to do before the series is merged into `main`.

## Purpose

- The user mounts **their** cloud storage as a network drive under `/mnt`:
  Dropbox, OneDrive (personal or work) or S3 compatible storage (AWS S3,
  Cloudflare R2, Backblaze B2, MinIO…).
- Everything happens in the browser. Kazo OS stays a static site: no server
  receives a token or a file, and nothing costs money.
- For Dropbox and OneDrive, the user chooses at sign-in between **all of their
  storage** and **a folder reserved for Kazo OS** (ADR 0267 §8).
- Apps go through the public SDK, behind the dangerous capability
  `cloud-accounts.manage`; they never see a token.

## What the user sees

- **File Explorer ▸ Connect a network drive…**: folder of this computer,
  accounts already connected, Dropbox, OneDrive, S3. After sign-in, the drive
  opens in the explorer.
- **Settings ▸ Network ▸ Cloud accounts**: the accounts, their state and their
  drives; Reconnect, Remove.
- **Terminal**: `mount -t dropbox|onedrive|s3 …`, `umount`, `df`,
  `/proc/mounts`.
- A guest account, or a session opened by a passkey without PRF, has no
  keyring: the window explains it and only offers the computer's folder.

## Switchable connectors (ADR 0282 §5)

The three shipped connectors live in their own assembly,
`Platform/Kazo.Os.Platform.CloudDrives` (package `Kazo.Os.Platform.CloudDrives`),
on the model of resource accounting (ADR 0066). One can be left out at build
time or hidden at run time.

**At build time.** `build/Kazo.Os.CloudDrives.targets`, imported by the three
hosts, reads three MSBuild properties, all `true` by default:

```bash
dotnet publish OS/Kazo.Os.Ecosystem -c Release -p:KazoOsDropboxDrives=false
dotnet publish OS/Kazo.Os.Ecosystem -c Release \
  -p:KazoOsDropboxDrives=false -p:KazoOsOneDriveDrives=false -p:KazoOsS3Drives=false
```

| Property | Constant | Runtime switch |
| --- | --- | --- |
| `KazoOsDropboxDrives` | `KAZOOS_DROPBOX_DRIVES` | `Kazo.Os.CloudDrives.Dropbox.IsSupported` |
| `KazoOsOneDriveDrives` | `KAZOOS_ONEDRIVE_DRIVES` | `Kazo.Os.CloudDrives.OneDrive.IsSupported` |
| `KazoOsS3Drives` | `KAZOOS_S3_DRIVES` | `Kazo.Os.CloudDrives.S3.IsSupported` |

- A connector set to `false` is not composed (`CloudDriveServiceCollectionExtensions`
  in `Kazo.Os.Ecosystem.Host`): it is never offered, and neither is its OAuth
  provider, even when `appsettings.json` has its app id.
- All three `false`: the host no longer references `Kazo.Os.Platform.CloudDrives`
  and the assembly leaves the download (`KAZOOS_CLOUD_DRIVES` undefined).
  `build/publish-trimmed.sh --no-cloud-drives` checks it.
- Each property also becomes a `RuntimeHostConfigurationOption`
  (`CloudDriveFeatures`), read at startup: a connector switched off is ignored
  even when the assembly stays for another one.
- As in ADR 0066, the composition lives in `Kazo.Os.Ecosystem.Host`: set the
  properties in a `Directory.Build.props` or on the command line for the whole
  graph. The `Kazo.Os.Ecosystem.Host` NuGet package is built with the three
  connectors; a host from another repository hides them with the runtime
  setting below.

**At run time**, without publishing again, two lists that add up (the system
file never turns back on a type the host or the build switched off):

```json
"KazoOs": { "CloudDrives": { "Disabled": "dropbox, s3" } }
```

```ini
# /etc/kazoos/cloud-drives.config (system layer)
disabled = onedrive
```

`Disabled` also takes a JSON array (`["dropbox", "s3"]`); types are separated
by commas, semicolons or spaces, case ignored. The file is read again on each
question: a change applies at once. A disabled type is offered nowhere:
`GetNetworkDriveTypesAsync` (Explorer ▸ Connect a network drive…, `mount -t`)
leaves it out, `NetworkDriveService` refuses to mount a new one, and Settings ▸
Cloud accounts no longer lists its provider (`CloudProviderCatalog`). Each
connector is registered behind a `SwitchableDriveSource` (Platform.Core) that
asks.

### Existing drives of a disabled type

A drive mounted before its type was disabled (by the runtime setting, the system file or a publish that leaves the connector out) is not lost:

- at the next login, `NetworkDriveService.RestoreAsync` keeps its line in `~/.local/state/kazoos/network-drives.json` but does not mount it; it shows neither in the Explorer nor in `mount`;
- the line survives rewrites of the table (another drive mounted or unmounted);
- its mount point stays reserved: neither a default name nor an explicit `mount` takes it (`InvalidMountPoint`);
- once the type is enabled again, the drive comes back at the next login, with its keys;
- disabling a type during a session does not unmount drives already mounted: they stay until logout.

Only the three shipped types (`dropbox`, `onedrive`, `s3`) are kept this way; a line of a type no version ever shipped is still dropped, as before. Showing these drives greyed out ("provider missing", ADR 0282 §3) comes with work item 4.

## Map of the documents

| Piece | Work item | Document |
| --- | --- | --- |
| Decisions | 0 | [ADR 0267](adr/0267-cloud-drives-as-network-drives.md), [plan](plans/cloud-drives-plan.md) |
| Network drive sources, quota, revision | 1 | [`network-drive-sources.md`](network-drive-sources.md) |
| Cloud accounts: PKCE, redirect page, sealed tokens, app ids, redirect URIs, public pages | 2 | [`cloud-accounts.md`](cloud-accounts.md), [ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md) |
| Shared connector base, Dropbox, contract suite | 3 | [`cloud-drive-connectors.md`](cloud-drive-connectors.md) |
| OneDrive (Microsoft Graph) | 4 | [`onedrive-drives.md`](onedrive-drives.md) |
| Window, Settings page, commands, SDK | 5 | [`cloud-drives-ui.md`](cloud-drives-ui.md), [ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md) |
| S3 compatible and the CORS rule | 6 | [`s3-drives.md`](s3-drives.md) |
| Series check, real trials | 7 | this document |

## Kazo OS registrations

Benoit registered the apps on 2026-11-15. The ids are public and live in
`OS/Kazo.Os.Ecosystem/wwwroot/appsettings.json`
([`cloud-accounts.md`](cloud-accounts.md#app-ids)):

| Provider | Registration | Scope | Id |
| --- | --- | --- | --- |
| Dropbox | "Kazo Os Drive" (Full Dropbox) | All the storage | `im7y4ma8wtsospi` |
| Dropbox | "Kazo OS Folder" (App folder) | Reserved folder | `mcp8j0gt4h4v6s3` |
| Microsoft Entra | "Kazo OS" (single-page application, tenant `common`) | Both | `7f89ba67-5655-4721-991a-53c64998f45e` |

No secret is used or copied (OAuth with PKCE). The public pages the providers
ask for are `https://os.kazo.ca/privacy.html` and
`https://os.kazo.ca/terms.html`. The "Chooser, Saver and Embedder" section of
the Dropbox console is not used: Kazo OS does not use these JavaScript
components, it calls the v2 API directly.

## Series check

Done on 2026-11-15 on the `cloud-drives/7-final` branch (`cloud-drives/base`
with `main` 3.3.4 merged in). The detailed results are in the work item 7 PR.

- Release build of `Kazo.Os.sln`: 0 errors, 0 warnings.
- Non-browser tests, one suite at a time.
- E2E suites, one at a time.
- `build/publish-trimmed.sh` and `Kazo.Os.TrimmedPublish.E2E.Tests`.
- SDK compatibility: `PublicAPI.*.txt` and `Kazo.Os.SdkCompatibility.Tests`.

No automated test calls a real provider: fake OAuth server, fake Dropbox, fake
Graph and fake S3 in memory.

## Real trials

To be done by Benoit before the series is merged into `main`, on a Kazo OS
that holds the series: a preview served from `cloud-drives/base`, or
`dotnet run` of the Ecosystem project on `localhost:5000`. The redirect URI
follows the page's scheme: `http://` and `https://localhost:5000` are two
distinct URIs, each to register in Entra (single-page application) and in both
Dropbox apps. Each trial records: passed, failed (with the screenshot
and the text of the error), or not done.

### Preparation

1. Sign in with a password (not the guest): cloud accounts need the keyring.
2. Allow pop-ups for the site if the browser blocks them.

### Dropbox, all the storage ("Kazo Os Drive")

1. Explorer ▸ Connect a network drive ▸ Dropbox ▸ "All of my Dropbox storage"
   ▸ Sign in and mount. The Dropbox window opens, then closes by itself after
   approval.
2. The drive opens and shows the account's folders.
3. Open a text file, change it in the Text Editor, save; check the change on
   dropbox.com.
4. Create a folder, rename a file, delete a file (confirmation asked); check
   on dropbox.com.
5. Copy a file of about 100 MiB to the drive: it arrives whole. A file larger
   than 128 MiB (the OS limit, ADR 0245) is refused with a clear message.
6. Change a file on dropbox.com while it is open, then save in Kazo OS: a
   conflict is reported, nothing is overwritten.
7. Sign out, sign in again: the drive comes back with no new sign-in.
8. Settings ▸ Network ▸ Cloud accounts ▸ Remove: the drive goes away; on
   dropbox.com, Settings ▸ Connected apps no longer lists "Kazo Os Drive".

### Dropbox, reserved folder ("Kazo OS Folder")

1. Same sign-in with "A folder reserved for Kazo OS".
2. The drive starts empty; a file created there shows on dropbox.com in
   `Apps/Kazo OS Folder` (the folder carries the app's name).
3. The account's other files are not visible.
4. Both accounts (all the storage and reserved folder) can be mounted at the
   same time on two drives.

### OneDrive personal (Microsoft account)

1. Connect OneDrive, "All of my OneDrive storage", with an outlook.com or
   hotmail.com account. Microsoft offers to pick the account.
2. Dropbox trials 2 to 7; the file of about 100 MiB goes through a chunked
   upload session.
3. Again with the reserved folder: a file created there shows in
   `Apps/Kazo OS` on onedrive.live.com.
4. **After 24 hours**: the account turns "needs reconnect"; Reconnect in
   Settings opens Microsoft's window and the drive comes back with no other
   step.

### OneDrive work or school (Microsoft Entra)

1. Same sign-in with an organization account.
2. If the organization requires an administrator's consent, record
   Microsoft's exact message (expected result: a clean refusal, not a Kazo OS
   error).
3. Otherwise, OneDrive personal trials 2 to 4.

### Installed PWA on iOS

1. On iPhone or iPad, Safari ▸ the preview's address ▸ Share ▸ Add to Home
   Screen, then open Kazo OS from the icon.
2. Connect Dropbox: the sign-in window opens and returns into the PWA (not
   into Safari); the drive mounts.
3. Same trial with OneDrive.
4. Close the PWA, open it again: the drive comes back.
5. If the sign-in returns into Safari instead of the PWA, record the iOS
   version: it is the known risk of ADR 0267, and another redirect page will
   be needed.

### S3 compatible (optional)

With a bucket (Cloudflare R2 has a free tier): put the CORS rule of
[`s3-drives.md`](s3-drives.md#the-cors-rule-to-put-on-the-bucket) on it, then
connect, read, write, delete.

## After the trials

- Dropbox: ask for production of each app before 50 users (free review, an
  icon and a description are asked).
- Microsoft: publisher verification (Microsoft Partner Network) removes the
  "unverified publisher" warning from the consent screen; it is not required
  to work.
- The series goes to `main` when Benoit decides.

## Known limits

- OneDrive: a "single-page application" registration receives a 24-hour
  refresh token; the user signs in again each day (Microsoft's rule, with no
  workaround without a server).
- No offline cache (Benoit's decision, question 6 of the evaluation).
- No Google Drive or WebDAV in this version (ADR 0267 §7).

## Work item 7 task list

- [x] Merge `main` (3.3.4) into the work item branch.
- [x] App ids in `appsettings.json` (PR #545).
- [x] `privacy.html` and `terms.html` pages (PR #547).
- [x] Release build of the solution.
- [x] Tests, E2E suites, trimmed publish, SDK compatibility.
- [x] This document, the indexes and `features.md`.
- [ ] Real trials by Benoit (above).
