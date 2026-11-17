using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Storage;
using Kazo.Os.Platform.Core.Execution;

namespace Kazo.Os.Platform.Core.Storage;

/// <summary>
/// <see cref="IAppStorageGateway"/> over <see cref="OriginStorageService"/> (ADR 0250), one per app
/// instance. Reading needs no capability, like <c>df</c>. Asking for persistence is for the features
/// and the system apps; registering a marker and declaring usage for the features. An entry point of a
/// feature (ADR 0260 §2) acts for its feature: <c>x86 vm create</c> asks for the whole of Os-x86.
/// </summary>
internal sealed class AppStorageGateway : IAppStorageGateway
{
    /// <summary>The code of the refusal: only a feature, or a system app, may do this.</summary>
    public const string FeatureOnlyCode = "manifest.feature.only";

    private readonly Func<OriginStorageService> _service;
    private readonly AppManifest _caller;
    private readonly bool _isSystemApp;
    private readonly string? _featureId;
    private readonly string _featureName;
    private readonly AppAmbientContextBinding _ambient = new();
    private EventHandler<KazoStorageChangedEventArgs>? _changed;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="service">The OS's storage service.</param>
    /// <param name="caller">The manifest of the app.</param>
    /// <param name="isSystemApp">Whether the app is a system app of the host (ADR 0048 §1).</param>
    /// <param name="featureName">The name of the caller's feature, when the caller is an entry point of one.</param>
    public AppStorageGateway(Func<OriginStorageService> service, AppManifest caller, bool isSystemApp, string? featureName = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _isSystemApp = isSystemApp;
        _featureId = caller.Kind == AppKind.Feature ? caller.Id : caller.Parent;
        _featureName = caller.Kind == AppKind.Feature ? caller.Name : featureName ?? caller.Parent ?? caller.Name;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default) =>
        _service().GetEstimateAsync(_featureId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default)
    {
        if (_featureId is null && !_isSystemApp)
        {
            throw Refused("ask the browser to protect the storage");
        }

        return _service().RequestPersistenceAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default) =>
        _service().RegisterMarkerAsync(RequireFeature("register a storage marker"), _featureName, path, cancellationToken);

    /// <inheritdoc />
    public ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default) =>
        _service().ReportUsageAsync(RequireFeature("declare its storage usage"), _featureName, bytes, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default) =>
        [.. (await _service().GetFeaturesAsync(cancellationToken).ConfigureAwait(false))
            .Select(static record => new KazoFeatureStorageUsage(record.FeatureId, record.Name, record.UsageBytes, record.ReportedAtUtc))];

    /// <inheritdoc />
    public event EventHandler<KazoStorageChangedEventArgs>? Changed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            _changed += value;
            // ADR 0250 §2: what the boot found reaches the feature at its next launch, as soon as it listens.
            if (_featureId is not null && _service().TakePendingChange(_featureId) is { } pending)
            {
                _ambient.Wrap<KazoStorageChangedEventArgs>(change => value(this, change))(pending);
            }
        }

        remove => _changed -= value;
    }

    private string RequireFeature(string what) => _featureId ?? throw Refused(what);

    private KazoUnauthorizedAccessException Refused(string what) =>
        new($"{_caller.Id} cannot {what}: only a feature may ({FeatureOnlyCode}).");
}
