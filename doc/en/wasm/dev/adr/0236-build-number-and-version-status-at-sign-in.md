> [Version française](../../../../fr/wasm/dev/adr/0236-build-number-and-version-status-at-sign-in.md)

# ADR 0236: build number and version status on the sign-in screen

## Status

Accepted on 2026-11-08; Benoit chose every recommendation ("Je suis d'accord
avec tes recommandations", see "Questions asked to Benoit"). Original request:
"on the login page, display on the right of the version whether the current
version is the latest or outdated, with a build number that increases with each
version; we can build it in the publish action with a counter".

- **Completes:** [ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (`version.json` gets the build number, which is compared too) and
  [ADR 0173](0173-automatic-releases-from-the-version-file.md) (the workflows
  compute the number).
- **Does not change:** `build/Versions.props` stays the only version; the
  banner and the restart of ADRs 0162, 0174 and 0223.

## Context

- The sign-in screen (and the first-account, recovery and error screens) shows
  "Kazo OS 3.2.1" in its footer (`ProjectInformation`, ADR 0187 §7). Nothing
  says whether it is the version the site serves now.
- The site publishes `/version.json`; `HostReleaseMonitor` reads it every
  5 minutes and the first read comes 5 minutes after boot: the sign-in screen
  never knows in time.
- A GitHub Pages deployment without a change of `Versions.props` keeps the same
  version number. Only the fingerprint of `dotnet.js` (ADR 0162) sees that
  change, and not always.

## Decision

1. **Build number = number of commits** of `HEAD` (`build/build-number.sh`,
   `git rev-list --count HEAD`). On `main` it grows with every merge, and every
   workflow that builds the same commit gives the same number: GitHub Pages
   (`deploy-to-pages.yml`), the Release zip and the server (`release.yml`). The
   workflows clone the whole history (`fetch-depth: 0`); the script refuses a
   shallow clone, which would count too few.
2. The number reaches MSBuild through the **`KazoOsBuildNumber`** environment
   variable (read as a property by every project). `OS/Kazo.Os.Ecosystem` only
   keeps a positive integer and writes it:
   - as `AssemblyMetadata("KazoOsBuildNumber", "<n>")` on its assembly, read
     back in the OS by `HostBuildNumber.Read(Assembly.GetEntryAssembly())`;
   - in `version.json`: `{ "product": "Kazo OS", "version": "3.2.1", "build": 1234 }`.
   A local build has no number: `version.json` has no `build` field and the
   screen says **"build dev"**.
3. **`HostReleaseMonitor`** receives the running number (`RunningBuild`) and
   keeps a **`Status`** (`HostReleaseStatus`): `NotChecked`, `UpToDate`,
   `UpdateAvailable`, `Unreachable`, with the `StatusChanged` event and
   `AvailableBuild`.
   - Another served version: an update, as before (`ReleaseAvailable`).
   - Same version, both numbers known and different: an update without a new
     version (the existing banner shows without a number).
   - Either number unknown (local build, site without a number): only the
     version counts.
   - Files changed under the page (ADR 0162): an update too.
   - Unreachable site or unreadable file: `Unreachable`, never an update.
   - An outdated page stays outdated until the restart, even when the site
     becomes unreachable: its files do not change.
4. The browser host **checks at boot** (`CheckAsync` on top of the 5-minute
   loop), so before or while the sign-in screen shows.
5. **Display** in `ProjectInformation`, on one line that wraps on a phone:
   "Kazo OS 3.2.1" · "build 1234" · a badge (`role="status"`):
   - "Up to date" (green, `--kos-success`);
   - "Outdated: 3.2.2 (build 1240) available", "Outdated: build 1240
     available" or "Outdated: update available" (amber, `--kos-warning`);
   - "Checking…" before the first answer (pulsing dot, still with reduced
     motion);
   - "Status unknown (offline)".
   Without a monitor (a host without a browser), the badge does not show. The
   "Restart to update" banner stays the way to update.

## Consequences

- The number is the same for the same source wherever the CI builds it; a
  rebuild of the same commit keeps its number (its files are identical). A
  commit that only touches documentation increases the count without a
  deployment: published numbers can skip.
- When the GitHub Pages cache reuses a publish (no OS source changed),
  `version.json` keeps that publish's number: open sessions stay "Up to date",
  which is right.
- A host from another repository that uses the packages without passing
  `KazoOsBuildNumber` shows "build dev" and compares versions only.
- The full clone adds a few seconds to the workflows.

## Rejected options

- **`github.run_number`** of the Pages workflow (Benoit's first idea): the
  Release zip and the server are built by another workflow, with another
  counter; the same OS would have several numbers.
- **A counter in a repository variable** incremented by the workflow: needs a
  token that writes variables, and two parallel runs can take the same number.
- **Latest GitHub Release** as the reference: already rejected by ADR 0174 (a
  self-hosted server would always be "outdated", a call to github.com).

## Questions asked to Benoit

Asked in the thread "Afficher l'état de la version au login" on 2026-11-08:

1. Source of the number: **A commit count (recommended)**, B
   `github.run_number`, C counter in a repository variable.
2. Reference for "latest": **A the site's `version.json` (recommended)**, B the
   latest GitHub Release.
3. Local build: **A "build dev" (recommended)**, B nothing.
4. Where to show it: **A sign-in screen and Settings ▸ About (recommended)**, B
   sign-in screen only. Settings is a public-SDK app that cannot see
   `HostReleaseMonitor`: it reads the number through `KazoOsSdk.BuildNumber`
   (an SDK addition) and shows "build 1234" next to the version, without a
   badge.

## Implementation

- `Platform/Kazo.Os.Platform.Blazor/LazyLoading/`: `HostBuildNumber`,
  `HostReleaseStatus`, `HostReleaseMonitor` (`RunningBuild`, `AvailableBuild`,
  `Status`, `StatusChanged`, `ParseBuild`).
- `OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj`: metadata and
  `version.json`.
- `OS/Kazo.Os.Ecosystem.Host/`: `ProjectInformation.razor(.cs, .css)`,
  `Project.Build*` and `Project.Status.*` texts, boot check in `App.razor`,
  number given to the monitor in `KazoOsBrowserHostExtensions`.
- `Shared/Kazo.Os.App.Abstractions/KazoOsSdk.cs`: `BuildNumber`,
  `ReadBuildNumber`; Settings ▸ About (`SettingsWindow.razor`).
- `build/build-number.sh`, `.github/workflows/deploy-to-pages.yml`,
  `.github/workflows/release.yml`.
- Tests: `HostReleaseMonitorTests`, `ProjectInformationTests`.
