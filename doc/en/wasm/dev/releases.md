> [Version française](../../../fr/wasm/dev/releases.md)

# Publishing a Kazo OS version

A new version is published by changing **a single file**:
`wasm2/KazoOs/build/Versions.props`. Once the change is merged into `main`,
GitHub Actions creates the tag, the GitHub Release and pushes the packages to
nuget.org ([ADR 0173](adr/0173-automatic-releases-from-the-version-file.md)).
A prerelease is published the same way from the permanent `beta` branch
([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md), see
[Publishing a prerelease](#publishing-a-prerelease-beta-channel)).

## The version file

```xml
<KazoOsVersion>2.2.2</KazoOsVersion>
```

`Directory.Build.props` imports it: every assembly, each of the 42 `Kazo.Os.*`
packages, the `dotnet new` templates, the generated manifests
(`sdkCompatibility.minimumVersion`) and `KazoOsSdk.Version` (shown in
Settings ▸ About and on the welcome screen) derive from it. No code or test
repeats the number.

Pick the number following SemVer
([`sdk-packages.md`](../framework/sdk-packages.md#single-version)):

| Change | Example |
| --- | --- |
| Fix, no public API added | `2.1.0` → `2.1.1` |
| Addition to the public API (`PublicAPI.Unshipped.txt` not empty) | `2.1.1` → `2.2.0` |
| SDK break (`*REMOVED*` in an SDK package) | `2.2.0` → `3.0.0` |
| Pre-release (Release marked "pre-release"), **on `beta` only** | `3.4.0-preview.1` |

Each branch has its channel, checked by `build/tools/check-release-channel.sh`
in *Verify Kazo OS WASM* (push and PRs to the branch) and in *Release Kazo OS*:
`main` refuses a version with a suffix, `beta` refuses a version without one.
Other branches (`<series>/base`, work items) accept both.

## Publishing, step by step

1. Create a branch from `main`.
2. For a minor or major version, fold the public API:
   `build/tools/ship-public-api.sh` (`--check` to see without changing anything).
3. Change `KazoOsVersion` in `build/Versions.props`.
   In the same PR, group the entries of `changes/unreleased/` into
   `changes/<version>.json` ([`change-list.md`](change-list.md), ADR 0186).
4. (Advised) build once (`dotnet build Kazo.Os.sln -c Release`) and commit the
   rewritten manifests; the build regenerates them anyway.
5. Open the PR to `main`, wait for the green verification, merge.
6. Watch the **Actions** tab: *Verify Kazo OS WASM* runs on `main`, then
   *Release Kazo OS*. The Release shows up under **Releases** with the
   `v<version>` tag.

## What the `Release Kazo OS` workflow does

File: `.github/workflows/release.yml`. It starts when *Verify Kazo OS WASM*
ends **successfully** after a push to `main` or to `beta`, then:

1. reads `KazoOsVersion` from the verified commit and refuses it when it does
   not fit the channel of the branch (`check-release-channel.sh`); if the
   `v<version>` tag already exists, it stops (nothing to release);
2. packs with `build/pack-local.sh --ci`;
3. keeps only the `Kazo.Os.*.<version>.nupkg` and `.snupkg` files;
4. publishes the PWA (`dotnet publish`) and zips it as `kazoos-wasm-<version>.zip`;
5. pushes the packages to nuget.org when that is configured (see below);
6. creates the tag and the GitHub Release: title `Kazo OS <version>`, notes
   taken from `changes/<version>.json` (or from `changes/unreleased/` for a
   prerelease), packages and zip attached; a prerelease is marked
   "pre-release", a stable version "latest";
7. tells the app repositories (job `notify-apps`, see
   [Telling the app repositories](#telling-the-app-repositories)).

The tag is created last: if a step fails, rerunning the workflow (*Re-run
failed jobs*) starts over, and packages already on nuget.org are skipped
(`--skip-duplicate`).

### Running it by hand

**Actions** tab ▸ *Release Kazo OS* ▸ *Run workflow* (branch `main` or
`beta`):

- without option: releases the version of the branch if it is not tagged yet;
- with **republish**: packs the already tagged version again and pushes it to
  nuget.org. Useful when a Release was created before NuGet was set up.

## Publishing a prerelease (beta channel)

The permanent `beta` branch publishes the SDK as a prerelease the way `main`
publishes the stable version
([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §1-2). Its
version always carries a suffix: `<next version>-preview.<n>`.

1. Merge the series to try into `beta` (green PRs; an agent may merge into
   `beta`, never into `main`).
2. In a PR to `beta`, change `<n>` in `build/Versions.props`
   (`3.4.0-preview.1` → `3.4.0-preview.2`). Do **not** run
   `ship-public-api.sh` nor `collect-changes.sh`: the public API stays in
   `PublicAPI.Unshipped.txt` and the entries in `changes/unreleased/` until
   the stable version.
3. After the merge, *Verify Kazo OS WASM* runs on `beta`, then *Release Kazo
   OS*: `Kazo.Os.*` packages pushed to nuget.org as a prerelease, tag
   `v3.4.0-preview.2`, "pre-release" GitHub Release with the zipped site,
   image `ghcr.io/kazo-ca/kazoos-server:3.4.0-preview.2` (no `:latest`).
4. The notes of the Release list every entry of `changes/unreleased/`: they
   add up from one prerelease to the next.

A push to `beta` that does not change the version publishes nothing (the tag
already exists). The beta site (`os-beta.kazo.ca`) will follow every push of
`beta` (work item 2 of the `release-channels` series).

**Going stable.** A `beta` → `main` PR removes the suffix, folds the public
API and groups `changes/unreleased/` like an ordinary version (see
[Publishing, step by step](#publishing-step-by-step)); only Benoit merges it.
After each stable version, `main` is merged into `beta` and the version of
`beta` moves to the next prerelease.

**Cost.** A prerelease pushes the 42 packages to nuget.org, where a version
cannot be deleted (only unlisted): publish a prerelease when there is
something to try, not on every merge.

## Telling the app repositories

Once a version is released (stable or prerelease, never for a *republish*),
the `notify-apps` job of *Release Kazo OS* tells the app repositories, so
their **Kazo OS SDK update** workflow (reusable workflow of `Os-AppTemplate`)
moves each app to it at once instead of at its next hourly run
([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §4):

1. it waits until every package of the version is on nuget.org (at most 40
   minutes): the app CI restores them from there, so an earlier run would fail
   and open a `TODO` issue in every app. When the packages were not pushed
   (NuGet not configured) or are still missing, a warning, and no app is told;
2. it gets a token of the **Kazo OS automation** GitHub App
   (organization secrets `KAZO_AUTOMATION_APP_ID` and `KAZO_AUTOMATION_APP_KEY`);
3. `wasm2/KazoOs/build/tools/notify-app-repositories.sh` sends to each
   repository of `wasm2/KazoOs/build/app-repositories.txt` a
   `repository_dispatch` of type `kazoos-sdk-released` with
   `{"version": "<version>", "channel": "stable" | "beta"}`.

Without the App secrets: a warning and nothing else. A repository that refuses
the dispatch (App not installed, repository renamed) gets a warning; the
others are still told and the release stays green. A repository not listed
(a third-party app) finds the new tag at its hourly run.

**Adding an app repository:** one `owner/name` line in
`build/app-repositories.txt`, and the App installed on that repository (or on
all the repositories of the organization). `notify-app-repositories.sh
--version 3.4.0 --channel stable --dry-run` shows what would be sent.

## Announcement to open sessions

The published site contains `/version.json` (`{ "product": "Kazo OS", "version": "2.1.0" }`),
written by the build from `build/Versions.props`. Every open session reads it
again every 5 minutes; when the site serves another version, a banner says
"Kazo OS <version> is available" with "Restart to update"
([ADR 0174](adr/0174-announce-a-new-version-to-open-sessions.md)).

### Build number

The "Deploy to GitHub Pages" and "Release Kazo OS" workflows compute a build
number with `build/build-number.sh` (commit count of `HEAD`, full clone
required) and pass it to MSBuild through the `KazoOsBuildNumber` environment
variable. It is written on the `OS/Kazo.Os.Ecosystem` assembly and in
`version.json` (`"build": 1234`). The sign-in screen shows
"Kazo OS 3.2.1 · build 1234" and a badge "Up to date", "Outdated: …" or
"Status unknown (offline)", checked at boot; the same version with another
number is an update too. A local build says "build dev"
([ADR 0236](adr/0236-build-number-and-version-status-at-sign-in.md)). To
reproduce the CI build locally:
`KazoOsBuildNumber="$(build/build-number.sh)" dotnet publish OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj -c Release`.

## Setting up publishing to nuget.org

Two options; the first is recommended (no key to store or renew).

### Option A: Trusted Publishing (recommended)

1. Sign in to [nuget.org](https://www.nuget.org) with the account that will own
   the packages.
2. Account menu (top right) ▸ **Trusted Publishing** ▸ **Create** (or *Add new
   trusted publishing policy*).
3. Fill in:
   - *Policy name*: `Kazo OS release`;
   - *Package owner*: your account (or the nuget.org organization that will own
     the packages);
   - *Publisher*: **GitHub Actions**;
   - *Repository owner*: `Kazo-ca`;
   - *Repository*: `Os`;
   - *Workflow file*: `release.yml` (the name only, without `.github/workflows/`);
   - *Environment*: leave empty.
4. In GitHub: repository `Kazo-ca/Os` ▸ **Settings** ▸ **Secrets and
   variables** ▸ **Actions** ▸ **Variables** tab ▸ **New repository variable**:
   name `NUGET_USER`, value = your nuget.org **user name** (the profile name,
   not the email address).

The workflow calls `NuGet/login@v1`, which exchanges GitHub's OIDC token for a
key valid for one hour.

### Option B: API key

1. nuget.org ▸ account menu ▸ **API Keys** ▸ **Create**.
2. *Key name*: `Kazo OS GitHub Actions`; *Expires in*: 365 days; *Select
   scopes*: **Push** ▸ *Push new packages and package versions*; *Glob
   pattern*: `Kazo.Os.*`. Create, then **Copy**.
3. GitHub ▸ **Settings** ▸ **Secrets and variables** ▸ **Actions** ▸
   **Secrets** tab ▸ **New repository secret**: name `NUGET_API_KEY`, value =
   the copied key.
4. Note the expiry date: an expired key makes the push fail.

If `NUGET_USER` exists, the workflow uses option A; otherwise `NUGET_API_KEY`;
without either, it publishes the GitHub Release alone with the warning
"nuget.org publishing is not configured".

### Optional: reserving the `Kazo.Os.` prefix

A prefix reservation shows the "verified" badge and prevents a third party from
publishing a `Kazo.Os.*` package. It is requested by email to
`account@nuget.org` (see the *ID prefix reservation* page of the NuGet
documentation), after the first release.

## Setting up the Kazo OS automation GitHub App

One GitHub App of the `Kazo-ca` organization gives the automations their
token (ADR 0271, Q6 = A): *Release Kazo OS* tells the apps with it, the
**Kazo OS SDK update** of each app opens and merges its pull requests with it,
and the publication to the Store opens the catalog pull request with it. Done
once, by an owner of the organization:

1. **Create the App.** `Kazo-ca` organization ▸ *Settings* ▸ *Developer
   settings* ▸ *GitHub Apps* ▸ *New GitHub App*. Name `Kazo OS automation`,
   homepage `https://github.com/Kazo-ca/Os`, *Webhook* unchecked (*Active*
   off). Repository permissions: **Contents: Read and write**, **Pull
   requests: Read and write**, **Issues: Read and write** (Metadata: Read is
   added by itself). *Where can this GitHub App be installed?*: **Only on this
   account**. *Create GitHub App*.
2. **Note the App ID** shown at the top of the App page.
3. **Create a private key.** Same page ▸ *Private keys* ▸ *Generate a private
   key*: a `.pem` file downloads. Keep it out of every repository.
4. **Install the App.** Left menu ▸ *Install App* ▸ `Kazo-ca` ▸ *Install*:
   *All repositories* (simplest), or *Only select repositories* with `Os`,
   `Os-AppMarketPlace` and every repository of `build/app-repositories.txt`.
5. **Organization secrets.** `Kazo-ca` ▸ *Settings* ▸ *Secrets and variables*
   ▸ *Actions* ▸ *New organization secret*, twice:
   `KAZO_AUTOMATION_APP_ID` = the App ID, `KAZO_AUTOMATION_APP_KEY` = the whole
   content of the `.pem` file (from `-----BEGIN` to `-----END ...-----`).
   *Repository access*: **All repositories**, or the same list as step 4. On
   the GitHub Free plan, organization secrets only reach **public**
   repositories: a private repository needs both secrets as repository
   secrets.
6. **Let it merge the rebuilds** (ADR 0271 Q5 = A). In each app repository
   and in `Os-AppMarketPlace`, if `main` is protected: *Settings* ▸ *Rules* ▸
   *Rulesets* (or *Branches*) ▸ the rule of `main` ▸ *Bypass list* ▸ *Add
   bypass* ▸ `Kazo OS automation`. Never in `Kazo-ca/Os`.
7. **Check.** *Actions* ▸ *Release Kazo OS* of the next version: the
   `notify-apps` job shows "N/N repositories told"; each app gets a *Kazo OS
   SDK update* run started by `repository_dispatch`.

## Checking a release

- Repository **Releases**: the `Kazo OS <version>` Release, its `.nupkg`,
  `.snupkg` and `kazoos-wasm-<version>.zip`.
- Summary of the *Release Kazo OS* job: the package list and the line
  "NuGet: packages pushed to nuget.org" (or "not configured").
- nuget.org: a new package is indexed within minutes
  (`https://www.nuget.org/packages/Kazo.Os.AppSdk`).

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| *Release Kazo OS* does not start | *Verify Kazo OS WASM* failed or was cancelled on that commit; fix it, or wait for the next commit of `main` (same version, not tagged yet). |
| "already released" in the summary | The `v<version>` tag exists: change the version. |
| `notify-apps` warns "was not told" | The Kazo OS automation App is not installed on that repository, or the repository was renamed: install the App or fix `build/app-repositories.txt`. |
| "Release channel" error | The version does not fit the branch: `main` wants `X.Y.Z`, `beta` wants `X.Y.Z-preview.<n>`. Fix `build/Versions.props` in the PR. |
| `403` on the NuGet push | Expired key, wrong scope (glob), or a Trusted Publishing policy that does not match the repository or the `release.yml` file. |
| `NuGet/login` fails | `NUGET_USER` does not match the nuget.org profile name, or the policy does not exist. |

See also [`ci-workflows.md`](ci-workflows.md) and
[`../framework/sdk-packages.md`](../framework/sdk-packages.md).
