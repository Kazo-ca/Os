namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>What part of the user's storage an account reaches, chosen at connection (ADR 0267 section 8).</summary>
public enum CloudAccessScope
{
    /// <summary>All of the user's storage.</summary>
    Full,

    /// <summary>Only a folder reserved for Kazo OS (<c>Apps/Kazo OS</c> on Dropbox, the app folder on OneDrive).</summary>
    AppFolder
}

/// <summary>Whether an account's tokens still work.</summary>
public enum CloudAccountState
{
    /// <summary>The account has a token, or can renew one without the user.</summary>
    Connected,

    /// <summary>
    /// The provider refused to renew the token (expired after 24 hours on OneDrive, access revoked):
    /// the user must sign in again. The account and its drives are kept.
    /// </summary>
    NeedsReconnect
}

/// <summary>Why a cloud account operation was refused.</summary>
public enum CloudAccountError
{
    /// <summary>Nobody is signed in.</summary>
    NotSignedIn,

    /// <summary>
    /// The user's keyring is locked: an account without a password (the guest), or a sign-in with a
    /// passkey whose authenticator gives no PRF output. Tokens would have nowhere safe to live.
    /// </summary>
    KeyringLocked,

    /// <summary>The provider is unknown, or this OS has no app id for it (or for that scope).</summary>
    ProviderNotConfigured,

    /// <summary>No account with that id.</summary>
    AccountNotFound,

    /// <summary>The browser blocked the sign-in window.</summary>
    WindowBlocked,

    /// <summary>The sign-in window was closed, or nothing came back in time.</summary>
    Cancelled,

    /// <summary>The provider answered with an error (the user refused, a wrong app id).</summary>
    Denied,

    /// <summary>The provider could not be reached (offline, a network error, a server error).</summary>
    ProviderUnavailable,

    /// <summary>The provider refused the token request or sent an answer Kazo OS cannot read.</summary>
    InvalidResponse,

    /// <summary>The provider no longer renews the token: the account needs <see cref="CloudAccountService.ReconnectAsync"/>.</summary>
    ReconnectRequired,

    /// <summary>A reconnection signed in to another account than the one the drives belong to.</summary>
    AccountMismatch
}

/// <summary>A refused cloud account operation, with a message for the user.</summary>
/// <param name="error">Why it was refused.</param>
/// <param name="message">What to tell the user (English; the UI shows its own translated text per <see cref="Error"/>).</param>
/// <param name="innerException">The underlying failure, when there is one.</param>
public sealed class CloudAccountException(CloudAccountError error, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>Gets why the operation was refused.</summary>
    public CloudAccountError Error { get; } = error;
}

/// <summary>A cloud account of the signed-in user, as the UI and the connectors see it. Never carries a token.</summary>
/// <param name="Id">The OS's id of the account (random, stable across reconnections).</param>
/// <param name="ProviderId">The provider: <c>dropbox</c> or <c>onedrive</c>.</param>
/// <param name="Scope">The part of the storage the account reaches.</param>
/// <param name="DisplayName">What the provider calls the account (an e-mail address or a name), or the provider's name.</param>
/// <param name="ProviderAccountId">The provider's own id of the account, used to refuse a reconnection to another account.</param>
/// <param name="State">Whether its tokens still work.</param>
/// <param name="ConnectedAtUtc">When the user last signed in.</param>
public sealed record CloudAccount(
    string Id,
    string ProviderId,
    CloudAccessScope Scope,
    string DisplayName,
    string? ProviderAccountId,
    CloudAccountState State,
    DateTimeOffset ConnectedAtUtc);

/// <summary>A provider this OS can connect, with the scopes it has an app id for.</summary>
/// <param name="Id">The provider id: <c>dropbox</c> or <c>onedrive</c>.</param>
/// <param name="DisplayName">Its name: Dropbox, OneDrive.</param>
/// <param name="Scopes">The scopes offered; never empty.</param>
public sealed record CloudProviderInfo(string Id, string DisplayName, IReadOnlyList<CloudAccessScope> Scopes);

/// <summary>
/// Releases the drives of an account before it is removed (ADR 0267 section 5: removing an account
/// unmounts its drives). The network drive service implements it once drives can belong to an account.
/// </summary>
public interface ICloudAccountDrives
{
    /// <summary>Unmounts every drive of <paramref name="accountId"/> and forgets them.</summary>
    /// <param name="accountId">The account being removed.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    ValueTask ReleaseAsync(string accountId, CancellationToken cancellationToken = default);
}
