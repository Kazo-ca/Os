using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Core.Features;

/// <summary>
/// The catalog of capabilities of the running OS (ADR 0246 §1): the built-in catalog plus the
/// permissions of the features installed and enabled now. It follows the running catalog: a feature
/// installed, disabled or removed while the page runs adds or removes its permissions at once.
/// </summary>
public sealed class InstalledCapabilityCatalog : ICapabilityCatalog, IDisposable
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry? _enablement;
    private readonly Lock _gate = new();
    private FeatureCapabilityCatalog? _current;

    /// <summary>Creates the catalog over the running app catalog.</summary>
    /// <param name="catalog">The running catalog, whose features declare permissions.</param>
    /// <param name="enablement">Which apps are enabled; <see langword="null"/> takes every feature of the catalog.</param>
    public InstalledCapabilityCatalog(AppCatalog catalog, IAppEnablementRegistry? enablement = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement;
        if (_enablement is not null)
        {
            _enablement.Changed += OnChanged;
        }
        else
        {
            _catalog.Changed += OnChanged;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDescription> All => Current.All;

    /// <inheritdoc />
    public CapabilityDescription? Find(string capability) => Current.Find(capability);

    /// <summary>Gets the feature that declares <paramref name="capability"/>, or <see langword="null"/> for a built-in one or an unknown one.</summary>
    /// <param name="capability">The identifier.</param>
    public string? GetProvider(string capability) => Find(capability)?.ProviderId;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_enablement is not null)
        {
            _enablement.Changed -= OnChanged;
        }
        else
        {
            _catalog.Changed -= OnChanged;
        }
    }

    private FeatureCapabilityCatalog Current
    {
        get
        {
            lock (_gate)
            {
                // ADR 0246 §3: only an installed, enabled feature's permissions are known.
                return _current ??= new FeatureCapabilityCatalog(
                    BuiltInCapabilityCatalog.Instance,
                    _catalog.Manifests.Values.Where(manifest => manifest.Kind == AppKind.Feature && (_enablement?.IsEnabled(manifest.Id) ?? true)));
            }
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _current = null;
        }
    }
}
