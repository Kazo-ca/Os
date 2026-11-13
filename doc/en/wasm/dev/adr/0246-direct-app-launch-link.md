> [Version française](../../../../fr/wasm/dev/adr/0246-direct-app-launch-link.md)

# ADR 0246: launch a Kazo OS Store app from a direct link

## Status

Accepted on 2026-11-13. Benoit took recommendation A for all four questions
(see [Questions](#questions-asked-to-benoit)): parameter `autolaunch`, no
confirmation, the existing guest continues without a click, install for the
guest only. Series `app-link/base`.

- **Builds on:** [ADR 0187](0187-guest-start-and-sign-in-credits.md) (guest
  start when no account exists), [ADR 0212](0212-hack-store-app-build.md) (the
  Store app, source `catalog:official/<appId>`, argument `--app=<appId>`),
  [ADR 0208](0208-hack-store-sources-trust-build.md) (install for me, block
  list), [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) and
  version 2.2.2 (official catalog signature required), and the dangerous
  capability gate (`DangerousCapabilityGate`).
- **Supersedes no ADR.**

## Context

Benoit wants a link that can go in an app's readme, on a site or in a message,
and that opens the app in Kazo OS with nothing else to do:

> Start Kazo OS in guest mode (if there is no account), download an
> application automatically, then launch it. If the user has an account, only
> open the Store at the application's page.
> Example: `https://os.kazo.ca/?autolunch=ca.kazo.word`

What already exists:

- the **Start as guest** button under the first-account form, and **Continue
  as guest** while the guest is the only account (ADR 0187);
- the Store app (`org.kazoos.store`) opens an app's page with the argument
  `--app=<appId>` (`StoreWindow.razor.cs`);
- `KazoAppPackages.InstallAsync` installs from a catalog, with the
  `KazoInstallScope` scope (for me or for everyone); a package of the official
  catalog must carry its signature (`AppPackageDefaults.OfficialCatalogSigningKey`);
- nothing reads the page address when the OS starts yet.

Anyone can write a link: it must never install anything other than an app of
the signed official catalog, nor skip an approval the OS usually asks for.

## Decision

### 1. The link

`https://<OS address>/?autolaunch=<appId>`, for example
`https://os.kazo.ca/?autolaunch=ca.kazo.word`. The parameter name is
`autolaunch` (correct English); see question Q1.

- The parameter carries **only an app id**, never an address, a repository, a
  catalog or a version. A malformed id (not reverse-DNS `a.b[.c…]`, longer than
  128 characters, characters outside `a-z`, `0-9`, `.`, `-`) is ignored: the OS
  starts as usual.
- One parameter is read; if it is repeated, only the first one counts.
- The same link works on every OS host: the standalone PWA, a static site
  hosted elsewhere, the OS served by `Kazo.Os.Server`.

### 2. Read once, then removed from the address

At start, the host (`App.razor`) reads the parameter with a C# parser
(`AppLaunchLink.TryParse`, `Platform.Core`, testable without a browser), keeps
the request in the host state (`EcosystemHostState.PendingAppLaunch`), then
rewrites the address without the parameter (`NavigationManager.NavigateTo(…,
replace: true)`, no reload). Reloading the page, or restarting it for an
update, does not start the install again. The request does not survive closing
the tab: nothing is written to storage.

### 3. No account: start as guest, install, launch

If no account exists (`GuestAccountService.CanCreateAsync`), the host does not
show the first-account form: it creates the guest and opens its session like
the **Start as guest** button, then:

1. an OS progress window ("Installing Kazo Word…", name and publisher read from
   the catalog, progress bar, **Cancel** button) opens on the desktop;
2. the app is looked up **only** in the `official` catalog
   (`catalog:official/<appId>`). The catalog and author signatures are checked
   as for any install, and so are the block list (`app-policy.config`) and the
   minimum OS version;
3. it is installed **for the guest** (`KazoInstallScope` "for me"): no
   administrator window (see Q4);
4. it is launched as from the Start menu. The dangerous capabilities it
   declares go through the usual gate: the approval prompt shows at launch,
   the link grants nothing by itself.

No confirmation before the install (see Q2): the link only targets the signed
official catalog and a fresh guest session that holds no data yet.

If the guest is **already the only account** (earlier visit), the OS continues
as guest without a click and does the same; if the app is already installed,
it launches it without reinstalling (see Q3).

### 4. An account exists: open the Store at the app's page

If an account other than the guest exists, the sign-in screen shows as usual;
a short banner under the title says "Kazo Word will open in the Kazo OS Store
after you sign in". Once the session is open, the OS launches the Store with
`--app=<appId>`. Nothing is installed or launched without the user's click in
the Store.

### 5. Errors

Every error leaves the OS usable and ends with an OS notification with an
**Open the Kazo OS Store** button:

| Case | Message |
| --- | --- |
| App missing from the official catalog (or only in another catalog) | "The Kazo OS Store does not offer the app `<appId>`." |
| Catalog unreachable (offline) | "The Kazo OS Store cannot be reached. Try again once online." |
| App on the block list | The usual block-list message. |
| OS version too old | The usual Store message, with the required version. |
| Signature refused, broken package | The usual install message (`.transaction` journal). |
| Cancelled by the user | No notification. |

### 6. Log

Each link handled writes a line to the OS apps log (`app-link.received`,
`app-link.installed`, `app-link.launched`, `app-link.store-opened`,
`app-link.failed` with the reason), like `kazopkg`.

### 7. App readmes

The readme of each Store app can show a **Try in Kazo OS** link to
`https://os.kazo.ca/?autolaunch=<appId>`. That work happens in the app
repositories, once this series is in `main` and released.

## Work items of the `app-link/base` series

| No. | Branch | Content |
| --- | --- | --- |
| 0 | `app-link/0-adr-direct-launch-link` | This ADR, `features.md`, memory. |
| 1 | `app-link/1-read-link-and-guest-start` | `AppLaunchLink.TryParse` and its tests, reading and removing the parameter, `PendingAppLaunch`, automatic guest start, sign-in screen banner, Store `--app=` after an account signs in. |
| 2 | `app-link/2-install-and-launch` | `AppLaunchLinkRunner` service (`Platform.Core`): lookup in the official catalog only, install for the guest, progress window and Cancel, launch, errors as notifications, log. Unit tests with a test catalog. |
| 3 | `app-link/3-e2e-and-docs` | Playwright tests (no account, existing guest, existing account, unknown id, reload), doc `dev/app-launch-link.md` (fr + en), change entry. |

No change to the app SDK is planned: everything happens in the host and
`Platform.Core`.

## Consequences

- A link is enough to try an app: no account, no terminal, no click in the
  Store.
- The link can only install an app of the signed official catalog, and grants
  no dangerous capability: the safeguards stay those of a normal install.
- A visitor who already has an account never sees an app install without their
  click.
- The app installed by the guest stays theirs: "Create my account" ▸ Migrate
  keeps it, "Delete the guest" removes it (see Q4).
- When the PWA is installed, the browser may open the link in a tab rather than
  in the PWA window; the OS works the same in both.

## Questions asked to Benoit

Asked in the project thread on 2026-11-13. Benoit answered A to all four the same day.

- **Q1. Parameter name.** A `autolaunch` (recommended, correct English);
  B `autolunch` as in the example; C `autolaunch` documented and `autolunch`
  accepted too.
- **Q2. Confirmation before the guest install.** A none, automatic install and
  launch (recommended); B one "Install and open" click in a window showing the
  app, the publisher and its permissions; C automatic unless the app declares
  a dangerous capability.
- **Q3. The guest already exists (only account).** A continue as guest without
  a click and launch the app (recommended); B treat the guest as an account:
  sign-in screen, then the Store page.
- **Q4. Scope of the guest install.** A for the guest only, no administrator
  window (recommended); B for the machine, with one **Confirm** click from the
  guest, and the app stays if the guest is deleted.

## Task list

- [x] Read ADR 0187, the Store app (`--app=`) and the install API.
- [x] Write this ADR (fr + en) and update the indexes.
- [x] Ask Benoit the four questions in the thread.
- [ ] Work item 1: read the link, guest start, Store after sign-in.
- [ ] Work item 2: install and launch, errors, log.
- [ ] Work item 3: Playwright tests, doc, change entry.
