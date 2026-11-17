using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.FileSystem.DriveProviders;

/// <summary>Writes one line to a feature's own log, <c>/var/log/apps/&lt;feature&gt;.log</c>.</summary>
/// <param name="featureId">The feature the line is about.</param>
/// <param name="message">The line, without the time stamp.</param>
public delegate void FeatureLogWriter(string featureId, string message);

/// <summary>
/// A drive provider a feature declares, created in the feature's own context (ADR 0282 §3): the
/// instance and the context its calls run in.
/// </summary>
/// <param name="Provider">The provider instance.</param>
/// <param name="Context">The feature's service context; <see langword="null"/> when the host started no process (tests).</param>
internal sealed record LoadedDriveProvider(KazoDriveProvider Provider, IFeatureServiceContext? Context)
{
    /// <summary>
    /// Makes the feature's execution context ambient for one call into the provider, so a façade of
    /// the SDK it calls (<c>KazoFiles</c>, <c>KazoNet</c>…) acts for the feature, never for the app
    /// whose file operation reached the drive.
    /// </summary>
    /// <returns>The scope to dispose after the call; <see langword="null"/> when there is no feature context.</returns>
    public IDisposable? Enter() => Context?.EnterFeature();
}

/// <summary>
/// Loads the drive providers features declare (ADR 0282 §3), one instance per page and per type,
/// created on the first drive of that type that is opened, in the feature's own process (like a
/// feature's service, ADR 0262 §4). A provider that does not load is never cached as absent: the next
/// drive of its type tries again.
/// </summary>
/// <remarks>
/// The assembly comes from the lazy loader, the process from
/// <see cref="Lifecycle.AppLifecycleOrchestrator.StartFeatureService"/> (account <c>kazoos-svc</c>,
/// the feature's permissions). Failures are written to the feature's own log and to the system log;
/// the caller turns them into a disconnected drive.
/// </remarks>
public sealed class FeatureDriveProviderLoader
{
    private readonly FeatureAssemblyLoader _load;
    private readonly FeatureServiceProcessStarter _start;
    private readonly FeatureLogWriter? _featureLog;
    private readonly ILogger _logger;
    private readonly CancellationToken _pageStopping;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<LoadedDriveProvider?>> _loads = new(StringComparer.Ordinal);

    /// <summary>Creates the loader of one page.</summary>
    /// <param name="load">Loads a feature's assembly by file name.</param>
    /// <param name="start">Starts the feature's service process and gives its execution context.</param>
    /// <param name="featureLog">Writes to <c>/var/log/apps/&lt;feature&gt;.log</c>; <see langword="null"/> writes to the system log only.</param>
    /// <param name="logger">The system log.</param>
    /// <param name="pageStopping">Cancelled when the page stops.</param>
    public FeatureDriveProviderLoader(
        FeatureAssemblyLoader load,
        FeatureServiceProcessStarter start,
        FeatureLogWriter? featureLog = null,
        ILogger<FeatureDriveProviderLoader>? logger = null,
        CancellationToken pageStopping = default)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _featureLog = featureLog;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _pageStopping = pageStopping;
    }

    /// <summary>
    /// Gets the provider of <paramref name="registration"/>, created on the first call of the page;
    /// <see langword="null"/> when its assembly, its class, its process or its constructor fails. Two
    /// drives opened together share one creation.
    /// </summary>
    /// <param name="registration">The feature and the drive type it declares.</param>
    [RequiresUnreferencedCode("A drive provider is created by reflection from its feature's loaded assembly; AppPackageApiCheck verified its references at install time.")]
    internal Task<LoadedDriveProvider?> LoadAsync(AppDriveProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        string key = Key(registration);
        lock (_gate)
        {
            if (_loads.TryGetValue(key, out Task<LoadedDriveProvider?>? pending))
            {
                return pending;
            }

            Task<LoadedDriveProvider?> created = CreateAsync(registration);
            if (!created.IsCompleted || created.Result is not null)
            {
                _loads[key] = created;
            }

            // A failure is never cached: the next drive of the type tries again (ADR 0262 §4).
            _ = created.ContinueWith(
                completed =>
                {
                    lock (_gate)
                    {
                        if (completed.Result is null && _loads.TryGetValue(key, out Task<LoadedDriveProvider?>? current) && current == completed)
                        {
                            _loads.Remove(key);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return created;
        }
    }

    /// <summary>The cache key: a new version of the feature is a new provider.</summary>
    internal static string Key(AppDriveProviderRegistration registration) =>
        $"{registration.Feature.Id}@{registration.Feature.Version}/{registration.Provider.Type}";

    [RequiresUnreferencedCode("A drive provider is created by reflection from its feature's loaded assembly.")]
    private async Task<LoadedDriveProvider?> CreateAsync(AppDriveProviderRegistration registration)
    {
        AppManifest feature = registration.Feature;
        AppDriveProviderManifest declaration = registration.Provider;
        try
        {
            Assembly? assembly = await _load(declaration.ProviderAssembly + ".dll", _pageStopping).ConfigureAwait(false);
            if (assembly?.GetType(declaration.ProviderType, throwOnError: false) is not { } implementation)
            {
                Fail(feature.Id, declaration.Type, assembly is null
                    ? $"the assembly {declaration.ProviderAssembly} could not be loaded"
                    : $"the class {declaration.ProviderType} is not in {declaration.ProviderAssembly}");
                return null;
            }

            if (!typeof(KazoDriveProvider).IsAssignableFrom(implementation) || implementation.IsAbstract)
            {
                Fail(feature.Id, declaration.Type, $"{declaration.ProviderType} is not a concrete KazoDriveProvider");
                return null;
            }

            IAppExecutionContext? process = _start(feature, implementation);
            if (process is null)
            {
                Fail(feature.Id, declaration.Type, "the feature's service process did not start");
                return null;
            }

            FeatureServiceContext context = new(process, CancellationTokenSource.CreateLinkedTokenSource(_pageStopping, process.CancellationToken).Token);
            ConstructorInfo? withContext = implementation.GetConstructor([typeof(IFeatureServiceContext)]);
            object? instance;
            using (context.EnterFeature())
            {
                instance = withContext is not null ? withContext.Invoke([context]) : Activator.CreateInstance(implementation);
            }

            if (instance is not KazoDriveProvider provider)
            {
                Fail(feature.Id, declaration.Type, $"{declaration.ProviderType} could not be created");
                return null;
            }

            _logger.LogInformation("drives: {Type} served by {Feature} {Version}", declaration.Type, feature.Id, feature.Version);
            return new LoadedDriveProvider(provider, context);
        }
        catch (OperationCanceledException) when (_pageStopping.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Third-party code: whatever its constructor throws becomes a disconnected drive.
            Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Fail(feature.Id, declaration.Type, $"the provider could not be created: {cause.GetType().Name}: {cause.Message}");
            return null;
        }
    }

    /// <summary>Writes a failure to the feature's own log and to the system log.</summary>
    internal void Fail(string featureId, string type, string reason)
    {
        _featureLog?.Invoke(featureId, $"drives: {type}: {reason}");
        _logger.LogWarning("drives: {Feature}: {Type}: {Reason}", featureId, type, reason);
    }
}
