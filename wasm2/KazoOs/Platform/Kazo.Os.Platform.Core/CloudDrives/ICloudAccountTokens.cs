using Kazo.Os.Platform.Core.CloudAccounts;

namespace Kazo.Os.Platform.Core.CloudDrives;

/// <summary>
/// What a cloud connector needs from the cloud accounts (ADR 0267 §5): the account a drive
/// belongs to, a valid access token, a renewal when the provider refuses one, and the sign-in
/// window to reconnect. <see cref="CloudAccountService"/> implements it; tests use a fake.
/// A connector never sees a refresh token.
/// </summary>
public interface ICloudAccountTokens
{
    /// <summary>Raised after an account is added, reconnected, removed or needs a reconnection.</summary>
    event Action? Changed;

    /// <summary>Finds an account of the signed-in user; <see langword="null"/> when it is not in the list.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<CloudAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Gets a valid access token, renewed first when it is about to expire.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <exception cref="CloudAccountException">The account needs a reconnection, the provider cannot be reached, or the keyring is locked.</exception>
    ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Renews an access token the provider refused (HTTP 401) before the expiry the OS knew.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="rejectedAccessToken">The token the provider refused.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <exception cref="CloudAccountException">As <see cref="GetAccessTokenAsync"/>.</exception>
    ValueTask<string> RenewAccessTokenAsync(string accountId, string rejectedAccessToken, CancellationToken cancellationToken = default);

    /// <summary>Opens the provider's sign-in window again for the account; needs a user gesture.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <exception cref="CloudAccountException">The sign-in failed or was closed.</exception>
    ValueTask<CloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default);
}
