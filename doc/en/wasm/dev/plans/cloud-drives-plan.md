> [Version française](../../../../fr/wasm/dev/plans/cloud-drives-plan.md)

# Plan of the "cloud drives" series (`cloud-drives/base`)

Approved by Benoit on 2026-11-13. Decisions:
[ADR 0267](../adr/0267-cloud-drives-as-network-drives.md). The user's cloud
storage (Dropbox, OneDrive, S3 compatible) is mounted as a network drive, with
no server and no cost.

## Goal

- The user connects **their own** storage; Kazo OS provides none.
- Everything happens in the browser: Kazo OS stays a static site.
- First version: Dropbox, OneDrive, S3 compatible. Google Drive and WebDAV are
  not part of it (ADR 0267, section 7).

## Work items

| # | Branch | Content | Depends on |
| --- | --- | --- | --- |
| 0 | `cloud-drives/0-adr` | ADR 0267, this plan, `features.md`, memory. **Done.** | — |
| 1 | `cloud-drives/1-drive-sources` | `NetworkDriveService` with several sources (ADR 0267 §3), drive table with type and account, optional quota and revision interfaces (§4), `/proc/mounts`, `df`, `KazoDriveInfo`. A fake in-memory remote source serves the tests. No real provider. **Done** ([`network-drive-sources.md`](../network-drive-sources.md)). | 0 |
| 2 | `cloud-drives/2-cloud-accounts` | Cloud account service (§5): PKCE, sign-in window, static return page and `BroadcastChannel`, tokens in the keyring, renewal, removal, app identifiers in settings. A fake OAuth server for E2E tests. Checks the keyring of the guest account. Gives Benoit the exact return addresses to register. **Done**: [`cloud-accounts.md`](../cloud-accounts.md). | 0 |
| 3 | `cloud-drives/3-dropbox` | Dropbox connector (API v2), both scopes (§8), a contract test suite shared by all connectors. | 1, 2 |
| 4 | `cloud-drives/4-onedrive` | Microsoft Graph connector, both scopes, reconnect after 24 hours. | 1, 2 |
| 5 | `cloud-drives/5-ui` | "Connect a network drive" dialog with the providers and the scope choice, Settings page for cloud accounts, mobile explorer, `mount` and `umount` commands (§11). Texts in French and English. **Done** ([`cloud-drives-ui.md`](../cloud-drives-ui.md), [ADR 0270](../adr/0270-cloud-drives-in-the-app-sdk.md)). | 1, 2 |
| 6 | `cloud-drives/6-s3` | S3-compatible connector (SigV4), keys in the keyring, guide for the CORS rule. | 1 |
| 7 | `cloud-drives/7-final` | Series check: build, all suites, trimmed publish, trials with real accounts, doc `cloud-drives.md`, `features.md`. | 3 to 6 |

Work items 1 and 2 run in parallel. Work items 3, 4, 5 and 6 run in parallel
afterwards; work item 5 is developed with the fake source of work item 1.

## Rules of the series

- One branch per work item, created from `cloud-drives/base`, one PR to
  `cloud-drives/base`. Never a merge into `main`: Benoit decides.
- Each PR adds its change entry (ADR 0186) and updates the docs in French and
  English.
- Merge bar: green local build and tests, E2E suites one at a time,
  `build/publish-trimmed.sh` when the work item touches published code.
- Automated tests never call a real provider: fake HTTP handler and fake OAuth
  server.
- No secret in the repository. App identifiers are public; the app secrets the
  consoles show are not used and are never copied.
- Aucun jeu sur ce repository.

## What Benoit creates himself

Everything is free. The exact return addresses come from work item 2
([`cloud-accounts.md`](../cloud-accounts.md#redirect-uris-to-register)); the
registrations can be prepared before and receive the addresses afterwards.

1. **Two Dropbox apps** (Dropbox developer console, "Scoped access"):
   - one app of type **Full Dropbox** and one of type **App folder** (its name
     becomes the folder `Apps/<name>`; an app name cannot contain "Dropbox");
   - permissions of both: `account_info.read`, `files.metadata.read`,
     `files.metadata.write`, `files.content.read`, `files.content.write`;
   - return addresses: the one of os.kazo.ca and those of `localhost` for
     development;
   - "Allow public clients (Implicit Grant & PKCE)" enabled;
   - to hand over: the two **App keys**. The "App secrets" are not used.
   - Later: apply for production when an app nears 50 users (free review; an
     icon and a description are asked).
2. **One Microsoft Entra app registration** (Entra admin center, "App
   registrations"; a free Entra tenant is enough):
   - account types: accounts in any organizational directory **and personal
     Microsoft accounts**;
   - platform **"Single-page application"** (SPA) with the same return
     addresses;
   - delegated Microsoft Graph permissions: `Files.ReadWrite`,
     `Files.ReadWrite.AppFolder`, `offline_access`, `User.Read`;
   - to hand over: the **Application (client) ID**. No secret to create.
3. **A privacy policy page** on os.kazo.ca. It says that tokens and files stay
   in the user's browser and that Kazo OS has no server that receives them.
   Dropbox asks for it for production. Work item 7 proposes its text.
4. **One trial per connector with a real account**, before the series is
   merged into `main`, and a trial of the PWA installed on iOS.

Nothing to create for S3: the user brings their bucket and keys.

## Proofs made before the series

On 2026-11-13, from Chromium on a page isolated as Kazo OS is: the file and
token APIs of Dropbox, Google and Microsoft Graph answer and are readable; Box
is blocked. Not checked, for lack of an account: a full PKCE exchange and the
real token lifetimes. Work items 3 and 4 confirm them with Benoit's apps.
