> [Version française](../../../../fr/wasm/dev/adr/0276-welcome-wizard-at-first-sign-in.md)

# ADR 0276: a welcome wizard at the first sign-in

## Status

Accepted on 2026-11-15: Benoit chose Q1 = A, Q2 = A, Q3 = A, Q4 = C and
Q5 = A (see [Questions](#questions-asked-to-benoit)). Series
`first-run-theme/base`.

- **Completes:** [ADR 0104](0104-localization.md) §2 (a language change waits
  for the next sign-in): the wizard is part of the sign-in, so its language
  applies at once.
- **Builds on:** [ADR 0044](0044-settings-as-files.md) §8 and §10 (settings as
  files, device-local user state), [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  (themes), [ADR 0202](0202-desktop-wallpaper-images.md) and
  [ADR 0217](0217-mobile-home-wallpaper.md) (wallpapers),
  [ADR 0193](0193-local-time-and-time-zone.md) (time zone),
  [ADR 0187](0187-guest-start-and-sign-in-credits.md) (guest account) and
  [ADR 0258](0258-direct-app-launch-link.md) (direct link).

## Context

Benoit, 2026-11-15:

> à la première connection, j'aimerais que l'utilisateur puisse choisir son thème.

Kazo OS has 7 desktop themes and 2 mobile themes, wallpapers, two languages
and a time zone setting, but a new visitor lands on the default theme, in
English, without knowing these choices exist: they are only in Settings. Real
systems ask these questions once, before the first desktop.

Each setting is already a file of the user, read live by the shell (except
the language, read once per session). What is missing is the screen that
offers them at the right moment, and the memory of "already done".

## Decision

1. **A full screen before the desktop (Q1 A).** The host gains the view
   `EcosystemHostView.Welcome`, shown between the sign-in progress and the
   desktop. The session is already open: the wizard writes the user's files
   with the user's rights. The desktop (and so `MarkSystemStarted`, the
   what's-new notification and the direct link) only shows at the end.
2. **Four steps (Q4 C)**, in this order, each applied on click, as in
   Settings:
   1. **Language**: "Follow the system language" or one of the languages of
      `KazoLocale.AvailableLocales` (without `C.UTF-8`). The choice is the
      `LANG` line of `~/.config/locale.conf`; the other variables of the file
      are kept. The login environment is read again at once
      (`LoginEnvironmentProvider.ReloadAsync`), so the wizard, the desktop and
      the apps started afterwards speak the chosen language. Processes the
      sign-in already started keep theirs.
   2. **Theme**: the built-in themes of the device's form factor and the
      user's themes (ADR 0103), with the `ThemePreview` sample of Settings.
      The whole screen takes the theme on click (`desktopThemeId` or
      `mobileThemeId` of `appearance.config`).
   3. **Wallpaper**: "Theme background" or one of the built-in wallpapers
      (`WallpaperCatalog.BuiltIn`): key `wallpaper` on a computer,
      `mobileWallpaper` on a phone. A personal file is chosen later in
      Settings.
   4. **Time zone**: "Automatic" (the device's zone) or a zone of
      `KazoTimeZone.Available`, with a search box and the time it gives
      (`timeZone` of `datetime.config`).

   Buttons: **Back**, **Next**, **Finish** on the last step, and **Skip**,
   which closes the wizard and keeps what was already chosen. A text says
   everything can be changed in Settings.
3. **Who sees it (Q2 A).** Every account, at its first sign-in, the guest
   included. While a direct link `?autolaunch=<app>` waits, the wizard is
   skipped for that session without being marked done: it shows at the next
   sign-in.
4. **Memory per account and form factor (Q3 A, Q5 A).** The file
   `~/.local/state/kazoos/welcome.json` (`{"schemaVersion":1,"completed":["desktop"]}`)
   lists the form factors (`desktop`, `mobile`) whose wizard was finished or
   skipped. A missing or unreadable file means nothing is done.
   - No form factor done: the four steps. An account that existed before the
     update has no file, so it sees the wizard once, with its current settings
     selected.
   - The other form factor already done: only **Theme** and **Wallpaper**,
     which belong to the form factor; language and time zone are shared.
   - The current form factor done: nothing.

   The state is device-local (ADR 0044 §10): it is not synchronized.
5. **Locked settings.** A step whose key the administrator locked
   (`/etc/kazoos/*.config`) is not shown. When no step is left, the form
   factor is marked done without a screen.
6. **Never blocking.** If the state cannot be read (storage error), the wizard
   is not shown. If a write fails, the step shows the error and the user can
   go on. If the state cannot be written, the desktop opens anyway; the wizard
   will come back.
7. **Where the code lives.** `WelcomeSetupService` (Platform Core, folder
   `Welcome/`) holds the state, the plan of steps and the language write; the
   `WelcomeWizard` component is in the host (`Kazo.Os.Ecosystem.Host`), next
   to `LoginProgressScreen`. No API of the app SDK changes.

## Consequences

- A new visitor chooses a language and a look before seeing the desktop; a
  visitor in a hurry clicks "Skip" once.
- Existing accounts see the wizard once after the update.
- **End-to-end tests.** Every path that creates a profile and waits for the
  desktop must pass the wizard. The test helpers of this repository do
  (`E2ESupport`, `PwaTestSupport` and the direct paths). The app repositories
  (`tests/e2e/host.mjs`, `tests/installed/*.mjs`) break the day they move to
  the OS version that has the wizard; the fix is one click on "Skip". A `TODO`
  issue tracks it.
- The language chosen in the wizard applies in the session, while the one of
  Settings still waits for the next sign-in (ADR 0104 §2 unchanged for
  Settings).

## Questions asked to Benoit

Asked in the thread on 2026-11-15. Benoit's answer: "Q4. C, pour le reste je
suis d'accord avec tes recommendations".

**Q1. Where does the choice appear?**

- **A (recommended, chosen):** a full-screen "Welcome" screen, between the
  sign-in progress and the desktop.
- **B:** a welcome window on the desktop. Almost no test breaks, but the
  window can be closed or hidden by accident.
- **C:** in the form that creates the first account. Covers neither the guest
  nor accounts created later.

**Q2. Who sees this screen?**

- **A (recommended, chosen):** every account at its first sign-in, the guest
  included; skipped when arriving by a direct link.
- **B:** everyone but the guest.
- **C:** only the first account of the machine.

**Q3. Accounts that already exist at the update?**

- **A (recommended, chosen):** they see the screen once, with their current
  settings selected.
- **B:** new accounts only; needs a migration that marks existing accounts.

**Q4. What does the screen hold?**

- **A (recommended):** the theme only, with "Skip".
- **B:** the theme and the accent colour.
- **C (chosen by Benoit):** a wizard with several steps: language, theme,
  wallpaper, time zone.

**Q5. Desktop and phone?**

- **A (recommended, chosen):** the themes of the device in use; the other
  form factor asks for its theme once, at its first session.
- **B:** both lists at once.
- **C:** once only, for the form factor of that day.

## Tasks

- [ ] `WelcomeSetupService` (state, plan of steps, language) and
  `LoginEnvironmentProvider.ReloadAsync`, with tests.
- [ ] Host view `Welcome` and component `WelcomeWizard` (four steps, desktop
  and phone, EN/FR).
- [ ] Tests: unit, E2E `WelcomeWizardTests`, updated test helpers.
- [ ] Docs FR/EN (`welcome-wizard.md`, `features.md`, memory), screenshots.
- [ ] `TODO` issue for the tests of the app repositories.
