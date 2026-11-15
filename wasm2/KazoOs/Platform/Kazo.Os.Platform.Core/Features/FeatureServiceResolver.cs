using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Features;

/// <summary>Loads the assembly of a feature's service, by file name (<c>OsX86.dll</c>); <see langword="null"/> when it does not load.</summary>
/// <param name="assemblyFileName">The assembly's file name.</param>
/// <param name="cancellationToken">Cancels the load.</param>
public delegate ValueTask<Assembly?> FeatureAssemblyLoader(string assemblyFileName, CancellationToken cancellationToken);

/// <summary>
/// Starts the process a feature's services run in and gives its execution context
/// (<see cref="AppLifecycleOrchestrator.StartFeatureService"/>), or <see langword="null"/> when it
/// did not start.
/// </summary>
/// <param name="runAs">The feature, or the child its services run as.</param>
/// <param name="implementation">The service's type.</param>
public delegate IAppExecutionContext? FeatureServiceProcessStarter(AppManifest runAs, Type implementation);

/// <summary>A provider of a contract appeared, was disabled or removed (ADR 0262 §2).</summary>
/// <param name="ContractType">Full name of the contract.</param>
public sealed record FeatureServiceChange(string ContractType);

/// <summary>
/// The services the features of the page provide (ADR 0262 §1, §2, §4): who provides a contract,
/// whether a given app may use it, and the one instance per page, created on the first request in
/// the feature's own execution context. A <see langword="null"/> answer is never cached.
/// </summary>
/// <remarks>
/// The registry is the running catalog (<see cref="AppCatalog.GetServiceProviders"/>), read again on
/// every request: a feature installed or enabled while the page runs (ADR 0214 §1) is found at the
/// next call, and one disabled or removed stops being served (its instance is dropped and
/// <see cref="Changed"/> is raised).
/// </remarks>
public sealed class FeatureServiceResolver : IDisposable
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly FeatureAssemblyLoader _load;
    private readonly FeatureServiceProcessStarter _start;
    private readonly Func<IInstalledLibraryState?>? _libraries;
    private readonly ILogger _logger;
    private readonly CancellationToken _pageStopping;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Provided> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _creating = new(StringComparer.Ordinal);
    private Dictionary<string, string> _usable;

    /// <summary>Creates the resolver of one page.</summary>
    /// <param name="catalog">The running catalog, the registry of providers.</param>
    /// <param name="enablement">Which apps are enabled (ADR 0048).</param>
    /// <param name="load">Loads a feature's assembly.</param>
    /// <param name="start">Starts the feature's service process.</param>
    /// <param name="libraries">What the page loaded of the shared libraries (ADR 0261 §3); <see langword="null"/> for none.</param>
    /// <param name="logger">Writes the choices and failures to <c>/var/log/syslog</c>.</param>
    /// <param name="pageStopping">Cancelled when the page stops.</param>
    public FeatureServiceResolver(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        FeatureAssemblyLoader load,
        FeatureServiceProcessStarter start,
        Func<IInstalledLibraryState?>? libraries = null,
        ILogger<FeatureServiceResolver>? logger = null,
        CancellationToken pageStopping = default)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _libraries = libraries;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _pageStopping = pageStopping;
        _usable = SnapshotUsable();
        _enablement.Changed += OnCatalogChanged;
    }

    /// <summary>Raised when the provider of a contract appeared, was disabled or removed.</summary>
    public event EventHandler<FeatureServiceChange>? Changed;

    /// <summary>
    /// Gets why <paramref name="contractType"/> is, or is not, available to <paramref name="caller"/>,
    /// without loading anything (ADR 0262 §2, §3).
    /// </summary>
    /// <param name="caller">The manifest of the app that asks.</param>
    /// <param name="contractType">Full name of the contract.</param>
    public KazoFeatureServiceStatus GetStatus(AppManifest caller, string contractType)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractType);
        if (_catalog.FindServiceProvider(contractType) is not { } provider)
        {
            return KazoFeatureServiceStatus.NotInstalled;
        }

        AppManifest feature = provider.Feature;
        KazoFeatureServiceStatus found = new(KazoFeatureServiceState.Available) { FeatureId = feature.Id, InstalledVersion = feature.Version };
        if (!_enablement.IsEnabled(feature.Id))
        {
            return found with { State = KazoFeatureServiceState.Disabled };
        }

        bool own = string.Equals(caller.Id, feature.Id, StringComparison.Ordinal) || string.Equals(caller.Parent, feature.Id, StringComparison.Ordinal);
        AppDependencyManifest? dependency = caller.Dependencies.FirstOrDefault(item => string.Equals(item.AppId, feature.Id, StringComparison.Ordinal));
        if (!own && dependency is null)
        {
            return found with { State = KazoFeatureServiceState.NotDeclared };
        }

        if (dependency is not null && !InRange(feature.Version, dependency))
        {
            return found with
            {
                State = KazoFeatureServiceState.VersionMismatch,
                RequestedRange = dependency.MaximumVersion is null
                    ? $"[{dependency.MinimumVersion}, )"
                    : $"[{dependency.MinimumVersion}, {dependency.MaximumVersion}]",
            };
        }

        if (WaitsForRestart(feature.Id, provider.Service.ContractAssembly))
        {
            return found with { State = KazoFeatureServiceState.RestartRequired };
        }

        lock (_gate)
        {
            if (_failures.TryGetValue(contractType, out string? code))
            {
                return found with { State = KazoFeatureServiceState.LoadFailed, ErrorCode = code };
            }
        }

        return found;
    }

    /// <summary>
    /// Gets the instance that serves <paramref name="contract"/> for <paramref name="caller"/>, created
    /// on the first request in the feature's context, or <see langword="null"/> with the reason.
    /// </summary>
    /// <param name="caller">The manifest of the app that asks.</param>
    /// <param name="contract">The contract type, as the caller sees it.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    [RequiresUnreferencedCode("A feature's service is created by reflection from its loaded assembly; AppPackageApiCheck verified its references at install time.")]
    public async ValueTask<(object? Instance, KazoFeatureServiceStatus Status)> ResolveAsync(
        AppManifest caller, Type contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(contract);
        string contractType = contract.FullName ?? contract.Name;
        KazoFeatureServiceStatus status = GetStatus(caller, contractType);
        if (status.State is not (KazoFeatureServiceState.Available or KazoFeatureServiceState.LoadFailed))
        {
            return (null, status);
        }

        AppServiceProvider provider = _catalog.FindServiceProvider(contractType)!;
        SemaphoreSlim creating;
        lock (_gate)
        {
            if (_instances.TryGetValue(contractType, out Provided? existing) && SameProvider(existing, provider))
            {
                return Answer(existing.Instance, contract, status, contractType);
            }

            creating = _creating.TryGetValue(contractType, out SemaphoreSlim? semaphore) ? semaphore : _creating[contractType] = new SemaphoreSlim(1, 1);
        }

        // One creation at a time per contract: two apps asking together get the same instance.
        await creating.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_instances.TryGetValue(contractType, out Provided? existing) && SameProvider(existing, provider))
                {
                    return Answer(existing.Instance, contract, status, contractType);
                }
            }

            (object? instance, string? failure) = await CreateAsync(provider, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (instance is null)
                {
                    // Never cached as « absent »: the next call tries again (ADR 0262 §4).
                    _failures[contractType] = failure!;
                    return (null, status with { State = KazoFeatureServiceState.LoadFailed, ErrorCode = failure });
                }

                _failures.Remove(contractType);
                _instances[contractType] = new Provided(provider.Feature.Id, provider.Feature.Version, instance);
            }

            _logger.LogInformation("features: {Contract} served by {Feature} {Version}", contractType, provider.Feature.Id, provider.Feature.Version);
            return Answer(instance, contract, status with { State = KazoFeatureServiceState.Available, ErrorCode = null }, contractType);
        }
        finally
        {
            creating.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _enablement.Changed -= OnCatalogChanged;
        List<Provided> dropped;
        lock (_gate)
        {
            dropped = [.. _instances.Values];
            _instances.Clear();
        }

        foreach (Provided provided in dropped)
        {
            Release(provided);
        }
    }

    [RequiresUnreferencedCode("A feature's service is created by reflection from its loaded assembly.")]
    private async ValueTask<(object? Instance, string? Failure)> CreateAsync(AppServiceProvider provider, CancellationToken cancellationToken)
    {
        AppProvidedServiceManifest service = provider.Service;
        Assembly? assembly = await _load(service.ImplementationAssembly + ".dll", cancellationToken).ConfigureAwait(false);
        if (assembly?.GetType(service.ImplementationType, throwOnError: false) is not { } implementation)
        {
            _logger.LogWarning("features: {Feature}: {Type} could not be loaded from {Assembly}", provider.Feature.Id, service.ImplementationType, service.ImplementationAssembly);
            return (null, assembly is null ? "features.service.load-failed" : "features.service.type-missing");
        }

        // ADR 0262 §1: the service runs as the feature, or as its service entry point.
        AppManifest runAs = service.EntryPoint is { } child && _catalog.Manifests.TryGetValue(child, out AppManifest? childManifest)
            ? childManifest
            : provider.Feature;
        IAppExecutionContext? feature = _start(runAs, implementation);
        if (feature is null)
        {
            _logger.LogWarning("features: {Feature}: the service process of {Contract} did not start", provider.Feature.Id, service.ContractType);
            return (null, "features.service.process-failed");
        }

        FeatureServiceContext context = new(feature, CancellationTokenSource.CreateLinkedTokenSource(_pageStopping, feature.CancellationToken).Token);
        try
        {
            ConstructorInfo? withContext = implementation.GetConstructor([typeof(IFeatureServiceContext)]);
            using (context.EnterFeature())
            {
                return (withContext is not null ? withContext.Invoke([context]) : Activator.CreateInstance(implementation), null);
            }
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMethodException or MemberAccessException or TypeLoadException)
        {
            Exception cause = exception.InnerException ?? exception;
            // ADR 0262 §4: in the feature's own log, /var/log/apps/<feature>.log.
            feature.Logging.Log(DiagnosticSeverity.Error, $"features: the service {service.ContractType} could not be created: {cause.GetType().Name}: {cause.Message}");
            _logger.LogWarning("features: {Feature}: the service {Contract} could not be created ({Error})", provider.Feature.Id, service.ContractType, cause.GetType().Name);
            return (null, "features.service.constructor-failed");
        }
    }

    private (object? Instance, KazoFeatureServiceStatus Status) Answer(object instance, Type contract, KazoFeatureServiceStatus status, string contractType)
    {
        if (contract.IsInstanceOfType(instance))
        {
            return (instance, status with { State = KazoFeatureServiceState.Available, ErrorCode = null });
        }

        // The caller sees another copy of the contracts than the instance implements (ADR 0261 §3).
        _logger.LogWarning("features: {Contract} of {Feature} is another copy than the caller's; a restart loads one copy", contractType, status.FeatureId);
        return (null, status with { State = KazoFeatureServiceState.RestartRequired });
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        Dictionary<string, string> now = SnapshotUsable();
        List<Provided> dropped = [];
        List<string> changed;
        lock (_gate)
        {
            changed = [.. now.Keys.Union(_usable.Keys, StringComparer.Ordinal)
                .Where(contract => !string.Equals(now.GetValueOrDefault(contract), _usable.GetValueOrDefault(contract), StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)];
            _usable = now;
            foreach (string contract in changed)
            {
                _failures.Remove(contract);
                if (_instances.Remove(contract, out Provided? provided))
                {
                    dropped.Add(provided);
                }
            }
        }

        foreach (Provided provided in dropped)
        {
            Release(provided);
        }

        foreach (string contract in changed)
        {
            _logger.LogInformation("features: the provider of {Contract} changed ({Provider})", contract, now.GetValueOrDefault(contract) ?? "none");
            Changed?.Invoke(this, new FeatureServiceChange(contract));
        }
    }

    /// <summary>Gets, for each contract, the enabled feature and version that provides it.</summary>
    private Dictionary<string, string> SnapshotUsable()
    {
        Dictionary<string, string> usable = new(StringComparer.Ordinal);
        foreach (AppManifest feature in _catalog.Manifests.Values.Where(static manifest => manifest.Kind == AppKind.Feature).OrderBy(static manifest => manifest.Id, StringComparer.Ordinal))
        {
            foreach (AppProvidedServiceManifest service in feature.ProvidedServices ?? [])
            {
                usable.TryAdd(service.ContractType, _enablement.IsEnabled(feature.Id) ? $"{feature.Id}@{feature.Version}" : $"{feature.Id}@{feature.Version}:disabled");
            }
        }

        return usable;
    }

    private static void Release(Provided provided)
    {
        // A dropped instance is not used again; the feature's process stops with the page.
        if (provided.Instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private bool WaitsForRestart(string featureId, string library) =>
        _libraries?.Invoke()?.GetPendingRestarts().Any(restart =>
            string.Equals(restart.AppId, featureId, StringComparison.Ordinal)
            && string.Equals(restart.Library, library, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool SameProvider(Provided provided, AppServiceProvider provider) =>
        string.Equals(provided.FeatureId, provider.Feature.Id, StringComparison.Ordinal)
        && string.Equals(provided.Version, provider.Feature.Version, StringComparison.Ordinal);

    private static bool InRange(string version, AppDependencyManifest dependency)
    {
        SemanticVersion installed = SemanticVersion.Parse(version);
        return installed.CompareTo(SemanticVersion.Parse(dependency.MinimumVersion)) >= 0
            && (dependency.MaximumVersion is null || installed.CompareTo(SemanticVersion.Parse(dependency.MaximumVersion)) <= 0);
    }

    private sealed record Provided(string FeatureId, string Version, object Instance);
}
