> [Version française](../../../../fr/wasm/dev/adr/0268-store-in-start-menu-quick-access.md)

# ADR 0268: the Kazo OS Store in the Start menu's Quick access

## Status

Accepted on 2026-11-13: Benoit chose Q1 = A and, for Q2, the Store between
File Explorer and Settings (see [Questions](#questions-asked-to-benoit)). Series
`store-quick-access/base`.

- **Partly supersedes:** [ADR 0188](0188-hack-store-and-app-catalog.md)
  ("pinned by default in the Start menu's Pinned section") and
  [ADR 0212](0212-hack-store-app-build.md) §6 (the default list
  `org.kazoos.store`). The `defaultPinsApplied` mechanism of ADR 0212 §6 stays.
- **Builds on:** [ADR 0044](0044-settings-as-files.md) §8 (the
  `~/.config/kazoos/start-menu.json` file).

## Context

Benoit, 2026-11-13:

> make the `Kazo OS Store` be in the `Quick access` section rather than in the
> `Pinned` section

The Start menu has two places to launch an app outside the program list: the
**Pinned** section (the user's pins, which they order and remove) and the
**Quick access** panel on the right (fixed system links: File Explorer and
Settings). Since ADR 0212 §6, the Store is added once to every profile's pins.
It takes a place the user sees as their own, while it is a system tool like
Settings.

## Decision

1. **Quick access link.** The Start menu's Quick access panel gets a third
   link, **Store** ("Find and install apps"), between File Explorer (first)
   and Settings (last) (Q2). It launches `org.kazoos.store` like the other two links, through the
   desktop (`OnAppSelected`); it is greyed out when the app cannot be launched.
2. **No default pin anymore.** `StartMenuSettingsDocuments.DefaultPinnedAppIds`
   becomes empty: a new profile has no pins. The `defaultPinsApplied`
   mechanism stays for a future default pin.
3. **One-time migration (Q1 A).** `RetiredDefaultPinnedAppIds` lists the former
   default pins (`org.kazoos.store`). When a profile whose `defaultPinsApplied`
   holds one of them is loaded, the app is removed from the pins **and** from
   `defaultPinsApplied`, then the file is written. The migration therefore does
   not run again: if the user pins the Store again, it stays pinned. A Store the
   user pinned before any default pin (absent from `defaultPinsApplied`) stays
   too.
4. **Mobile.** Nothing changes: the mobile home screen lists every app and does
   not use the Start menu pins.

## Consequences

- The Store no longer appears twice for existing users.
- A user who deliberately kept the default pin loses it once; they can pin it
  again (the pin button stays in the program list).
- A failed write keeps the result in memory for the session; the next load
  tries again (as in ADR 0212 §6).

## Questions asked to Benoit

Asked in the thread on 2026-11-13.

**Q1. Remove the Store from the pins of existing profiles?**

- **A (recommended, chosen by Benoit):** yes, once, for the profiles where it was pinned by
  default. If it is pinned again, it stays.
- **B:** no, new profiles only. Existing profiles see it in Pinned and in Quick
  access until each user unpins it.

**Q2. Position of the Store in Quick access?**

- **A (recommended):** last, after File Explorer and Settings.
- **B:** first, above File Explorer.

Benoit's answer: neither A nor B, "Settings last, File Explorer first, so in
between".

## Tasks

- [x] Store link in Quick access (`AppLauncher.razor`, EN/FR texts).
- [x] Empty `DefaultPinnedAppIds`, `RetiredDefaultPinnedAppIds` and migration in
  `StartMenuPreferencesService`.
- [x] Tests: `StartMenuPreferencesServiceTests` (one-time migration, pin added
  back, user's pin kept), E2E `StartMenuQuickAccessTests`.
- [x] FR/EN docs: `app-installation.md`, `features.md`, memory.
