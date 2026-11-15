> [Version française](../../../fr/wasm/dev/app-launch-link.md)

# Direct app launch link

## Purpose

A link `https://os.kazo.ca/?autolaunch=<appId>` opens Kazo OS straight into an app of the Kazo OS
Store. With no account, the OS starts as the guest, installs the app from the signed official
catalog and launches it, with no click at all. With an account, it opens the Store at the app's
page after sign-in. Decision: [ADR 0258](adr/0258-direct-app-launch-link.md), series
`app-link/base`.

Example: `https://os.kazo.ca/?autolaunch=ca.kazo.word` opens Kazo Word.

The same link works on every OS host: the standalone PWA, a static site hosted elsewhere, the OS
served by `Kazo.Os.Server`.

## For the user

| Situation | What happens |
| --- | --- |
| No account on this machine | The OS runs **Start as Guest** with no click. A progress window shows the app, its publisher and the step (lookup, install, opening), with **Cancel**. The app is installed for the guest only, then opens. |
| The guest is the only account (earlier visit) | The OS runs **Continue as Guest** with no click. If the app is already installed from the official catalog, it opens without being downloaded again. |
| Another account exists | The sign-in screen stays the same, with a notice: "`ca.kazo.word` will open in the Kazo OS Store after you sign in." After sign-in, the Store opens at the app's page. Nothing is installed without the user's click. |
| Page reload **before** the link has finished (restart for an update, F5 during the install) | The tab kept the link ([ADR 0265](adr/0265-direct-link-survives-update-restart.md)): it runs again, the guest continues without a click and the app opens (without a new download when it was already installed). |
| The app is refused because a newer version waits (ADR 0239 §2) | "Restart" reloads the tab onto the new version, which runs the link again and opens the app. "Later" forgets the link. |
| Page reload **after** the link has finished | The parameter was removed from the address and the tab forgot the link: nothing starts again. The usual screen shows. |
| Malformed id | The link is ignored (but removed from the address); the OS starts as usual. |

### Errors

Every error leaves the OS usable and ends with an OS notification "The app could not be opened",
with an **Open the Kazo OS Store** button that opens the Store at the app's page.

| Case | Message |
| --- | --- |
| App missing from the official catalog (even if another catalog offers it) | The Kazo OS Store does not offer the app `<appId>`. |
| Catalog unreachable (offline) | The Kazo OS Store cannot be reached. |
| App blocked (`app-policy.config`) | The usual block-list message. |
| OS version too old | The usual Store message, with the required version. |
| Signature refused, broken package | The usual install message. |
| Cancelled by the user | No notification. |

## Safety

- The parameter carries **only an app id** (`a.b[.c…]`, `a-z`, `0-9`, `.`, `-`, at most 128
  characters), never an address, a repository, a catalog or a version.
- The app is looked up **only** in the `official` catalog (`catalog:official/<appId>`). The catalog
  and author signatures, the block list and the minimum OS version are checked as for any install.
- The link grants no dangerous capability: the usual approval prompt shows at launch.
- Someone who already has an account never sees an app install without their click.
- Only the app id is kept, in the tab's `sessionStorage` (key `kazoos.appLaunchLink`), until the link has finished; it is checked again when read back. Closing the tab forgets it, and other tabs do not see it (ADR 0265).

## Architecture

| Element | Where | Role |
| --- | --- | --- |
| `AppLaunchLink` | `Platform.Core/AppLinks` | `TryParse(address, out link)` reads the first `autolaunch` of the query (not the `#…` fragment); `IsValidAppId` applies the rules above; `RemoveFromAddress` removes **every** `autolaunch` and keeps the other parameters and the fragment. |
| `EcosystemHostState.PendingAppLaunch` | `Ecosystem.Host` | Keeps the link in memory between reading and the first desktop; `TakePendingAppLaunch` returns it once. |
| `AppLaunchLinkKeeper`, `IAppLaunchLinkStore`, `BrowserAppLaunchLinkStore` | `Ecosystem.Host/AppLinks` | ADR 0265: at boot, the address's link, otherwise the one the tab kept (`AppLaunchLink.TryFromAppId`); kept in `sessionStorage` until the link has finished, unless the app was refused for an update (kept until the restart, "Later" erases it). |
| `App.razor` | `Ecosystem.Host` | Reads the link **after** the boot update (ADR 0239, which reloads the same address) and before the first screen, then rewrites the address (`NavigateTo(…, replace: true)`, no reload). Starts or continues the guest once. At the first desktop: guest session → `IAppLaunchLinkRunner`; another account → `org.kazoos.store --app=<appId>`, then `RecordStoreOpened`. |
| `IAppLaunchLinkRunner`, `AppLaunchLinkRunner` | `Platform.Core/AppLinks` | Looks the app up in the official catalog, installs it for the session user through the app management gateway (the same as the Store's and `kazopkg`'s), launches it through the orchestrator, and turns every failure into a notification. A second link while one runs is ignored. |
| `AppLaunchLinkOperation` | `Platform.Core/AppLinks` | The current step (`Looking`, `Installing`, `Launching`), the name, the publisher (owner of the repository) and the version the progress window shows. **Cancel** works until the launch. |
| `AppLaunchProgress` | `Platform.Blazor/Shell` | The OS progress window (`.app-link-progress`, `data-app-id`), not modal, on desktop and mobile. |

### Log

Every link writes a line to `/var/log/apps/org.kazoos.app-link.log` (source
`org.kazoos.app-link`, properties `appId`, `reason`, `version`):

| Message | When |
| --- | --- |
| `app-link.received` | A link starts in the guest's session. |
| `app-link.installed` | The app was installed (`version`). |
| `app-link.launched` | The app was launched. |
| `app-link.store-opened` | An account signed in and the Store opened at the app's page. |
| `app-link.failed` | Failure, with `reason`: `not-offered`, `unreachable`, `cancelled`, `busy`, `not-approved`, `no-session`, `unsupported`, `launch:<code>`, `error`, or the install's code (`apps.package.blocked`, `apps.package.bad-signature`…). |

## Tests

- **Unit** (`Tests/Kazo.Os.Platform.Core.Tests/AppLinks/`): `AppLaunchLinkTests` (parsing, refused
  ids, removal from the address) and `AppLaunchLinkRunnerTests` (official catalog only, app already
  installed, theme pack with the same id, errors, cancel, second link, log).
  `EcosystemHostStateTests` covers `TakePendingAppLaunch`; `AppLaunchLinkKeeperTests`
  (`Tests/Kazo.Os.Ecosystem.Tests/AppLinks/`) covers the address-then-tab order and the erasing (ADR 0265).
- **End to end** (`Tests/Kazo.Os.TrimmedPublish.E2E.Tests/TrimmedPublishTests.AppLaunchLink.cs`,
  run by `build/publish-trimmed.sh`, see [`trimmed-publish.md`](trimmed-publish.md)):
  1. no account: the guest starts, the app installs and opens, the address loses the parameter; a
     reload starts nothing again; a second visit continues as the guest and opens the app without
     downloading it again;
  2. with an account: the sign-in notice, then the Store at the app's page, nothing downloaded;
  3. app missing from the catalog: notification, its button opens the Store; then **Cancel**
     during the download: no app and no notification;
  4. reload during the install (ADR 0265): the address no longer has the parameter, yet the link
     runs again and the app opens; a reload afterwards runs nothing.

  The official catalog is served by a Playwright route. It lists the test package of ADR 0206
  (`Tests/fixtures/installed-app`), signed by a P-256 key the script creates on every run with
  `openssl`. The publish under test accepts the public half of that key for `catalog:official`, next
  to the real key (`-p:KazoOsAppSigningKeys`, ADR 0214 §2): the signature check is the real one. No
  private key is committed.

## "Try it" link of app readmes

Since Kazo OS 3.3.3, the readme of each Store app (Chess, Webamp, IRC, Kazo Word, Kazo
Spreadsheets) links to `https://os.kazo.ca/?autolaunch=<appId>` in its "Try it" section, as do
the `kazoos-solution` template and Kazo-ca/Os-AppTemplate. The section sits outside the Store
listing block, so `store-readme.mjs` leaves it as written.

## Task list

- [x] Work item 0: ADR 0258 (fr + en), `features.md`, memory.
- [x] Work item 1: read the link, guest start, Store after an account signs in.
- [x] Work item 2: install and launch, progress window, errors, log.
- [x] Work item 3: `app-link.store-opened` line, end-to-end tests, this document, change entry,
  `features.md`.
- [ ] After the release: **Try it** link in the app readmes.
