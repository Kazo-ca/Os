> [Version française](../../../../fr/wasm/dev/adr/0285-settings-features-section-adjustments.md)

# ADR 0285: the "Features" section of Settings, implementation adjustments

## Status

Accepted on 2026-11-17.

- **Work item:** `feature/21-settings-features-section-code` (series
  `feature/base`, code of ADR 0249).
- **Adjusts:** [ADR 0249](0249-settings-features-section.md) §1 (actions and
  state of a feature), §2 (rendering contract), §3 (role of an administrator)
  and §4 (`ShowSettingsPageAsync`). Its decisions stay true: a "Features" group
  after "Applications" (answer 2 A), an "Installed features" page, one entry per
  page provided, a page drawn in the Settings window but run in the context of
  its entry point (answer 1 A), `RequiresRole`, no elevation by Settings.
- **Questions asked to Benoit** in the work item's PR (series 24, questions 283
  to 286), at the end of this ADR.

## Context

ADR 0249 was written before the code of ADRs 0247 (roles) and 0283 (role
names), and before looking at ADR 0048 as it is coded (installation through
`apps.config`). While coding the section, four points could not follow its
text to the letter:

- §3 says "an administrator without the role does not see it either". Answer
  233 B of ADR 0247, accepted later, says the opposite: every administrator
  holds every role of the features, implicitly.
- §2 does not say how Settings, which references only the SDK, gets a page's
  rendering: the generator refuses that an app inject anything but an SDK type
  (`KOSAPP0031`, [ADR 0055](0055-base-apps-on-public-sdk-only.md) §3).
- §1 asks for "enable/disable" actions and a "restart required" state; the OS
  only knows the installation and uninstallation of ADR 0048
  (`/etc/kazoos/apps.config`), and a feature arrives live
  ([ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §1), with no
  restart.
- §4 does not say how the intent reaches Settings, nor what it requires.

## Decision

### 1. An administrator sees the pages that require a role

A page marked `RequiresRole` shows for the user who holds the role and for
every administrator (ADR 0247, answer 233 B; ADR 0283 §4). Roles are named
`<Feature>:<role>` (`x86:vm-admin`, ADR 0283 §2). The check is the launcher's
(`FeatureRoleService.OpensTo`): without a roles service, only an administrator
passes.

### 2. The rendering contract: `IFeatureSettingsPages`

- The contract is public, in `Kazo.Os.AppSdk.Blazor` (namespace
  `Kazo.Os.AppSdk.Blazor.Settings`), injected in Settings: `GetPages(host)`,
  `OpenAsync(host, pageId)`, `Changed`. The OS answers only Settings
  (`org.kazoos.settings`): any other host gets no page.
- `FeatureSettingsPages` (Platform.Blazor) reads the catalog live and filters:
  feature and page enabled, role, and `MobileSupported` on a phone. It
  refreshes the list when enablement changes (`IAppEnablementRegistry`) or when
  `roles.config` is read again (`FeatureRoleService.Changed`, added).
- Opening a page starts an **auxiliary process**, a child of Settings' process,
  with the page's id and capabilities (`ps` shows it, `argv`:
  `--type=settings-page`), like the renderer of a site
  ([ADR 0084](0084-blazor-sites.md) §3).
  `AppLifecycleOrchestrator.StartAuxiliaryAsync` now accepts a page and checks
  its role again. Leaving the page or closing Settings stops that process; if
  the OS stops it (`kill`), Settings offers to open it again.
- The rendered content cascades the page's context, hides Settings' window
  gateway (`IAppWindowGateway` cascaded as null) and puts the page in
  `AppErrorBoundary` (ADR 0238) with *Retry* only (`AllowClose = false`): the
  window belongs to Settings.
- A page is never launched alone: `LaunchAsync` refuses it
  (`lifecycle.settings-page.not-launchable`).

### 3. The manifest of a page

- `AppKind.SettingsPage` (`settingsPage`), reserved to the children of a
  feature (`manifest.feature.only` in the validator, `KOSAPP0033` in the
  generator).
- `settingsPage` options: `order` (0 by default) and `mobileSupported` (true
  by default), refused on any other kind (`manifest.settingsPage.forbidden`).
  The title is the entry point's `Name`, the icon the first of its `Icons`.
- A page runs on the main thread (`manifest.execution.window`) and may ask for
  the OS file dialogs, like a window.

### 4. "Installed features"

- Each feature shows its entry points and their kind (with the role they
  require), the permissions it declares (ADR 0246), its roles (ADR 0247) and
  the apps that depend on it (ADR 0248). `KazoAppInfo` gains `RequiresRole`,
  `Roles` and `ProvidedCapabilities` for that.
- Enabling and disabling are the installation and uninstallation of
  [ADR 0048](0048-system-apps-and-uninstall.md) for every user
  (`/etc/kazoos/apps.config`, an administrator authenticates): the page reuses
  the install row of `InstalledAppsPanel`. *Details* opens the app's page
  (`AppDetailsPanel`), *Open in the Store* launches the Store on the feature,
  and the page says how to remove it (`sudo kazopkg remove --system <id>`; the
  dependent apps are disabled, ADR 0248 §3).
- There is no "restart required" state: a feature installed or updated is
  usable live (ADR 0214 §1). Question 284.

### 5. Opening a page from elsewhere

- `KazoApps.ShowSettingsPageAsync(id)` sends `ShowSettingsPageIntent`, which
  requires `apps.launch` and reaches Settings with `--page=<id>` (intent
  `ShowAppSettings`, for an instance already open). The id must be a known
  page (`intent.show-settings-page.not-found`) and enabled
  (`intent.show-settings-page.disabled`).
- The role is Settings' answer: without it, Settings opens on "Installed
  features" with a message, as ADR 0249 §4 says.

## Options set aside

- **Injecting `IServiceProvider` in Settings**: refused by `KOSAPP0031`.
- **Rendering the page in Settings' process** (lent context): against answer
  1 A.
- **A "disabled" state apart from uninstallation**: a second state file for
  what `apps.config` already holds (question 283).

## Consequences

- App Abstractions: `AppKind.SettingsPage`, `SettingsPageOptions`,
  `AppManifest.SettingsPage`, `FeatureEntryPoint.SettingsPage`,
  `ShowSettingsPageIntent`, validator, JSON schema and the
  `feature-settings-page.valid.json` fixture.
- SDK: `KazoSettingsPageBase`, `IFeatureSettingsPages` and its records,
  `KazoApps.ShowSettingsPageAsync`, `IAppIntentGateway.ShowSettingsPageAsync`,
  `KazoAppInfo.RequiresRole`, `Roles`, `ProvidedCapabilities`.
- Platform: `FeatureSettingsPages`, `StartAuxiliaryAsync` for a page,
  `AppErrorBoundary.AllowClose`, `FeatureRoleService.Changed`, worker relay
  (`int.settings-page`).
- Settings: group `Nav.Group.Features`, page `features`
  (`InstalledFeaturesPanel`), `FeatureSettingsPageView`, `--page=<id>`.
- Docs: [`feature-settings-pages.md`](../../framework/feature-settings-pages.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`, the
  `changes/unreleased` entry.

## Questions asked to Benoit (series 24)

283. **Enabling and disabling a feature.**
     - A) **(Recommended, coded)** They are the uninstallation and
       reinstallation of ADR 0048 for every user, through the install row the
       page reuses.
     - B) A separate "disabled" state that keeps the feature installed but
       stops its children (a new system document).
284. **The "restart required" state of ADR 0249 §1.**
     - A) **(Recommended, coded)** Not shown: a feature arrives and updates
       live (ADR 0214 §1).
     - B) Show it when an update waits for a page reload
       ([ADR 0239](0239-start-on-the-latest-version.md)), once the SDK exposes
       that state.
285. **Where does the `IFeatureSettingsPages` contract live?**
     - A) **(Recommended, coded)** Public in `Kazo.Os.AppSdk.Blazor`, injected
       in Settings; the OS answers only Settings.
     - B) In an assembly internal to the OS, with an exception to `KOSAPP0031`
       for Settings only.
286. **End-to-end tests.**
     - A) **(Recommended, coded)** Unit and rendering tests here; the full
       path will be tested with the first real feature (Os-x86).
     - B) Add a test feature and a Playwright test now.

## References

- [ADR 0249](0249-settings-features-section.md): the section adjusted.
- [ADR 0247](0247-feature-declared-roles.md) and
  [ADR 0283](0283-feature-roles-isinrole-adjustments.md): roles.
- [ADR 0256](0256-app-libraries-implementation-adjustments.md): model of an
  implementation-adjustment ADR.
