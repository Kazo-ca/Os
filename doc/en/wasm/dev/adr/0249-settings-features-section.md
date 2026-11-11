> [Version française](../../../../fr/wasm/dev/adr/0249-settings-features-section.md)

# ADR 0249: "Features" section of Settings and pages provided by a Feature

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: D38 ("a
'Features' section in Settings"), 122 ("an Os-x86 admin page in Settings →
Features plus an equivalent terminal command"), 140 B (his words: "two
sections, one for the VMs, then a section for the users", "graphical
administration from the first milestone") and 160 A (the role opens the
pages).

- **Completes:** [ADR 0096](0096-added-intents-and-app-defined-intents.md) §4 ("app
  settings"), [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md)
  (embeddable components), [ADR 0156](0156-replaceable-file-control-provider.md)
  §3 ("OS components" page), [ADR 0243](0243-feature-app-type.md) §2
  (`SettingsPage` entry point) and [ADR 0247](0247-feature-declared-roles.md)
  (role, per-operation authentication).
- **For:** the Os-x86 pages: "Virtual machines" (limits, common VMs,
  restrictions, shared images) and "Users" (VMs per user, assignment in V2),
  both under the `x86.vm-admin` role.

## Context

- The Settings sections and pages are a **hard-coded list**:
  `Apps/System/Kazo.Os.Apps.Settings/SettingsWindow.Navigation.cs:32-57`
  (`AllPages`, `SettingsPage(Id, GroupKey, LabelKey, Icon)` :8), filtered by
  `PagesFor` (:76-82); rendering is a `@switch (_activePage)` in
  `SettingsWindow.razor:62` onward (for example `case "os-components"` →
  `<OsComponentsPanel />`, :300-304). No "Features" section exists; no app
  can add a page.
- The only per-app hook is ADR 0096 §4: `ShowAppSettingsAsync`
  (`AppGatewayContracts.cs:561-567`) opens the app itself with
  `--show-settings`, or Settings with `--app=<id>` on the generic
  `AppDetailsPanel` page (`SettingsWindow.razor:539-544`,
  `AppIntentDispatcher.cs:480-509`). A Feature has no window of its own
  (ADR 0243 §1); its administration page must live **inside** Settings.
- Embeddable components (ADR 0148) render a provider's fragment in the
  host's window, **with the host's rights** (§4). Settings declares
  `apps.manage`, `permissions.manage`, `system.power`, `system.backup`,
  `users.manage` (all dangerous, [`capabilities.md`](../../framework/capabilities.md)):
  an Os-x86 page rendered the ADR 0148 way **would inherit those rights**,
  and Settings would inherit the page's bugs.
- Settings' administration operations go through the administration
  gateways and the OS authentication agent at every operation (ADR 0247,
  context); a Feature page must do the same through
  `IKazoFeatureAuthentication`.

## Decision

### 1. A "Features" section in Settings

- New `Features` group in the navigation, between "Applications" and
  "Network", with a built-in page **"Installed features"** (`features`) that
  lists each Feature (ADR 0243): name, version, publisher, state (enabled,
  disabled, restart required), its entry points (children) with their type,
  its declared permissions (ADR 0246) and its roles (ADR 0247), the apps that
  depend on it (ADR 0248), and the actions: enable/disable (admin,
  `apps.manage`), open in the Store, remove (through the Store or `kazopkg`,
  with the warning of ADR 0248 §3). It reuses `AppDetailsPanel` for what is
  common.
- Under the group, **one entry per page provided** by an installed, enabled
  Feature whose required role the user holds (§3): the page's label and
  icon, prefixed with the Feature's name in the navigation ("Os-x86 ▸ Virtual
  machines"). Without an installed Feature, the group only shows "Installed
  features" (empty: "No feature. The Kazo OS Store offers some.").
- Rendering is **decoupled from the hard-coded list**: `AllPages` stays for
  the built-in pages; an `IFeatureSettingsPages` (read from the catalog, hot,
  ADR 0214 §1) supplies the Features' entries.

### 2. The `SettingsPage` entry point

- `AppKind` gains `SettingsPage`, **reserved to the children of a Feature**
  (ADR 0243 §2; `manifest.feature.only` elsewhere). It declares: the page's
  Blazor component (derived from `KazoSettingsPageBase`, provided by
  `Kazo.Os.AppSdk.Blazor`), `Title`, `Icon`, `Order`, `RequiresRole`
  (ADR 0247 §2) and its permission subset (ADR 0243 §3).
- The page is rendered **in the Settings window** (same frame, same
  navigation, same theme: `--kos-*` styles, no MudBlazor in the SDK), but
  runs in the **execution context of the child**: its identity
  (`org.kazoos.x86.admin`), its permissions, its process visible in `ps`
  (ADR 0243 §2), its gateways. The page receives **no** gateway of Settings;
  Settings receives nothing from the page. The OS enters the child's context
  for the rendering (`KazoExecutionContext.Enter`, like a component rendered
  by the window launcher), unlike ADR 0148 §4 (host's rights), because here
  the host is more privileged than the provider.
- A page that throws follows ADR 0238: the error stays in the page area
  ("<Feature> ran into a problem", *Retry*), Settings goes on.
- The page may open its own dialogs (SDK dialogs, in its context) and call
  `IKazoFeatureAuthentication` (ADR 0247 §3) for a sensitive operation: the
  OS secure window shows over Settings.
- Mobile: Feature pages follow the mobile presentation of Settings (full
  screen, ADR 0171); a page declares `MobileSupported = false` to appear on
  the desktop only.

### 3. Role and elevation

- `RequiresRole` filters the navigation: the page appears only to users who
  hold the role (ADR 0247 §2); an administrator without the role does not see
  it either (ADR 0247, question 2). Without `RequiresRole`, the page is
  visible to every user (for example an Os-x86 "My virtual machines" page for
  personal settings, outside this ADR).
- Opening a page asks for no authentication; every **sensitive operation**
  asks for it (ADR 0247 §2, §3). Settings performs no elevation on behalf of
  the page.

### 4. Opening a page from elsewhere

- `KazoApps.ShowSettingsPageAsync("org.kazoos.x86.admin")` (permission
  `apps.launch`) opens Settings on that page: that is what BoxedWine does for
  "Administer the VMs" (160 A: BoxedWine opens the page, it does not act
  itself) and what `sudo x86 admin` suggests in the terminal ("see also
  Settings ▸ Features ▸ Os-x86"). Without the role, Settings opens on
  "Installed features" with a message.
- `--page=<child id>` is the matching Settings argument
  (`AppIntentDispatcher`, like `--app=`).

## Rejected options

- **A. An administration window of Os-x86's own**: a Feature has no window
  of its own (ADR 0243) and Benoit asked for the sections in Settings
  (140 B).
- **B. An embeddable component (ADR 0148) rendered by Settings**: the host's
  rights (Settings, five dangerous permissions) given to the Feature's code;
  the opposite of the principle "a Feature only has its permissions".
- **C. A web page (`Web`) of the Feature in a frame**: no Blazor SDK, no OS
  dialogs, no `IKazoFeatureAuthentication`; and a `Web` page cannot ask for
  `filesystem.*` (ADR 0073).
- **D. Generalize to every app** (any app adds a page to Settings): Settings
  would become a launcher; ADR 0096 §4 (`--show-settings`) stays the way of
  ordinary apps.

## Consequences

- Settings: `Features` group, `features` page, `IFeatureSettingsPages`,
  rendering of Feature pages in their context (`FeatureSettingsPageHost`),
  `--page=` argument, mobile presentation.
- SDK: `AppKind.SettingsPage`, `KazoSettingsPageBase` (`Kazo.Os.AppSdk.Blazor`),
  `RequiresRole`, `MobileSupported`, `KazoApps.ShowSettingsPageAsync`;
  `PublicAPI.Unshipped.txt`, `sdk-compat` fixture.
- Platform: `AppCatalog` exposes the `SettingsPage` children; the launcher
  enters the child's context for the rendering; `AppErrorBoundary` (ADR 0238)
  around each page.
- Docs: new `framework/feature-settings-pages.md`,
  [`app-model.md`](../../framework/app-model.md), `features.md`,
  `changes/unreleased` entry.
- Tests: Feature installed → entry in the navigation; `RequiresRole` without
  the role → absent, with the role → present; the page does not get
  `apps.manage` from Settings (test of a refused gateway); a page that throws
  does not take Settings down; `ShowSettingsPageAsync` opens the right page;
  disabling the Feature removes the page hot.

## Questions asked to Benoit

1. **In which context does a Feature page rendered in Settings run?**
   - A) **(Recommended)** The `SettingsPage` child's (its permissions, its
     process), the OS entering that context for the rendering; Settings lends
     it nothing.
   - B) Settings', like an ADR 0148 component: simpler, but the Feature
     inherits `apps.manage`, `users.manage`, `system.power`.
2. **Where does the "Features" group go?**
   - A) **(Recommended)** A group of its own, after "Applications", with the
     "Installed features" page and one entry per provided page.
   - B) Under "Applications", next to "OS components".
   - C) One group per Feature ("Os-x86", "Other feature"), with no "Installed
     features" page.

Applied while waiting: 1 A, 2 A.
