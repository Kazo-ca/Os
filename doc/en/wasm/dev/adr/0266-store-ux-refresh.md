> [Version française](../../../../fr/wasm/dev/adr/0266-store-ux-refresh.md)

# ADR 0266: a new home and a new app page for the Kazo OS Store

## Status

Accepted on 2026-11-13. Benoit answered the questions asked in the thread (see
the end of the document). Series `store-ux/base`.

- **Builds on:** [ADR 0188](0188-hack-store-and-app-catalog.md) (Store and
  catalog), [ADR 0212](0212-hack-store-app-build.md) (the Store v1),
  [ADR 0189](0189-app-sources-trust-and-per-user-install.md) (install for
  oneself or for everyone), [ADR 0213](0213-theme-packs-build.md) (Themes
  tab), [ADR 0257](0257-offer-a-store-app-for-an-unsupported-file.md)
  (`fileHandlers` in the catalog) and
  [ADR 0258](0258-direct-app-launch-link.md) (`?autolaunch=<appId>` link).
- **Changes:** the presentation described by ADR 0212 (home by category rows,
  Themes tab always visible). The catalog format, trust and installation do not
  change.
- **Repository touched:** Kazo-ca/Os
  (`wasm2/KazoOs/Apps/System/Kazo.Os.Apps.Store/`, and the SDK for the install
  progress).

## Context

The Store 1.0.0 of Kazo OS 3.3.3 was tested with Playwright on os.kazo.ca, in a
desktop window and in phone mode (2026-11-13).

- **Bugs**: an app's page does not open at the top (every view shares the same
  scrolling area); a click on a screenshot does nothing; Markdown lists written
  on two lines are cut; the app's name is written twice; the row of the
  Installed tab is centred; the search is ignored in the Installed tab; the
  keyboard focus is invisible; on a phone, the header takes three lines; an
  install only shows "Working…".
- **Design**: with six apps and one row per category, the home is almost empty
  and nothing is featured; the default window is too small for the app page;
  capabilities are technical identifiers; the install button scrolls away; the
  Themes tab leads to an empty page while no catalog offers a pack.

The bug fixes (work items 1 and 2) need no architecture decision. This ADR
settles the presentation choices of work items 3 to 6.

## Decision

1. **Same visual identity.** The Store keeps the OS theme tokens (`--kos-*`).
   No colour of its own.

2. **Home.**
   - A "Featured" banner shows one app: icon, name, short description, first
     screenshot and install button. The app is chosen automatically: the most
     recently published one (`publishedAt` of its latest compatible version)
     among the apps that are not installed; if there is none, the most recently
     published one. The catalog does not change.
   - A single grid of every app replaces the category rows, sorted from the
     newest to the oldest. Chips filter by category ("All" first, with the
     number of apps). The "See all" page of a category goes away; the OS Back
     (ADR 0220) removes the filter.
   - Each card has a button: "Install" (for me), "Open" or "Update". The trust
     badge is shown on a card only when the app is **not** verified; the app
     page always shows it.
   - The Store window opens larger by default (about 1100 × 720).
   - Loading shows empty cards instead of the word "Loading".

3. **Install button.**
   - On a card and in the "Featured" banner: an "Install" button, which
     installs for me, and, attached to its right, a menu button that offers
     "Install for everyone". The menu button is shown only when the current
     user is an administrator (`KazoUserInfo.IsAdministrator`).
   - On the app page: the two current buttons are kept ("Install for me",
     "Install for everyone").
   - The OS keeps its own administrator prompt (ADR 0189 §4): the Store does
     not decide security, it only hides a useless option.

4. **App page.**
   - A bar stays visible while scrolling: Back, then the icon, the name and the
     install button once the page's header has left the screen.
   - Capabilities are written in plain language, grouped, sensitive ones first;
     the technical identifiers stay reachable ("Show the technical
     identifiers"). A capability with no known text is shown by its identifier.
   - The page shows the file types the app opens (`fileHandlers`, ADR 0257), a
     "Source code" link instead of the raw address, and a button that copies
     the "Try it" link (ADR 0258).
   - The information panel follows the scroll.
   - Long texts (descriptions) use a system sans-serif font (`system-ui`), with
     no download; titles, buttons and labels keep the theme font.
   - The page becomes its own component (`StoreAppPage.razor`), out of
     `StoreWindow.razor`.

5. **Themes tab.** It is hidden while no catalog offers a pack and no pack is
   installed. `--page=themes` then leads to the home.

6. **Messages.** The result of an action shows in a message that closes by
   itself; an error stays until it is closed.

7. **Install progress.** During an install, the app page and the card show the
   current step (download, verification, installation) and, for the download,
   how far it is. Only the buttons of that app are disabled.
   `KazoAppPackages.InstallAsync` reports no progress today: work item 4 adds an
   optional overload to the SDK (`IProgress<KazoPackageInstallProgress>`),
   backward compatible, in `PublicAPI.Unshipped.txt`.

8. **Phone.** The header fits on two lines, the tabs move to the bottom of the
   screen, touch targets are at least 44 px, and portrait screenshots come
   before landscape ones.

## Consequences

- `StoreWindow.razor` (470 lines) is split: app page, card, banner and viewer
  become components with their `.razor.css` files.
- ADR 0212 stays true for installation and trust; its description of the home
  is replaced by this one.
- The SDK grows by one overload (work item 4): minor or patch version, Benoit's
  choice at release time.
- The French and English texts of the Store change (`localization/fr.json`,
  `Strings`); `app-installation.md` and `features.md` are updated by the work
  items.
- The Store screenshots in the docs will have to be taken again at the end of
  the series.

## Work items of the `store-ux/base` series

| No. | Branch | Content |
| --- | --- | --- |
| 0 | `store-ux/0-adr` | This ADR. |
| 1 | `store-ux/1-quick-fixes` | Scroll back to the top, Markdown lists, repeated title, Installed row, search in Installed, visible focus, phone header. |
| 2 | `store-ux/2-screenshot-viewer` | Screenshot viewer inside the Store window. |
| 3 | `store-ux/3-app-page` | Decisions 4 and 6: app page component, sticky bar, plain-language capabilities, information. |
| 4 | `store-ux/4-install-progress` | Decision 7: progress in the SDK and in the Store. |
| 5 | `store-ux/5-home` | Decisions 2, 3 and 5: banner, grid, filters, cards and their menu button, window size, Themes tab. After work item 3. |
| 6 | `store-ux/6-mobile` | Decision 8, and a check of the whole series on a phone. After work item 5. |

Mock-ups validated by Benoit: home and app page, desktop and phone (Artifact
"Maquettes du Kazo OS Store" of the series' thread).

## Questions asked to Benoit (2026-11-13)

First set:

1. Scope: A fixes and app page only; **B ✨ A plus a new home, same visual
   identity (chosen)**; C a new visual identity.
2. Install button of the app page: A ✨ one button and a menu; **B two buttons
   (chosen)**.
3. Enlarging a screenshot: **A ✨ viewer inside the Store window (chosen)**;
   B a layer over the whole OS screen; C a separate window.
4. Empty Themes tab: **A ✨ hide it (chosen)**; B keep it.
5. Mock-ups before the code: **A ✨ yes (chosen)**; B no.

Second set, about the mock-ups:

1. Font of the descriptions: **A ✨ sans-serif for long texts (chosen)**; B
   everything in the theme font.
2. "Install" button of a card: A ✨ it installs for me; B no install button on
   cards. Benoit's answer: "Installe pour moi, avec un dropdown à côté qui a
   l'option installer pour tout le monde si on est admin."
3. Choice of the featured app: **A ✨ automatic, the newest (chosen)**; B a
   field in the catalog.
