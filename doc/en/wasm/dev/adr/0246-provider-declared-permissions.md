> [Version française](../../../../fr/wasm/dev/adr/0246-provider-declared-permissions.md)

# ADR 0246: permissions declared by a Feature, dangerous ones included, never granted silently

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: D32 (series 3),
143 ("the same rules as Kazo: administrator approval, refusal by the user,
never a silent grant", 2026-11-11), 176 A (names and levels of the Os-x86
permissions, 2026-11-11) and 160 A ("manage the subsystems" disappears in
favor of the role of ADR 0247).

- **Completes:** [ADR 0049](0049-dangerous-capabilities-and-admin-approval.md)
  (catalog, machine approval, per-user refusal) and
  [ADR 0243](0243-feature-app-type.md) §5 (only a Feature declares
  permissions).
- **Fixes the root of:** issue [#432](https://github.com/Kazo-ca/Os/issues/432)
  (the seeder silently grants every identifier outside the catalog), which
  also affects the "topic" permissions of [ADR 0040](0040-declared-topic-permissions.md).
- **For:** the Os-x86 permissions: `x86.programs.launch` (normal), `x86.vm`
  (dangerous, covers everything), `x86.vm.use` (normal), `x86.vm.destroy`
  (dangerous); BoxedWine declares them in its manifest.

## Context

- The capability catalog is **frozen in the code**:
  `Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs:196-287`
  (`Descriptions`, each entry `Normal(...)` or `Dangerous(...)`,
  17 dangerous ones); `IsDangerous` (:323-331) returns false for every
  identifier outside the catalog, which its comment says explicitly for the
  bus topic permissions.
- The validator refuses an unknown capability (`AppManifestValidator.cs:846-864`,
  `manifest.capability.unknown` :858) except a well-formed topic permission
  (:854). An app therefore **cannot** declare `x86.programs.launch` today.
- At sign-in, `CleanProfileCapabilityGrantSeeder` (`Platform/Kazo.Os.Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs:48-72`)
  grants **silently** every declared capability that `IsDangerous` deems
  non-dangerous (:51-55, `Grant` :66-71), called from
  `LocalSessionService.cs:295-307`. The topic permissions of ADR 0040 are
  thus granted without the explicit approval the ADR requires (:34-37,
  :145-148): this is issue #432. A permission declared by a Feature would
  meet the same fate as long as the seeder does not know it as dangerous.
- Dangerous capabilities follow ADR 0049: machine approval in
  `/etc/kazoos/app-permissions.config` (the only source of an `allow`,
  `AppPermissionDecisions.cs:17-22`), per-user refusal in `~/.config` (§4,
  §9), secure window at first launch (§5, §6), or text mode in the terminal
  (§5: banner, then administrator name and password through the Terminal
  app's dialogs, `TerminalCredentialPrompt.cs:38-71`). PR #428 makes a
  machine `allow` withdraw the refusal of the user who asks for it
  (`AppPermissionAdministration.cs:295-305`).
- The approval window shows a **risk statement** per dangerous capability
  (ADR 0049 §1); a Feature must be able to supply its own.

## Decision

### 1. A Feature declares its permissions

`FeatureDefinition.ProvidedCapabilities` lists `ProvidedCapability` entries:

| Field | Role |
|---|---|
| `Id` | identifier, under the **Feature's prefix** (§2): `x86.programs.launch` |
| `Risk` | `Normal` or `Dangerous` (`CapabilityRisk`) |
| `Title`, `RiskStatement` | displayed title and, when dangerous, the risk statement (localized resources of the Feature, ADR 0222) |
| `Covers` | for a permission that covers others (`x86.vm` covers `x86.vm.use` and `x86.vm.destroy`): the covered list |

- The manifest writes them; the validator checks the form, the prefix and
  the absence of a duplicate with the catalog or another installed Feature
  (`apps.package.capability-already-provided`).
- The OS capability catalog becomes **built-in catalog + catalogs of the
  installed Features**, read by everything that consults `AppCapabilities`
  today: validator (for an app installed after the Feature), seeder,
  dangerous-capability gate, `DangerousCapabilityEvaluator`, Settings ▸
  Capabilities and ▸ Permissions, `kazopkg info`, the approval screen.

### 2. Reserved prefix and uniqueness

- Each Feature declares a `CapabilityPrefix` (one segment, lower case:
  `x86`). Its permissions start with `<prefix>.`. A prefix of the built-in
  catalog (`filesystem`, `network`, `apps`, `system`…) is refused; two
  Features that declare the same prefix → the second is refused at
  installation (`apps.package.capability-prefix-taken`).
- An app that declares a `<prefix>.*` permission whose prefix no installed
  Feature bears: refused at installation (`manifest.capability.unknown`,
  unchanged), **unless** it declares a dependency (ADR 0245 §3) to a Feature
  whose prefix matches; the permission is then accepted and **stays
  ungranted** as long as the Feature is not installed (an absent optional
  dependency).

### 3. The same rules as the catalog, never silently

- A permission declared **normal** is granted at sign-in like a normal
  catalog capability, **if and only if** the Feature that declares it is
  installed and enabled. Otherwise it is not granted (no error: the app sees
  `GetServiceAsync` return null).
- A permission declared **dangerous** follows ADR 0049 to the letter: never
  granted without an `allow` in the machine file, written after an
  administrator's approval (first-launch window or Settings ▸ Permissions);
  per-user refusal in `~/.config`; `block` policy honored; the Feature's risk
  statement shown; grant materialized by (app, user, permission)
  (`IndexedDbCapabilityGrantRepository.cs:289-290`).
- **The seeder no longer knows a "non-dangerous by default"**: it only grants
  the identifiers the catalog (built-in + Features) describes as normal. Any
  **unknown** identifier is ignored with a warning in `/var/log/syslog`
  (`capability.unknown-skipped`). The topic permissions of ADR 0040 enter the
  catalog with their classification (question 2): this is the fix of #432.
- `Covers`: granting `x86.vm` also grants what it covers; the approval screen
  says so ("covers: use, delete and export your VMs"). A covering permission
  is at least as dangerous as what it covers (validator).

### 4. The Feature checks, through the OS secure screen

- Every operation of a provided service (ADR 0245) or of a Feature command
  requires its permissions through `IFeatureServiceContext.Caller.Require("x86.vm.use")`,
  which throws `UnauthorizedAccessException` like the SDK gateways. The
  Feature never evaluates a decision file itself.
- When a declared dangerous permission is **pending** for the calling app
  (ADR 0049 §4) and the call comes from a **terminal command** (`./x.exe`,
  `x86 vm delete`), the request goes through the **OS secure window**
  (ADR 0049 §6, `AdministratorAuthenticationOverlay`), not through the
  terminal text mode: the output of a guest program relayed in that terminal
  (ADR 0252) could imitate the Terminal app's banner and dialogs. The text
  mode of ADR 0049 §5 stays for the built-in catalog capabilities
  (question 3).

### 5. What does not change

- A permission declared by a Feature protects what **that Feature** does; the
  OS derives no right of its own from it (it opens neither an OS file nor an
  OS network).
- `network.real.access` stays in the built-in catalog: Os-x86 declares it in
  `Capabilities` (181 A), not in `ProvidedCapabilities`.
- Removing the Feature removes its permissions from the catalog; the
  `allow`/`deny` lines stay in the files (ignored, ADR 0049 §4: "a line that
  targets … a normal capability is ignored") and become effective again at
  reinstallation.

## Rejected options

- **A. The topic permissions of ADR 0040 as the model** (identifiers outside
  the catalog accepted): that is precisely the silently granted mechanism
  (#432); a dangerous permission cannot live there.
- **B. Add the Os-x86 permissions to the OS built-in catalog**: a third-party
  Feature could not declare any without an OS version; contrary to "a Feature
  is a separate repository".
- **C. All Feature permissions dangerous** (one administrator approval covers
  everything, 176 B): `x86.programs.launch` (launching a program one chose
  oneself) would ask for an administrator password; Benoit chose 176 A.
- **D. Let the Feature read `app-permissions.config` itself**: every Feature
  would rewrite the evaluator, and a bug there would grant rights.

## Consequences

- `AppCapabilities` becomes a service (`ICapabilityCatalog`) composed of the
  built-in catalog and the installed Features; `IsDangerous`, `Describe`,
  `GetRisk` read that service; `CleanProfileCapabilityGrantSeeder` grants
  only what is known; `DangerousCapabilityGate` and
  `DangerousCapabilityEvaluator` consult the composed catalog;
  `AppManifestValidator` accepts a Feature prefix declared as a dependency;
  codes `apps.package.capability-already-provided`,
  `apps.package.capability-prefix-taken`, `capability.unknown-skipped`.
- `IFeatureServiceContext.Caller.Require`; approval request of a Feature
  permission through the secure window even from the terminal.
- Settings ▸ Capabilities and ▸ Permissions, `kazopkg info`, the approval
  screen show the Features' permissions with their risk statement and their
  originating Feature.
- Issue #432: closed by this ADR (the seeder); issue #433 (overwritable
  refusal file) does not depend on it and stays open.
- Docs: [`capabilities.md`](../../framework/capabilities.md) ("Permissions
  declared by a Feature" section), [`dangerous-capability-approval.md`](../dangerous-capability-approval.md),
  `features.md`, `changes/unreleased` entry.
- Tests: declared normal permission granted only with the Feature installed;
  declared dangerous → window, machine `allow`, user refusal, `block`; unknown
  identifier → ignored and logged, never granted (regression of #432 with a
  random identifier and a topic permission); prefix taken → refused;
  `Covers`; operation without permission → exception; request from a command
  → secure window.

## Questions asked to Benoit

1. **Who reserves a Feature's prefix?**
   - A) **(Recommended)** The first installed Feature that declares it; a
     second one with the same prefix is refused; no central registry.
   - B) A registry in the official Store catalog (prefix per publisher),
     checked at publication; safer, but a Feature outside the Store has no
     guaranteed prefix.
2. **The topic permissions of ADR 0040 (`topic-publish:…`,
   `topic-subscribe:…`), in the seeder fix?**
   - A) **(Recommended)** Classified **dangerous** and approved as ADR 0040
     requires (`UserApproval | AdministratorApproval`): end of the silent
     grant; the apps that use them see an approval window once.
   - B) Classified normal (current behavior, made explicit): #432 stays open
     for topics.
3. **The approval request of a Feature permission from a terminal command?**
   - A) **(Recommended)** Always through the OS secure window (§4); the text
     mode of ADR 0049 §5 stays for the built-in catalog.
   - B) The terminal text mode, as for the built-in catalog (simpler, but
     imitable by a guest program whose output goes through the terminal).
   - C) The secure window for every dangerous permission, Feature or not
     (changes ADR 0049 §5 for every command).

Applied while waiting: 1 A, 2 A, 3 A.
