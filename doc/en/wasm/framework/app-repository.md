> [Version française](../../../fr/wasm/framework/app-repository.md)

# App repository: the `kazoos-solution` template

## Purpose

The starting point of a Kazo OS app in its own repository: a solution that
runs in the OS under the debugger, whose Kazo OS version is set by a
submodule, that is published to the Kazo OS Store with a tag, and that follows
new Kazo OS versions by itself. Decisions:
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md),
[ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) (beta
channel, reusable workflows, SDK update).
The general guide for a repository that uses Kazo OS is
[`building-on-kazoos.md`](building-on-kazoos.md).

## Creating the repository

Two ways, same result:

- **From GitHub**: "Use this template" on
  [`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate),
  then `git clone --recurse-submodules`. The submodule is already there.
- **From the command line or Visual Studio**:

  ```bash
  dotnet new install Kazo.Os.Templates
  dotnet new kazoos-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/Os-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
  git -C external/KazoOs checkout v<Kazo OS version>
  ```

  `dotnet new` cannot create the submodule link; without it, the build stops
  on `KOSAPP0070` with the command to run. In Visual Studio (New project) or
  Rider, the template is called "Kazo OS app solution".

## What is generated

| Path | Content |
| --- | --- |
| `<Name>.sln` | the solution (`src` and `tests` folders) |
| `src/<Name>/` | the window app; its `[AppManifest]` class (with `Version`) generates `manifest.json` |
| `src/<Name>.Os/` | the host: the whole Kazo OS, the base apps and the app; the project to start |
| `tests/<Name>.Tests/` | xUnit tests of the app |
| `Directory.Build.props` | submodule root, profile, import of `Versions.props` |
| `Directory.Build.targets` | import of the external source mode, submodule check |
| `.gitmodules` | `external/KazoOs`, shallow clone |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, optional `screenshots/` |
| `.github/workflows/ci.yml` | CI (pushes to `main` and `beta`, pull requests): submodule checked, README matching `store/` (`.github/scripts/store-readme.mjs --check`), build and tests in `Release` |
| `.github/workflows/publish-to-kazoos-store.yml` | submission to the catalog on a `v*` tag (stable or beta channel, from the version) |
| `.github/workflows/tag-release.yml` | tags a commit of `main` (or of `beta` for a beta version) with the manifest's version, then publishes (`workflow_dispatch`) |
| `.github/workflows/publish-packages.yml` | unsigned package pushed to the `kazoos-packages` branch on a `v*` tag ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)) |
| `.github/workflows/sdk-update.yml` | **Kazo OS SDK update**: moves the app to a new Kazo OS version, tests it and publishes it (see [below](#kazo-os-sdk-update)) |
| `.github/scripts/` | `check-kazoos-submodule.sh`, `store-readme.mjs`, `bump-app-version.sh` (changes `Version` of the `[AppManifest]` class and of `manifest.json`) |

Each file of `.github/workflows/` is a few lines long: it calls a
[reusable workflow](#the-reusable-workflows) of `Kazo-ca/Os-AppTemplate`.
| `README.md`, `readme-fr.md` | at the top, the app's Store listing (the text of `store/description.{en,fr}.md` and the screenshots), a "Try it" section and a short presentation of Kazo OS with a link to its repository; then these steps, for the created repository |

## The Kazo OS version

`external/KazoOs/wasm2/KazoOs/build/Versions.props` is the repository's
version: `Directory.Build.props` imports it, and every `Kazo.Os.*` reference
uses `$(KazoOsVersion)`. Changing the version means moving the submodule to
another release tag and committing that change. The submodule is never edited:
CI refuses a submodule that is modified or off its tag
(`.github/scripts/check-kazoos-submodule.sh`).

## The two profiles

| Profile | Kazo OS comes from | Restore output |
| --- | --- | --- |
| `Debug` (`KazoOsUseSource=true`) | the projects of the submodule: step into the SDK and the OS, breakpoints anywhere | `obj/source/` |
| `Release` (`KazoOsUseSource=false`) | the `Kazo.Os.*` packages of nuget.org, at the submodule's version | `obj/` |

`-p:KazoOsUseSource=true` or `false` forces either one;
`-p:KazoOsSourceRoot=<folder>` points at another copy of Kazo OS (the folder
of `Kazo.Os.sln`). The first `Debug` build compiles the whole OS.

### How the `Debug` profile works

`build/Kazo.Os.ExternalSource.targets` (in the Kazo OS repository) is
imported from the submodule. It:

1. finds the projects that can be packages: `Shared/*`, `Platform/*`, `OS/*`,
   `Infrastructure/*`, `Server/*`, `Tools/*` and `Apps/*/*` (a package = the
   project of the same name);
2. replaces every `PackageReference` to one of them with a `ProjectReference`,
   keeping `IsSystemApp` for the host's base apps;
3. removes `Kazo.Os.App.Build` and imports `Kazo.Os.Apps.targets` for the
   projects that declare `KazoOsAppProject` or `KazoOsAppHost`: the same
   manifest generator, analyzers and host aggregation as in the Kazo OS
   repository (`KazoOsExternalSource=true`).

The SDK boundary (`KOSAPP0030`) checks the references that enter the
submodule; a project of your repository may reference another one. An app
added with `dotnet new kazoos-window` (or `-command`, `-service`, `-site`)
already declares `KazoOsAppProject=true`; add it to the host with
`dotnet add src/<Name>.Os reference …`.

## Publishing to the Kazo OS Store

1. Fill `store/`: 256×256 PNG icon (200 KB at most), English short description
   of 120 characters at most (French is optional), descriptions, and in
   `store.json` the category (closed list of the catalog), tags and license.
   `repository` comes from `--repository`; `project` names the app's `.csproj`,
   which the catalog builds (at the root, `KazoOsPack` would also run on the
   tests project, which has no such target).
   Then run `node .github/scripts/store-readme.mjs` (`npm run store:readme`
   in a repository with a `package.json`): it copies the descriptions, the
   icon and the screenshots to the top of `README.md` and `readme-fr.md`,
   between the `Store listing` / `Fiche du Store` comments, so the repository
   shows the same listing as the Store, followed by a link to Kazo OS. CI runs
   it with `--check` and fails when a README differs from `store/`. The
   "Try it" section links to `https://os.kazo.ca/?autolaunch=<app id>`
   ([ADR 0258](../dev/adr/0258-direct-app-launch-link.md), Kazo OS 3.3.3): the
   link starts Kazo OS, installs the app from the official Store and opens it.
   The `kazoos-solution` template writes the id given by `--appId`.
2. Change `Version` in the `[AppManifest]` class, commit it to `main`, then tag
   it: run the **Tag a release** workflow (`tag-release.yml`, from the Actions
   tab or, for an agent, through the GitHub API), or create the tag
   `v<version>` and push it yourself. The workflow tags a commit of `main` (the
   head by default) with the manifest's version and starts the two publishing
   workflows on it, since a tag pushed with the workflow token does not start
   them; with `publish` off it only tags (a version already published).
3. The **Publish to the Kazo OS Store** workflow checks the submodule, builds the
   app in `Release`, packs it (`KazoOsPack`), refuses a tag that is not the
   manifest version and produces `apps/<appId>_<version>/` (content of
   `store/`, the **package's** `manifest.json`, `store.json` with the tag's
   commit), left as a workflow artifact. The package's manifest is the generated
   manifest plus every static resource `KazoOsPack` added to it
   ([ADR 0221](../dev/adr/0221-pack-all-static-web-assets-of-an-app.md)): the one
   the catalog compares with the package its CI builds. The package also holds
   the app's satellite assemblies (its per-culture `.resx` files,
   [ADR 0222](../dev/adr/0222-installed-app-satellite-assemblies.md)).
4. With the `HACK_STORE_TOKEN` secret (an author token allowed to fork and open
   a pull request), the workflow opens the pull request to
   `Kazo-ca/Os-AppMarketPlace` from your fork. Without it, download the
   artifact and open the pull request yourself.

The catalog never takes a binary: after review and merge, its CI compiles the
app at the given commit (ADR 0188 §4 to §6).

### The `HACK_STORE_TOKEN` secret

It is a GitHub personal access token of **the app's author**: neither Kazo OS
nor the catalog provides it. With it, the workflow reads the author's name,
forks `Kazo-ca/Os-AppMarketPlace` to the author's account, pushes the
`<appId>_<version>` branch there and opens the pull request to the catalog.

Creating the token (a "classic" token):

1. GitHub ▸ profile picture ▸ **Settings** ▸ **Developer settings** ▸
   **Personal access tokens** ▸ **Tokens (classic)** ▸ **Generate new token
   (classic)**.
2. *Note*: for example `<repository> → Kazo OS Store`; *Expiration*: short (90
   days for example), renewed when it ends.
3. Scopes: **`repo`** (fork, push, open the pull request) and **`workflow`**
   (GitHub refuses a push that brings to the fork changes of the catalog's
   `.github/workflows` the fork does not have yet).
4. **Generate token**, then copy the token: it is shown only once.

Adding it to the app's repository: **Settings** ▸ **Secrets and variables** ▸
**Actions** ▸ **New repository secret**, name `HACK_STORE_TOKEN`. Or from the
command line:

```bash
gh secret set HACK_STORE_TOKEN --repo <owner>/<repository>
```

- **Why "classic"**: a fine-grained token is valid for one owner only (the
  author's account **or** an organization), while the workflow touches the
  author's fork and the catalog's repository. If the organization of the app's
  repository forbids classic tokens, allow them in its settings (*Personal
  access tokens*).
- **Caution**: `repo` gives access to all the author's repositories. Keep a
  short expiration, put the token only in this secret, revoke it at the
  slightest doubt.
- **Without the secret**, nothing blocks: the `apps/<appId>_<version>/` folder
  stays as a workflow artifact, and the author opens the pull request.
- This token signs nothing: apps are signed only in the marketplace
  repository ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)).

### Beta channel

An app version with a suffix (`1.2.1-beta.1`) is a beta version
([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §5):
it is published from the app's `beta` branch (**Tag a release**, branch
`beta`) and goes to the `beta` branch of the catalog, which only a
pre-release Kazo OS reads. `main` refuses a version with a suffix, `beta` a
version without one.

### The automation token

For the `Kazo-ca` repositories, the workflows use the **organisation GitHub
App** ([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md)
§7, Q6) instead of `HACK_STORE_TOKEN`: two organisation secrets,
`KAZO_AUTOMATION_APP_ID` (the app's id) and `KAZO_AUTOMATION_APP_KEY` (its
private key), passed with `secrets: inherit`. With them, the catalog pull
request comes from a `submit/<appId>_<version>` branch of the catalog itself,
without a fork, and the SDK update opens and merges its pull requests. Without
them, `HACK_STORE_TOKEN` (the author's fork) then the artifact submitted by
hand remain the paths of a third-party repository.

## Publishing on `kazoos-packages`

[ADR 0219](../dev/adr/0219-app-authors-signing-tool.md) §4, without signing
since 2026-11-07. The
same `v*` tag runs **Publish the Kazo OS packages** (`publish-packages.yml`,
the only workflow with `contents: write`): it fetches the `kazoos-packages`
branch (orphan the first time), packs the app there (`KazoOsPack`,
`KazoOsPackageOutput`), keeps the versions already published, then pushes the
branch. `kazopkg install <owner>/<repo>` then installs the app without the
Store.

These packages are **not signed**: no app repository signs, only
`Kazo-ca/Os-AppMarketPlace` signs the Kazo OS Store packages. The OS checks
them by their SHA-256 and asks an administrator for an untrusted source
(ADR 0208, ADR 0214 §2.4). An app repository that still has a
`HACKEROS_APP_SIGNING_KEY` secret (ADR 0219) can delete it: nothing reads it
any more.

## Kazo OS SDK update

`sdk-update.yml` ("Kazo OS SDK update",
[ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §4)
starts when Kazo OS announces a version (`repository_dispatch` of type
`kazoos-sdk-released`, payload `{ "version": "3.5.0-preview.1", "channel": "beta" }`,
sent to the repositories of `build/app-repositories.txt`), every hour for the
other repositories, or by hand (Actions tab, optional version, `force` to redo
a version already handled).

1. It finds the newest version among the tags of the submodule's repository
   (or the one asked) and compares it with the one of `main` and of `beta`. A
   stable version comes before a pre-release.
2. It makes the branch `kazoos-sdk/<Kazo OS version>`: the submodule on the
   tag, and the app's version raised by `bump-app-version.sh`.
   - **Stable version**: from `main`; `x.y.z` becomes `x.y.(z+1)`.
   - **Pre-release**: from `beta` (made from `main` the first time); the
     version becomes `x.y.(z+1)-beta.<n>`, where `x.y.z` is `main`'s and `<n>`
     follows the tags already there. `beta` is rebuilt from `main` when it only
     holds commits of this workflow; otherwise `main` is merged into it, after
     aligning `beta`'s version and submodule with `main`'s so those lines do
     not conflict.
   The branch stays afterwards: it marks the version as handled, so the next
   hourly check does not redo it.
3. The app's CI (`kazoos-ci.yml`) runs on that branch.
4. Green:
   - **pre-release**: `beta` moves to the branch, the tag is created and the
     version is submitted to the catalog's beta channel;
   - **stable minor or major**: pull request to `main` merged by the
     automation (Q5 = A), tag, publishing (Q4 = B);
   - **stable patch**: the pull request is closed once green; the app still
     builds, nothing is published.
5. Red (or `main` that does not merge into `beta`): a `TODO` issue with the
   run's link; nothing is merged or published.

The app repository has nothing to set when the automation secrets are there.
Without them, the workflow token is used: GitHub Actions must then be allowed
to create pull requests (**Settings** ▸ **Actions** ▸ **General**), and a
protected `main` may refuse the merge (the pull request stays open, with a
comment). The last step of a pre-release needs the catalog's `beta` branch
(work item 4 of the `release-channels/base` series).

## The reusable workflows

The workflows are written once, in
[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
(`on: workflow_call`), and each app repository calls them with one line
([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §7):

```yaml
jobs:
  build:
    uses: Kazo-ca/Os-AppTemplate/.github/workflows/kazoos-ci.yml@v1
```

| Reusable workflow | Called by | Inputs |
| --- | --- | --- |
| `kazoos-ci.yml` | `ci.yml`, the SDK update | `ref`, `solution`, `npm-test` (`npm ci` then `npm test`), `e2e-test` (Playwright Chromium, `npm run test:e2e`), `timeout-minutes` |
| `kazoos-publish-to-store.yml` | `publish-to-kazoos-store.yml`, `kazoos-tag-release.yml` | `app-directory`, `ref`; outputs `pull-request`, `channel` |
| `kazoos-publish-packages.yml` | `publish-packages.yml`, `kazoos-tag-release.yml` | `app-directory`, `ref` |
| `kazoos-tag-release.yml` | `tag-release.yml`, the SDK update | `app-directory`, `commit`, `branch` (`main` or `beta`), `publish` |
| `kazoos-sdk-update.yml` | `sdk-update.yml` | `app-directory`, `version`, `force` and the CI's inputs |

Only the `external/KazoOs` submodule is fetched: another submodule (the
sources of an engine whose build is committed, like the Craft apps) is not
needed. A repository whose CI has steps of its own (a pinned build, for
example) keeps its workflow next to these calls.

The `v1` tag of `Os-AppTemplate` names the latest compatible version: it moves
with each change that does not break the calls; a change that breaks them
takes `v2`. The scripts (`.github/scripts/`) stay in each repository: an author
runs them by hand too.

## The GitHub template repository

[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
is the output of `dotnet new kazoos-solution -n Kazo.Os.App --templateRepository true`
plus the submodule link. It is regenerated from the template, never edited by
hand. `--templateRepository true` adds the reusable workflows (`kazoos-*.yml`)
and makes the repository's workflows call their local copy
(`uses: ./.github/workflows/kazoos-ci.yml`): a pull request of the template is
tested before `v1` moves. The SDK update does not run in this repository, and
an app id `com.example.*` is not submitted to the Store. It can only work with
a submodule on a release that contains this template (2.2.0 or later, ADR 0207
§11).

## Tasks

- [x] External source mode (`Kazo.Os.ExternalSource.targets`, `Kazo.Os.Apps.targets`).
- [x] `kazoos-solution` template, CI and publishing workflow.
- [x] `ide.host.json` of the templates.
- [x] Test of both profiles (`Kazo.Os.Templates.E2E.Tests`).
- [ ] Template repository regenerated on the release that contains this template.
- [x] `publish-packages.yml` workflow and package signing (ADR 0219).
- [x] Signing removed from app repositories and the template (ADR 0219).
- [x] Reusable workflows, beta channel, SDK update and `bump-app-version.sh` (ADR 0271, work item 5).
- [ ] `v1` tag of `Os-AppTemplate` created after the merge into `main` (Benoit).
