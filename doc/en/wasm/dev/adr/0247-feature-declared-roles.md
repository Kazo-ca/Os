> [Version française](../../../../fr/wasm/dev/adr/0247-feature-declared-roles.md)

# ADR 0247: roles declared by a Feature, OS authentication per sensitive operation, log with app, operation and requester

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: 140 B (graphical
administration from the first milestone, "a role that allows managing the
VMs"), 156 B (his words: "a real Kazo role. The feature must be able to
declare roles") and 160 A (the role opens the pages and the commands; every
sensitive operation also asks for the OS administrator authentication;
"manage the subsystems" disappears), 2026-11-11.

- **Completes:** [ADR 0013](0013-local-user-session.md) (users and sessions),
  [ADR 0042](0042-sudo-root-elevation.md) (`sudo` elevation),
  [ADR 0049](0049-dangerous-capabilities-and-admin-approval.md) §6 (secure
  window) and [ADR 0243](0243-feature-app-type.md) §5.
- **For:** the Os-x86 role that manages the VMs (VM and users pages of
  Settings ▸ Features, ADR 0249; `sudo x86 admin`).

## Context

- Kazo OS knows three authority levels: `User`, `Administrator`, `System`
  (`Shared/Kazo.Os.App.Abstractions/AppAuthority.cs:9-19`, `Satisfies`
  :24-33). A user has an `Authority` and Unix groups (`LocalUser.cs:147,
  :150-153`, `LocalGroup` :64) used for file rights. There is **neither a
  declared role, nor a `UserRole`**, nor a role in a manifest.
- Every administration operation requires a dangerous permission **and** an
  administrator authentication **drawn by the OS**, at each operation, even
  for an administrator (accounts:
  `Platform/Kazo.Os.Platform.Core/Administration/AppUserAccountsGateway.cs:71-73, 83-84, 142-144`;
  backup: `AppSystemBackupGateway.cs:42, 68`; permissions:
  `AppPermissionAdministrationGateway.cs:85, 94`). The agent
  (`Elevation/AdministratorAuthenticationAgent.cs:16`) logs `app`,
  `operation`, `requestedBy`, `admin`, `outcome` (:175-198) to
  `/var/log/auth.log`; the ticket lives 30 s, single use
  (`InMemoryElevationTicketStore.cs:14, 37-50`).
- `AdministratorGate` is **internal** (`Administration/AdministratorGate.cs:13`)
  and only handed to the built-in gateways
  (`Execution/AppExecutionContextFactory.cs:473-488`); nothing in `Shared/`
  exposes it. The only path open to an app is a dialog **of the app** that
  collects the password and verifies it (`Shared/Kazo.Os.AppSdk.Blazor/WindowAppBase.cs:452-489`,
  injectable `IAdminElevationVerifier`, `Policy/ElevationContracts.cs:46`):
  the app sees the password, which contradicts the secure screen that
  decisions 122 and D61 require for Os-x86.
- `sudo` (ADR 0042) gives a command `IsRootElevated` and the `System`
  authority (`AppOperationContext.cs:58-59`); the OS verifies
  (`Shell/ShellHostPrompts.cs:41-59`) but the input goes through the Terminal
  app's dialogs (`TerminalCredentialPrompt.cs:38-51`).
- In guest mode, `guest` is a password-less administrator that exists only
  while no account is registered, and elevates by simple confirmation
  (ADR 0187 §2; `Sessions/GuestAccountService.cs:99-100, 126-129`;
  `Elevation/AdminElevationVerifier.cs:36-48`).

## Decision

### 1. A Feature declares roles

`FeatureDefinition.Roles` lists `FeatureRole` entries:

| Field | Role |
|---|---|
| `Id` | `<prefix>.<name>` with the prefix of ADR 0246: `x86.vm-admin` |
| `Title`, `Description` | shown in Settings ▸ Users (localized) |
| `Grants` | what the role **opens**: entry points of the Feature (`SettingsPage` pages, commands or subcommands marked `RequiresRole`), and declared permissions of the Feature it grants to the user for those entry points |

- A role is **assigned by an administrator**, user by user, in Settings ▸
  Users (new "Roles" column per user, with the OS authentication like any
  account change, ADR 0120) or by `sudo kazorole grant <user> <role>`. The
  assignment is written to `/etc/kazoos/roles.config` (`[roles]`,
  `x86.vm-admin=<guid>,<guid>`; stable user identifiers, never names,
  ADR 0169) and synchronized like the other system documents.
- An administrator **does not** hold the roles by default: they assign the
  role to themselves when they want the pages (as a Linux admin joins
  `docker`). Guest mode (ADR 0187) assigns every declared role to `guest`.
- The role is read through `IAppExecutionContext.Roles` (list of
  identifiers) and `KazoUser.Current.HasRole("x86.vm-admin")`. Removing the
  Feature removes the role from the display; the lines stay in
  `roles.config` and come back with the Feature.

### 2. The role opens; the OS authenticates every sensitive operation

- An entry point marked `RequiresRole = "x86.vm-admin"` is **visible and
  launchable** only by a user who holds the role: page absent from Settings
  ▸ Features (ADR 0249), subcommand refused with "role required" (code 126)
  in the terminal. The role **grants no `Administrator` authority**: the user
  stays `User` everywhere else.
- A **sensitive operation** (configure, delete, share a common VM; import a
  shared image; change a limit, an imposed rule, a machine refusal: 124.1,
  124.4, 124.5 of Os-x86) **also** requires the OS authentication, **at every
  operation**, like Kazo's administration gateways. The ADR does not list the
  operations: the Feature marks them in its code through the API of §3.

### 3. The OS authentication agent, for Features

The SDK gains, **reserved to the entry points and services of a Feature**
(ADR 0243 §5; an ordinary app gets `UnauthorizedAccessException`):

```csharp
public interface IKazoFeatureAuthentication
{
    ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation,            // "x86.vm.delete-shared", shown and logged
        KazoAuthenticationScope scope,  // RoleHolder or Administrator
        CancellationToken ct = default);
}
```

- `RoleHolder`: the session user **re-authenticates themselves** (password or
  passkey, ADR 0168), like `sudo` for an administrator; they must hold the
  role the entry point asks for.
- `Administrator`: an **enabled administrator** authenticates (themselves or
  another account, like ADR 0049 §5), for the operations Os-x86 reserves to
  admins (per-VM restrictions, 124.1; machine refusal, 124.5).
- The window is **the OS's** (`AdministratorAuthenticationOverlay`, ADR 0049
  §6, title "<Feature> asks: <operation>"), never a dialog of the Feature;
  from a terminal command too (same reason as ADR 0246 §4). The Feature never
  sees the password. Three failures cancel.
- The returned ticket lives 30 s, single use, **bound to the named
  operation** (`InMemoryElevationTicketStore`, scope = Feature + operation);
  the Feature presents it to the OS to log (§4) and executes. A ticket of
  another operation is refused.
- In guest mode: simple confirmation (ADR 0187), logged.

### 4. Log

- Every authentication writes to `/var/log/auth.log` (0600, ADR 0045):
  `feature.authenticate app=<feature> operation=<op> requestedBy=<guid>
  scope=<RoleHolder|Administrator> admin=<guid|-> outcome=<ok|denied|cancelled>`,
  through `AdministratorAuthenticationAgent.Record` (:175-198) extended with
  `scope`. A role refusal (`RequiresRole` not held) writes
  `feature.role-denied app= entryPoint= user=`.
- The Feature **also** writes the user's event (ADR 0254) with the subject
  (VM) so that the affected user sees it.
- Assigning or revoking a role: `role.grant` / `role.revoke` in `auth.log`,
  with `admin`, `user`, `role`.

### 5. What does not change

- `AppAuthority` keeps its three levels; a role is neither a level nor a Unix
  group.
- `AdministratorGate` stays internal to the built-in gateways; ordinary apps
  keep the `WindowAppBase` dialog (to be revisited outside this series).
- `sudo x86 …` stays possible: a command elevated by `sudo` has
  `IsRootElevated` and passes the `Administrator` checks without a new window
  (like `AdministratorGate.cs:25`).

## Rejected options

- **A. The role is enough, without per-operation authentication** (160 B):
  less than what Kazo requires of its own administrators; a session left open
  would allow deleting everyone's VMs.
- **B. "Manage the subsystems" as an app permission** (160 C): an app
  (BoxedWine) would administer every VM with the agent exposed to apps;
  Benoit chose A: BoxedWine opens the Settings page, it does not act on other
  users' VMs.
- **C. A role = the `Administrator` authority**: would give a VM manager all
  the rights of a Kazo admin (accounts, backup).
- **D. Unix groups as roles**: they protect files, not entry points nor
  operations, and no page shows them.
- **E. The app dialog (`WindowAppBase`) for a Feature**: the Feature would see
  the password; D61 forbids it.

## Consequences

- SDK: `FeatureDefinition.Roles`, `FeatureRole`, `RequiresRole` on an entry
  point, `IAppExecutionContext.Roles`, `KazoUser.Current.HasRole`,
  `IKazoFeatureAuthentication`, `KazoAuthenticationTicket`,
  `KazoAuthenticationScope`; `PublicAPI.Unshipped.txt`, `sdk-compat` fixture.
- Platform: `roles.config` (system document, synchronized), roles read when
  building the context (`AppExecutionContextFactory`), `AdministratorGate`
  extended (`RoleHolder`), tickets scoped to Feature + operation, `feature.*`
  and `role.*` entries in `auth.log`.
- Settings ▸ Users: "Roles" column, assignment with authentication;
  `kazorole` command (`grant`, `revoke`, `list`).
- Docs: [`system-administration.md`](../../framework/system-administration.md)
  ("Feature roles" section), [`sensitive-settings.md`](../sensitive-settings.md),
  [`logging.md`](../logging.md), `features.md`, `changes/unreleased` entry.
- Tests: role assigned → page visible, subcommand accepted; not assigned →
  page absent, 126; sensitive operation → OS window, ticket bound to the
  operation, refusal of a ticket of another operation; complete `auth.log`;
  ordinary app → exception; guest mode → confirmation; `sudo x86` without a
  second window.

## Questions asked to Benoit

1. **To whom can a Feature role be assigned?**
   - A) **(Recommended)** To any user, by an administrator; the sensitive
     operation re-authenticates through the user themselves (`RoleHolder`)
     or through an administrator (`Administrator`) as the Feature asks.
   - B) To administrators only (a role = a subset of admin rights); simpler,
     but a standard user can never manage the common VMs, which 140 B wanted
     to allow through the role.
2. **Does an administrator hold every role by default?**
   - A) **(Recommended)** No: they assign it to themselves (one line of
     `roles.config`); the VM administration pages do not clutter every admin.
   - B) Yes, implicitly.

Applied while waiting: 1 A, 2 A.
