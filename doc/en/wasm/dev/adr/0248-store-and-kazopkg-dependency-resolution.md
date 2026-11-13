> [Version française](../../../../fr/wasm/dev/adr/0248-store-and-kazopkg-dependency-resolution.md)

# ADR 0248: dependency resolution by the Kazo OS Store and `kazopkg`

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (series 15 of the Os-x86 design); they are recorded at the end of
this ADR and the decision applies them. Source decisions: decision 7
("installing BoxedWine installs Os-x86 at the same time"), D12 ("the Store and
kazopkg resolve the dependencies, one consent screen, an SDK call 'request the
installation' for an optional dependency; uninstalling Os-x86 warns and
disables the dependent apps") and 72.10 (guidelines of 2026-11-10: "installing
BoxedWine must offer to install Os-x86 when needed, with an administrator's
consent; on refusal, the installation of BoxedWine stops with a clear
message").

- **Completes:** [ADR 0005](0005-deterministic-app-dependency-order.md)
  (dependency order), [ADR 0048](0048-system-apps-and-uninstall.md) §4
  (enabling an app enables its present dependencies),
  [ADR 0182](0182-install-apps-from-github.md) (`kazopkg install`),
  [ADR 0212](0212-hack-store-app-build.md) (Store app) and
  [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) (transactions).
- **For:** the BoxedWine app (mandatory `Dependencies` to Os-x86) and any app
  that depends on a Feature or on another app.

## Context

- A manifest declares dependencies with a version range and an `Optional`
  flag (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:91, :203-212`). The
  catalog refuses an app whose mandatory dependency is missing
  (`Platform/Kazo.Os.Platform.Core/AppCatalog.cs:487-503`,
  `catalog.dependency.missing`); for an installed app, the host drops it with
  one log line (`OS/Kazo.Os.Ecosystem.Host/HostAppCatalog.cs:104-129`).
- **Nothing installs a missing dependency**: neither the package installer
  (`Apps/Packages/AppPackageInstaller.cs`, `PrepareAsync` :266, checks
  :630-657), nor `kazopkg` (`Apps/Commands/Kazo.Os.Commands/Kazopkg/`), nor
  the Store (`Apps/System/Kazo.Os.Apps.Store/`). A personal installation whose
  mandatory dependency is missing **succeeds**, then the app disappears from
  the catalog with one log line.
- **Removing a package does not look at who depends on it**
  (`AppPackageInstaller.RemoveAsync` :716-753): the dependents fall at the
  next catalog reload. Only the enable/disable layer of ADR 0048 knows the
  dependents (`AppsConfigDocument.ExpandDependents` :219-239, used by
  `AppInstallationService.cs:143, 366-377`).
- The Store opens on an app through `--app=<appId>`
  (`StoreWindow.razor.cs:52, :98-102`); an app can already call it through
  `IAppIntentGateway.LaunchAsync`. The Store catalog (`catalog.json`) does not
  list the dependencies of an entry.
- A Feature installs system-wide only (ADR 0260 §4): an app installed "for
  me" that depends on a Feature therefore has a dependency that requires an
  administrator.

## Decision

### 1. The installation resolves the mandatory dependencies

- Before installing a package, the installer (`AppPackageInstaller`, for the
  Store and `kazopkg`) builds the **plan**: the requested package, then each
  mandatory dependency (`Optional = false`) **absent or out of range**,
  transitively, in the order of ADR 0005 (dependencies first).
- A dependency is looked up **in the configured catalogs** (trusted sources
  of ADR 0208, official catalog first) by its `appId`; the chosen version is
  the most recent one that satisfies **all** the ranges of the packages in
  the plan and of the installed apps that already depend on it. For
  `kazopkg install <GitHub repository>` (ADR 0182), a dependency that is in no
  catalog stops the installation with its identifier and the expected range
  (`apps.package.dependency-unresolved`): `kazopkg` does not guess a
  repository.
- A dependency **already installed but disabled** (ADR 0048) is re-enabled in
  the plan, not reinstalled.
- The plan is **shown before any download**: list of the packages with
  version, source, size, and the set of dangerous permissions of each (a
  Feature: its whole set, ADR 0260 §3). The Store shows it in its validation
  window (`AppInstallApprovalOverlay`, ADR 0208); `kazopkg` prints it then
  asks for a confirmation (`--yes` skips it).
- **One consent** for the whole plan: when a package of the plan is a Feature
  or requires a system install, an administrator's authentication is asked
  **once**, for the plan; a personal install of the requested package stays
  personal, its Feature dependency goes system-wide.
- **Refusal or failure = nothing is installed**: the plan is a transaction
  (`.transaction` journal of ADR 0214 §3 extended to the plan); an
  administrator's refusal, a refused package (signature, API, ADR 0182 §5) or
  a cut cancel everything, with the message "The installation of <app> is
  stopped: <reason>"; never an installed app whose dependency is missing.

### 2. Optional dependency: on request

- An `Optional = true` dependency does not enter the plan. The app installs
  without it; `KazoFeatures.GetServiceAsync<T>()` returns null (ADR 0262).
- The SDK gains `KazoApps.RequestInstallAsync(appId, ct)` (permission
  `apps.launch`, already normal): opens the Store on the package
  (`--app=<appId>`), or, without the Store, a message "Install <app> with
  `kazopkg install <appId>`". The app never installs by itself; the
  resolution of §1 applies to the requested package.
- The Store shows on an app's page its dependencies (mandatory and optional)
  and, for each, "installed", "to install (with this app)" or "optional".

### 3. Removing a package others depend on

- `kazopkg remove` and the Store list the installed apps that declare the
  package as a **mandatory** dependency and warn: "<app1>, <app2> will no
  longer work and will be disabled". Confirmation (or `--yes`).
- After the removal, these apps are **disabled** (ADR 0048, `apps.config`),
  not removed: their data and their package stay; reinstalling the dependency
  re-enables them (`AppInstallationService.ReinstallAsync` :244-256,
  `ExpandDependencies`). Apps with an optional dependency are not touched
  (they get null).
- Removing a Feature keeps its data (ADR 0260 §4).

### 4. Update

- Updating a package whose new version requires a newer dependency follows
  §1 (plan, one consent). Updating a dependency to a version **out of the
  range** of an installed dependent is refused with the list of dependents
  and their range (`apps.package.dependency-conflict`); no `--force` exists
  for this case (the dependent would be disabled silently).

### 5. Store catalog

- `catalog.json` gains, per entry, `dependencies` (identifier, range,
  optional), copied from the manifest at publication (`publish-packages.yml`
  workflow of the `kazoos-solution` template, ADR 0219): the Store builds the
  plan without downloading each package. The catalog CI checks it (issue
  AppMarketPlace #4, same mechanism). Without this field (old catalog), the
  Store downloads the manifest to read it.

## Rejected options

- **A. The app installs its dependency itself** (SDK "install" call): an
  ordinary app that installs a system package is contrary to `apps.manage`
  (dangerous); and the consent would no longer be unique.
- **B. Refuse any installation with a missing dependency, without resolving
  it**: the user would have to find and install Os-x86 before BoxedWine;
  72.10 asks for the opposite.
- **C. One consent per package of the plan**: two windows for BoxedWine
  (BoxedWine, then Os-x86); D12 wants a single screen.
- **D. Remove the dependents too**: loss of the apps' data and settings;
  disabling is enough and reversible.

## Consequences

- `AppPackageInstaller`: `BuildPlanAsync`, transaction over the plan, codes
  `apps.package.dependency-unresolved`, `apps.package.dependency-conflict`;
  `kazopkg install`/`remove`/`update` (plan display, `--yes`); Store (plan in
  `AppInstallApprovalOverlay`, dependencies on the page);
  `KazoApps.RequestInstallAsync` (SDK, `PublicAPI.Unshipped.txt`);
  `catalog.json` (`dependencies`) and the template's publication workflow.
- Docs: [`app-installation.md`](../app-installation.md),
  [`kazoos-store.md`](../plans/kazoos-store.md) (catalog format),
  [`app-repository.md`](../../framework/app-repository.md), `features.md`,
  `changes/unreleased` entry.
- Tests: app + absent Feature → two-package plan, one consent, Feature
  system-wide; refusal → nothing installed; dependency not found → stop with
  message; optional → installed without, `RequestInstallAsync` opens the
  Store; removal → warning, dependents disabled, reinstallation re-enables
  them; out-of-range update → refused.

## Questions asked to Benoit

1. **A mandatory dependency not found in the configured catalogs, for
   `kazopkg install <GitHub repository>`?**
   - A) **(Recommended)** Stop with the identifier and the range; the user
     adds a source or installs the dependency first.
   - B) `kazopkg` looks for a GitHub repository of the same publisher
     (`Kazo-ca/Os-App-<name>`): handy, but a naming convention becomes a
     security rule.
2. **A personal (`--user`) installation of an app that depends on a Feature?**
   - A) **(Recommended)** Allowed: the app is personal, the Feature goes
     system-wide with an administrator's authentication in the same consent.
   - B) Refused: an app that depends on a Feature installs system-wide.

Maintainer's answers (2026-11-11): 1 A, 2 A.
