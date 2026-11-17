> [Version française](../../../../fr/wasm/dev/adr/0283-feature-roles-isinrole-adjustments.md)

# ADR 0283: roles of the features, implementation adjustments (`IsInRole` and `<Feature>:<role>`)

## Status

Accepted on 2026-11-17.

- **Work item:** `feature/20-feature-declared-roles-code` (series `feature/base`,
  code of ADR 0247, PR #604).
- **Adjusts:** [ADR 0247](0247-feature-declared-roles.md) §1 (form of the roles,
  reading through `HasRole`) and §3 (`RoleHolder` scope). Its other decisions
  stay true: a feature declares its roles, an administrator grants them, every
  administrator holds them all implicitly (answer 233 B), an entry point may
  require a role (`RequiresRole`), and every sensitive operation goes through
  the OS's authentication.
- **Questions asked to Benoit** in PR #604 (series 22, questions 277 to 280).
  Answers of 2026-11-17: his own answer to 277 (§1 to §3 below), A to 278, 279
  and 280 (§6). Follow-up questions 281 and 282 (series 23), asked in the same
  PR: A to both on 2026-11-17 (§3 and §5).

## Context

ADR 0247 names a role `<prefix>.<name>`, like a permission (`x86.vm-admin`),
and reads it through `KazoUser.Current.HasRole(role)`. Its `RoleHolder` scope
assumes an entry point marked `RequiresRole`. While writing the code, one
question remained open (277): a command open to all, such as `x86`, may have a
sensitive subcommand (`x86 admin`), while `RequiresRole` is carried by a whole
manifest.

Benoit's answer (question 277, 2026-11-17), translated from French: "I think
that to make development simpler, the feature must be able to ask the OS
whether the user has the permissions... We will need a `KazoUser` object we can
ask like `_kazoUser.IsInRole("admin")` or `_kazoUser.IsInRole("x86:manager")`
(the prefix must stay the name of the feature that declares the roles)."

## Decision

### 1. The user is asked through `IsInRole`

- `KazoUser.Current` returns the object of the process's user (`KazoUserInfo`,
  like the other services of the SDK). Its `IsInRole(string role)` method says
  whether they are in a role; `KazoUser.IsInRole(role)` is its shortcut.
- `IsInRole` replaces `HasRole`, which was never published (added by the same
  work item, still in `PublicAPI.Unshipped.txt`).

### 2. Two kinds of role names

- **Role of the OS**: a simple name. The only one today is `admin`
  (`KazoRoles.Administrator`): true for an administrator and for root under
  `sudo`. Any other simple name is no role of the OS and returns false.
- **Role of a feature**: `<Feature>:<role>`, for example `x86:vm-manager`. The
  `:` separator (`KazoRoles.FeatureSeparator`) tells it apart from a permission
  (`x86.vm.use`, ADR 0246).
- The prefix is **the name of the feature that declares the role**: never
  chosen by the caller nor by another feature. The validator refuses a role
  whose prefix is not its own feature's (`manifest.role.id`); the manifest
  writes the full id (`"id": "x86:vm-manager"`), and `RequiresRole`,
  `roles.config` and `kazorole` use that same id.

### 3. The "name of the feature" is its `CapabilityPrefix`

The prefix of a role is the feature's `CapabilityPrefix` (ADR 0246):

- it is short and validated (a lowercase letter, then lowercase letters,
  digits and hyphens);
- it is reserved against the built-in catalog, and the installer refuses a
  feature whose prefix an installed feature already took
  (`apps.package.capability-prefix-taken`, ADR 0246 §2). It is therefore unique
  among the installed features;
- it is stable: changing it would rename all its permissions and break the apps
  that ask for them;
- it already is Os-x86's short name: `x86`, like its permissions `x86.vm.use`.

The feature's id (`ca.kazo.x86`) is not kept: it is long and contains dots. No
new field is added. Follow-up question 281 in PR #604: answer A.

### 4. The administrators (233 B)

An administrator, and root under `sudo`, answer true to any
`IsInRole("<feature>:<role>")`, with no line in `roles.config`.
`IAppExecutionContext.Roles` lists, for them, every role of the installed
features.

### 5. `RequiresRole` stays, and a subcommand checks `IsInRole`

- `RequiresRole` on an entry point stays possible, in addition: the OS then
  hides and refuses that entry point (126 "role required").
- A subcommand of a command open to all, such as `x86 admin`, checks
  `KazoUser.Current.IsInRole("x86:vm-manager")` itself.
- The OS's per-operation authentication (`KazoFeatureAuthentication`, `sudo`)
  stays apart and unchanged. The `RoleHolder` scope asks, from an entry point
  marked `RequiresRole`, for that role. From another entry point of the
  feature, it asks for **a role of the feature**: the feature checks the exact
  role with `IsInRole` before asking. A user with no role of the feature never
  gets that scope. Follow-up question 282 in PR #604: answer A.

### 6. Other answers (278, 279, 280: A)

- 278: `roles.config` is not synchronized in this work item; the `TODO` issue
  [#607](https://github.com/Kazo-ca/Os/issues/607) follows the list of
  synchronized system documents.
- 279: Settings ▸ Users shows a "Roles of the features" panel, a table of
  accounts × roles with check boxes.
- 280: `kazorole list` is open to every user, without `sudo`.

## Options set aside

- **A new manifest field** (`ShortName`) as the feature's name: it would
  duplicate `CapabilityPrefix`, which already has the wanted guarantees (§3).
- **The feature's id as prefix** (`ca.kazo.x86:vm-manager`): long, with dots,
  and not the name the maintainer writes (`x86`).
- **The dot as separator** (`x86.vm-manager`, text of ADR 0247): a role would
  be confused with a permission.
- **`RoleHolder` refused outside an entry point with `RequiresRole`**: the
  `x86 admin` subcommand could not ask its user's password again.
- **A `role` parameter on `AuthenticateAsync`**: two overloads with optional
  parameters in the public API, for a check `IsInRole` already makes.

## Consequences

In PR #604:

- SDK: `KazoUserInfo.IsInRole`, `KazoUser.IsInRole`, `KazoRoles`
  (`Administrator`, `FeatureSeparator`); `HasRole` removed.
- Manifest: roles `<prefix>:<name>` (validator, JSON schema, fixture).
- Platform: the `RoleHolder` scope accepts, from an entry point without
  `RequiresRole`, a user who holds a role of the feature.
- Tests: form of the roles (`FeatureRoleManifestTests`), `IsInRole` (`admin`,
  simple names, shortcut), `RoleHolder` from an entry point open to all.
- Docs: [`system-administration.md`](../../framework/system-administration.md),
  [`sensitive-settings.md`](../sensitive-settings.md), `features.md` and the
  `changes/unreleased` entry.

## References

- [ADR 0247](0247-feature-declared-roles.md): roles declared by a feature.
- [ADR 0246](0246-provider-declared-permissions.md): permissions declared by a
  feature, `CapabilityPrefix`.
- [ADR 0256](0256-app-libraries-implementation-adjustments.md): model of an
  implementation-adjustment ADR.
