> [Version française](../../../fr/wasm/framework/feature-services.md)

# Feature services: `KazoFeatures`

## Purpose

A feature of the OS (kind `Feature`, [ADR 0260](../dev/adr/0260-feature-app-type.md))
provides **services** to other apps: a typed object, for example `Ix86` of
Os-x86, on which an app calls operations that return a result.
[ADR 0262](../dev/adr/0262-feature-service-registry-and-async-resolution.md)
sets the model: the feature **declares** its services, the app **asks** for
them with `KazoFeatures.GetServiceAsync<T>()`, like `GetService<T>()` of
ASP.NET's dependency injection but asynchronous (the feature's code is loaded
only when first needed), and the OS returns `null` when the feature is absent.

## Providing a service (the feature)

1. The **contract** is an interface of a shared library
   (`<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` in its project,
   [`app-repository.md`](app-repository.md#shared-library-kazoossharedlibrary)),
   for example `Os-x86.Interface`. Client apps carry the same library; the OS
   loads one copy of it per page.
2. The **implementation** is a class of the feature's assembly, with a public
   constructor that takes an `IFeatureServiceContext`, or no parameter.
3. The `FeatureDefinition` declares it:

```csharp
public override IReadOnlyList<FeatureService> ProvidedServices =>
    [new(typeof(Ix86), typeof(X86Service))];

// Optional: the services run with the capabilities of this child.
public override string? ServiceEntryPoint => "daemon";
```

The generator checks the declaration (`KOSAPP0035`: a contract that is not an
interface of another assembly, an implementation that does not implement it or
has no usable constructor, an entry point that is not a child). The package is
refused when the contract is not in a shared library of the package
(`apps.package.service-contract-not-shared`). **One provider per contract**: a
second feature that provides the same contract is refused at installation
(`apps.package.service-already-provided`).

```csharp
public sealed class X86Service(IFeatureServiceContext context) : Ix86
{
    public async Task<int> LaunchAsync(string image, CancellationToken cancellationToken)
    {
        // The app that calls, to check ITS permissions (ADR 0246).
        KazoFeatureCaller? caller = context.Caller;
        // Act with the feature's permissions, never the caller's.
        using (context.EnterFeature())
        {
            return await RunAsync(image, cancellationToken);
        }
    }
}
```

- The instance is created **when first needed, once per page**, in the
  **feature's context**: a process of its own, as the `kazoos-svc` account,
  with the capabilities of the feature (or of its `ServiceEntryPoint` child).
  Every app of the page gets the same instance.
- `IFeatureServiceContext.Feature` is that context; `Caller` names the app
  whose call runs (`null` in the constructor); `Stopping` is cancelled when
  the page stops.
- A call runs on the calling app's code path: an SDK facade (`KazoFiles`,
  `KazoNet`…) called directly acts **for the caller**. `EnterFeature()` makes
  the feature's context ambient for a block; `Caller` keeps naming the caller.
- A permission the caller lacks is an **exception** of the operation
  (`UnauthorizedAccessException`), never a `null` at resolution. The feature
  declares its permissions
  ([ADR 0246](../dev/adr/0246-provider-declared-permissions.md),
  [`capabilities.md`](capabilities.md#permissions-declared-by-a-feature-adr-0246))
  and each operation requires them with `context.Caller.Require("x86.vm.use")`,
  or tests them with `context.Caller.Has(...)`: the OS decides (grant,
  administrator approval, covering permission), the feature never reads a
  decision file.

## Consuming a service (the app)

The app declares the feature in its dependencies, optional when it runs
without it:

```csharp
public override IReadOnlyList<AppDependencyManifest> Dependencies =>
    [new("ca.kazo.x86", "1.0.0", MaximumVersion: "1.99.0", Optional: true)];
```

The build requires a dependency as soon as the app calls `KazoFeatures`
(`KOSAPP0034`). Then:

```csharp
Ix86? x86 = await KazoFeatures.GetServiceAsync<Ix86>();
if (x86 is null)
{
    KazoFeatureServiceStatus status = await KazoFeatures.GetStatusAsync<Ix86>();
    // NotInstalled, Disabled, VersionMismatch, LoadFailed, RestartRequired, NotDeclared
    return;
}

await x86.LaunchAsync("disk.img", cancellationToken);
```

| Member | Role |
| --- | --- |
| `GetServiceAsync<T>()` | The instance, or `null`. The `null` is **never cached**: a feature installed in the meantime is found at the next call, without a restart. |
| `IsAvailable<T>()` | Synchronous, loads nothing: to show or hide a button. |
| `GetStatusAsync<T>()` | The reason: `Available`, `NotInstalled`, `Disabled`, `VersionMismatch` (installed version, requested range), `LoadFailed` (code), `RestartRequired`, `NotDeclared`. |
| `Changed` | A feature that provides a contract appeared, was disabled or removed; the handler runs in the app's context. |

- **Optional dependency out of range**: the app stays in the catalog and
  `GetServiceAsync<T>()` returns `null` with `VersionMismatch`. A required
  dependency out of range still removes the app from the catalog
  (`catalog.dependency.incompatible`).
- **Feature disabled or removed**: the instance is dropped, `Changed` is
  raised, the next calls return `null`. The app does not keep the instance
  beyond its immediate use.
- **Constructor that throws**: `LoadFailed` (`features.service.constructor-failed`),
  a line in `/var/log/apps/<feature>.log`, a new attempt at the next call.
- **Older copy of the contracts already loaded**: `RestartRequired`
  ([ADR 0261](../dev/adr/0261-app-libraries-and-shared-versions.md) §3).

## Architecture

- **SDK** (`Kazo.Os.AppSdk.Features`): `KazoFeatures`, `IAppFeatureGateway`
  (property `IAppExecutionContext.Features`, which answers "not installed" by
  default), `KazoFeatureServiceStatus`, `KazoFeatureServiceState`,
  `IFeatureServiceContext`, `KazoFeatureCaller`. `FeatureDefinition.ProvidedServices`
  and `ServiceEntryPoint`, `FeatureService` and `AppProvidedServiceManifest`
  are in `Kazo.Os.App.Abstractions`.
- **Platform**: `AppCatalog.GetServiceProviders` and `FindServiceProvider`
  (the registry, read from the manifests of the running catalog, at startup
  and while running); `FeatureServiceResolver` (one instance per page: status,
  loading of the assembly by the lazy loader, the feature's process through
  `AppLifecycleOrchestrator.StartFeatureService`); `AppFeatureGateway` (each
  app's gateway, which answers for its manifest);
  `AppCatalog.ValidateDependencies` (optional dependency out of range).
- **Shared libraries**: the provider of a shared library
  ([ADR 0261](../dev/adr/0261-app-libraries-and-shared-versions.md) §2, rule 1)
  is the feature that carries it and declares that it provides one of its
  interfaces; the provisional rule of
  [ADR 0256](../dev/adr/0256-app-libraries-implementation-adjustments.md) §5 is
  removed.

## Tests

`FeatureServiceResolverTests`, `KazoFeaturesTests`, `FeatureServicePackageTests`,
`InstalledLibrariesTests` (Platform.Core); `FeatureServiceManifestTests` and the
schema fixtures `feature-provides-service.valid.json`,
`invalid-services-on-service.json` (App.Abstractions);
`FeatureServiceInspectorTests` (Tools.ManifestGenerator).

## Tasks

- [x] Declaration (`ProvidedServices`, `ServiceEntryPoint`, the manifest's `providedServices`, schema, validator).
- [x] Build checks (`KOSAPP0034`, `KOSAPP0035`) and package checks (`service-contract-not-shared`, `service-already-provided`).
- [x] `KazoFeatures` and `IAppFeatureGateway`; `FeatureServiceResolver`; the feature's context as `kazoos-svc`.
- [x] Optional dependency out of range kept in the catalog.
- [x] Provider of a shared library restricted to the features that declare one of its contracts.
- [ ] `Tests/fixtures/sdk-compat` fixture and minor SDK version, with the release.
