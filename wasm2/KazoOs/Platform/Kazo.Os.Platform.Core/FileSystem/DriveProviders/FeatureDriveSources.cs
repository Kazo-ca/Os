using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.DriveProviders;

/// <summary>
/// Drive sources that come and go while the OS runs (ADR 0282 §3): <see cref="HostFolders.NetworkDriveService"/>
/// reads them at each use, next to the sources the host was built with.
/// </summary>
public interface IDynamicDriveSources
{
    /// <summary>Raised after the sources changed (a provider installed, enabled, disabled, removed or updated).</summary>
    event Action? Changed;

    /// <summary>Gets the sources now, at most one per type.</summary>
    IReadOnlyList<INetworkDriveSource> GetSources();

    /// <summary>
    /// Gets the feature that declares <paramref name="type"/> in the catalog, enabled or not; <see langword="null"/>
    /// when none does.
    /// </summary>
    /// <param name="type">The filesystem type.</param>
    string? FindFeature(string type);
}

/// <summary>
/// The drive types the features of the running catalog provide (ADR 0282 §1, §3): one
/// <see cref="FeatureDriveSource"/> per type of an installed and enabled feature, kept while the
/// same version of the feature provides it. A feature installed, enabled, disabled, removed or
/// updated while the page runs changes the list at once (ADR 0214 §1) and raises <see cref="Changed"/>.
/// </summary>
public sealed class FeatureDriveSources : IDynamicDriveSources, IDisposable
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly Func<AppDriveProviderRegistration, FeatureDriveSource> _create;
    private readonly Lock _gate = new();
    private Dictionary<string, FeatureDriveSource> _sources = new(StringComparer.Ordinal);

    /// <summary>Creates the registry of one page.</summary>
    /// <param name="catalog">The running catalog.</param>
    /// <param name="enablement">Which apps are enabled.</param>
    /// <param name="create">Creates the source of one declared type.</param>
    public FeatureDriveSources(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        Func<AppDriveProviderRegistration, FeatureDriveSource> create)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _catalog.Changed += OnCatalogChanged;
        _enablement.Changed += OnCatalogChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IReadOnlyList<INetworkDriveSource> GetSources()
    {
        List<FeatureDriveSource> dropped = [];
        IReadOnlyList<INetworkDriveSource> result;
        lock (_gate)
        {
            Dictionary<string, FeatureDriveSource> next = new(StringComparer.Ordinal);
            foreach (AppDriveProviderRegistration registration in _catalog.GetDriveProviders())
            {
                string type = registration.Provider.Type;
                // One provider per type (the installer refuses a second one); the first in catalog order wins.
                if (next.ContainsKey(type) || !_enablement.IsEnabled(registration.Feature.Id))
                {
                    continue;
                }

                next[type] = _sources.TryGetValue(type, out FeatureDriveSource? kept) && Same(kept.Registration, registration)
                    ? kept
                    : _create(registration);
            }

            dropped.AddRange(_sources.Values.Where(source => !next.TryGetValue(source.Type, out FeatureDriveSource? now) || now != source));
            _sources = next;
            result = [.. next.Values.OrderBy(static source => source.Type, StringComparer.Ordinal)];
        }

        foreach (FeatureDriveSource source in dropped)
        {
            source.Dispose();
        }

        return result;
    }

    /// <inheritdoc />
    public string? FindFeature(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return _catalog.FindDriveProvider(type)?.Feature.Id;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _enablement.Changed -= OnCatalogChanged;
        List<FeatureDriveSource> sources;
        lock (_gate)
        {
            sources = [.. _sources.Values];
            _sources = new(StringComparer.Ordinal);
        }

        foreach (FeatureDriveSource source in sources)
        {
            source.Dispose();
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private static bool Same(AppDriveProviderRegistration kept, AppDriveProviderRegistration now) =>
        string.Equals(kept.Feature.Id, now.Feature.Id, StringComparison.Ordinal)
        && string.Equals(kept.Feature.Version, now.Feature.Version, StringComparison.Ordinal)
        && string.Equals(kept.Provider.Provider, now.Provider.Provider, StringComparison.Ordinal);
}
