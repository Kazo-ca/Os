> [Version française](../../../fr/wasm/framework/feature-settings-pages.md)

# Pages of Settings ▸ Features

A **feature** (ADR 0260) has no window of its own. Its administration pages
live in Settings, in the "Features" section
([ADR 0249](../dev/adr/0249-settings-features-section.md), adjusted by
[ADR 0285](../dev/adr/0285-settings-features-section-adjustments.md) and
[ADR 0287](../dev/adr/0287-disable-a-feature-without-uninstalling-it.md)).

## What the user sees

- A **Features** group, after "Applications". It starts with **Installed
  features**: each feature with its entry points, the permissions and roles it
  declares, the apps that depend on it, and its actions (enable or disable for
  every user, details, open in the Store, how to remove it). Without a feature:
  "No feature. The Kazo OS Store offers some."
- Then **one entry per page** an installed and enabled feature provides, named
  "Os-x86 ▸ Virtual machines". A page that requires a role shows only for its
  holders and for administrators (ADR 0247, answer 233 B).
- The page shows in the Settings window, but it runs in **its own process** (a
  child of Settings in `ps`, `--type=settings-page`), with its id and
  capabilities. An error of the page stays in its area ("Os-x86 ran into a
  problem", *Retry*); Settings goes on.

## Disabling a feature (ADR 0287)

- Disabling a feature **does not uninstall it** (answer 283 B). It stays
  installed for the Store and `kazopkg`; its files, data, roles and granted
  permissions stay.
- Only an administrator disables or enables, for the whole machine: the OS
  authentication window shows, except under `sudo`. The state is kept in
  `/etc/kazoos/features.config`, section `[disabled]` (`ca.kazo.x86=true`),
  which the OS watches.
- The feature, its entry points (services, windows, commands, Settings pages)
  and the apps that depend on it stop and disappear. The confirmation lists
  them, with the processes stopped (a clean stop, then forced after the grace
  period).
- Enabling puts it back in service, with its dependents, and starts its
  automatic services at once.
- The feature's card shows "Enabled" or "Disabled" and the *Disable* or
  *Enable* button. In Settings ▸ Applications, a disabled app carries the
  "Disabled" badge. A feature uninstalled through `apps.config` keeps
  *Reinstall*.
- In the terminal: `sudo kazopkg disable <id>` (list, confirmation, `--yes`)
  and `sudo kazopkg enable <id>`; `kazopkg list` and `info` mark a disabled
  feature. An update keeps it disabled; removing the package forgets its line.
- For an app: `KazoAppManagement.DisableFeatureAsync(id)` (returns a
  `KazoDisableReport`) and `EnableFeatureAsync(id)`, capability `apps.manage`;
  the state is `KazoAppInstallState.Disabled`.

## Declaring a page

A page is an entry point of kind `SettingsPage` of a `FeatureDefinition`. Its
component derives from `KazoSettingsPageBase` (`Kazo.Os.AppSdk.Blazor`).

```csharp
public sealed class X86Feature : FeatureDefinition
{
    public override string Id => "ca.kazo.x86";
    public override string Name => "Os-x86";
    public override string? CapabilityPrefix => "x86";
    public override IReadOnlyList<FeatureRole> Roles => [new("x86:vm-admin", "VM administrator")];

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
    [
        new("admin", "Virtual machines", AppKind.SettingsPage, typeof(VmAdminPage))
        {
            RequiresRole = "x86:vm-admin",
            SettingsPage = new SettingsPageOptions { Order = 1, MobileSupported = false },
            Icons = ["icons/vm.svg"],
            Capabilities = ["x86.vm.use"],
        },
    ];
}
```

```razor
@inherits Kazo.Os.AppSdk.Blazor.KazoSettingsPageBase

<h3>@PageTitle</h3>
@* AppContext is the page's context: the Kazo* facades act with its rights. *@
```

| Element | Role |
| --- | --- |
| `AppKind.SettingsPage` | Reserved to the children of a feature (`manifest.feature.only`, `KOSAPP0033`). |
| `SettingsPageOptions.Order` | Place among the feature's pages (0 by default, then by title). |
| `SettingsPageOptions.MobileSupported` | `false`: the page shows on the desktop only. |
| `Name`, `Icons` | Title and icon of the menu entry. |
| `RequiresRole` | Role of the feature that opens the page (ADR 0247 §2). |
| `Capabilities` | Subset of the feature's capabilities the page's process holds. |

A page runs on the main thread and may open the OS dialogs. It receives
**nothing** from Settings (neither context nor window gateway). Opening the
page asks for no authentication; a sensitive operation calls
`KazoFeatureAuthentication.AuthenticateAsync` (ADR 0247 §3), whose OS window
shows over Settings.

## Opening a page from an app

```csharp
await KazoApps.ShowSettingsPageAsync("ca.kazo.x86.admin");
```

- Requires `apps.launch`. Settings opens (or receives the intent if it is
  already open) with `--page=ca.kazo.x86.admin`.
- Without the role, or when the feature is disabled, Settings opens on
  "Installed features" with a message.
- An unknown or disabled page throws `KazoAppLaunchException`
  (`intent.show-settings-page.not-found` or `.disabled`).
- A page is never launched alone (`KazoApps.LaunchAsync` refuses it:
  `lifecycle.settings-page.not-launchable`).

## Architecture

| Piece | Where | Role |
| --- | --- | --- |
| `IFeatureSettingsPages` | `Kazo.Os.AppSdk.Blazor.Settings` | Contract injected in Settings: `GetPages`, `OpenAsync`, `Changed`. The OS answers only Settings. |
| `FeatureSettingsPages` | `Platform.Blazor/Settings` | Reads the catalog live, filters (enablement, role, mobile), starts the page's process (`StartAuxiliaryAsync`) and renders its component in its context, inside `AppErrorBoundary` (*Retry* only). |
| `InstalledFeaturesPanel` | Settings | The "Installed features" page; the state and *Enable*/*Disable* of each feature (`FeatureEnablementRow`, ADR 0287); an uninstalled feature keeps the install row of `InstalledAppsPanel`; *Details* opens `AppDetailsPanel`. |
| `FeaturesConfigDocument`, `AppInstallationService` | `Platform.Core/Apps` | `/etc/kazoos/features.config`; `DisableFeatureAsync`/`EnableFeatureAsync`, `Disabled` state (ADR 0287). |
| `FeatureSettingsPageView` | Settings | Opens the page, stops it when Settings leaves it, offers *Open again* if the OS stopped it. |
| `ShowSettingsPageIntent` | `AppIntentDispatcher` | Settings with `--page=<id>`. |

## Task list

- [x] `SettingsPage` kind, `SettingsPageOptions`, validator, schema, generator.
- [x] `KazoSettingsPageBase`, `IFeatureSettingsPages`, `KazoApps.ShowSettingsPageAsync`.
- [x] `FeatureSettingsPages`, page process, role, mobile, error barrier.
- [x] Settings: group, "Installed features", pages, `--page=<id>`.
- [x] Tests: manifest, platform, kernel, intent, Settings.
- [x] Disable a feature without uninstalling it: `features.config`, Settings, `kazopkg disable|enable`, SDK (ADR 0287).
- [ ] Full path with a real feature (Os-x86), question 286.
