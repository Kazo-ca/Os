> [Version française](../../../../fr/wasm/dev/adr/0245-feature-service-registry-and-async-resolution.md)

# ADR 0245: Feature service registry and asynchronous resolution (`KazoFeatures.GetServiceAsync<T>()`, null when absent)

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: decision 7 ("the
OS returns null when the feature is not installed"), 15 ("like
`GetService<T>()` with ASP.NET DI"), D10, D31, D36 and 158 A (`Ix86` in
`Os-x86.Interface`, `x86-Lib` implements it, the Feature registers the
instance) of the Os-x86 design.

- **Completes:** [ADR 0243](0243-feature-app-type.md) (only a Feature
  provides an interface) and [ADR 0244](0244-app-libraries-and-shared-versions.md)
  (one copy of the contracts per page).
- **Does not change:** component contracts (ADR 0063, 0148, 0156), which
  remain the way to provide a **rendering**; this ADR covers a **service**
  without a user interface.
- **For:** `Ix86`; BoxedWine and `BoxedWine.Lib` which call it; the Terminal
  does not need it (ADR 0251, 0252).

## Context

- There is **no service registry between apps**: installed apps add nothing
  to the dependency-injection container
  (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:343-391`), and `[Inject]`
  of a non-SDK type is refused at app build
  (`KOSAPP0031`, `Tools/Kazo.Os.Tools.ManifestGenerator/SdkBoundaryInspector.cs:34-47`).
- The only communication mechanisms are intents (ADR 0096;
  `IAppIntentGateway`, `Shared/Kazo.Os.Simulation.Abstractions/Gateways/AppGatewayContracts.cs:500-587`),
  without a reply, 64 KiB JSON payload; the topic bus (ADR 0040); and
  component contracts (ADR 0148), which deliver a `RenderFragment` and a
  controller to a **window** app, run with the host's rights (ADR 0148 §4).
  None gives an app a typed `Ix86` object to call `LaunchAsync(...)` on with
  a result.
- ADR 0148 rejected a global DI service because it would force loading the
  provider at boot; an app's code is loaded only at first use
  (`AppLifecycleOrchestrator.EnsureDescriptorAsync`). A **synchronous**
  `GetService<T>()` therefore cannot load a provider that is not in memory:
  resolution must be asynchronous.
- An app that declares Os-x86 as an optional dependency with a maximum
  version is **removed from the catalog** when an out-of-range version is
  installed (`Platform/Kazo.Os.Platform.Core/AppCatalog.cs:508-522`,
  `catalog.dependency.incompatible`, even for `Optional`): it does not get
  null, it disappears.

## Decision

### 1. A Feature provides a service

- `FeatureDefinition.ProvidedServices` lists provisions
  `(contract, implementation)`: the **contract type** (an interface of a
  shared library, `OsX86.Interface.Ix86`) and the **implementing type** in an
  assembly of the Feature (`OsX86.X86Service`). The manifest writes them as
  assembly-qualified type names.
- **One provider per contract** (D35): two installed Features that provide
  the same contract → the second is refused at installation
  (`apps.package.service-already-provided`, with the other Feature's name).
- The implementation is **instantiated at first need**, once per page, in the
  **execution context of the Feature** (its identity, its permissions: the
  whole set of ADR 0243 §3, restricted to the child declared as
  `ServiceEntryPoint` if there is one), never in the caller's. It receives
  an `IFeatureServiceContext` (the Feature's context, the caller's identity
  for ADR 0253, the page's stop token).
- An ordinary app can provide nothing (ADR 0243 §5).

### 2. An app consumes through `KazoFeatures`

A static facade of the SDK, in the style of `KazoApps` and `KazoUser`
(`Shared/Kazo.Os.AppSdk/Apps/KazoApps.cs:20`, `KazoUser.cs:24`), behind an
`IAppFeatureGateway` of the execution context:

```csharp
public static class KazoFeatures
{
    public static ValueTask<T?> GetServiceAsync<T>(CancellationToken ct = default) where T : class;
    public static bool IsAvailable<T>() where T : class;
    public static ValueTask<KazoFeatureServiceStatus> GetStatusAsync<T>(CancellationToken ct = default) where T : class;
    public static event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed;
}
```

- `GetServiceAsync<T>()` returns **the provider's instance**, or **null** when
  there is no usable provider: Feature absent, disabled (ADR 0048), outside
  the version range the app declares (§3), or whose code does not load. The
  null is **never cached**: the next call tries again (a Feature installed in
  between, ADR 0214 §1, is found).
- A returned instance is the same for every caller of the page (singleton
  per page); the app does not keep it beyond its immediate use (`Changed`
  warns of a disabling or a removal, and the following calls then return
  null).
- `IsAvailable<T>()` is **synchronous**: true when a usable provider is
  installed, without loading it; used to show or hide a button.
- `GetStatusAsync<T>()` returns the reason: `Available`, `NotInstalled`,
  `Disabled`, `VersionMismatch(installed, requested)`, `LoadFailed(code)`,
  `RestartRequired` (older copy loaded, ADR 0244 §3), `NotDeclared` (§3). An
  app that wants to offer the installation opens the Store on the Feature
  (`KazoApps.RequestInstallAsync`, ADR 0248).
- **Missing permission = exception, not null**: resolution checks no
  permission of the caller; each operation of the contract requires its own
  (ADR 0246) and throws `UnauthorizedAccessException`, like the SDK gateways.
  An app thus learns that it lacks a permission, not that the Feature is
  absent.

### 3. "Referencing the feature" = a manifest dependency

- An app that calls `GetServiceAsync<T>()` **declares** the Feature in
  `Dependencies` (`AppDependencyManifest`, `Shared/Kazo.Os.App.Abstractions/AppManifest.cs:203-212`),
  mandatory or optional (`Optional = true`), with its version range. The
  generator requires it (`KOSAPP0034` when a shared contract type is used
  without a dependency to a Feature that provides it… which it cannot know at
  compile time: it therefore only requires that a dependency to **a** Feature
  exists); at run time, a call without a declared dependency returns
  `NotDeclared` and null.
- **Optional dependency out of range = absent, not removed from the
  catalog**: `AppCatalog.ValidateDependencies` no longer refuses an app for an
  `Optional` dependency out of range (:508-522); `GetServiceAsync<T>()`
  returns null with `VersionMismatch`. A mandatory dependency out of range
  keeps the current behavior (`catalog.dependency.incompatible`).
- The Feature's version and its contracts' version are tied by ADR 0244
  (common major); the dependency range is about the Feature's version.

### 4. Loading and restart

- First `GetServiceAsync<T>()`: the OS loads the Feature's assembly (like
  `EnsureDescriptorAsync`), chooses the contracts copy (ADR 0244 §2),
  instantiates the implementation, then returns the instance. When the loaded
  copy is older than the Feature's: `RestartRequired` and null, with the
  restart offer of ADR 0243 §6.
- A provider whose constructor throws: `LoadFailed`, logged to
  `/var/log/apps/<feature>.log`, null; retried at the next call.

## Rejected options

- **A. A global DI service registered at boot**: loads the provider at boot,
  refused by ADR 0148; and `[Inject]` outside the SDK stays forbidden.
- **B. Intents (`SendIntentAsync`) as the only channel**: no reply, 64 KiB
  JSON, no type; `Ix86` has operations with results (launch and follow a
  program, detect a format, list the VMs).
- **C. A component contract (`kazoos.x86`)**: reserved to window apps,
  delivers a rendering, runs with the host's rights (ADR 0148 §4): the
  opposite of what a Feature that enforces permissions requires.
- **D. Cache the null**: a Feature installed hot would never be found before
  a restart, against ADR 0214.
- **E. Return null when a permission is missing**: the app could not tell
  "not installed" from "not allowed"; D31 says "missing permission =
  exception".

## Consequences

- SDK: `KazoFeatures`, `IAppFeatureGateway`, `KazoFeatureServiceStatus`,
  `IFeatureServiceContext`; `FeatureDefinition.ProvidedServices`;
  `PublicAPI.Unshipped.txt`, `sdk-compat` fixture, minor SDK version.
- Platform: provider registry in `AppCatalog` (read from the manifests at
  boot and hot), `FeatureServiceResolver` (loading, Feature context, instance
  per page), `AppCatalog.ValidateDependencies` (optional dependency out of
  range), code `apps.package.service-already-provided`.
- Docs: new `framework/feature-services.md`,
  [`app-model.md`](../../framework/app-model.md), `features.md`,
  `changes/unreleased` entry.
- Tests: Feature absent → null and `NotInstalled`; installed hot → found at
  the second call without a restart; disabled → null and `Changed`; optional
  out of range → app still in the catalog, `VersionMismatch`; two providers →
  refused; operation without permission → exception; provider that throws →
  `LoadFailed` and retry.

## Questions asked to Benoit

1. **Shape of the app-side API?**
   - A) **(Recommended)** The static `KazoFeatures` facade (like `KazoApps`,
     `KazoUser`) over a gateway of the context; testable through the context.
   - B) An injected gateway only (`IAppExecutionContext.Features`), without a
     facade.
2. **In which context does the service implementation run?**
   - A) **(Recommended)** The Feature's (its permissions), the caller's
     identity passed for the journal (ADR 0253): an app never gains the
     Feature's permissions, and the Feature checks the caller's permissions at
     every operation (ADR 0246).
   - B) The caller's (like a component, ADR 0148 §4): simple, but a security
     feature cannot trust it.

Applied while waiting: 1 A, 2 A.
