> [Version française](../../../../fr/wasm/dev/adr/0277-trimmed-api-check-before-publishing.md)

# ADR 0277: check the trimmed OS's APIs before publishing an app

## Status

Accepted on 2026-11-16: Benoit chose Q1 = A. Series `todo-wins/base`, for
issue [#346](https://github.com/Kazo-ca/Os/issues/346) (`TODO`).

- **Completes:** [ADR 0182](0182-install-apps-from-github.md) §5.3 (the API
  check at install time) and [ADR 0242](0242-framework-api-for-installed-apps.md)
  (exact signatures, whole `System.Private.CoreLib`). The same check now also
  runs **before** publishing.
- **Delivers:** step 5 of [ADR 0188](0188-hack-store-and-app-catalog.md) §5
  ("API check against the trimmed runtime") in the catalog's CI.
- **Builds on:** [ADR 0113](0113-trimming-implementation-adjustments.md)
  (trimmed publish) and [ADR 0261](0261-app-libraries-and-shared-versions.md)
  (the app's libraries in `lib/`, checked like its main assembly).

## Context

- The OS's Release publish trims the .NET framework (ADR 0113). An app
  installed after the publish only finds what the OS kept.
- `AppPackageApiCheck` (ADR 0182 §5.3, exact signatures since ADR 0242) refuses
  such an app **at install time**:
  `kazopkg: <appId> uses APIs this OS does not have`.
- The author sees it nowhere before that: their build, tests and end-to-end
  tests run on the full runtime, or in a host that keeps what the app calls.
  `KazoOsPack` checks nothing, and the catalog's CI (`validate.yml` of
  Os-AppMarketPlace) builds and analyses the package but does not run this
  check.
- Real cases: a third-party app refused by `kazopkg install` for
  `string.Create(int, TState, SpanAction)` and `Enumerable.ToHashSet`
  (2026-11-01); IRC and Webamp, which passed the old check and failed at the
  first call (ADR 0242). These are common APIs, called without a thought.

## Decision

### 1. The API surface of every OS version is published

- `publish-trimmed.sh`, and the publish of a release
  ([ADR 0173](0173-automatic-releases-from-the-version-file.md)), write
  `kazoos-api-surface.json`: for every framework assembly the publish keeps,
  its types and, for each type, its methods (name, generic arity, exact
  parameter types in the metadata decoder's text form from ADR 0242) and its
  fields.
- The file is read from the assemblies **after ILLink and before Webcil**
  (`obj/Release/net10.0/linked/`), with `System.Reflection.Metadata`. The
  `.wasm` files of `_framework` are not read.
- `Kazo.Os.App.Build` carries the surface of its own version
  (`build/api-surface/kazoos-api-surface.json`, compressed). An author who
  builds against SDK `X.Y.Z` therefore has the surface of OS `X.Y.Z`, with no
  download.
- The SDK and platform assemblies (`Kazo.Os.*`) are not in the surface: they
  are not trimmed for apps
  ([ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)) and their
  APIs are already tracked by `PublicAPI.Shipped.txt` and the `sdk-compat`
  fixture.

### 2. `KazoOsPack` checks the package it just wrote

- An `ApiSurfaceFile : IRuntimeApiSurface` class reads the file. The check is
  **the same code** as at install time (`AppPackageApiCheck`): one judgement,
  at build time as in the OS.
- After writing the `.kazoapp`, `KazoOsPack` checks every assembly of the
  package (entry points, components, libraries in `lib/`, ADR 0261). A missing
  member gives error `KOSAPP0100`, one line per member, for example:
  `Enumerable.ToHashSet<TSource>(IEnumerable<TSource>) in System.Linq is not in Kazo OS 3.4.1 (trimmed)`.
- For common gaps, the message suggests a replacement, from a short, tested
  table (for example `ToHashSet()` → `new HashSet<T>(source)`).
- `KazoOsApiCheck=false` turns the check off: the error becomes warning
  `KOSAPP0101`, which says the package may be refused at install time.
- The app template (`Os-AppTemplate`, [ADR 0176](0176-app-repository-template.md))
  runs `KazoOsPack` in Release in its CI: it gets the check with no workflow
  change.

### 3. The catalog runs the check again

- `validate.yml` of Os-AppMarketPlace adds step 5 of ADR 0188 §5: the catalog
  tool runs the same check on the built package, against the surface of the
  **latest stable** OS version (read from that version's `Kazo.Os.App.Build`
  NuGet package).
- A failure blocks the submission PR, and the review (`pr-comment.yml`) lists
  the missing members. It is the second barrier, for a package that does not
  go through `KazoOsPack`.

### 4. What does not change

- The install-time check (`kazopkg`, Store) stays: it is the one that counts,
  on the OS really loaded, which can be newer than the build's.
- A newer OS can keep fewer members than an older one. That case stays covered
  by the install, and by the apps' CI when the SDK changes
  ([ADR 0271](0271-beta-channel-and-automatic-app-updates.md)).

## Questions asked to Benoit

Asked in the thread on 2026-11-16.

**Q1. Where do we check that an app calls no API the trimmed OS removed?**

- **A (recommended, chosen):** at packaging time (`KazoOsPack`) and in the Store's CI.
  The error reaches the author at build time, before any publish; the Store
  stays a second barrier.
- **B:** only in the Store's CI. Less work, but the author only learns it when
  submitting the app.
- **C:** a command to run by hand. Nothing stops publishing an app that will
  not install.

## Consequences

- An app that calls a removed API fails its Release build, with the list of
  members and a replacement when one is known.
- `Kazo.Os.App.Build` grows by the compressed surface (size to measure in
  work item 1).
- The check adds no dependency on the server or the network.

## Work items

1. `todo-wins/7-api-surface-file`: generate `kazoos-api-surface.json` in
   `publish-trimmed.sh` and in the release publish, `ApiSurfaceFile`, tests (a
   surface written then read back, a missing member found).
2. `todo-wins/8-pack-api-check`: the check in `KazoOsPack`, `KOSAPP0100`,
   `KOSAPP0101`, the replacement table, a fixture that calls a trimmed API,
   docs `app-installation.md`, `framework/app-repository.md` and the SDK guide.
3. Os-AppMarketPlace: step 5 of `validate.yml` (PR in that repository).
