> [Version française](../../fr/wasm/features.md)

# Kazo OS: overview and features

## At a glance

Kazo OS is a **complete operating system that runs in the browser**. It
offers a desktop with windows, a mobile interface, a persistent file system,
user accounts, a terminal with a real shell and more than 70 Linux commands, a
simulated network with its own web sites, and a suite of core applications. It
works offline and installs like an app, with nothing more than a recent
browser.

Kazo OS is also a **development platform**. Its public SDK, published on
NuGet, lets you write windowed applications, shell commands, services and web
sites in C# that run inside the OS. Developers get everything the OS already
provides (windows, files, network, security, themes, languages, mobile) and
focus on their application.

Kazo OS is written in C# with Blazor WebAssembly (.NET 10). The current
version is **2.2.2**. Try it at <https://os.kazo.ca/>.

## The goal of the ecosystem

The Kazo OS ecosystem lets a developer **write an application once, in C#,
and run it wherever Kazo OS runs**: online, offline, on their own server or
embedded in another product. The OS provides the complete environment
(desktop, mobile, files, terminal, network, accounts), and the application is
then distributed through the **Kazo OS Store**, the Kazo OS app store.

The ecosystem is made of:

- **Kazo OS**, the OS itself (this repository);
- **the Kazo OS SDK**, the `Kazo.Os.*` NuGet packages used to build on the
  OS from another repository;
- **the template repository** [Os-AppTemplate](https://github.com/Kazo-ca/Os-AppTemplate),
  the starting point of a new application;
- **the catalog** [Os-AppMarketPlace](https://github.com/Kazo-ca/Os-AppMarketPlace),
  the reviewed list of applications offered by the Kazo OS Store (in development);
- **the applications** themselves, each in its own repository.

## Why build for or with Kazo OS

- **A familiar API.** The file API looks like `System.IO` (`KazoFile`,
  `KazoDirectory`, `KazoPath`…) and an application is described by a plain C#
  class (`[AppManifest]`). A .NET developer feels at home right away.
- **Everything is already provided.** Windows, context menus, file dialogs,
  clipboard, notifications, themes, English and French translation,
  accessibility, virtual keyboard, mobile interface: the application gets them
  without writing any code.
- **No server to maintain.** The OS and its applications run entirely in the
  user's browser, offline included. An optional server adds synchronization
  and access to the real network when you want them.
- **Secure by design.** Each application declares the capabilities it needs
  (real network, other users' files…). Sensitive capabilities require an
  administrator's approval, as on a real system.
- **Real Linux behavior.** Everything is a file: settings in `/etc` and
  `~/.config`, logs in `/var/log`, processes in `/proc`. An application fits
  in with the shell, the permissions and the existing tools.
- **A stable, versioned SDK.** The packages share a single version, the public
  surface is checked on every build, and application compatibility is
  verified by tests.
- **Complete development tools.** `dotnet new` templates, template repository,
  debugging in Visual Studio, unit and end-to-end tests, automatic releases.
- **Built-in distribution.** An application installs from its GitHub
  repository with one command, or with one click from the Kazo OS Store.

## Ways to use and deploy Kazo OS

| Use | For whom | Description |
| --- | --- | --- |
| **Online** | Everyone | Open <https://os.kazo.ca/>. Kazo OS installs like an app (PWA) and then works offline. |
| **Self-hosted static site** | Organizations, teachers | Each version publishes a `kazoos-wasm-<version>.zip` archive in the [GitHub Releases](https://github.com/Kazo-ca/Os/releases). It can be dropped on any static hosting (GitHub Pages, web server). See [`github-pages-deployment.md`](dev/github-pages-deployment.md). |
| **With the Kazo OS server** | Organizations, advanced users | The optional ASP.NET Core server serves the OS and adds accounts synchronized across devices and a relay to the real network. See [`server-hosting.md`](dev/server-hosting.md). |
| **Embedded in your own product** | Developers, companies | The NuGet packages and the `dotnet new kazoos-host` template let you compose your own host with your own applications. See [`building-on-kazoos.md`](framework/building-on-kazoos.md). |
| **Developing applications** | Developers | SDK NuGet packages, `dotnet new` templates, template repository, then installation from the terminal with `kazopkg`. See [`sdk-developer-guide.md`](framework/sdk-developer-guide.md). |
| **Installable local server** | Advanced users | Every version publishes the Kazo OS server for Windows, Linux and macOS (`kazoos-server-<version>-<system>`) and the Docker image `ghcr.io/kazo-ca/kazoos-server`. It installs as a service on your computer or on a machine of your local network, without being exposed to the internet, and the online OS links to it with a pairing code (Settings > Server connection > Local server). See [`installable-server.md`](dev/installable-server.md). |

## Available features

This section lists what is released in the `main` branch and online.

### Desktop and windows

- **Desktop** with icons, shortcuts and context menus, like a desktop OS.
  ([`desktop.md`](dev/desktop.md))
- **Windows** that can be moved, resized, maximized or snapped to the screen
  edges, with plain animations to open, close, minimize into the taskbar,
  maximize and snap, turned off by "Reduce motion".
  ([`window-model.md`](framework/window-model.md), [`window-animations.md`](dev/window-animations.md))
  A window that comes to the front does not move in the page: an app in a
  frame (iframe), such as Kazo Word or the browser, keeps its page, also on
  mobile when switching apps. ([ADR 0244](dev/adr/0244-stable-window-order-in-the-page.md))
- **Windows that stay on their screen** (version 3.3.3): an app can keep its
  window from changing screen (`window.canChangeScreen: false`). Craft apps in
  an iframe (Kazo Word and the next ones) stay on their screen with the
  warning "known issue, a fix is in progress": changing screen reloaded the
  iframe and lost the document. The real fix planned: build a WebAssembly
  version of each Craft app ourselves, without an iframe.
  ([ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md), [#482](https://github.com/Kazo-ca/Os/issues/482))
- **Window styles inspired by WinForms** (version 3.1.0): an app picks the
  `Normal`, `Tool` (close only, the style of the OS dialogs) or `None` style
  (client area only, transparent: a click where the app draws nothing reaches
  the desktop), and the `ShowIcon`, `Resizable`, `MaximizeBox`, `MinimizeBox`,
  `ShowInTaskbar` and `Modal` options, in its manifest or at run time. It opens
  its own windows and modal dialogs (`ShowWindowAsync`, `ShowDialogAsync`) and
  minimizes, maximizes, moves or resizes its window.
  ([ADR 0234](dev/adr/0234-window-styles-inspired-by-winforms.md),
  [`window-model.md`](framework/window-model.md))
- **Taskbar and Start menu** with pinned applications and open windows. A
  right click on the taskbar gives the window menu, "New window", the task
  manager, "Show the desktop" and cascaded or tiled windows.
  ([`taskbar-context-menus.md`](dev/taskbar-context-menus.md))
  Hovering a window's button shows a live preview; resting on it shows the
  window alone ("peek"). ([`taskbar-window-previews.md`](dev/taskbar-window-previews.md))
- **Mouse Back and Forward buttons**: the Back button, Alt+← and the phone's
  Back (Android button or gesture, iOS swipe) go back inside the active app
  (File Explorer, browser, Settings, Kazo OS Store) instead of moving the tab back
  and leaving Kazo OS; the Forward button and Alt+→ go forward.
  ([ADR 0220](dev/adr/0220-mouse-back-forward-buttons.md))
  The Start menu shows the signed-in user and their Power button: sign out or
  restart. ([ADR 0195](dev/adr/0195-start-menu-power-button.md))
  A dot next to the clock shows unread notifications, with their count on
  hover. ([ADR 0196](dev/adr/0196-unread-notifications-dot.md))
- **Notification bubbles**: each notification from the OS or an app shows for
  a few seconds above the taskbar (at the top on a phone), then stays in the
  clock panel. A ready update arrives there with its "Restart" button.
  ([`notification-toasts.md`](dev/notification-toasts.md))
  **Do not disturb** (clock panel or Settings ▸ Notifications) stops the
  notification bubbles and sounds without losing the notifications; a moon
  next to the clock shows it. ([ADR 0216](dev/adr/0216-do-not-disturb.md))
- **Multiple screens**: each extra screen opens in its own browser window,
  with its own taskbar, and the OS remembers them. Windows can be dragged from
  one screen to another. ([`screen-service.md`](dev/screen-service.md),
  [`per-screen-taskbars.md`](dev/per-screen-taskbars.md),
  [`drag-between-screens.md`](dev/drag-between-screens.md))
- **Detached windows**: an application can show its content in a real,
  separate browser window. ([`detached-display-windows.md`](dev/detached-display-windows.md))

### Mobile interface

- **Phone-style home screen** with shortcut pages, a dock and an app drawer.
  ([`mobile-home-screen.md`](dev/mobile-home-screen.md))
- **Touch-friendly applications**, including the file explorer and
  full-screen dialogs.
- **Virtual keyboard** provided by the OS.

### Files

- **Persistent file system** stored in the browser, with home folders,
  permissions and owners as on Linux. A file may be up to 128 MiB (version
  3.3.2). ([`project-overview.md`](dev/project-overview.md#file-system),
  [ADR 0245](dev/adr/0245-raise-the-maximum-file-size.md))
- **File explorer** with views, search, drag and drop, upload and download.
  ([`file-explorer.md`](dev/file-explorer.md))
- **Improved file explorer**: resizable left pane, hover tooltips, total
  folder size, folder content previews, Compress, Download, Copy path and
  Send to menu, drag out of the browser to download (Chrome and Edge),
  network drives refreshed on their own.
  ([ADR 0185](dev/adr/0185-file-explorer-improvements.md))
- **`.zip` archive opened as a folder** in the explorer, read-only: an opened
  file is extracted to `/tmp`, dragging out of the archive extracts.
  ([ADR 0185](dev/adr/0185-file-explorer-improvements.md),
  [`file-explorer.md`](dev/file-explorer.md))
- **Trash** with restore. ([`trash.md`](dev/trash.md))
- **Default applications** per file type, for each user.
  ([`user-file-associations.md`](dev/user-file-associations.md))
- **Store app offered for a file**: opening a file no installed app supports
  offers the Store apps that declare its type (for example Kazo Word for a
  `.docx`) in the File Explorer and "Open with", with "View in the Store"; the
  file opens after the install. `open` in the terminal shows the `kazopkg
  install` command. ([ADR 0257](dev/adr/0257-offer-a-store-app-for-an-unsupported-file.md))
- **Search and categories in "Open with"**: the File Explorer's box filters the
  installed apps by name, id or description, and by category; each row keeps
  its name and its id inside its border, on a computer as on a phone.
  ([`file-explorer.md`](dev/file-explorer.md))
- **Computer folder mounted as a network drive**: a real folder of the
  computer appears in the OS (Chrome and Edge on desktop).
  ([ADR 0181](dev/adr/0181-host-folder-mounted-as-a-network-drive.md))
- **Network drives readable by apps**: an app with `filesystem.user-home.*`
  reads and writes the user's network drives like their personal files,
  without a dangerous capability; another user's drive and `/mnt` stay system
  files. ([ADR 0231](dev/adr/0231-network-drives-count-as-personal-files.md))
- **Cloud storage as a network drive**: the user connects their Dropbox, their
  OneDrive (personal or work) or an S3-compatible storage and finds it under
  `/mnt`, in the File Explorer, Settings ▸ Network ▸ Cloud accounts and
  `mount -t`. Everything happens in the browser, with no server; they choose
  between their whole storage and a folder reserved for Kazo OS. Apps go
  through the SDK behind `cloud-accounts.manage`.
  ([ADR 0267](dev/adr/0267-cloud-drives-as-network-drives.md),
  [ADR 0269](dev/adr/0269-cloud-tokens-stay-on-the-device.md),
  [ADR 0270](dev/adr/0270-cloud-drives-in-the-app-sdk.md),
  [`cloud-drives.md`](dev/cloud-drives.md))
- **Faster and lighter files**: content is kept in binary instead of base64 (about 25%
  less space, saving 52.5 MiB from 3.4 s to 1.0 s in Chromium), each 256 KiB chunk checked
  by its fingerprint when read; old content is converted once at the first boot, behind a
  waiting screen. ([ADR 0272](dev/adr/0272-binary-file-content.md))
- **Large files read and written in pieces**: beyond 16 MiB, a file opens as a stream that
  loads only the chunks read, and is saved in batches handed to the browser, which halves
  the memory taken. ([ADR 0275](dev/adr/0275-partial-read-and-file-address.md) §1, 2 and 4)
- **A single copy of the files of an installed app**: the service worker serves an app's
  files from its folder, with no copy in a cache; a 64 MiB app whose previous version is kept
  takes 128 MiB instead of 236 MiB. ([ADR 0274](dev/adr/0274-single-copy-of-app-files.md))
- **Space reclaimed after a rewrite or a deletion**: old content is erased after 1 day, or
  after 5 minutes when space is short. ([ADR 0273](dev/adr/0273-short-orphan-content-retention.md))
- **Backup and restore** of the whole machine in a downloaded archive.
  ([`system-backup.md`](dev/system-backup.md))

### Terminal and shell

- **Full terminal**, with colors and copy and paste.
  ([`terminal-emulator.md`](dev/terminal-emulator.md))
- **POSIX shell**: variables, pipes, redirections, scripts, conditions and
  loops. ([`shell-interpreter.md`](dev/shell-interpreter.md))
- **More than 70 Linux commands**: `ls`, `cp`, `grep`, `nano`, `sudo`, `ps`,
  `top`, `systemctl`, `zip`, `curl`, `ping`, `nmap` and many more.
  ([`linux-commands.md`](dev/linux-commands.md))

### Core applications

- **Text editor** and **code editor** with syntax highlighting.
  ([`text-editor.md`](dev/text-editor.md), [`code-editor.md`](dev/code-editor.md))
- **Web browser** for the sites of the simulated network and, with a connected
  Kazo OS server, real sites through its proxy (cookies and signing in to
  accounts included); an address-bar icon tells where the page came from.
  ([`browser-html-rendering.md`](dev/browser-html-rendering.md), [ADR 0210](dev/adr/0210-browser-real-sites-through-the-proxy.md))
- **Settings**: accounts, appearance, screens, language, accessibility,
  system. ([`settings-app.md`](dev/settings-app.md))
- **Task manager** and system monitor (processes, memory, CPU).
  ([`task-manager.md`](dev/task-manager.md))
- **KazoPaint** (drawing), **Calculator**, **Theme Editor**, **Icon Viewer**,
  **Error Log Viewer**, **Diagnostic** and **Web Server Manager**.
  ([`kazo-paint.md`](dev/kazo-paint.md), [`theme-editor.md`](dev/theme-editor.md))

### Network and web

- **Simulated network** with domain names, ports, `ping` and HTTP.
  ([`sdk-network.md`](framework/sdk-network.md))
- **Internal web sites** under `.hack`, served from the OS: static sites,
  ASP.Kazo sites (controllers, Razor views) and Blazor sites.
  ([`static-sites.md`](dev/static-sites.md),
  [`asp-kazo-developer-guide.md`](framework/asp-kazo-developer-guide.md),
  [`blazor-sites.md`](framework/blazor-sites.md))
- **Access to the real network** through the optional server, for allowed
  applications. ([`server-network-relay.md`](dev/server-network-relay.md))
- **Real network for chat apps**: `KazoClientWebSocket` reaches a real
  WebSocket server through the browser, without a Kazo OS server; through the
  relay, TLS to the target (`KazoTcpConnectOptions { UseTls = true }`,
  certificate checked by the server) and ports declared by the app in its
  manifest, approved in the OS then issued by the server, unless `BlockedPorts`.
  ([`server-network-relay.md`](dev/server-network-relay.md),
  [ADR 0235](dev/adr/0235-real-network-for-chat-apps.md),
  [ADR 0233](dev/adr/0233-server-issued-real-network-grants.md))
- **Installable local server**: the Kazo OS server installs as a service
  (Windows, Linux, macOS, Docker) on your computer or on a machine of your
  local network in HTTPS, and the OS, even online on GitHub Pages, links to it
  with a pairing code. ([`installable-server.md`](dev/installable-server.md),
  [ADR 0191](dev/adr/0191-installable-local-server.md))

### Accounts and security

- **Multiple accounts** with passwords, administrators and `sudo`. An
  administrator adds and removes users.
  ([ADR 0169](dev/adr/0169-administrator-adds-and-removes-users.md))
- **Start as a guest**: try the OS without creating an account, then "Create
  my account", keeping the guest's files or not; credits on the sign-in
  screen. ([`guest-account.md`](dev/guest-account.md),
  [ADR 0187](dev/adr/0187-guest-start-and-sign-in-credits.md))
- **Passkeys**: sign in with a fingerprint, face, device PIN or security key.
  ([`passkeys.md`](dev/passkeys.md))
- **Forgot password**: from the sign-in screen, erase the machine and start
  over, after a confirmation; can be turned off at deployment. Every reset
  erases everything the OS keeps in the browser.
  ([`sign-in-factory-reset.md`](dev/sign-in-factory-reset.md),
  [ADR 0226](dev/adr/0226-forgot-password-factory-reset.md),
  [ADR 0227](dev/adr/0227-factory-reset-erases-everything.md))
- **Application capabilities** and administrator approval of sensitive
  capabilities. ([`capabilities.md`](framework/capabilities.md),
  [`dangerous-capability-approval.md`](dev/dangerous-capability-approval.md))
- **Sensitive settings** protected and encrypted.
  ([`sensitive-settings.md`](dev/sensitive-settings.md))
- **Synchronization across devices** when the OS is connected to the optional
  server. ([`synchronization.md`](dev/synchronization.md))

### System

- **Processes** with priorities, limits and signals; commands run in Web
  Workers so they never block the interface.
  ([`worker-processes.md`](dev/worker-processes.md),
  [`process-limits.md`](dev/process-limits.md))
- **CPU and memory accounting** per process, and an OOM killer when memory
  runs low. ([`resource-accounting.md`](dev/resource-accounting.md),
  [`oom-killer.md`](dev/oom-killer.md))
- **Unresponsive windows** detected, with an offer to close them.
- **An app that crashes stays in its window**: the window shows "App ran into
  a problem" with the error, *Try again* and *Close the app*; the rest of the
  OS keeps running.
  ([ADR 0238](dev/adr/0238-an-app-failure-stays-in-its-window.md))
  ([`process-priority-and-unresponsive-windows.md`](dev/process-priority-and-unresponsive-windows.md))
- **Detailed error page**: the app and method at fault, the exception, its
  message, the folded stack, "Copy details" and "Reload", with passwords and
  tokens masked. ([ADR 0240](dev/adr/0240-detailed-error-page.md))
- **Virtual `/proc` and `/dev`**, logs in `/var/log`.
  ([`virtual-proc-and-dev.md`](dev/virtual-proc-and-dev.md),
  [`logging.md`](dev/logging.md))
- **New version announcements**: an open session learns that a new version is
  available; it downloads in the background and "Restart to update" opens it in
  every tab. ([ADR 0174](dev/adr/0174-announce-a-new-version-to-open-sessions.md),
  [ADR 0223](dev/adr/0223-restart-onto-the-new-version.md))
- **Share preview**: a link to `os.kazo.ca` shared on Facebook, LinkedIn or X
  shows the Kazo OS image, its title and its description.
  ([`github-pages-deployment.md`](dev/github-pages-deployment.md#share-preview-facebook-linkedin-x))
- **Build number and version status on the sign-in screen**: next to
  "Kazo OS 3.3.3", the build number (commit count, the same for the site, the
  Release and the server) and a badge "Up to date", "Outdated: …" or "Status
  unknown (offline)", checked at boot.
  ([ADR 0236](dev/adr/0236-build-number-and-version-status-at-sign-in.md))
- **Always on the latest version**: when a newer version is deployed, Kazo OS
  shows "Updating Kazo OS" at start and restarts onto it; an app is not opened
  on an outdated copy (an "Update required" dialog offers the restart); an
  installed app finds its files right after a deployment, even after Ctrl+F5;
  a Ctrl+F5 also replaces the service worker of an older version, and the next
  F5 stays on the latest one.
  ([ADR 0237](dev/adr/0237-activate-the-worker-of-a-newer-page.md),
  [ADR 0239](dev/adr/0239-start-on-the-latest-version.md),
  [ADR 0241](dev/adr/0241-replace-an-older-worker-at-boot.md))
- **What's new since your last visit**: the Settings ▸ What's new page lists the
  changes by version with a **New** badge; a notification after sign-in points out
  what has not been seen. The same list writes the
  notes of the GitHub Release, of the NuGet packages and
  [`CHANGELOG.md`](../../../CHANGELOG.md).
  ([`change-list.md`](dev/change-list.md), [ADR 0186](dev/adr/0186-whats-new-change-list.md))

### Personalization

- **Illustrated app icons**: the system apps have colour icons that show what
  each app does, and the Start menu, the desktop and the explorer show each
  app's own icon, installed apps included.
  ([ADR 0232](dev/adr/0232-illustrated-app-icons-everywhere.md))
- **Welcome wizard**: at the first sign-in, users choose their language,
  theme, wallpaper and time zone before the desktop; each choice applies at
  once and the wizard can be skipped.
  ([`welcome-wizard.md`](dev/welcome-wizard.md),
  [ADR 0276](dev/adr/0276-welcome-wizard-at-first-sign-in.md))
- **OS themes**, and an editor to create your own.
  ([`theme-files.md`](framework/theme-files.md))
- **Image wallpapers**: a PNG, JPEG or WebP image (4 shipped with the OS,
  your own or those of a shared folder), for every screen or one screen at a
  time, and a separate image for the mobile home screen, in Settings ▸ Appearance;
  "Set as wallpaper" in the menu of an image sets the desktop one.
  ([ADR 0202](dev/adr/0202-desktop-wallpaper-images.md), [ADR 0217](dev/adr/0217-mobile-home-wallpaper.md), [ADR 0218](dev/adr/0218-set-as-wallpaper-file-menu.md), [`wallpapers.md`](dev/wallpapers.md))
- **English and French**, the language follows the user's locale.
  ([`localization.md`](framework/localization.md))
- **Accessibility**: display and animation preferences, keyboard navigation.
  ([`accessibility.md`](dev/accessibility.md))
- **Local time and time zone**: the clock, `date`, `ls -l` and the apps show
  the time in the user's zone, automatic or chosen in Settings ▸ Date and time.
  ([`time-zone.md`](framework/time-zone.md))
- **Cursor themes**: the pointer of the whole OS as PNG images, System,
  Kazo OS Neon, Gothic Crimson or your own themes, chosen in Settings ▸
  Appearance ▸ Cursors. ([`cursor-themes.md`](framework/cursor-themes.md))
- **Icon packs**: PNG images replace the icons of apps, folders and files
  everywhere in the OS, chosen in Settings ▸ Appearance; a "Néon" sample pack
  is included. ([`icon-packs.md`](dev/icon-packs.md),
  [ADR 0203](dev/adr/0203-icon-packs.md))
- **System sounds**: notifications, warnings, errors, sign-in and sign-out. Two
  built-in themes (Kazo OS and Soft), your own themes in OGG or MP3, volume and
  mute in Settings ▸ Sound.
  ([`system-sounds.md`](dev/system-sounds.md), [ADR 0204](dev/adr/0204-os-event-sounds.md))

### For developers

- **Public SDK on NuGet** (`Kazo.Os.*`, version 2.2.2): file API, windows,
  terminal, network, background jobs, clipboard, dialogs, Blazor components.
  ([`sdk-reference.md`](framework/sdk-reference.md),
  [`sdk-packages.md`](framework/sdk-packages.md))
- **Shared module of the Craft apps** (version 3.3.3): `Kazo.Os.AppSdk.Craft`,
  shipped and served by the OS, holds the iframe frame (proxy, file picker,
  shortcuts, per-user settings) and the document logic of the Craft apps
  (Kazo Word, Kazo Spreadsheets…), written once instead of one copy per app.
  ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md),
  [`craft-apps.md`](framework/craft-apps.md))
- **Craft app exports in Kazo OS** (version 3.3.4): a Craft app's exports
  (Export, Print to PDF, OCR… of Kazo Pdf) go through the OS save dialog
  instead of the browser's downloads, and are saved as copies.
  ([ADR 0264](dev/adr/0264-craft-app-exports-reach-the-window.md))
- **`dotnet new` templates** for a windowed application, a command, a
  service, a site, a complete host or a whole application repository, shown
  in Visual Studio and Rider.
  ([`sdk-developer-guide.md`](framework/sdk-developer-guide.md))
- **Publishing an application on its `kazoos-packages` branch**: the
  template's `publish-packages.yml` workflow publishes the package, unsigned;
  applications are signed only in the Kazo OS Store repository.
  ([ADR 0219](dev/adr/0219-app-authors-signing-tool.md),
  [`app-repository.md`](framework/app-repository.md))
- **Install an application from its GitHub repository** with the `kazopkg`
  command. ([ADR 0182](dev/adr/0182-install-apps-from-github.md),
  [`app-installation.md`](dev/app-installation.md)) Its images, scripts and
  Web Workers are served at the same address as for an application built into
  the OS, so an application that wraps a JavaScript project works once
  installed, also after a Ctrl+F5 or an install made in another tab.
  ([ADR 0206](dev/adr/0206-serve-installed-app-assets.md),
  [ADR 0225](dev/adr/0225-serve-installed-app-files-to-every-page.md)) Its
  package holds all its static resources (collocated scripts included) and the
  OS loads its scoped CSS: installed, it looks the same as when built into the
  OS. ([ADR 0221](dev/adr/0221-pack-all-static-web-assets-of-an-app.md))
  Its translations (satellite assemblies of per-culture `.resx` files) are in
  its package and loaded by the OS: once installed, it speaks French as when
  compiled into the OS. ([ADR 0222](dev/adr/0222-installed-app-satellite-assemblies.md))
  It finds the whole .NET core library (`System.Private.CoreLib` is not
  trimmed), and the install refuses, with the exact method, an app that calls
  a method the published OS lacks, instead of letting it crash.
  ([ADR 0242](dev/adr/0242-framework-api-for-installed-apps.md))
  It can show its own HTML pages in an iframe, with parameters in the address,
  also once the OS is published and offline.
  ([ADR 0243](dev/adr/0243-serve-installed-app-pages.md))
- **Trusted sources and install for oneself**: an application from a trusted
  source is installed for oneself without an administrator; an untrusted
  source, an application of the block list or an install for everyone go
  through a two-step approval (an administrator signs in, then a window shows
  the application, its source and its capabilities). Sources and block list in
  Settings ▸ Sources and block list and `kazopkg sources` / `kazopkg policy`.
  Applications of the official Kazo OS Store are signed by the catalog and checked
  by the OS: an unsigned or changed package is refused.
  ([ADR 0189](dev/adr/0189-app-sources-trust-and-per-user-install.md),
  [ADR 0208](dev/adr/0208-hack-store-sources-trust-build.md),
  [ADR 0214](dev/adr/0214-hack-store-hot-add-signing-transactions.md))
- **Kazo OS Store**: the Kazo OS app store, in the Start menu's Quick access
  panel ([ADR 0268](dev/adr/0268-store-in-start-menu-quick-access.md)),
  on desktop and mobile. Home by categories, search, app page (screenshots,
  description, size, license, capabilities, trust), install for oneself or for
  everyone, open, update, uninstall, Installed tab and "Restart to finish". It
  checks for updates at sign-in then every 6 hours and says so in a
  notification; `kazopkg install <appId>` reads the same catalogs and asks
  the user to choose when several list the app. Since version 3.3.4: featured
  app, grid filtered by category, "Install" button on the cards, screenshot
  viewer, app page with a bar that stays at the top and plain-language
  capabilities, install progress, and on the phone tabs at the bottom and
  44 px touch targets ([ADR 0266](dev/adr/0266-store-ux-refresh.md)).
  ([ADR 0188](dev/adr/0188-hack-store-and-app-catalog.md),
  [ADR 0212](dev/adr/0212-hack-store-app-build.md),
  [ADR 0215](dev/adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md))
- **Launch an app from a direct link** (version 3.3.3): the link
  `https://os.kazo.ca/?autolaunch=<appId>` starts Kazo OS as guest when there
  is no account, installs the app from the signed official catalog and
  launches it; with an account, it opens the Store at the app's page. The link
  survives the restart for an update (version 3.3.4, [ADR 0265](dev/adr/0265-direct-link-survives-update-restart.md)).
  ([ADR 0258](dev/adr/0258-direct-app-launch-link.md), [`app-launch-link.md`](dev/app-launch-link.md))
- **Installs without a restart, and safe**: an application installed or
  updated opens at once if it was not already loaded. The packages of a source
  that has a key are signed and verified, the install is transactional
  (nothing broken after a power cut, `kazopkg repair`), the previous version
  is kept for "Go back to" (`kazopkg rollback`) and the application's data is
  backed up before its first start in a new version. ([ADR 0214](dev/adr/0214-hack-store-hot-add-signing-transactions.md))
- **Theme packs**: the Kazo OS Store Themes tab installs `.kazotheme` packs
  (themes, wallpapers, icons, sounds and cursors), for oneself without an
  administrator or for everyone with the two-step approval. A pack holds no
  code (no SVG, no remote address, limited sizes) and its parts show up in
  Settings ▸ Appearance.
  ([ADR 0190](dev/adr/0190-theme-packs.md),
  [ADR 0213](dev/adr/0213-theme-packs-build.md))
- **`kazoos-solution` application repository template**: application, host
  and tests ready to run, the OS as a submodule to debug in its sources, NuGet
  packages in production, CI and submission to the Kazo OS Store catalog with a
  tag. ([ADR 0176](dev/adr/0176-app-repository-template.md),
  [ADR 0207](dev/adr/0207-solution-template-implementation.md),
  [`app-repository.md`](framework/app-repository.md))
- **Automatic releases**: GitHub Release, NuGet packages and OS archive for
  each new version. ([`releases.md`](dev/releases.md))
- **Browser tests every night**: the Playwright suites run at night on `main`
  when it changed; a failure opens an issue.
  ([`ci-workflows.md`](dev/ci-workflows.md#browser-tests-every-night))
- **Installable features ("Feature" app type)**: an optional feature of the OS installed for the whole machine by an administrator, with its child entry points. ([ADR 0260](dev/adr/0260-feature-app-type.md), [`app-installation.md`](dev/app-installation.md#features-adr-0260))
- **App libraries and shared versions**: a package carries the app's libraries, and a shared library (`KazoOsSharedLibrary`) is loaded once, in its newest compatible version. ([ADR 0261](dev/adr/0261-app-libraries-and-shared-versions.md), [ADR 0256](dev/adr/0256-app-libraries-implementation-adjustments.md))
- **Feature services**: an app asks for an interface a Feature provides with `KazoFeatures.GetServiceAsync<T>()`, with no dependency in its binary. ([ADR 0262](dev/adr/0262-feature-service-registry-and-async-resolution.md), [`feature-services.md`](framework/feature-services.md))

## In development

This section lists the planned features, with their architecture decision
(ADR) and their tracking issue. When an ADR is not in `main` yet, the link
points to its PR.

### Desktop, windows and files

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Switch user | Open another session without closing the first one. Stays greyed out in the Power menu by Benoit's choice: one session at a time, Sign out to change account. | [0195](dev/adr/0195-start-menu-power-button.md), [0201](dev/adr/0201-switch-user-stays-greyed.md) | [#290](https://github.com/Kazo-ca/Os/issues/290) |
| File address served on demand | An app gets an address to read a large file (audio, video) without copying it in memory, with partial reads to seek in a track. `storage/base` series; the partial read is done (work item 3), the address is still to do. | [0275](dev/adr/0275-partial-read-and-file-address.md) | [#379](https://github.com/Kazo-ca/Os/issues/379) |
| Drag files onto an app | Public SDK contract to receive files dragged from the File Explorer or the desktop. | To write | [#380](https://github.com/Kazo-ca/Os/issues/380) |

### Network

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Kazo server relay state for apps | An app with access to the real network knows, without trying, whether the Kazo server's relay is ready (no server, relay turned off, permission pending), and a connection failure gives the real reason. `todo-wins/base` series. | [0278](dev/adr/0278-real-network-relay-state-for-apps.md) | [#418](https://github.com/Kazo-ca/Os/issues/418) |

### Kazo OS Store

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Application catalog | Catalog format, checking and publishing of the offered applications. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [AppMarketPlace #1](https://github.com/Kazo-ca/Os-AppMarketPlace/issues/1) |
| API check before publishing | An app's build (`KazoOsPack`) and the Store's CI refuse an app that calls an API the published OS removed, listing the missing members, instead of refusing it only at install time. `todo-wins/base` series. | [0277](dev/adr/0277-trimmed-api-check-before-publishing.md) | [#346](https://github.com/Kazo-ca/Os/issues/346) |
| Beta channel and automatic app updates | A beta version of Kazo OS online (`os-beta.kazo.ca`), the SDK as a prerelease on nuget.org, and each app rebuilding and offering its update when the SDK ships. The OS side is done: the `beta` branch, the beta site with its "Beta" badge, the beta catalog and the reusable app workflows. Prereleases 3.4.0-preview.1 to 4 shipped on 2026-11-15, each app repository is aligned (work item 7) and the apps offered their beta version by themselves; the stable 3.4.0 follows. What is left is the trial of the app updates for the stable 3.4.0 (work item 8). | [0271](dev/adr/0271-beta-channel-and-automatic-app-updates.md), [plan](dev/plans/release-channels-plan.md) | [#456](https://github.com/Kazo-ca/Os/issues/456) |

### Personalization

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Catalog checks of theme packs | The catalog CI checks the `kind: "theme-pack"` entries as the OS does at install. | [0213](dev/adr/0213-theme-packs-build.md) | [AppMarketPlace #4](https://github.com/Kazo-ca/Os-AppMarketPlace/issues/4) |

### Project name

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| HackerOS becomes Kazo OS | The name HackerOS is already taken by a Linux distribution. Everything is renamed: displayed name, `Kazo.Os.*` namespaces and packages (SDK 3.0.0), `Kazo` prefix instead of `Hack`, commands, extensions and paths; user data is migrated on the first boot. | [0228](dev/adr/0228-rename-to-kazoos.md), [0229](dev/adr/0229-kazo-os-display-name-and-new-address.md), [0230](dev/adr/0230-rename-migration-by-previous-version.md) | Series `rename/base` |

### Installable features (Features) and Os-x86

Os-x86 ([Os-x86](https://github.com/Kazo-ca/Os-x86)) is an optional OS
feature, installed system-wide by an administrator, that runs x86 virtual
machines in the browser (Linux, FreeDOS, Windows programs through Wine); the
BoxedWine app is its interface. It asks the OS for the "Feature" app type and
the extension points below (lot 1, `feature/base` series, ADR 0246 to 0256 and 0260 to 0262;
one row per ADR).

| Feature | Description | ADR | Issue |
| --- | --- | --- | --- |
| Permissions declared by a Feature | A Feature declares its own permissions, dangerous ones included; they follow the rules of the OS catalog and are never granted silently. | [0246](dev/adr/0246-provider-declared-permissions.md) | — |
| Roles declared by a Feature | A Feature declares roles an administrator assigns; every sensitive operation goes through the OS authentication. | [0247](dev/adr/0247-feature-declared-roles.md) | — |
| Dependency resolution | The Store and `kazopkg` install the dependencies declared in the manifest, with one consent; removing a package warns and disables the dependent apps. | [0248](dev/adr/0248-store-and-kazopkg-dependency-resolution.md) | — |
| “Features” section of Settings | Settings lists the installed Features and shows the pages they provide, by the user's role. | [0249](dev/adr/0249-settings-features-section.md) | — |
| Storage persistence and eviction | The OS asks the browser not to erase its storage, shows the quota and warns when the browser erased data anyway. | [0250](dev/adr/0250-storage-persistence-and-eviction.md) | — |
| Shell route to execute-capable apps | `./program.exe` in the terminal is typed by a Feature's format detector, then handed to the app that declares `execute` for that MIME type, without an execute bit nor a script reading. | [0251](dev/adr/0251-shell-route-to-execute-capable-apps.md) | — |
| The Terminal shows a guest program | A Feature's console program (Linux shell, text-mode DOS program) shows in the Kazo Terminal: per-cell colors, function keys, Ctrl+C to the program. | [0252](dev/adr/0252-terminal-shows-a-guest-program.md) | — |
| Marked sender | An app launched or a file opened by another app knows the sender, marked by the OS, for its journal. | [0253](dev/adr/0253-marked-sender-on-launch-and-open.md) | — |
| Per-user events | A per-user events journal (permission granted, update, conflict resolved), visible by the user and the administrators. | [0254](dev/adr/0254-per-user-events.md) | — |
| Relay: identities and decisions of a Feature | The server relay accepts from a Feature an identity per flow, a decision and limits, and logs per identity. | [0255](dev/adr/0255-relay-identities-and-decisions-of-a-feature.md) | — |

### Optional ported applications

Well-known open source applications adapted to Kazo OS, each in its own
repository. They are not part of the OS: they will be installed separately,
with `kazopkg` and then from the Kazo OS Store.

| Application | Description | Repository | Issue |
| --- | --- | --- | --- |
| Chess | Chess, based on chess.js; first application planned for the catalog. | [Os-App-Chess](https://github.com/Kazo-ca/Os-App-Chess) | [#2](https://github.com/Kazo-ca/Os-App-Chess/issues/2), [#3](https://github.com/Kazo-ca/Os-App-Chess/issues/3) |
| Webamp | Music player in the style of Winamp, associated with audio files, `.m3u`/`.pls` playlists and `.wsz` skins; also opens the files of network drives (ADR 0231). Plan under discussion. | [Os-App-Webamp](https://github.com/Kazo-ca/Os-App-Webamp) | [#1](https://github.com/Kazo-ca/Os-App-Webamp/issues/1), [#381](https://github.com/Kazo-ca/Os/issues/381) |
| IRC | Native IRC chat client (C# and Blazor) for real IRC networks, desktop and mobile: WebSocket without a server, and TCP with TLS through the relay when the server is connected ([ADR 0235](dev/adr/0235-real-network-for-chat-apps.md)). **Available in the Kazo OS Store** (version 1.0.1, Kazo OS 3.2.0 or later; `kazopkg install Kazo-ca/Os-App-IRC`). | [Os-App-IRC](https://github.com/Kazo-ca/Os-App-IRC) | [#395](https://github.com/Kazo-ca/Os/issues/395), [#393](https://github.com/Kazo-ca/Os/issues/393), [#394](https://github.com/Kazo-ca/Os/issues/394) |
| Kazo Word | Word processor for `.docx` and `.odt` documents, based on WordCraft by the ArtCraft team (MIT or Apache-2.0), shown in a Kazo OS window and associated with document files (double-click, "Open with"). Version 0.1.0, alpha like WordCraft: spelling in English only, Asian fonts limited, WebGPU or WebGL 2 needed. Kazo OS 3.3.2 or later ([ADR 0243](dev/adr/0243-serve-installed-app-pages.md), [ADR 0244](dev/adr/0244-stable-window-order-in-the-page.md), [ADR 0245](dev/adr/0245-raise-the-maximum-file-size.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Word`). | [Os-App-Word](https://github.com/Kazo-ca/Os-App-Word) | [Plan](https://github.com/Kazo-ca/Os-App-Word/blob/main/doc/en/plan.md) |
| EmulatorJS | Retro console emulator. | [Os-App-EmulatorJS](https://github.com/Kazo-ca/Os-App-EmulatorJS) | Coming |
| BoxedWine | Run old Windows programs in the OS. | [Os-App-BoxedWine](https://github.com/Kazo-ca/Os-App-BoxedWine) | Coming |
| Kazo Spreadsheets | Spreadsheet for `.xlsx` and `.ods` workbooks (and `.csv`), based on GridCraft by the ArtCraft team (MIT or Apache-2.0), compiled from source, shown in a Kazo OS window by the shared Craft app module and associated with workbook files (double-click, "Open with"), following the [Craft app port guide](dev/craft-app-port-guide.md). Version 0.1.0, alpha like GridCraft; WebGPU or WebGL 2 needed. Kazo OS 3.3.3 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Spreadsheets`). | [Os-App-Spreadsheets](https://github.com/Kazo-ca/Os-App-Spreadsheets) | [Plan](https://github.com/Kazo-ca/Os-App-Spreadsheets/blob/main/doc/en/plan.md) |
| Kazo Pdf | PDF reader and editor (annotate, fill in and create forms, sign, organize pages, text recognition), based on PdfCraft by the ArtCraft team (MIT or Apache-2.0), compiled from source with its OCR models (CC-BY-SA-4.0), shown in a Kazo OS window by the shared Craft app module and associated with `.pdf` files (double-click), following the [Craft app port guide](dev/craft-app-port-guide.md). Its exports go through the OS "Save a copy" dialog. Version 0.1.0, alpha like PdfCraft; WebGL 2 needed. Kazo OS 3.3.4 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md), [ADR 0264](dev/adr/0264-craft-app-exports-reach-the-window.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-PDF`). | [Os-App-PDF](https://github.com/Kazo-ca/Os-App-PDF) | [Plan](https://github.com/Kazo-ca/Os-App-PDF/blob/main/doc/en/plan.md) |
| Kazo Fig | Interface design and prototyping for `.figcraft` files (and `.fig`, `.sketch`, `.penpot`, `.jam`), based on FigCraft by the ArtCraft team (MIT or Apache-2.0), compiled from source, shown in a Kazo OS window by the shared Craft app module and associated with design files (double-click, "Open with"), each tab bound to its own file, following the [Craft app port guide](dev/craft-app-port-guide.md). Version 0.1.0, alpha like FigCraft; WebGPU or WebGL 2 needed. Kazo OS 3.3.3 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Fig`). | [Os-App-Fig](https://github.com/Kazo-ca/Os-App-Fig) | [Plan](https://github.com/Kazo-ca/Os-App-Fig/blob/main/doc/en/plan.md) |
| Kazo Deck | Presentation editor (masters and layouts, themes, shapes, tables, charts, pictures, transitions, animations, slide show), based on DeckCraft by the ArtCraft team (MIT or Apache-2.0), compiled unchanged from our fork, shown in a Kazo OS window by the shared Craft app module and associated with `.pptx`, `.ppsx`, `.potx` and `.deckcraft` files (double-click, "Open with" for `.txt`, `.md`, `.rtf`), one presentation per window bound to its file, following the [Craft app port guide](dev/craft-app-port-guide.md). Slide show in the browser's full screen (F5, Shift+F5, "Present"), presenter view off for now; exports through the OS "Save a copy" dialog. Version 0.1.0, alpha like DeckCraft, English interface; needs WebGPU or WebGL 2. Kazo OS 3.3.4 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md), [ADR 0264](dev/adr/0264-craft-app-exports-reach-the-window.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Deck`). | [Os-App-Deck](https://github.com/Kazo-ca/Os-App-Deck) | [Plan](https://github.com/Kazo-ca/Os-App-Deck/blob/main/doc/en/plan.md) |
| Kazo Vector | Vector drawing (pen and Bézier curves, shapes, exact path operations, gradients, live effects, type, artboards, layers, symbols), based on VectorCraft by the ArtCraft team (MIT or Apache-2.0), compiled from our fork with its host mode, shown in a Kazo OS window by the shared Craft app module and associated with `.svg`, `.svgz`, `.ai`, `.eps`, `.dxf`, `.emf`, `.wmf` and `.vectorcraft` files (double-click; "Open with" for `.pdf` and `.afdesign`), each tab bound to its own file, following the [Craft app port guide](dev/craft-app-port-guide.md). Its exports go through the OS "Save a copy" dialog; no recovery after a crash for now. Version 0.1.0, alpha like VectorCraft; WebGPU or WebGL 2 needed. Kazo OS 3.3.4 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md), [ADR 0264](dev/adr/0264-craft-app-exports-reach-the-window.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Vector`). | [Os-App-Vector](https://github.com/Kazo-ca/Os-App-Vector) | [Plan](https://github.com/Kazo-ca/Os-App-Vector/blob/main/doc/en/plan.md) |
| Kazo Photo | Layered image editor (adjustment layers, masks, styles, selections, filters, type, shapes), based on PhotoCraft by the ArtCraft team (MIT or Apache-2.0), compiled from our fork with its host mode, shown in a Kazo OS window by the shared Craft app module and associated with `.psd`, `.psb`, RAW, Krita, GIMP, OpenRaster… files (double-click; "Open with" for PNG, JPEG, WebP…), each tab bound to its own file, following the [Craft app port guide](dev/craft-app-port-guide.md). Its exports go through the OS "Save a copy" dialog; about fifteen OFL font families ship with the app. Version 0.1.0, alpha like PhotoCraft; WebGPU or WebGL 2 needed. Kazo OS 3.3.4 or later ([ADR 0259](dev/adr/0259-shared-craft-app-host-module.md), [ADR 0263](dev/adr/0263-windows-that-stay-on-their-screen.md), [ADR 0264](dev/adr/0264-craft-app-exports-reach-the-window.md)). **Available in the Kazo OS Store** (version 0.1.0; `kazopkg install Kazo-ca/Os-App-Photo`). | [Os-App-Photo](https://github.com/Kazo-ca/Os-App-Photo) | [Plan](https://github.com/Kazo-ca/Os-App-Photo/blob/main/doc/en/plan.md) |
| Kazo Design | Page layout, port of [DesignCraft](https://github.com/storytold/designcraft) (ArtCraft team) following the [Craft app port guide](dev/craft-app-port-guide.md). Planned. | [Os-App-Design](https://github.com/Kazo-ca/Os-App-Design) | [#1](https://github.com/Kazo-ca/Os-App-Design/issues/1) |

## Keeping this document current

Every PR to `main` that ships, changes or plans a feature updates this
document **in both languages**:

- a shipped feature moves from "In development" to "Available features";
- a new `TODO` issue or a new feature ADR adds a row to "In development";
- the link to an ADR's PR is replaced by the link to the ADR once it is
  merged.

The PR template ([`.github/pull_request_template.md`](../../../.github/pull_request_template.md))
has a checkbox as a reminder.
