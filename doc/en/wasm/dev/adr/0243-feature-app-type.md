> [Version française](../../../../fr/wasm/dev/adr/0243-feature-app-type.md)

# ADR 0243: the "Feature" app type, an optional OS feature installed system-wide

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), first ADR of lot 1 of
the `feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (series 15 of the Os-x86 design); they are recorded at the end of
this ADR and the decision applies them. Source decisions, taken by Benoit
during the design of Os-x86: D33, D37, D38, D39 (series 3 and 4, 2026-11-09),
172 A then 222 A (identifiers `ca.kazo.x86` and `ca.kazo.boxedwine`,
2026-11-11).

- **Completes:** [ADR 0047](0047-app-definition-and-generated-manifests.md)
  (`AppDefinition`, generated manifests), [ADR 0048](0048-system-apps-and-uninstall.md)
  (system apps, machine or user uninstall) and
  [ADR 0182](0182-install-apps-from-github.md) (installing a package).
- **Opens:** ADRs 0244 to 0255 of the same series, which give a Feature its
  shared libraries, its service registry, its permissions, its roles, its
  dependencies, its Settings pages, its format detectors and its relay.
- **For:** Os-x86 (`ca.kazo.x86`), which runs x86 virtual machines in the
  browser, and any feature of the same kind.

## Context

- Os-x86 is, by decision 7 of its design, "an optional feature of the OS, in
  the manner of WSL": delivered by the Kazo OS Store, installed system-wide by
  an administrator, used by other apps (BoxedWine) through an interface the OS
  resolves, and by the terminal (`x86` command, programs launched by their
  path). It has no ordinary app: one command, two security windows (network
  permission, rules page) and administration pages in Settings.
- The Kazo OS app model has **one type per identifier**: `AppManifest.Kind`
  (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:27`) is `Window`,
  `Terminal`, `Service` or `Web` (`AppKind.cs:9-30`). The validator applies the
  rules of the type to the whole manifest: a `terminal` block is only allowed
  when `Kind == Terminal` (`AppManifestValidator.cs:295-316`,
  `manifest.terminal.forbidden`), file handlers only on a window app
  (`:318-321`, `manifest.fileHandlers.forbidden`).
- A `.kazoapp` package carries **one manifest** and therefore one type
  (`Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackage.cs:47, :78-84,
  :190-194`). Os-x86 would therefore need at least three packages, three
  identities, three permission screens, with no link between them.
- The process, the service state and the command exist once per app
  identifier; the shell protects a command name only for system apps
  (`AppCatalog.cs:343-374`: system apps first, then activation order, the
  loser gets `catalog.command.duplicate`).
- The "for me" install (`~/.local/share/kazoos/apps`, ADR 0208) and the
  system install (`/usr/lib/kazoos/apps`, ADR 0182 §5) both exist; nothing
  forces a package to exist only system-wide.
- Nothing in the model distinguishes an app that **provides** something to
  the others (an interface, permissions, roles, an executable format, a
  Settings page) from an ordinary app. Component contracts (ADR 0148, 0156)
  are the only provision mechanism, reserved to window apps and file controls.

## Decision

### 1. A new app kind: `AppKind.Feature`

- `AppKind` gains `Feature` (JSON value `feature`). A Feature is a **unit of
  installation, update, removal, Store presentation and permission approval**,
  with a single identity (`Id`, for example `ca.kazo.x86`), a single
  manifest, a single package.
- A Feature has no entry point of its own (`EntryPoint` is ignored; the
  generator emits `KOSAPP0032` when it is declared): it has neither a window
  nor a process. What it runs are its child entry points.
- In the definition class, a Feature derives from `FeatureDefinition` (a
  specialized `AppDefinition`): `Id`, `Name`, `Description`, `Version`,
  `PublisherId`, `Capabilities` (the whole set, §3), `Dependencies`,
  `EntryPoints` (§2), and the declarations the following ADRs add
  (`ProvidedServices`, `ProvidedCapabilities`, `Roles`, `BinaryFormats`).

### 2. Typed child entry points

- `FeatureDefinition.EntryPoints` lists **children**, each with an identifier
  `<feature>.<suffix>` (`ca.kazo.x86.cli`, `.permission`, `.rules`,
  `.admin`), an ordinary `Kind` (`Terminal`, `Window`, `Service`, or
  `SettingsPage` introduced by ADR 0249), its entry point (Blazor type or
  command), its own options (`Terminal`, `Window`, `ServiceStartup`), and its
  **permission subset** (§3).
- The manifest generator writes **one manifest per child** in the package,
  next to the Feature's manifest, with `parent: "<feature>"`. The validator
  applies to each child the rules of its type (`terminal` block on a
  `Terminal` child, window options on a `Window` child), and to the Feature
  the rules of the `Feature` kind (no entry point, no file handler, no
  extension association: D64, Os-x86 registers no association).
- The catalog exposes each child as a full app for everything that is
  **execution**: a process visible separately in `ps` and the Task Manager
  (child's name, grouped under the Feature), terminal command, window,
  service. It exposes the Feature for everything that is **identity**: Store,
  `kazopkg list`, Settings ▸ Applications, permission screen, enabling and
  disabling (ADR 0048: disabling the Feature disables its children),
  uninstall (one operation).
- A child cannot be installed, enabled or removed alone; an ordinary app
  cannot declare `parent`.
- The command of a `Terminal` child of a Feature comes **before** the command
  of an ordinary app with the same name in `AssignCommands` (after system
  apps): `x86` cannot be masked by an app installed later. Two Features that
  declare the same name: the older one wins, the other gets
  `catalog.command.duplicate` and the administrator decides by disabling one.

### 3. Permissions: as a block, then per entry point

- `FeatureDefinition.Capabilities` declares the **whole set** of the Feature's
  permissions. This set is what the Store, `kazopkg` and the approval screen
  show, **once**, at installation.
- Each child declares its **subset** (the child's `Capabilities`, empty
  allowed). The validator refuses a child permission that is not in the set
  (`manifest.feature.capability-outside-set`) and applies the type rules to
  the child (a `Web` page does not ask for `filesystem.*`, ADR 0073).
- At run time, a child's context only has its subset. The whole set is never
  granted to a process.
- The **administrator's consent at installation** (ADR 0182 §5 step 4,
  ADR 0208) counts as the machine approval of the Feature's dangerous catalog
  permissions: the OS writes the `allow` lines of the machine file (ADR 0049
  §4) at installation, for the Feature and its children, and the first-launch
  approval screen (ADR 0049 §5) does not come back. The per-user refusal
  (ADR 0049 §4, §9) stays possible, in Settings ▸ Permissions. The permissions
  **declared by the Feature itself** follow ADR 0246.

### 4. System-wide installation only, by an administrator

- A Feature installs into `/usr/lib/kazoos/apps/<id>/<version>/`, never into
  `~/.local/share/kazoos/apps`: `kazopkg install --user` and the Store's
  personal install refuse it (`apps.package.feature-system-only`), with the
  message "A feature installs for the whole machine, by an administrator".
- It requires the same administrator authentication as any system install
  (ADR 0182 §5 step 4, `sudo kazopkg install`, or the OS authentication agent).
- Update and removal: same rules. Removing a Feature keeps its data (ADR 0048
  §2; Os-x86's data lives outside Kazo's Files) and warns about the apps that
  depend on it (ADR 0248).

### 5. What only a Feature can do

| Model capability | ADR | Refusal for an ordinary app |
|---|---|---|
| Provide an interface resolved by `KazoFeatures.GetServiceAsync<T>()` | 0245 | `manifest.feature.only` |
| Declare provider permissions, dangerous ones included | 0246 | `manifest.feature.only` |
| Declare roles | 0247 | `manifest.feature.only` |
| Provide pages to Settings ▸ Features | 0249 | `manifest.feature.only` |
| Declare format detectors (a program's type from its header) | 0251 | `manifest.feature.only` |
| Give a per-flow identity to the relay | 0255 | refused at run time |
| Depend on another Feature (`Dependencies` to a `Feature`) | 0248 | an ordinary app may too (mandatory or optional dependency) |

An ordinary app that declares one of these is refused by the generator
(diagnostic `KOSAPP0033`) and by the validator at installation.

### 6. Usable right after installation

- A Feature added hot follows ADR 0214 §1: its `Terminal` and `Window`
  children can be launched immediately, its service starts at the next boot,
  and its interface (ADR 0245) resolves from the installation on.
- **Exception**: when an older copy of its contracts is already loaded in the
  page (ADR 0244 §3), the OS says so and offers a restart, like the Store's
  "Restart to finish" (ADR 0212 §3).
- Update or removal: a restart is offered only when the Feature's code is
  loaded (ADR 0214 §1.2).

### 7. Identifiers

- A Feature identifier has the same form as apps (`a.b.c` in lower case).
  Os-x86 takes `ca.kazo.x86` and its children `ca.kazo.x86.cli` (`x86`
  command), `ca.kazo.x86.permission` (network permission window),
  `ca.kazo.x86.rules` (rules page), `ca.kazo.x86.admin` (Settings
  pages); the BoxedWine app takes `ca.kazo.boxedwine` (172 A, `ca.kazo.`
  prefix set by 222 A, like `ca.kazo.irc`).
- No Kazo code reserves the `org.kazoos.` prefix to system apps (checked in
  `AppManifestValidator.cs` and `AppCatalog.cs`: the system-app status comes
  from the trust list of ADR 0048 §1, not from the prefix). The maintainer
  keeps it so (222 A): `org.kazoos.` stays with the built-in apps of this
  repository, with no reservation in the validator (222 C not retained).

## Rejected options

- **A. Three ordinary packages** (a command, a window, a service), linked by
  dependencies: three identities in the Store and three permission screens
  for one feature; nothing prevents installing half of it; none of the three
  can provide an interface nor declare a permission.
- **B. One package with several manifests and no new kind**: the package
  would group apps, but the OS would still have no notion of an installation
  unit nor of reserved rights; the validator, the Store and Settings would
  keep seeing three apps.
- **C. A window app with embeddable components** (ADR 0148) to provide
  everything: a component runs with the rights of the host app (ADR 0148 §4),
  which is the opposite of what a security feature (network permission
  window) requires; and a terminal command is not a component.
- **D. Personal installation allowed**: a feature that declares a format
  detector, permissions or a role acts on the whole machine; two users with
  two versions would give two behaviors of the shell.

## Consequences

- `Shared/Kazo.Os.App.Abstractions`: `AppKind.Feature`, `FeatureDefinition`,
  `AppManifest.Parent`, child manifests in the package (`kazoos-package.json`
  lists the manifests), new diagnostics `KOSAPP0032`, `KOSAPP0033` and codes
  `manifest.feature.*`, `apps.package.feature-system-only`;
  `PublicAPI.Unshipped.txt` and the `Tests/fixtures/sdk-compat` fixture.
- `AppCatalog`: children attached to their Feature, priority of Feature
  commands in `AssignCommands`, enabling and disabling as a block.
- `AppPackage`, `AppPackageInstaller`, `kazopkg`, Store: reading several
  manifests, refusal of the personal install, permission screen on the whole
  set, machine `allow` written at installation.
- Settings ▸ Applications shows a Feature with its children; the "Features"
  section is ADR 0249.
- Docs to write with the code: [`app-model.md`](../../framework/app-model.md)
  (`Feature` kind, `FeatureDefinition`), [`app-installation.md`](../app-installation.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  `changes/unreleased` entry.
- Expected tests: a Feature with three children passes the generator and the
  catalog; a child with a permission outside the set is refused; an ordinary
  app with `parent` or `ProvidedServices` is refused; `--user` refused; `x86`
  wins over an app installed later; disabling the Feature removes its
  children; the installation writes the machine `allow` lines and no approval
  window appears at first launch.
- The code of this ADR is the first work item of the `feature/base` series
  after the thirteen ADRs are merged; no code before.

## Questions asked to Benoit

1. **The `org.kazoos.` prefix for Os-x86 and BoxedWine, which are not system
   apps of this repository?** The code does not reserve it; the Store app
   published by Kazo (IRC) uses `ca.kazo.irc`.
   - A) **(Recommended)** `ca.kazo.x86` and `ca.kazo.boxedwine`: the
     `org.kazoos.` prefix stays with the built-in apps of this repository, as
     for IRC.
   - B) `org.kazoos.x86` and `org.kazoos.boxedwine` (172 A as is): the prefix
     says "published by Kazo"; ADR 0048 §1 does not use it.
   - C) Reserve `org.kazoos.` to system apps in the validator and take A.
2. **Does the installation consent count as the machine approval of the
   Feature's dangerous catalog permissions (§3), or does the first-launch
   window stay?**
   - A) **(Recommended)** It counts as approval: the administrator sees the
     whole set at installation (D39) and does not type the password again at
     the first launch of `x86`.
   - B) Both: consent at installation, then the ADR 0049 §5 window at the
     first launch of each child that has a dangerous permission.
3. **Child manifests: one file per child in the package (`manifest.json` +
   `entrypoints/<suffix>.json`), or a single manifest with an `entryPoints`
   array?**
   - A) **(Recommended)** One file per child, each validated as an ordinary
     manifest of its type; the existing validator code serves as is.
   - B) A single manifest with an array: fewer files, but a new schema and a
     validator to rewrite.

Maintainer's answers (2026-11-11): 1 A (222 A: `ca.kazo.x86` and
`ca.kazo.boxedwine`, applied across the series; `org.kazoos.` stays with the
built-in apps of this repository, with no reservation in the validator, C not
retained), 2 A, 3 A.
