> [Version française](../../../../fr/wasm/dev/adr/0271-beta-channel-and-automatic-app-updates.md)

# ADR 0271: Kazo OS beta channel and automatic app updates

## Status

**Accepted** on 2026-11-14 by Benoit, who answered the six questions
(evaluation, no code). See [Questions asked to Benoit](#questions-asked-to-benoit).
Series `release-channels/base`, plan:
[`release-channels-plan.md`](../plans/release-channels-plan.md).

- **Completes:** [ADR 0173](0173-automatic-releases-from-the-version-file.md)
  (release from the version file), [ADR 0176](0176-app-repository-template.md)
  and [ADR 0207](0207-solution-template-implementation.md) (app repository,
  submodule and packages), [ADR 0188](0188-hack-store-and-app-catalog.md)
  (catalog).
- **Covers point 1 of issue [#456](https://github.com/Kazo-ca/Os/issues/456)**
  (prerelease SDK). Points 2 (check the installed apps before an OS update)
  and 3 (keeping older versions) stay in that issue.

## Context

Benoit, 2026-11-14 (translated):

> Make each application able to publish an update automatically as soon as a
> beta version is available or when a new NuGet is deployed. This implies: a
> hosted version of Kazo OS on os.kazo.ca/beta, deployed automatically; a beta
> (preview) version tag and pre-release NuGet packages; each application able
> to build and publish an update; updating the template repository.

What exists (checked in the code on 2026-11-14):

| Subject | State |
| --- | --- |
| OS release | `release.yml` releases when `build/Versions.props` of `main` carries a version with no tag. It already accepts a suffix (`3.4.0-preview.1`): the Release is then marked "pre-release" and the server image does not get `:latest`. But it only looks at `main`. |
| Site | `deploy-to-pages.yml` publishes the head of `main` to GitHub Pages (`os.kazo.ca`, `CNAME` file) on **every** push, not on every version. One Pages artifact per repository, so one domain. |
| Browser storage | One IndexedDB database `kazoos`, `kazoos-cache-*` caches that each worker cleans by prefix, and a service worker of scope `/`. Two versions of the OS on the **same origin** share all of it. |
| App compatibility | `KazoOsSdk.IsCompatible`: same major version and `minimum ≤ SDK of the OS`. An app's minimum is the SDK version it was compiled with. An app compiled on 3.3.4 installs on 3.4.0-preview.1 and on 3.4.0; an app compiled on 3.4.0-preview.1 does **not** install on 3.3.4; an app compiled on 3.x is refused by 4.0. |
| App prereleases | The catalog and the installer already ignore a prerelease app version (`latestVersion`, `AppPackageIndex.Latest`); a user already on a prerelease follows prereleases. |
| App repository | Submodule `external/KazoOs` on a tag `v<version>` (CI checks it, prerelease included), nuget.org packages in `Release`. Publishing = change the manifest's `Version`, merge into `main`, run "Tag a release", then the catalog PR (`HACK_STORE_TOKEN` secret, missing in several repositories). All manual. |
| Catalog | `Os-AppMarketPlace`: one folder `apps/<id>_<version>/` per version, compiled by the catalog's CI at the given commit, signed there only, 3 versions kept per app. Merging into the catalog's `main` = human review. |
| App workflows | Each app repository has its own **copy** of the template's workflows: 11 repositories to change for each change. |

## Decision

### 1. Two channels: stable and beta

- **Stable** does not change: `main`, version with no suffix, `os.kazo.ca`.
- **Beta** is a permanent branch **`beta`** of the `Kazo-ca/Os` repository,
  created from `main`. Its version always carries a suffix:
  `<next version>-preview.<n>`. The series Benoit wants to try before `main`
  are merged into it; an agent may merge a green PR into `beta` (it is not
  `main`). `main` is merged into `beta` after each stable version.
- Guards in `release.yml`: `main` refuses a version with a suffix, `beta`
  refuses a version without one.
- A beta version becomes stable through a PR `beta` → `main` that removes the
  suffix. **Only Benoit merges it.**

### 2. SDK prerelease: tag, GitHub Release, nuget.org

- `release.yml` (same file, to keep nuget.org's Trusted Publishing policy)
  also starts after a successful verification on `beta`. When the version of
  `beta` has no tag: `Kazo.Os.*` packages pushed to nuget.org as a prerelease,
  tag `v<version>`, "pre-release" GitHub Release, zip of the site.
- Releasing a beta = changing `<n>` in `build/Versions.props` on `beta` (same
  gesture as ADR 0173). The beta **site** follows every push of `beta`, as
  `os.kazo.ca` follows `main`.
- The public API is not folded into `PublicAPI.Shipped.txt` for a prerelease;
  `changes/unreleased/` is not collected.

### 3. The beta site

- The beta site lives on a **separate origin, `os-beta.kazo.ca`**;
  `os.kazo.ca/beta` redirects to it. Reason: on the same origin, the beta
  would open the user's `kazoos` database. A beta that raises the database
  schema makes the stable OS unable to open it again (IndexedDB never goes
  down a version), the two workers delete each other's caches, and the worker
  of `/` serves its own page for `/beta/`.
- Hosting: a second static site (Cloudflare Pages, already used for the
  `hackos.kazo.ca` redirect), deployed by a workflow on every push of `beta`.
  GitHub Pages serves one domain per repository.
- The beta says what it is: PWA name "Kazo OS Beta", a "Beta" badge on the
  sign-in screen and in Settings ▸ About. Its data is separate from the stable
  site's; nothing is copied from one to the other.
- `version.json` and `HostReleaseMonitor` (ADR 0174, 0236) work unchanged:
  each site reads its own `version.json`.

### 4. Apps follow the SDK

A shared workflow **"Kazo OS SDK update"** in each app repository:

- **Triggers:** a `repository_dispatch` sent by `release.yml` to the
  repositories listed in `build/app-repositories.txt`, and a scheduled check
  (every hour) of the tags of `Kazo-ca/Os`, for third-party repositories that
  do not receive the dispatch.
- **On a prerelease:** the `beta` branch of the app repository receives
  `main`, the submodule moves to the prerelease tag, the version becomes
  `<next patch version>-beta.<n>`. CI builds and tests in `Release` on the
  prerelease packages. Green: the beta version is submitted to the catalog's
  beta channel, with no intervention. Red: a `TODO` issue is opened in the app
  repository with the log. Nothing touches `main`.
- **On a stable version:** a PR to the app's `main` moves the submodule and
  raises the patch version. CI always runs: it is the check that the app still
  compiles. For a minor or a major of the SDK, the green PR is merged by the
  automation, the tag is created and the version is submitted to the catalog;
  for a patch, the PR is closed once green and nothing is published (Q4 = B,
  Q5 = A).
- **App version number:** a script of the template
  (`.github/scripts/bump-app-version.sh`) changes `Version` of the
  `[AppManifest]` class; a real change of the app is still made by its author.

### 5. The catalog has a beta channel

- Beta versions of apps go to the `beta` branch of `Os-AppMarketPlace` (its
  submodule follows the OS prerelease, to validate a manifest that uses a new
  feature). Publishing writes `catalog-beta.json` on the `packages` branch:
  the content of `catalog.json` plus the beta versions. Beta versions do not
  count in the 3 versions kept and are removed when the matching stable
  version ships.
- **"Rebuild only" submission:** the catalog tool compares the submitted
  commit with the app's last published commit. When only the submodule pointer
  and the manifest version differ, and the declared capabilities are
  identical, the PR may be merged by the automation once validation is green,
  in beta and in stable (Q5 = A): there is no new app code to review. Any other submission waits for a human
  review, as today.
- Signing stays in the catalog repository only (`KAZO_STORE_SIGNING_KEY`),
  beta included.

### 6. For the user

- The channel follows the OS: a prerelease OS reads `catalog-beta.json` and
  offers the beta versions of apps; the stable OS reads `catalog.json` and
  never sees a beta version. No setting to understand.
- Settings ▸ About of the stable OS gets a "Try the beta" link to
  `os-beta.kazo.ca`, and the beta a "Back to the stable version" link.

### 7. One set of workflows for the apps

- App workflows become **reusable workflows** hosted in
  `Kazo-ca/Os-AppTemplate` (`on: workflow_call`), called by one line
  `uses: Kazo-ca/Os-AppTemplate/.github/workflows/<name>.yml@v1` in each
  repository. A future change is made once, in the template.
- The `kazoos-solution` template (in this repository) generates those calls;
  the template repository is regenerated, then the 11 app repositories are
  aligned.
- **Token:** a GitHub App of the organization, whose key is an organization
  secret, replaces the per-repository secrets (Q6 = A), which also fixes the repositories without
  `HACK_STORE_TOKEN`. For a `Kazo-ca` repository, the catalog PR comes from a
  branch of the catalog, with no fork; the fork path stays for third parties.

### 8. What stays a human decision

| Gesture | Who |
| --- | --- |
| Merging into `main` of `Kazo-ca/Os`, including `beta` → `main` | Benoit |
| Choosing the number of the next stable version | Benoit |
| Merging into `main` of an app and of the catalog a submission that changes code or capabilities | Benoit (or the authorized thread, as for Os-App-Word) |
| Merging a "rebuild only", beta or stable, into `main` of an app and into the catalog | Automation (an exception Benoit wants to the rule on `main`, for app repositories and the catalog only, never for `Kazo-ca/Os`) |
| Creating the beta site, the DNS, the tokens and the secrets | Benoit, once |
| Releasing a beta (changing `<n>` on `beta`), merging into `beta` | Agents, green PR |
| Rebuilding and publishing the apps in beta | Automation |

## Consequences

- A beta costs 42 packages on nuget.org (a prerelease cannot be deleted, only
  unlisted) and a rebuild of each app: hence the release by a change of `<n>`
  and not on every push.
- Rebuilding an app on a newer SDK raises its minimum: it no longer installs
  on an older OS (offline PWA not updated yet, installed server left on an
  earlier version). The catalog keeps 3 versions with the minimum SDK of each,
  but today only the app suggestion for a file (ADR 0257) picks the newest
  compatible one: the Store and `kazopkg` offer the latest, which the
  installer refuses (`apps.package.sdk-incompatible`). Work item 3 makes every
  place pick the newest compatible version; it is also the reason for the
  recommendation of Q4.
- Craft apps (Word, Spreadsheets, Pdf, Fig, Photo, Deck…) embed a WASM build
  of their engine: a rebuild for the SDK reuses the pinned build, it does not
  recompile the engine.
- Two long-lived branches (`main`, `beta`) in three repositories (`Os`, each
  app, the catalog): `beta` can always be regenerated from `main`; it is not
  repaired, it is made again.
- The `Os-AppTemplate` repository becomes a run-time dependency of the apps'
  CI: its reusable workflows are versioned by tag (`@v1`).

## Rejected options

- **Prereleases on `main`.** `os.kazo.ca` follows `main`: every user would
  receive the beta.
- **Beta from any `<series>/base`.** One beta site: two series would overwrite
  each other. The `beta` branch brings them together.
- **`os.kazo.ca/beta` on the same origin, shared data.** Risk of making the
  stable OS unusable (see §3).
- **Beta channel as a setting in the stable Store.** A beta app compiled on a
  prerelease SDK does not install on the stable OS; the setting would show
  almost nothing.

## Questions asked to Benoit

Asked in the thread on 2026-11-14; the recommendation is in bold.

**Benoit's answers (2026-11-14):** Q1 = A with the name `os-beta.kazo.ca`; Q2 = A; Q3 = A; Q4 = B; Q5 = A; Q6 = A (GitHub App of the organization).

| # | Question | Choices |
| --- | --- | --- |
| Q1 | Where does the beta site live? | **A. `os-beta.kazo.ca`, `os.kazo.ca/beta` redirects** · B. `os.kazo.ca/beta`, same origin, with isolation to code (database, caches, worker) · C. `os.kazo.ca/beta`, data shared with stable |
| Q2 | Where do betas come from? | **A. Permanent `beta` branch** · B. One `<series>/base` chosen by hand, one at a time · C. `main` |
| Q3 | When does a NuGet prerelease go out? | **A. When `<n>` changes on `beta`** · B. On every push of `beta`, automatic number · C. Workflow run by hand |
| Q4 | Publish a new version of each app on each **stable** SDK version? | A. Always · **B. Always check, publish for a minor or a major** · C. Always check, publish for a major only |
| Q5 | Who merges the "rebuild only" submissions? | A. The automation, in beta and in stable · **B. The automation in beta, a human in stable** · C. A human everywhere |
| Q6 | Which token for the automations? | **A. A GitHub App of the organization** · B. A personal token as an organization secret · C. One secret per repository, as today |

## Tasks

See the plan: [`release-channels-plan.md`](../plans/release-channels-plan.md).

- [x] Evaluation, ADR and plan (work item 0).
- [x] Benoit's answers to Q1 to Q5 recorded here.
- [x] Answer to Q6.
- [ ] Work items 1 to 8 of the plan.
