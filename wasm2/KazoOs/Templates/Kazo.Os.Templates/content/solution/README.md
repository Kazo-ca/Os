> [Version française](readme-fr.md)

<!-- Store listing: generated from store/ by .github/scripts/store-readme.mjs. Edit store/, then run it. -->

<img src="store/icon.png" alt="" width="96" align="right">

# Kazo.Os.Template.Solution

Describe your app here: what it does, how to use it, what it needs.

This text is shown on the app's page in the Kazo OS Store.

<!-- /Store listing -->

## Try it

[Open Kazo.Os.Template.Solution in Kazo OS](https://os.kazo.ca/?autolaunch=com.example.template-app): the link starts Kazo OS, installs the app from the official Store and opens it.

Or open [Kazo OS](https://os.kazo.ca/) and install Kazo.Os.Template.Solution from the **Kazo OS Store**, or in the Kazo OS
Terminal, as an administrator: `sudo kazopkg install <owner>/<repository>`.

## About Kazo OS

[Kazo OS](https://github.com/Kazo-ca/Os) is a simulated operating system that runs in the browser, written in C# with Blazor WebAssembly. It gives a complete environment to build and run applications: desktop and windows, a persistent file system, processes, a POSIX shell, a simulated network, the Kazo OS Store, and a public SDK (NuGet packages) to make apps such as this one. Try it at <https://os.kazo.ca/> (PWA, works offline).

## Development

A Kazo OS app repository, made with the `kazoos-solution` template.

| Folder | Content |
| --- | --- |
| `src/Kazo.Os.Template.Solution` | The app (window app, its `[AppManifest]` class generates `manifest.json`) |
| `src/Kazo.Os.Template.Solution.Os` | A host that runs the whole Kazo OS with the base apps and this app: the project to start |
| `tests/Kazo.Os.Template.Solution.Tests` | Tests of the app |
| `external/KazoOs` | The Kazo OS repository as a git submodule, on a release tag |
| `store/` | What the Kazo OS Store shows: icon (PNG 256×256), short descriptions, descriptions, `store.json` |

### First steps

```bash
# A repository created from the GitHub template already has the submodule:
git clone --recurse-submodules <your repository>

# A folder made with "dotnet new kazoos-solution" adds it once:
git init
git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
git -C external/KazoOs checkout v<Kazo OS version>
```

Then open the `.sln` in Visual Studio or Rider and start `Kazo.Os.Template.Solution.Os`, or run
`dotnet run --project src/Kazo.Os.Template.Solution.Os`.

### The Kazo OS version

`external/KazoOs/wasm2/KazoOs/build/Versions.props` is the Kazo OS version of this repository.
To move to another version, move the submodule to its tag and commit it:

```bash
git -C external/KazoOs fetch --depth 1 origin tag v2.2.0
git -C external/KazoOs checkout v2.2.0
git add external/KazoOs
```

Never edit the submodule: the OS is not modified from an app repository, and CI refuses a
submodule with local changes or off its tag.

### Two build profiles

| Profile | Kazo OS comes from | Use |
| --- | --- | --- |
| `Debug` | the projects of the submodule (`KazoOsUseSource=true`) | step into the SDK and the OS, breakpoints anywhere |
| `Release` | the `Kazo.Os.*` packages of nuget.org, at the submodule's version | CI, `dotnet publish`, the Kazo OS Store |

Force either one with `-p:KazoOsUseSource=true` or `false`.

### Publishing to the Kazo OS Store

1. Fill `store/` (icon, short descriptions, descriptions, screenshots, category and tags in
   `store.json`), then run `node .github/scripts/store-readme.mjs`: it copies the descriptions and
   screenshots to the top of `README.md` and `readme-fr.md`, so the repository shows the same listing
   as the Store. CI fails when a README differs from `store/`.
2. Change `Version` in the app's `[AppManifest]` class and commit it to `main`, then tag it: run the
   **Tag a release** workflow (Actions tab, or the GitHub API for an agent), which tags the head of
   `main` with the manifest's version and starts the two publishing workflows; or push the tag
   yourself: `git tag v1.0.0 && git push --tags`.
3. The **Publish to the Kazo OS Store** workflow builds the app and makes the catalog folder
   `apps/<appId>_<version>/`. With a `HACK_STORE_TOKEN` secret (a token allowed to fork and open
   pull requests), it opens the pull request to `Kazo-ca/Os-AppMarketPlace` from your fork;
   without it, download the run's artifact and open the pull request yourself.

`HACK_STORE_TOKEN` is a GitHub personal access token of yours, not something Kazo OS gives you:

1. GitHub ▸ **Settings** ▸ **Developer settings** ▸ **Personal access tokens** ▸ **Tokens (classic)** ▸
   **Generate new token (classic)**, with a short expiration and the **`repo`** and **`workflow`**
   scopes (a fine-grained token cannot cover both your fork and the catalog's repository).
2. Copy it (shown once) into this repository: **Settings** ▸ **Secrets and variables** ▸ **Actions** ▸
   **New repository secret**, name `HACK_STORE_TOKEN` (or `gh secret set HACK_STORE_TOKEN`).

`repo` reaches all your repositories: keep the token only in this secret and revoke it if in doubt.

The catalog never takes a binary from you: once your pull request is reviewed and merged, its CI
compiles the app at the tagged commit.

### Following new Kazo OS versions

The **Kazo OS SDK update** workflow (`sdk-update.yml`) runs every hour, when Kazo OS announces a
release, or by hand from the Actions tab. When Kazo OS has a newer version, it moves the submodule
to it, raises the app's version (`.github/scripts/bump-app-version.sh`) on a `kazoos-sdk/<version>`
branch and runs the CI there:

- a Kazo OS pre-release: the `beta` branch takes it, as version `x.y.(z+1)-beta.<n>`, which goes to
  the beta channel of the Kazo OS Store;
- a minor or major stable release: a pull request to `main`, merged, tagged and published;
- a patch stable release: checked only, nothing is published.

A red CI opens a `TODO` issue and changes nothing. A version with a suffix (`1.2.1-beta.1`) is
always published from `beta`, one without from `main`. The workflows of this repository are short:
they call the shared workflows of [Kazo-ca/Os-AppTemplate](https://github.com/Kazo-ca/Os-AppTemplate)
(`@v1`), so they improve without a change here. Without the Kazo OS automation secrets, allow GitHub
Actions to create pull requests (**Settings** ▸ **Actions** ▸ **General**) for the update to open its
pull request.

### Publishing on the `kazoos-packages` branch

The same `v*` tag also runs **Publish the Kazo OS packages**: it packs the app and pushes it to the
`kazoos-packages` branch of this repository, keeping the versions already published. Kazo OS
users then install it with `kazopkg install <owner>/<repo>`, without the Kazo OS Store.

These packages are not signed: apps are signed only in the Kazo OS Store repository
(`Kazo-ca/Os-AppMarketPlace`), when it builds your app from the catalog (ADR 0219). Kazo OS checks
them by their SHA-256 and asks an administrator before installing from an untrusted source. Users
who want a signed package install your app from the Kazo OS Store.
