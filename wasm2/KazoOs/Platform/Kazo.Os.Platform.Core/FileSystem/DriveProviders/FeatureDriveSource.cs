using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.DriveProviders;

/// <summary>What the OS lends a provider for one drive: the context, and the non-secret values of its account.</summary>
/// <param name="Context">The context bound to the drive's account (ADR 0282 §3).</param>
/// <param name="Options">The non-secret values of a form account; empty for an OAuth account.</param>
public sealed record FeatureDriveConnection(IKazoDriveProviderContext Context, IReadOnlyDictionary<string, string> Options);

/// <summary>
/// Creates what the OS lends the provider of a declared type for one drive (ADR 0282 §3): bound to
/// the drive's account, refused when the account is missing or of another type.
/// </summary>
/// <param name="type">The drive type, e.g. <c>pcloud</c>.</param>
/// <param name="accountId">The account of the drive.</param>
/// <param name="availabilityChanged">Called when the provider reports that its service may be reachable again.</param>
/// <param name="cancellationToken">Cancels the read of the account list.</param>
public delegate ValueTask<FeatureDriveConnection> FeatureDriveContextFactory(
    string type,
    string accountId,
    Action availabilityChanged,
    CancellationToken cancellationToken);

/// <summary>
/// The <see cref="INetworkDriveSource"/> of one drive type a feature provides (ADR 0282 §3): the
/// platform's internal contract on one side, the feature's <see cref="KazoDriveProvider"/> on the
/// other. <see cref="HostFolders.NetworkDriveService"/> mounts what it gives like any other source.
/// </summary>
/// <remarks>
/// <para>
/// A drive of a provided type always belongs to an account the OS keeps (OAuth or form, ADR 0282
/// §3): <see cref="CreateAsync"/> checks that the account exists and is of this type, and never
/// calls the provider. Neither does <see cref="OpenAsync"/>: the provider is loaded the first time a
/// drive asks (<see cref="FeatureDriveFolder"/>), so a login never waits for it.
/// </para>
/// <para>
/// <see cref="INetworkDriveSource.AvailabilityChanged"/> is raised when the accounts change, when a
/// drive's folder finished opening, and when the provider calls
/// <see cref="IKazoDriveProviderContext.NotifyAvailabilityChanged"/>.
/// </para>
/// </remarks>
public sealed class FeatureDriveSource : INetworkDriveSource, IDisposable
{
    /// <summary>How long loading a provider and opening a drive may take before the drive stays disconnected.</summary>
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long <see cref="KazoDriveFolder.GetStateAsync"/> may take before the drive is unavailable.</summary>
    public static readonly TimeSpan DefaultStateTimeout = TimeSpan.FromSeconds(10);

    private readonly FeatureDriveProviderLoader _loader;
    private readonly FeatureDriveContextFactory _contexts;

    /// <summary>Creates the source of one declared type.</summary>
    /// <param name="registration">The feature and its declaration.</param>
    /// <param name="loader">The page's loader of providers.</param>
    /// <param name="accounts">The cloud accounts (the drive's account, its state, renewal and sign-in again).</param>
    /// <param name="contexts">Creates the context lent to the provider for one drive.</param>
    /// <param name="timeProvider">The clock of the time limits.</param>
    public FeatureDriveSource(
        AppDriveProviderRegistration registration,
        FeatureDriveProviderLoader loader,
        ICloudAccountTokens accounts,
        FeatureDriveContextFactory contexts,
        TimeProvider? timeProvider = null)
    {
        Registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        Time = timeProvider ?? TimeProvider.System;
        Accounts.Changed += RaiseAvailabilityChanged;
    }

    /// <summary>Gets the feature and the declaration this source serves.</summary>
    public AppDriveProviderRegistration Registration { get; }

    /// <summary>Gets the id of the feature that provides the type.</summary>
    public string FeatureId => Registration.Feature.Id;

    /// <inheritdoc />
    public string Type => Registration.Provider.Type;

    /// <summary>Gets or sets how long loading and opening may take; <see cref="DefaultOpenTimeout"/> by default.</summary>
    public TimeSpan OpenTimeout { get; init; } = DefaultOpenTimeout;

    /// <summary>Gets or sets how long the state question may take; <see cref="DefaultStateTimeout"/> by default.</summary>
    public TimeSpan StateTimeout { get; init; } = DefaultStateTimeout;

    /// <summary>Gets the cloud accounts.</summary>
    internal ICloudAccountTokens Accounts { get; }

    /// <summary>Gets the clock of the time limits.</summary>
    internal TimeProvider Time { get; }

    /// <inheritdoc />
    public event Action? AvailabilityChanged;

    /// <inheritdoc />
    /// <remarks>Always: the registry only lists the types of installed and enabled features.</remarks>
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    /// <inheritdoc />
    /// <remarks>
    /// The account must exist and be of this type. The form's values are not taken from the
    /// request: the OS keeps them in the account (non-secret options) and sealed (secrets).
    /// </remarks>
    public async ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Account))
        {
            throw new ArgumentException($"{Type}: choose an account first.", nameof(request));
        }

        CloudAccount? account = await Accounts.FindAccountAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (account is null || !string.Equals(account.ProviderId, Type, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{request.Account}: no {Registration.Provider.Name} account has this id.", nameof(request));
        }

        NetworkDriveReference reference = new(account.DisplayName, Key: null, Account: account.Id, Root: request.Root);
        return new NetworkDriveCreation(new FeatureDriveFolder(this, reference), reference, Registration.Provider.Name);
    }

    /// <inheritdoc />
    /// <remarks>Never calls the provider; a line with no account is dropped (<see langword="null"/>).</remarks>
    public ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ValueTask.FromResult<IHostFolder?>(string.IsNullOrEmpty(reference.Account) ? null : new FeatureDriveFolder(this, reference));
    }

    /// <inheritdoc />
    /// <remarks>Nothing to forget: the account stays until the user removes it (ADR 0282 §3).</remarks>
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Accounts.Changed -= RaiseAvailabilityChanged;

    /// <summary>Raises <see cref="AvailabilityChanged"/>.</summary>
    internal void RaiseAvailabilityChanged() => AvailabilityChanged?.Invoke();

    /// <summary>Loads the provider of the type, once per page.</summary>
    [RequiresUnreferencedCode("A drive provider is created by reflection from its feature's loaded assembly.")]
    internal Task<LoadedDriveProvider?> LoadProviderAsync() => _loader.LoadAsync(Registration);

    /// <summary>Creates the context of one drive.</summary>
    internal ValueTask<FeatureDriveConnection> ConnectAsync(string accountId) =>
        _contexts(Type, accountId, RaiseAvailabilityChanged, CancellationToken.None);

    /// <summary>Writes a failure of the provider to the feature's log.</summary>
    internal void LogProviderFailure(string reason) => _loader.Fail(FeatureId, Type, reason);
}
