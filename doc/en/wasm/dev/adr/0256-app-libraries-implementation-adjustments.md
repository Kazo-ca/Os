> [Version française](../../../../fr/wasm/dev/adr/0256-app-libraries-implementation-adjustments.md)

# ADR 0256: app libraries, implementation adjustments

## Status

Accepted on 2026-11-12.

- **Work item:** `feature/15-app-libraries-code` (series `feature/base`, code of
  ADR 0244).
- **Adjusts:** [ADR 0244](0244-app-libraries-and-shared-versions.md) §1 and §2.
  Its decisions stay true: the package carries the app's libraries, a shared
  library has one copy per page, compatibility is the major version, a loaded
  copy is never replaced while running. This ADR corrects two sentences of §2
  the code could not hold and settles what they left open.
- **Questions asked to Benoit** (series 17, questions 259 to 262, asked in the
  work item's PR). Answers of 2026-11-12: A to all four, with a precision on
  the first one, taken up in §1.

## Context

While writing the code of ADR 0244, four points did not hold as written or
were not decided:

- ADR 0244 §2 says the author marks the library "with an MSBuild property of
  its NuGet package (`buildTransitive` that sets
  `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` on the reference)". But the
  same paragraph wants an app to use it "by project reference, dll or nuget":
  a referenced DLL has no `buildTransitive`, and an MSBuild item set in the
  app's project would let the app mark a library that is not, which §2
  forbids.
- ADR 0244 §2 says that, without a Feature, two major versions "coexist (each
  loaded for its apps, separately)". Every installed app is loaded in the
  default load context (ADR 0182, and option A set aside by ADR 0244): it
  holds one assembly per simple name.
- Nothing said what to do with two apps that each carry a library that is
  **not shared** with the same simple name (two versions of the same NuGet
  package).
- Rule 1 of §2 speaks of "an installed Feature that provides an interface of
  this library (ADR 0245)"; ADR 0245 is not coded yet.

## Decision

### 1. An ordinary library goes with the app, nothing more

Precision from Benoit (2026-11-12, answer to question 259), in French: « Donc
une librairie ou une dll référencée par l'application n'est gérée ni par
Kazo OS ni par le market place. Elle accompagne simplement l'application. »
(A library or a DLL the app references is managed neither by Kazo OS nor by
the marketplace. It simply goes with the app.)

- A library that is **not marked shared** is a file of the app's package. The
  OS checks its integrity like any file of the package (SHA-256, and the
  `libraries` list says what the assembly says of itself), checks it against
  the runtime like the app's assembly (ADR 0242), and loads it before the app.
  That is all.
- It has **no** catalog entry and no page in the Store, no dependency
  resolution, no version comparison between apps, no restart offer, no
  installation refusal based on its version.
- **Only** a library its author marked as shared (§2) enters the rules of
  ADR 0244 §2 and §3: one copy per page, the choice of the copy, the
  `apps.package.library-major-mismatch` refusal, "Restart to finish".

### 2. The shared mark is in the assembly (question 259, answer A)

Replaces the parenthesis of ADR 0244 §2 about `buildTransitive`.

- The author sets `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` in **the
  library's project**. `Kazo.Os.Apps.targets` turns it into
  `[assembly: AssemblyMetadata("Kazo.Os.SharedLibrary", "true")]`. A project
  that does not import that file (a contracts package with no dependency on
  Kazo OS) writes the item
  `<AssemblyMetadata Include="Kazo.Os.SharedLibrary" Value="true" />` itself.
- The mark follows the library, whether an app references it by project, by
  DLL or by NuGet package. `KazoOsPack` reads it in the assembly and copies it
  to `shared` of `kazoos-package.json`; `AppPackage.Read` refuses a package
  whose `shared` does not say what the assembly says
  (`apps.package.bad-description`).
- The app declares nothing and cannot mark a library.

### 3. Two major versions without a Feature: both installed, one per page (question 260, answer A)

Replaces "each loaded for its apps, separately" of ADR 0244 §2.

- Between apps, without a Feature, two major versions of a shared library are
  **both installed** (answer 1 A of ADR 0244, unchanged).
- Only one is in the page at a time: the first one loaded. The app of the
  other major version is served by the loaded copy, a line of
  `/var/log/syslog` says so, and the app is shown as "Restart to finish"
  (ADR 0244 §3). After the restart, the first app launched decides again.
- A separate load context for the apps of the other major version remains
  possible later; it needs the unified loading of ADR 0182 to be revisited and
  proven in the trimmed OS.

### 4. Two libraries that are not shared with the same simple name (question 261, answer A)

- The first one loaded serves both apps. When the version of the second one
  differs, a line of `/var/log/syslog` says so
  (`libraries: <name> <version> of <app> not loaded, <version> is already in
  the page and serves it`).
- Nothing else: no installation refusal, no restart offer (§1). An app that
  calls a member missing from the loaded copy gets the error in its window
  (ADR 0238), never the whole OS.

### 5. Provider of a shared library before ADR 0245 (question 262, answer A)

- Until Features declare the services they provide (ADR 0245), the provider of
  a shared library is **any installed Feature that carries it**; when several
  carry it, the one with the highest version.
- The code of ADR 0245 will narrow the rule to the Features that declare they
  provide an interface of the library (`InstalledLibraries.FindProvider`).

### 6. `apps.package.library-reserved` protects the OS

- The refusal of a library with the name of an assembly of the OS, of the .NET
  runtime or of a built-in app (ADR 0244 §1) holds for any library, shared or
  not. It is not a management of the library (§1): the OS refuses that a
  package replaces one of its assemblies, as it refuses a package that takes
  the identifier of a built-in app (`apps.package.built-in`). It looks at the
  name only, never at the version.
- A library already loaded from another package is not an assembly of the OS:
  it does not trigger this refusal.

### 7. Other implementation details

- **What the OS brings**, for `KazoOsPack`, is a pattern of assembly names
  (property `KazoOsProvidedAssemblyPattern`: `Kazo.Os.*`, `System*`,
  `Microsoft.*`, `MudBlazor*`, `netstandard`, `mscorlib`, `WindowsBase`), which
  the app's project can change. The refusal of §6 is the safety net at
  installation.
- **A restart is offered** (ADR 0244 §3) only when it changes something for
  the app: the loaded copy lacks what the app expects (another major version,
  or older than the app's) **and** the chosen copy is another one. A loaded
  copy that is newer, of the same major version, serves the app as it is.
- **"Installed and enabled apps"** (ADR 0244 §2, rule 2): the installed apps
  present in the running catalog. The copies compared at installation are
  those of that catalog.
- **Loading**: every library of the app is loaded before its assembly; no
  on-demand resolution (`AssemblyLoadContext.Default.Resolving`) is added.
- **Startup**: `libraries` is read from the `kazoos-package.json` of each
  installed app, one more read per app; an invalid list leaves the app out
  with a warning.

## Options set aside

- **Mark by `buildTransitive` only** (question 259, B): references by project
  and by DLL are not covered.
- **Mark in the assembly and a `buildTransitive` item accepted** (259, C): the
  app could mark a library.
- **A separate load context for the other major version** (260, B): a work
  item of its own, see §3. **Refusing the second installation** (260, C):
  contradicts answer 1 A of ADR 0244.
- **Refusing the second app** that carries a library that is not shared with
  the same name (261, B): that would be managing an ordinary library (§1).
  **One context per app** (261, C): option A set aside by ADR 0244.
- **No provider before ADR 0245** (262, B): an old copy of an app could then
  serve the Feature that brings the contract.

## Consequences

- `Os-x86.Interface` sets the mark in its own project; no client app has
  anything to declare.
- The docs say an ordinary library goes with the app:
  [`app-installation.md`](../app-installation.md#app-libraries-and-shared-versions-adr-0244),
  [`app-repository.md`](../../framework/app-repository.md#libraries-of-the-app).
- Tests: a library that is not shared is neither compared at installation nor
  a cause of a restart (`InstalledLibrariesTests`), on top of those of
  ADR 0244.

## References

- ADR 0182, 0222, 0238, 0242, 0243, 0244, 0245.
- Issue [#419](https://github.com/Kazo-ca/Os/issues/419).
