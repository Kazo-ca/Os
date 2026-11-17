> [Version française](../../../../fr/wasm/dev/adr/0281-app-end-to-end-tests-on-the-trimmed-os.md)

# ADR 0281: apps' end-to-end tests run on the published trimmed OS

## Status

Accepted on 2026-11-17: Benoit chose Q1 = A. Series `trimmed-e2e/base` (the
third lock against the refusal of Kazo AI 0.1.1).

- **Completes:** [ADR 0277](0277-trimmed-api-check-before-publishing.md)
  (static API check, at build time and in the Store) with a check at run time,
  in the OS users load.
- **Builds on:** [ADR 0113](0113-trimming-implementation-adjustments.md)
  (trimmed publish), [ADR 0173](0173-automatic-releases-from-the-version-file.md)
  (a release publishes `kazoos-wasm-<version>.zip`), [ADR 0182](0182-install-apps-from-github.md)
  (`kazopkg install` from a GitHub repository), [ADR 0176](0176-app-repository-template.md)
  (the apps' `external/KazoOs` submodule) and [ADR 0271](0271-beta-channel-and-automatic-app-updates.md) §7
  (the shared `kazoos-ci.yml` workflow of Os-AppTemplate).

## Context

- Kazo AI 0.1.1 was refused by the Store of os.kazo.ca 3.4.1: it called
  `MatchEvaluator`, `Enumerable.Sum<double>` and `ChannelWriter.WriteAsync`,
  which the trimmed publish had removed (fixed by ADR 0280, version 3.4.2).
- Its tests, like every app's, saw nothing. An app's end-to-end tests
  (`npm run test:e2e`: Chess, IRC, Webamp and Word) start the app's host with
  `dotnet run`: nothing is trimmed there, and the app is an assembly of the
  project, which trimming would keep anyway.
- ADR 0277 catches missing members by reading metadata, at build time
  (`KazoOsPack`) and in the Store. It does not see what only shows at run time:
  reflection, serialization, resources, the `wwwroot/` files of an installed
  app, the behaviour of the package as `kazopkg` installs it.
- IRC already has a manual test that does the right thing
  (`tests/installed/install-and-chat.mjs`): it serves a trimmed publish of the
  OS, installs IRC with `sudo kazopkg install` and chats. It does not run in CI
  because the OS must be published first (about ten minutes).

## Decision

### 1. The OS under test is the published release

- An app's CI downloads `kazoos-wasm-<version>.zip` from the Kazo-ca/Os GitHub
  Release its submodule names (`build/Versions.props` of `external/KazoOs`).
  It is the trimmed Release publish of that version, with the settings of
  os.kazo.ca. Nothing is rebuilt.
- The site is served by a plain static server, as a release host is: no
  COOP/COEP, no cache.

### 2. The app is installed as the Store installs it

- The CI builds the package as the Store does: `dotnet build -c Release
  -t:KazoOsPack`, NuGet packages from nuget.org.
- The harness answers
  `https://raw.githubusercontent.com/<repository>/kazoos-packages/` from that
  folder (a Playwright route: no request leaves the machine), then installs the
  app with `sudo kazopkg install <repository>` in the Terminal, as the
  administrator. The install-time API check (ADR 0182 §5.3, ADR 0242) is part
  of the test.
- A dangerous capability asked at the first launch is granted by the test's
  administrator.

### 3. One shared harness, versioned with the OS

- The harness is a JavaScript module of the Kazo OS repository,
  `wasm2/KazoOs/build/app-e2e/kazoos-app-e2e.mjs`: serve the OS, create the
  first account, install the app, open it from the launcher, grant a
  capability. An app reaches it through its submodule, so the harness always
  has the version of the OS it tests.
- It is test code, not OS code: the component asset rules (§I.2 of
  `AGENTS.md`) do not apply. Its only dependency is `playwright`, which the
  apps' tests already use.

### 4. A short test for every app

- `kazoos-ci.yml` runs for **every** app, even one with no end-to-end tests,
  `wasm2/KazoOs/build/app-e2e/smoke.mjs`: first boot, install, launch from the
  launcher, the app's window stays open for 15 s.
- It fails on a page error, a console error naming `MissingMethodException`,
  `MissingFieldException`, `TypeLoadException` or `FileNotFoundException`, a
  `kazopkg` refusal, or an app failure the OS reports.
- It is on by default. The `trimmed-smoke-test: false` input turns it off for
  an app that cannot run without an outside service; the run summary says so.

### 5. The apps' suites run on the trimmed OS

- With `e2e-test: true`, `kazoos-ci.yml` runs `npm run test:e2e` with
  `KAZOOS_E2E_WWWROOT` (the site of §1), `KAZOOS_E2E_PACKAGES` (the folder of
  §2) and `KAZOOS_E2E_REPOSITORY` (`<owner>/<repository>`).
- The app's harness (today `tests/e2e/host.mjs`) uses the shared harness when
  these variables are set, and its `dotnet run` host otherwise. The
  developer's local loop stays fast; CI tests what users run.
- Each app adapts its harness once (one work item per app). A suite not yet
  adapted ignores the variables and runs as before.

### 6. A version without the harness

- The harness comes with the first Kazo OS release that carries it. For an app
  whose submodule is older, the short test is skipped with a warning; the SDK
  update (ADR 0271) turns it on.

## Questions asked to Benoit

Asked in the thread on 2026-11-17.

**Q1. How do the apps' E2E tests run on the trimmed OS?**

- **A (recommended, chosen):** the published OS (`kazoos-wasm-<version>.zip`,
  the build of os.kazo.ca) with the app installed by `kazopkg`. The apps'
  suites run on it, and a shared short test also covers apps with no
  end-to-end tests.
- **B:** publish the app's host trimmed. Less work, but trimming keeps what
  the app calls: Kazo AI would have passed.
- **C:** the shared short test only. The suites stay on `dotnet run`, so a
  failure deeper in the app still passes.

## Consequences

- An app that breaks on the trimmed OS fails its CI, so its automatic SDK
  update fails too (ADR 0271): nothing is published.
- An app's CI downloads about 50 MiB more and takes a few more minutes (first
  boot of the OS, one install per test).
- The test depends on the Kazo OS GitHub Release (`kazoos-wasm-<version>.zip`),
  not on the server nor on os.kazo.ca.

## Work items

1. `trimmed-e2e/1-adr`: this ADR.
2. `trimmed-e2e/2-harness`: `build/app-e2e/` (shared harness, short test,
   README), checked on the trimmed publish with a real package; docs
   `framework/app-repository.md`.
3. Os-AppTemplate: `kazoos-ci.yml` (download the site, build the package,
   short test, the suites' variables, `app-directory` and `trimmed-smoke-test`
   inputs), a sample harness in the template, docs.
4. One work item per app with suites (IRC, Webamp, Word, Chess): its harness
   uses the shared one when CI asks for it. For Chess, tests only.
