> [Version française](../../../../fr/wasm/dev/adr/0261-app-libraries-and-shared-versions.md)

# ADR 0261: app libraries in the package (#419) and versions shared between apps

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. Both questions asked to Benoit got their answers on
2026-11-11: "who marks a library as shared" (A, §2) and "two majors without a
Feature" (A, both coexist); the decision applies them. Source decisions: D30
and D36 of the Os-x86 design (series 3 and 4, 2026-11-09); issue
[#419](https://github.com/Kazo-ca/Os/issues/419) (`TODO`).

First numbered 0244; renumbered 0261 on 2026-11-13 (Benoit's choice), because `main` had released ADR 0244 in Kazo OS 3.3.1 or 3.3.2.

- **Completes:** [ADR 0182](0182-install-apps-from-github.md) §5 (packages,
  `lib/`, API check), [ADR 0222](0222-installed-app-satellite-assemblies.md)
  (satellites of the declared assemblies) and [ADR 0242](0242-framework-api-for-installed-apps.md)
  (exact signature check).
- **Supersedes:** the third "limit of the first version" of ADR 0182 ("an
  app's dependencies other than the SDK are not carried").
- **For:** `Os-x86.Interface`, the NuGet package of Os-x86 contracts (`Ix86`
  and its types), embedded in every client app (BoxedWine, `BoxedWine.Lib`);
  and any app that has its own libraries (IRC and `Kazo.Irc.Protocol`, #419).

## Context

- A `.kazoapp` package only contains the assemblies the manifest
  **declares**: entry points and components
  (`Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackageWriter.cs:53-67`,
  `AppPackage.GetDeclaredAssemblies` :380-391). A library of the app's own
  (`ProjectReference`, NuGet package not provided by the OS) is not there;
  the installation then refuses the app for an "API this OS does not have"
  (`AppPackageApiCheck.cs:173-180`, code `apps.package.missing-api` in
  `AppPackageInstaller.cs:647-654`). This is issue #419; IRC works around it
  by compiling its protocol into its main assembly.
- The loader reuses the **first assembly loaded under a simple name**,
  without looking at its version (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:300-306`),
  in the default load context (:316). If two apps embedded `Os-x86.Interface`
  in two versions, the first one launched would impose its copy on the other
  for the whole page, and an old copy could mask the Feature's.
- The Os-x86 model (D30, D36) wants every client app to **embed** the
  contracts (so that `GetServiceAsync<Ix86>()` compiles and returns null when
  Os-x86 is absent, ADR 0262) and a single copy, **the most recent compatible
  one**, loaded per page.
- The SDK (`Kazo.Os.AppSdk*`, `Kazo.Os.App.Abstractions`) stays provided by
  the OS (ADR 0019); it is not an app library, and the `KOSAPP0030` rule
  (`build/Kazo.Os.Apps.targets:401-430`) keeps forbidding a package to touch it.

## Decision

### 1. The package carries the app's libraries

- `KazoOsPack` puts into `lib/` the assemblies **of the app's own**: outputs
  of `ProjectReference`s and NuGet packages not provided by the OS (neither
  the .NET runtime, nor the SDK, nor MudBlazor and its dependencies shipped
  by the host), with their satellites (ADR 0222).
- The package manifest (`kazoos-package.json`) gains a `libraries` list:
  simple name, assembly version, SHA-256, and for each library a `shared`
  flag (§2). `AppPackage.Read` checks each file like the declared assemblies;
  `AppPackageApiCheck` checks them against the runtime like the main assembly
  (ADR 0242).
- A library that bears the name of an OS or runtime assembly is refused
  (`apps.package.library-reserved`).
- The loader loads the libraries **before** the app's assembly, or at
  resolution (`AssemblyLoadContext.Default.Resolving`); a non-shared library
  serves only its app.

### 2. Shared libraries: one copy per simple name and per page

- A library marked `shared` is a **shared contract**: several apps embed it,
  a single copy is loaded. **The library's author marks it** (maintainer's
  answer, 2026-11-11), through an MSBuild property of its NuGet package
  (`buildTransitive` that sets `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>`
  on the reference), which `Os-x86.Interface` does; an app cannot make shared
  a library that is not.
- The marking **imposes nothing on the app** that uses the library, in the
  maintainer's words (translated): "no restriction requires providing it when
  it is used in an app through a project reference, a DLL or NuGet (when it is
  included in the application's package)". The app references it as it
  wishes (`ProjectReference`, DLL, `PackageReference`), `KazoOsPack` embeds
  it in `lib/` like any library (§1), with no extra declaration nor
  restriction; the `shared` flag of the package manifest is copied from the
  property and only serves Kazo, to load a single copy per major version.
- Choice rule, applied **at first need** (first launch of an app that needs
  it, or first resolution of ADR 0262):
  1. if an **installed Feature provides** an interface of that library
     (ADR 0262), its copy is loaded;
  2. otherwise, among the copies of the installed and enabled apps, **the most
     recent compatible one**: same major version as the one the triggering
     app asks for, highest minor version.
- **Compatibility = major version** (`AssemblyVersion` major, which is also
  the NuGet package major). A minor version **only adds** members: editing
  rule of shared contracts, checked by the public API tracking
  (`PublicAPI.Shipped.txt`) of `Os-x86.Interface` as for the SDK.
- At an app's installation, the installer compares each `shared` library of
  the package with the copies already installed: a **different major** from
  the copy an installed Feature provides is refused
  (`apps.package.library-major-mismatch`, message with both versions and the
  Feature's name); between apps without a Feature, different majors coexist
  (each loaded for its apps, separately) as long as no Feature decides.

### 3. Copy already loaded: a restart, never a hot replacement

- .NET does not unload an assembly from the default context. When a copy of a
  shared contract is already loaded while a Feature that provides a newer
  copy installs (ADR 0260 §6), or while an app that requires a higher minor
  launches, the OS **offers a restart** ("Restart to finish", ADR 0212 §3)
  and, meanwhile, serves the loaded copy: an app that asks for a member
  missing from that copy gets `MissingMethodException` in its window
  (ADR 0238), never the whole OS.
- The OS writes the chosen copy and the reason to `/var/log/syslog`
  (`libraries: Os-x86.Interface 1.2.0 loaded for ca.kazo.boxedwine (feature
  ca.kazo.x86)`).

### 4. What does not change

- The SDK and the runtime stay the OS's; `KOSAPP0030` holds.
- An app without a shared library sees no difference; an app without any
  library of its own neither.

## Rejected options

- **A. Load each app in its own `AssemblyLoadContext`**: would isolate the
  versions, but two copies of a contract would be two different types for
  .NET; `GetServiceAsync<Ix86>()` could never return the Feature's instance to
  an app loaded elsewhere; and the unified loading of ADR 0182 (default
  context, `IsLoaded` :250-255) would have to be redone.
- **B. A Feature's contracts in the OS SDK**: every third-party Feature would
  have to wait for an OS version to change its interface; contrary to "Os-x86
  is a separate repository".
- **C. Always the Feature's copy, with no major rule**: an app compiled
  against a newer major than the installed Feature would crash at the first
  call; refusing at installation is better (ADR 0242, same reasoning).
- **D. First loaded copy wins (current state)**: depends on the launch order;
  an old copy masks the Feature.

## Consequences

- `Tools/Kazo.Os.Tools.ManifestGenerator` and `KazoOsPack`: library
  collection, `shared` flag, `libraries` in `kazoos-package.json`;
  `AppPackage`, `AppPackageApiCheck`, `AppPackageInstaller` (new codes
  `apps.package.library-reserved`, `apps.package.library-major-mismatch`);
  `InstalledAppsBoot` (copy choice, log, restart offered).
- The `kazoos-solution` template and [`app-repository.md`](../../framework/app-repository.md)
  document the `KazoOsSharedLibrary` property; `Os-x86.Interface` sets it in
  its package. An app has **nothing to declare** to use a shared library:
  referencing it is enough; `KazoOsPack` copies the flag.
- The IRC app removes its workaround (#419, "Unblocks").
- Docs: [`app-installation.md`](../app-installation.md) (package format),
  [`sdk-packages.md`](../../framework/sdk-packages.md), `features.md`,
  `changes/unreleased` entry.
- Tests: a two-assembly package installed and launched in the trimmed OS
  (`Tests/fixtures/installed-app`); two apps with two minors of a shared
  contract → the highest loaded; a different major from the Feature refused;
  copy already loaded → restart offered and logged.

## Questions asked to Benoit

1. **With no Feature installed, two apps with two different majors of a
   shared contract?**
   - A) **(Recommended)** Both coexist (each loaded for its apps): nothing
     links them as long as no provider exists.
   - B) Refuse the second installation: one major per machine.

Maintainer's answer (2026-11-11): 1 A.

Settled on 2026-11-11: who marks a library as shared → its author, through
the MSBuild property of its package, imposing nothing on the app that uses it
(§2).
