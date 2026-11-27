> [Version française](../../../../fr/wasm/dev/adr/0287-disable-a-feature-without-uninstalling-it.md)

# ADR 0287: disable a feature without uninstalling it

## Status

Accepted on 2026-11-20.

- **Work item:** `feature/23-disable-feature-without-uninstall` (series
  `feature/base`).
- **Adjusts:** [ADR 0285](0285-settings-features-section-adjustments.md) §4
  ("Enabling and disabling are the installation and uninstallation of
  ADR 0048") and its rejected option "A 'disabled' state distinct from the
  uninstallation". The rest of ADR 0285 stays true, and so does the "enabled,
  disabled" state of [ADR 0249](0249-settings-features-section.md) §1, which
  this ADR implements.
- **Benoit's answers:** 283 B (series 24, PR #617), then 306 to 311 A
  (2026-11-20), recorded at the end of this ADR.

## Context

ADR 0285 had made "disable a feature" its uninstallation
(`/etc/kazoos/apps.config`, ADR 0048) and asked question 283. Benoit answered
283 B: disabling a feature does not uninstall it. It stays installed, its entry
points (children, services, commands, Settings pages) are stopped and hidden,
and the "disabled" state is kept in a system document. Enabling puts it back in
service. Questions 306 to 311 settled what 283 B did not say.

As on Windows ("Turn Windows features on or off") or with `systemctl disable`
then `mask`, the feature keeps its files, its data, its roles and its granted
permissions; only its right to run changes.

## Decision

### 1. The document `/etc/kazoos/features.config` (306 A)

- A `.config` document of [ADR 0011](0011-settings-scope-layout.md),
  `system:system 0644`, read without a schema. Only the keys of the
  `[disabled]` section count:

  ```ini
  # /etc/kazoos/features.config
  [disabled]
  ca.kazo.x86=true
  ```

- Only the id of a feature ([ADR 0260](0260-feature-app-type.md)) counts. A line
  naming a child, an ordinary app or a system app is ignored with a warning in
  `/var/log/syslog`; an id unknown to this version is ignored silently (the same
  file may serve several versions), as in `apps.config`.
- The state holds for the whole machine. Only an administrator disables or
  enables: the OS authentication window shows, except under `sudo`. There is no
  per-user file.
- The OS creates the file at the first disable and watches it: a hand edit
  applies at once.

### 2. What disabling does (307 A, 308 A)

- The feature, its children and the apps that depend on it through a
  non-optional dependency (BoxedWine for `ca.kazo.x86`) are disabled,
  dependents first. Their running processes (services, windows, Settings
  pages, commands) are stopped as at an uninstall: a clean stop, then forced
  after the grace period ([ADR 0097](0097-service-health-and-startup.md) §5).
- The checkpoint is the one of every app: `IAppEnablementRegistry`. A disabled
  app no longer shows in the launcher, among the terminal commands, in the pages
  of Settings ▸ Features, nor as the provider of a feature service
  ([ADR 0262](0262-feature-service-registry-and-async-resolution.md)).
- The confirmation (Settings and `kazopkg`) lists the dependent apps and the
  processes that will be stopped; it is the plan of an uninstall
  (`PlanUninstallAsync`), which computes the same set.
- Nothing is deleted: package files, data, `roles.config`, granted permissions
  and settings stay.
- An app both uninstalled and disabled is shown uninstalled: the uninstall
  wins.

### 3. What enabling does (307 A)

- The feature's line leaves `[disabled]`. The feature, its children and its
  dependents can be used again, dependencies first.
- The automatic services of those apps start at once for the session user,
  without waiting for a new login
  ([ADR 0286](0286-lazy-services-start-and-manual-kazowebd.md)). After a hand
  edit of the file, they start at the next login.

### 4. State and SDK contract

- `KazoAppInstallState.Disabled` (5): the feature, its children and the apps it
  takes along. The feature stays "installed" for the Store, `kazopkg` and
  `apps.config`.
- `IAppManagementGateway.DisableFeatureAsync(id)` returns a `KazoDisableReport`
  (apps disabled, processes stopped); `EnableFeatureAsync(id)` returns the apps
  enabled again. Facades `KazoAppManagement.DisableFeatureAsync` and
  `EnableFeatureAsync`. Both require `apps.manage` and an administrator. An id
  that is not a feature is refused (`InvalidOperationException`).
- They are default interface members that throw `NotSupportedException`: an
  older gateway stays compatible.

### 5. Settings ▸ Features (311 A)

- A feature's card shows its state ("Enabled" or "Disabled") and a *Disable* or
  *Enable* button, next to *Details* and *Open in the Store*. *Disable* opens the
  confirmation of §2. The page still says how to remove it (Store or
  `sudo kazopkg remove --system <id>`).
- A feature already uninstalled through `apps.config` keeps the former install
  row, with *Reinstall*.
- In Settings ▸ Applications, a disabled app carries the "Disabled" badge, with
  no button: it is enabled from Features.

### 6. `kazopkg disable` and `kazopkg enable` (309 A)

- `kazopkg disable <id>` and `kazopkg enable <id>`: an administrator, or
  `sudo`. `disable` lists the dependent apps and the processes, then asks for a
  confirmation; `--yes` answers for the user.
- `kazopkg list` and `kazopkg info` mark a disabled feature.

### 7. Update and removal (310 A)

- Updating, rolling back or repairing a disabled feature does not touch
  `features.config`: it stays disabled.
- Removing the package (`kazopkg remove --system`, Store) forgets its line: a
  new installation arrives enabled.

### 8. Logs

- `/var/log/syslog`: `apps: <id> disabled (by=<user>)` and
  `apps: <id> enabled (by=<user>)`, plus one line per stopped process.
- `/var/log/auth.log`: the administrator's authentication, operations
  `apps.feature.disable` and `apps.feature.enable`.

## Rejected options

- **Keep the uninstall of ADR 0285**: contrary to answer 283 B.
- **A `[disabled]` section in `apps.config`**: that file says what is installed
  for whom, has a per-user layer, and other parts of the OS read it
  (dependencies, packages). A separate file keeps the two states apart.
- **Leave the dependents visible, refusing to start** (308 B): a new launch
  state for every app, and dead entries in the launcher.

## Consequences

- Platform: `FeaturesConfigDocument`, `AppInstallationService`
  (`DisableFeatureAsync`, `EnableFeatureAsync`, `Disabled` state, file watch),
  `AppLifecycleOrchestrator.StartAutomaticServicesAsync`, the management
  gateway, package removal.
- SDK: `KazoAppInstallState.Disabled`, `KazoDisableReport`,
  `IAppManagementGateway.DisableFeatureAsync`/`EnableFeatureAsync`, facades.
- Settings: feature card, "Disabled" badge. `kazopkg`: `disable`, `enable`,
  mark in `list` and `info`.
- Docs: [`feature-settings-pages.md`](../../framework/feature-settings-pages.md),
  `features.md`, `changes/unreleased` entry.

## Benoit's answers

283. **Enabling and disabling a feature** (ADR 0285): **B**, a separate
     "disabled" state that keeps the feature installed and stops its children,
     in a system document.
306. **Who can disable, and for whom?** **A**: an administrator only, for the
     whole machine (`/etc/kazoos/features.config`).
307. **The processes of a feature being disabled.** **A**: stopped as at an
     uninstall (ADR 0097 §5), list shown before confirming; enabling restarts
     the automatic services without waiting for a login.
308. **The apps that depend on it.** **A**: disabled with it, listed before
     confirming, enabled again with it.
309. **A terminal command.** **A**: `sudo kazopkg disable|enable <id>`, state
     in `list` and `info`.
310. **An update of a disabled feature.** **A**: it installs, the feature stays
     disabled.
311. **The card's buttons.** **A**: *Enable*/*Disable*, *Details*, *Open in the
     Store*; removal stays with the Store and `kazopkg`; an uninstalled feature
     keeps *Reinstall*.

## References

- [ADR 0048](0048-system-apps-and-uninstall.md): uninstall through
  `apps.config`.
- [ADR 0248](0248-store-and-kazopkg-dependency-resolution.md): apps that depend
  on a removed package.
- [ADR 0285](0285-settings-features-section-adjustments.md): the adjusted
  section.
