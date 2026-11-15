> [Version française](../../../fr/wasm/dev/welcome-wizard.md)

# Welcome wizard at the first sign-in

Decision: [ADR 0276](adr/0276-welcome-wizard-at-first-sign-in.md). Series
`first-run-theme/base`.

## Purpose

At their first sign-in, users choose their language, theme, wallpaper and time
zone before the desktop opens. Each choice applies at the click: the screen
changes language or theme at once. The wizard is never mandatory ("Skip") and
everything stays changeable in Settings.

## For the user

| Situation | What happens |
| --- | --- |
| First sign-in of an account (created account, guest, account added by an administrator) | The wizard shows full screen between the sign-in and the desktop, with four steps: language, theme, wallpaper, time zone. |
| Account that existed before this version | It sees the wizard once, at its next sign-in. Its current settings are already selected. |
| Later sign-ins | The desktop opens directly. |
| First session on the other form factor (phone after computer, or the reverse) | The wizard asks only for the theme and the wallpaper of that form factor, once. |
| Arrival by a direct link `?autolaunch=<appId>` | The wizard does not show: the app opens. It shows at the next sign-in. |
| Setting locked by the administrator (`/etc/kazoos/*.config`) | The step of that setting is not shown. When no step is left, the desktop opens directly. |
| An update is ready during the wizard | The sign-in screens' "Update ready" notice does not show over the wizard (it would cover its buttons): the notification waits on the desktop. |

The buttons at the bottom: **Skip** (opens the desktop, the wizard does not
come back), **Back**, **Next**, and **Finish** on the last step. A choice
already made stays made when the user skips.

## Steps

| Step | Choices offered | File written |
| --- | --- | --- |
| Language | "System language" or a language of the OS (`KazoLocale.AvailableLocales`, without `C`) | `~/.config/locale.conf` (`LANG=…`) |
| Theme | The themes of the device's form factor: 7 on a computer, 2 on a phone, plus the user's themes | `~/.config/kazoos/appearance.config` (`desktopThemeId` or `mobileThemeId`) |
| Wallpaper | The theme's background or one of the shipped images (`WallpaperCatalog.BuiltIn`) | `appearance.config` (`wallpaper` or `mobileWallpaper`) |
| Time zone | "Automatic" (the device's zone) or a zone found by its name; the time in that zone is shown | `~/.config/kazoos/datetime.config` (`timeZone`) |

The language is applied to the running session: the login environment is read
again (`LoginEnvironmentProvider.ReloadAsync`), then the session language is
reloaded. So the desktop opens in the chosen language.

## Per-account state

`~/.local/state/kazoos/welcome.json`:

```json
{"schemaVersion":1,"completed":["desktop","mobile"]}
```

`completed` lists the form factors where the wizard was finished or skipped.
Missing file: nothing is done, the wizard shows. Unreadable file or refused
write: the desktop opens anyway (the wizard never blocks), a line goes to the
log, and the wizard comes back at the next sign-in when the state could not be
written.

## Architecture

| Element | Project | Role |
| --- | --- | --- |
| `WelcomeSetupService` | `Platform.Core/Welcome` | `GetPendingStepsAsync(platform)` returns the steps to show; `MarkCompletedAsync(platform)` writes the state; `SetLanguageAsync`, `SetWallpaperAsync`, `SetTimeZoneAsync` write the choices through `IOsSettingsService`. |
| `WelcomeStep` | `Platform.Core/Welcome` | `Language`, `Theme`, `Wallpaper`, `TimeZone`. |
| `LoginEnvironmentProvider.ReloadAsync` | `Platform.Core/Processes` | Forgets the environment kept for the session and reads it again, to take the language written during the session. |
| `EcosystemHostView.Welcome`, `EcosystemHostState.BeginWelcome()` | `Ecosystem.Host` | The view between the sign-in and the desktop. |
| `App.razor` (`EnterSessionAsync`) | `Ecosystem.Host` | After each sign-in: no direct link waiting and steps to show → `Welcome` view; otherwise the desktop. |
| `WelcomeWizard` | `Ecosystem.Host` | The component (`.razor`, `.razor.cs`, `.razor.css`): parameters `Steps`, `Platform`, `OnFinished`. The theme is written by `ThemePreferenceService`, the previews come from `ThemePreview`. Colours come from the active theme's variables, so the screen repaints with the chosen theme. |

The service is registered by `AddKazoOsEcosystemHost`; a host that does not
register it opens the desktop directly.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/Welcome/WelcomeSetupServiceTests.cs`: step
  plan, state, locks, writes, errors.
- `Tests/Kazo.Os.Ecosystem.Tests/EcosystemHostStateTests.cs`: the `Welcome` view.
- `Tests/Kazo.Os.UI.E2E.Tests/WelcomeWizardTests.cs`: full walk-through in
  Chromium (French, theme, wallpaper, time zone, files written, no wizard at
  the second sign-in).
- The other tests that open a session pass the wizard with
  `E2ESupport.PassWelcomeAsync(page)` (clicks `.welcome-skip` when shown).
  Useful selectors: `.welcome-surface`, `.welcome-skip`, `.welcome-next`,
  `.welcome-finish`, `[data-locale]`, `[data-theme-id]`, `[data-wallpaper]`,
  `[data-zone]`.
- The tests of the app repositories: issue
  [#553](https://github.com/Kazo-ca/Os/issues/553).

## Task list

- [x] Service, per-account state and tests.
- [x] Host view and component, computer and phone, English and French.
- [x] End-to-end tests and test helpers.
- [x] Docs, `features.md`, change entry.
