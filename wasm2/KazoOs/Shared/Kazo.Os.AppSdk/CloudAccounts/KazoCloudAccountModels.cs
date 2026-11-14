namespace Kazo.Os.AppSdk.CloudAccounts;

/// <summary>What part of the user's storage a cloud account reaches, chosen at connection (ADR 0267 §8).</summary>
public enum KazoCloudAccessScope
{
    /// <summary>All of the user's storage.</summary>
    Full,

    /// <summary>Only a folder reserved for Kazo OS (<c>Apps/Kazo OS</c> on Dropbox, the app folder on OneDrive).</summary>
    AppFolder
}

/// <summary>Whether a cloud account's sign-in still works.</summary>
public enum KazoCloudAccountState
{
    /// <summary>The account works, or renews its sign-in without the user.</summary>
    Connected,

    /// <summary>The service refused to renew the sign-in: the user signs in again with <see cref="KazoCloudAccounts.ReconnectAsync"/>.</summary>
    NeedsReconnect
}

/// <summary>Why a cloud account operation was refused (ADR 0270).</summary>
public enum KazoCloudAccountError
{
    /// <summary>Something else; the message says what.</summary>
    Other,

    /// <summary>Nobody is signed in.</summary>
    NotSignedIn,

    /// <summary>The user's keyring is locked (the guest, a passkey without PRF): the sign-in would have nowhere safe to live.</summary>
    KeyringLocked,

    /// <summary>The service is unknown here, or this OS has no app id for it.</summary>
    ProviderNotConfigured,

    /// <summary>No account with that id.</summary>
    AccountNotFound,

    /// <summary>The browser blocked the sign-in window.</summary>
    WindowBlocked,

    /// <summary>The sign-in window was closed, or nothing came back in time.</summary>
    Cancelled,

    /// <summary>The service refused the sign-in.</summary>
    Denied,

    /// <summary>The service could not be reached.</summary>
    ProviderUnavailable,

    /// <summary>The service sent an answer the OS cannot read.</summary>
    InvalidResponse,

    /// <summary>The service no longer renews the sign-in: <see cref="KazoCloudAccounts.ReconnectAsync"/>.</summary>
    ReconnectRequired,

    /// <summary>A reconnection signed in to another account than the drives' one.</summary>
    AccountMismatch
}

/// <summary>A refused cloud account operation (ADR 0270).</summary>
/// <param name="error">Why it was refused.</param>
/// <param name="message">The OS's message, in English; an app shows its own text for <see cref="Error"/>.</param>
public sealed class KazoCloudAccountException(KazoCloudAccountError error, string message) : InvalidOperationException(message)
{
    /// <summary>Gets why the operation was refused.</summary>
    public KazoCloudAccountError Error { get; } = error;
}

/// <summary>A cloud service the OS can connect (ADR 0270).</summary>
/// <param name="Id">The service, as <c>mount -t</c> takes it: <c>dropbox</c>, <c>onedrive</c>.</param>
/// <param name="DisplayName">Its name: Dropbox, OneDrive.</param>
/// <param name="Scopes">The scopes the user can choose; never empty.</param>
public sealed record KazoCloudProvider(string Id, string DisplayName, IReadOnlyList<KazoCloudAccessScope> Scopes);

/// <summary>A cloud account of the signed-in user (ADR 0270). Never carries a token or a key.</summary>
/// <param name="Id">The OS's id of the account, stable across reconnections.</param>
/// <param name="ProviderId">The service: <c>dropbox</c>, <c>onedrive</c>, <c>s3</c>.</param>
/// <param name="Scope">The part of the storage it reaches.</param>
/// <param name="DisplayName">What the service calls the account (an e-mail address, a name, a bucket).</param>
/// <param name="State">Whether its sign-in still works.</param>
/// <param name="ConnectedAtUtc">When the user last signed in.</param>
public sealed record KazoCloudAccount(
    string Id,
    string ProviderId,
    KazoCloudAccessScope Scope,
    string DisplayName,
    KazoCloudAccountState State,
    DateTimeOffset ConnectedAtUtc);

/// <summary>
/// The OS's cloud accounts as one app instance reaches them (ADR 0270), behind
/// <see cref="KazoCloudAccounts"/>. Every member but <see cref="CanConnectAsync"/> needs the
/// capability <c>cloud-accounts.manage</c>.
/// </summary>
public interface IAppCloudAccountsGateway
{
    /// <summary>Gets whether the signed-in user can connect an account (their keyring is open).</summary>
    ValueTask<bool> CanConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the sign-in window's script ahead of the click.</summary>
    ValueTask PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the services this OS can connect.</summary>
    ValueTask<IReadOnlyList<KazoCloudProvider>> GetProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the signed-in user's accounts.</summary>
    ValueTask<IReadOnlyList<KazoCloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the sign-in window of a service and adds the account.</summary>
    ValueTask<KazoCloudAccount> ConnectAsync(string providerId, KazoCloudAccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>Signs in again to an account.</summary>
    ValueTask<KazoCloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Removes an account: unmounts its drives, revokes its sign-in and forgets it.</summary>
    ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="IAppCloudAccountsGateway"/> of a host without cloud accounts.</summary>
public sealed class UnsupportedAppCloudAccountsGateway : IAppCloudAccountsGateway
{
    /// <summary>Gets the shared instance.</summary>
    public static UnsupportedAppCloudAccountsGateway Instance { get; } = new();

    private UnsupportedAppCloudAccountsGateway()
    {
    }

    /// <inheritdoc />
    public ValueTask<bool> CanConnectAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask PrepareAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoCloudProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoCloudProvider>>([]);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoCloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoCloudAccount>>([]);

    /// <inheritdoc />
    public ValueTask<KazoCloudAccount> ConnectAsync(string providerId, KazoCloudAccessScope scope, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    /// <inheritdoc />
    public ValueTask<KazoCloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default) =>
        throw Unsupported();

    /// <inheritdoc />
    public ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("This Kazo OS host has no cloud accounts.");
}
