> [Version française](../../../../fr/wasm/dev/adr/0267-cloud-drives-as-network-drives.md)

# ADR 0267: the user's cloud storage mounted as a network drive

## Status

Accepted on 2026-11-13. Benoit answered the six questions asked in the thread
(see the end of the document). Series `cloud-drives/base`, plan in
[`cloud-drives-plan.md`](../plans/cloud-drives-plan.md). Nothing is coded.

- **Builds on:** [ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  (network drives, `IHostFolder`, `NetworkDriveService`),
  [ADR 0185](0185-file-explorer-improvements.md) (watched drives),
  [ADR 0231](0231-network-drives-count-as-personal-files.md) (app access),
  [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md) (keyring),
  [ADR 0131](0131-cross-origin-isolation.md) (cross-origin isolation) and
  [ADR 0245](0245-raise-the-maximum-file-size.md) (128 MiB).
- **Changes no ADR.** The rules of ADR 0181 apply to cloud drives unless stated
  otherwise below.

## Context

Benoit wants the user to plug **their own** storage (Dropbox, OneDrive, Google
Drive…) into the file system, with no server to maintain and no cost: Kazo OS
is a static site (os.kazo.ca). An app registered with a provider is acceptable
when it is free.

Checked on 2026-11-13, with no real account, from Chromium on a page served
with `COOP: same-origin` and `COEP: credentialless` as Kazo OS is:

- The file and token APIs of Dropbox, Google and Microsoft Graph accept CORS
  requests from an isolated page; Box refuses them.
- **Dropbox**: OAuth code flow with PKCE and no secret, a refresh token with no
  expiry. Free; a free review is asked at the 50th user.
- **OneDrive**: OAuth code flow with PKCE and a return address of the "SPA"
  type. The refresh token lasts 24 hours and cannot be extended. Free.
- **Google Drive**: the code exchange needs a secret, so a server; without a
  server only the implicit flow is left (one hour, no refresh). The `drive`
  scope is restricted: verification and a paid security assessment every year.
  The free `drive.file` scope shows only the files the app created.
- **WebDAV / Nextcloud**: no CORS headers by default.
- **S3 compatible**: works with the user's keys and a CORS rule they set on
  their bucket; no app to register.
- With `COOP: same-origin`, a window opened to a provider loses its link to the
  OS (`window.opener`): the providers' window-based sign-in libraries do not
  work as they are.

The OS already has the network drives of ADR 0181: mounting at run time under
`/mnt`, the "disconnected" state and "Reconnect", permanent delete, the
explorer sidebar. `HostFolderFileSystemProvider` makes a file system out of any
`IHostFolder`.

## Decision

### 1. A cloud drive is a network drive

A cloud storage is mounted like an ADR 0181 drive, through a new `IHostFolder`.
Everything ADR 0181 decides stays true: mounting on a direct child of `/mnt`,
owned by the user who mounted, modes `0700` and `0600`, `chmod` refused,
protected root, permanent delete after confirmation, outside synchronization
and backup, unmounted when the user signs out. App access follows ADR 0231. No
new capability.

### 2. Connectors built into the OS (decided by Benoit)

Connectors ship in the OS, not as Store apps. Nothing is active until the user
connects an account. An SDK contract that would let a Store app provide a file
system stays possible later, through another ADR, on the interfaces of this
ADR.

- A connector is written in C# with `HttpClient` (which becomes `fetch` in the
  browser), in a platform library with no browser dependency, testable with a
  fake HTTP handler.
- The only JavaScript is the sign-in window (section 5), in
  Infrastructure.Browser.

### 3. Drive sources

`NetworkDriveService` only knows the browser's folder dialog today. It receives
a list of **drive sources**, one per type: `hostfs` (the existing one),
`dropbox`, `onedrive`, `s3`. A source can tell whether it is available, create
a drive (with what it asks the user), reopen it when the user signs in, and
forget it.

- The table `~/.local/state/kazoos/network-drives.json` gains the drive type
  (absent = `hostfs`) and, for a cloud drive, the account reference and the
  chosen root. It stays device-local.
- `DriveFormat` and `/proc/mounts` show the type:
  `dropbox:<account> /mnt/Dropbox dropbox rw,nosuid,nodev 0 0`.
- Default mount point: `/mnt/Dropbox`, `/mnt/OneDrive`, `/mnt/<bucket>`, then
  `-2`, `-3`… when the name is taken.

### 4. Optional interfaces of a folder

An `IHostFolder` may also offer:

- **the quota**: total size and free space, for `df` and `KazoDriveInfo` (a
  folder of the computer keeps showing `-`);
- **the service's revision** (Dropbox `rev`, Graph `eTag`, S3 `ETag`) instead
  of the revision derived from date and size; a write with an expected revision
  is sent to the service with that condition, and a refusal by the service
  becomes the revision conflict the OS already knows;
- **remote changes**, through the `IObservableHostFolder` interface of ADR 0185
  when the service allows it; otherwise the existing timed re-read applies.

### 5. Cloud accounts and sign-in

A cloud account service handles sign-in, tokens and their renewal.

- **Flow**: OAuth 2.0 code flow with PKCE (S256) and `state`, no secret.
- **Window**: the OS opens the provider in a browser window, after a user
  gesture. The provider comes back to a **static Kazo OS page**, served by the
  Infrastructure.Browser library (`_content/…/oauth-callback.html`), which
  hands the code to the OS through `BroadcastChannel` and closes. The hosts are
  not changed. Redirecting the whole page is forbidden: it would restart the
  OS.
- **Tokens**: in the user's keyring (ADR 0100), encrypted and unlocked when
  they sign in. The account list
  (`~/.local/state/kazoos/cloud-accounts.json`, `0600`) keeps references only.
  Nothing is synchronized or backed up. Apps never receive a token: they go
  through the file service.
- **Removing an account** unmounts its drives, revokes the token when the
  provider allows it, and erases it from the keyring.
- **App identifiers**: public, in the host settings (`appsettings.json`),
  replaceable by a file under `/etc/kazoos/` for an OS hosted at another
  address. A provider with no identifier is not offered.
- If the guest account has no keyring, connecting a cloud account is refused
  for it with a clear message; work item 2 checks this.

### 6. Providers of the first version (decided by Benoit)

**Dropbox, OneDrive and S3 compatible.**

- **Dropbox**: API v2. The token renews without the user.
- **OneDrive**: Microsoft Graph. After 24 hours the drive becomes
  "disconnected"; "Reconnect" opens the Microsoft window again. Content is
  downloaded through the `@microsoft.graph.downloadUrl` address, not
  `/content`.
- **S3 compatible** (Cloudflare R2, Backblaze B2, MinIO, AWS): the user types
  the address, the region, the bucket, an optional prefix and their keys;
  requests are signed (SigV4) in the OS; the keys go into the keyring. A guide
  describes the CORS rule to set on the bucket.

### 7. Google Drive, WebDAV and the others (decided by Benoit)

**Not in the first version.** Google Drive would need a server or a paid
assessment for a complete result. For those services the user mounts the local
folder their client keeps in sync (ADR 0181, desktop Chrome and Edge). We come
back to it with a new ADR if the rules change.

### 8. Scope of access (decided by Benoit: the user chooses)

When connecting, the user chooses between **their whole storage** and **a
folder reserved for Kazo OS**.

- **Dropbox** fixes the scope per app: two registered apps, "Full Dropbox" and
  "App folder" (`Apps/Kazo OS`).
- **OneDrive**: one registration; the OS asks for `Files.ReadWrite` or
  `Files.ReadWrite.AppFolder`.
- **S3**: the bucket and prefix typed are the scope.

The choice shows in the account list. Changing it means reconnecting the
account.

### 9. Offline (decided by Benoit)

**No cache.** Without the network, or when the token is no longer valid, the
drive is "disconnected": it stays visible and every operation answers
`ProviderUnavailable`, like an ADR 0181 drive without permission. It
reconnects by itself when the network returns if the token is still valid.

### 10. Limits

- A file is read and written whole in memory: the 128 MiB limit of ADR 0245
  applies. A larger file shows in the listing; reading it fails with the
  existing size error.
- Names Kazo OS refuses are ignored, as for a folder of the computer.
- Copying a folder is done file by file; an interrupted copy leaves a partial
  copy.

### 11. Interface

- **Explorer**: "Connect a network drive…" offers the folder of the computer
  (when the browser has it) and the configured providers. The mobile explorer
  gains the same entry: cloud drives work in every browser.
- **Settings**: a page lists the cloud accounts, their scope and their drives,
  and removes them.
- **Terminal**: `mount -t dropbox|onedrive|s3 [point]`, `mount -o remount`,
  `umount` and `df`, with the exit codes of ADR 0181.

## Questions asked to Benoit

Asked in the thread on 2026-11-13. Answer: the recommendation for all of them,
except question 4.

1. Providers of the first version: **Dropbox, OneDrive and S3** (recommended);
   Dropbox alone; everything, including limited Google Drive and WebDAV.
2. Google Drive: **not in the first version** (recommended); free limited
   mode; pay the yearly assessment; advanced mode where the user creates their
   own Google app.
3. Connectors: **built into the OS** (recommended); an SDK contract, then
   Store apps.
4. Scope: the whole storage (recommended); app folder only; **the user chooses
   when connecting** (Benoit's choice).
5. Who registers the apps: **Benoit**, with replaceable identifiers
   (recommended); each user brings their own.
6. Offline: **no cache** (recommended); read cache; cache with deferred
   writes.

## Consequences

- Benoit creates two Dropbox apps and one Microsoft Entra registration, all
  free, and publishes a privacy policy page on os.kazo.ca. The exact list is in
  the plan.
- One trial with a real account per connector is needed before the series is
  merged into `main`: automated tests use fake servers.
- A OneDrive user clicks "Reconnect" once a day.
- Any script of the OS origin can read a token in memory while the keyring is
  unlocked: this is the threat model already accepted for the keyring.
- To try on a real device: a PWA installed on iOS has storage separate from
  Safari, and the return page might not reach the OS.
- New public API (drive sources, optional interfaces, cloud accounts) in the
  `PublicAPI.Unshipped.txt` files; the app SDK does not change, except the
  format and size `KazoDriveInfo` already reports.
- Tests: several sources and restoring them, a contract suite shared by all
  connectors on a fake HTTP service, PKCE and the return page with a fake
  OAuth server in E2E, the `mount`, `umount` and `df` commands.
