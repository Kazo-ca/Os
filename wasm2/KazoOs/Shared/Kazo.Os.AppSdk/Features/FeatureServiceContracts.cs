namespace Kazo.Os.AppSdk.Features;

/// <summary>Why a feature's service is, or is not, available to the calling app (ADR 0262 §2).</summary>
public enum KazoFeatureServiceState
{
    /// <summary>A feature installed, enabled and in the declared version range provides the contract.</summary>
    Available = 0,

    /// <summary>No installed feature provides the contract.</summary>
    NotInstalled = 1,

    /// <summary>The feature that provides it is installed but disabled (ADR 0048).</summary>
    Disabled = 2,

    /// <summary>The feature's version is outside the range the app declares in its dependency (ADR 0262 §3).</summary>
    VersionMismatch = 3,

    /// <summary>The feature's code did not load, or its constructor threw; the next call tries again.</summary>
    LoadFailed = 4,

    /// <summary>An older copy of the contracts is loaded in the page; the OS offers « Restart to finish » (ADR 0261 §3).</summary>
    RestartRequired = 5,

    /// <summary>The app's manifest declares no dependency on the feature that provides the contract (ADR 0262 §3).</summary>
    NotDeclared = 6,
}

/// <summary>
/// The answer of <see cref="KazoFeatures.GetStatusAsync{T}"/> (ADR 0262 §2): the state, and what
/// explains it.
/// </summary>
/// <param name="State">The state.</param>
public sealed record KazoFeatureServiceStatus(KazoFeatureServiceState State)
{
    /// <summary>Gets the feature that provides the contract, or <see langword="null"/> when none does.</summary>
    public string? FeatureId { get; init; }

    /// <summary>Gets the installed version of that feature, or <see langword="null"/>.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Gets the range the app declares, written <c>[minimum, maximum]</c> or <c>[minimum, )</c>, for <see cref="KazoFeatureServiceState.VersionMismatch"/>.</summary>
    public string? RequestedRange { get; init; }

    /// <summary>Gets the stable code of a <see cref="KazoFeatureServiceState.LoadFailed"/>, such as <c>features.service.constructor-failed</c>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets whether <see cref="KazoFeatures.GetServiceAsync{T}"/> gives an instance.</summary>
    public bool IsAvailable => State == KazoFeatureServiceState.Available;

    /// <summary>No feature provides the contract.</summary>
    public static KazoFeatureServiceStatus NotInstalled { get; } = new(KazoFeatureServiceState.NotInstalled);
}

/// <summary>What <see cref="KazoFeatures.Changed"/> reports: a provider appeared, was disabled or removed.</summary>
/// <param name="contract">Full name of the contract whose provider changed.</param>
/// <param name="state">Its state for the app now.</param>
public sealed class KazoFeatureServiceChangedEventArgs(string contract, KazoFeatureServiceState state) : EventArgs
{
    /// <summary>Gets the full name of the contract, e.g. <c>OsX86.Interface.Ix86</c>.</summary>
    public string Contract { get; } = contract;

    /// <summary>Gets its state for the app now.</summary>
    public KazoFeatureServiceState State { get; } = state;
}

/// <summary>The app whose code calls a feature's service (ADR 0262 §1, ADR 0253).</summary>
/// <param name="AppId">The calling app.</param>
/// <param name="ProcessId">Its process.</param>
/// <param name="UserId">The user it runs for.</param>
public sealed record KazoFeatureCaller(string AppId, long ProcessId, string UserId)
{
    /// <summary>
    /// Gets the caller's own capability checks (its grants as the OS decided them), or
    /// <see langword="null"/> when none is known: every permission is then refused.
    /// </summary>
    public Kazo.Os.Simulation.Abstractions.Gateways.ICapabilityChecker? Permissions { get; init; }

    /// <summary>
    /// Gets whether the caller holds <paramref name="permission"/> (ADR 0246 §4): granted, or covered
    /// by a granted permission; a dangerous one only after an administrator's approval.
    /// </summary>
    /// <param name="permission">A permission, such as <c>x86.vm.use</c>.</param>
    public bool Has(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Permissions?.Evaluate(permission).Granted == true;
    }

    /// <summary>
    /// Requires <paramref name="permission"/> of the caller (ADR 0246 §4): each operation of a
    /// feature's service calls it; the feature never reads a decision file itself.
    /// </summary>
    /// <param name="permission">A permission, such as <c>x86.vm.use</c>.</param>
    /// <exception cref="UnauthorizedAccessException">The caller does not hold it.</exception>
    public void Require(string permission)
    {
        if (!Has(permission))
        {
            throw new UnauthorizedAccessException($"{AppId} lacks the permission '{permission}'.");
        }
    }
}

/// <summary>
/// What the OS gives a feature's service when it creates it (ADR 0262 §1): the implementation's
/// public constructor takes this context, or nothing. One instance serves every app of the page.
/// </summary>
/// <remarks>
/// A call from an app runs on that app's code path, so the SDK facades (<c>KazoFiles</c>,
/// <c>KazoNet</c>…) called directly inside an operation act for the <b>caller</b>, with its
/// permissions. To act as the feature, with the feature's permissions, wrap the work in
/// <see cref="EnterFeature"/>, or use the gateways of <see cref="Feature"/>. Check the caller's
/// permissions in each operation (ADR 0246) and throw <see cref="UnauthorizedAccessException"/>
/// when one is missing.
/// </remarks>
public interface IFeatureServiceContext
{
    /// <summary>Gets the feature's own execution context: its identity and capabilities (or those of its service entry point).</summary>
    IAppExecutionContext Feature { get; }

    /// <summary>Gets the app whose call runs now, or <see langword="null"/> outside a call (in the constructor).</summary>
    KazoFeatureCaller? Caller { get; }

    /// <summary>Gets a token cancelled when the page stops or the feature is disabled or removed.</summary>
    CancellationToken Stopping { get; }

    /// <summary>
    /// Makes the feature's context the ambient one until the scope is disposed: the SDK facades
    /// called inside act for the feature. <see cref="Caller"/> keeps naming the app that called.
    /// </summary>
    IDisposable EnterFeature();
}

/// <summary>
/// The feature services the calling app may use (ADR 0262 §2), behind <see cref="KazoFeatures"/>.
/// The OS implements it for each app; a host without feature services answers « not installed ».
/// </summary>
public interface IAppFeatureGateway
{
    /// <summary>Raised when a provider appears, is disabled or is removed.</summary>
    event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed;

    /// <summary>Gets the provider's instance of <paramref name="contract"/>, or <see langword="null"/>; a null is never cached.</summary>
    /// <param name="contract">The contract, an interface.</param>
    /// <param name="cancellationToken">Cancels the load of the feature's code.</param>
    ValueTask<object?> GetServiceAsync(Type contract, CancellationToken cancellationToken = default);

    /// <summary>Gets whether a usable provider is installed, without loading it.</summary>
    /// <param name="contract">The contract.</param>
    bool IsAvailable(Type contract);

    /// <summary>Gets why the service is, or is not, available.</summary>
    /// <param name="contract">The contract.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<KazoFeatureServiceStatus> GetStatusAsync(Type contract, CancellationToken cancellationToken = default);
}

/// <summary>The gateway of a context that has no feature services: every contract is « not installed ».</summary>
public sealed class UnsupportedAppFeatureGateway : IAppFeatureGateway
{
    /// <inheritdoc />
    public event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public ValueTask<object?> GetServiceAsync(Type contract, CancellationToken cancellationToken = default) => ValueTask.FromResult<object?>(null);

    /// <inheritdoc />
    public bool IsAvailable(Type contract) => false;

    /// <inheritdoc />
    public ValueTask<KazoFeatureServiceStatus> GetStatusAsync(Type contract, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(KazoFeatureServiceStatus.NotInstalled);
}
