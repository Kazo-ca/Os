using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;

namespace Kazo.Os.Platform.Core.Features;

/// <summary>
/// One app's access to the services of the features of the page (ADR 0262 §2), behind
/// <c>KazoFeatures</c>: the resolver answers for this app's manifest (its declared dependencies),
/// and <see cref="Changed"/> runs the app's handlers in the app's own ambient context.
/// </summary>
internal sealed class AppFeatureGateway : IAppFeatureGateway
{
    private readonly FeatureServiceResolver _resolver;
    private readonly AppManifest _caller;
    private readonly Lock _gate = new();
    private EventHandler<KazoFeatureServiceChangedEventArgs>? _changed;
    private IAppExecutionContext? _context;

    /// <summary>Creates the gateway of one app.</summary>
    /// <param name="resolver">The page's resolver.</param>
    /// <param name="caller">The app's manifest.</param>
    public AppFeatureGateway(FeatureServiceResolver resolver, AppManifest caller)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
    }

    /// <inheritdoc />
    public event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed
    {
        add
        {
            lock (_gate)
            {
                // Subscribed to the resolver only while the app listens.
                if (_changed is null)
                {
                    _resolver.Changed += OnResolverChanged;
                }

                _changed += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    _resolver.Changed -= OnResolverChanged;
                }
            }
        }
    }

    /// <summary>Gives the context the app's handlers run in.</summary>
    /// <param name="context">The app's execution context.</param>
    public void BindAmbientContext(IAppExecutionContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The feature's assembly arrives after publishing; AppPackageApiCheck verified its references at install time.")]
    public async ValueTask<object?> GetServiceAsync(Type contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        (object? instance, _) = await _resolver.ResolveAsync(_caller, contract, cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <inheritdoc />
    public bool IsAvailable(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return _resolver.GetStatus(_caller, NameOf(contract)).IsAvailable;
    }

    /// <inheritdoc />
    public ValueTask<KazoFeatureServiceStatus> GetStatusAsync(Type contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ValueTask.FromResult(_resolver.GetStatus(_caller, NameOf(contract)));
    }

    private void OnResolverChanged(object? sender, FeatureServiceChange change)
    {
        EventHandler<KazoFeatureServiceChangedEventArgs>? handlers = _changed;
        if (handlers is null)
        {
            return;
        }

        KazoFeatureServiceChangedEventArgs args = new(change.ContractType, _resolver.GetStatus(_caller, change.ContractType).State);
        using IDisposable? ambient = _context is { } context ? KazoExecutionContext.Enter(context) : null;
        handlers(this, args);
    }

    private static string NameOf(Type contract) => contract.FullName ?? contract.Name;
}
