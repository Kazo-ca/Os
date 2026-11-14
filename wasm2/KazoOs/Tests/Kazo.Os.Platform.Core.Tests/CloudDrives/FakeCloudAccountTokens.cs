using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives;

/// <summary>
/// The cloud accounts as a connector sees them (<see cref="ICloudAccountTokens"/>), without the
/// sign-in window, the keyring or a token server: each account has one current access token
/// (<c>token-1</c>, <c>token-2</c>…) that a renewal or a reconnection replaces.
/// </summary>
public sealed class FakeCloudAccountTokens : ICloudAccountTokens
{
    private readonly Dictionary<string, CloudAccount> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Gets or sets whether a token renewal cannot reach the provider (offline).</summary>
    public bool Offline { get; set; }

    /// <summary>Gets or sets what the next sign-in window does; <see langword="null"/> succeeds.</summary>
    public CloudAccountError? NextReconnectFails { get; set; }

    /// <summary>Gets the number of renewals asked after a refused token.</summary>
    public int Renewals { get; private set; }

    /// <summary>Gets the number of sign-in windows opened.</summary>
    public int Reconnections { get; private set; }

    /// <summary>Adds a connected account.</summary>
    public CloudAccount Add(string id, string providerId, CloudAccessScope scope = CloudAccessScope.Full, string? displayName = null)
    {
        CloudAccount account = new(id, providerId, scope, displayName ?? $"{id}@example.test", $"{providerId}:{id}", CloudAccountState.Connected, DateTimeOffset.UnixEpoch);
        _accounts[id] = account;
        _generation[id] = 1;
        return account;
    }

    /// <summary>Gets the access token the provider currently accepts for <paramref name="id"/>.</summary>
    public string CurrentToken(string id) => $"token-{id}-{_generation[id]}";

    /// <summary>The provider stopped renewing: the account needs its sign-in window.</summary>
    public void RequireReconnect(string id)
    {
        _accounts[id] = _accounts[id] with { State = CloudAccountState.NeedsReconnect };
        Changed?.Invoke();
    }

    /// <summary>Removes an account.</summary>
    public void Remove(string id)
    {
        _accounts.Remove(id);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public ValueTask<CloudAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_accounts.GetValueOrDefault(accountId));

    /// <inheritdoc />
    public ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken cancellationToken = default)
    {
        CloudAccount account = Usable(accountId);
        return ValueTask.FromResult(CurrentToken(account.Id));
    }

    /// <inheritdoc />
    public ValueTask<string> RenewAccessTokenAsync(string accountId, string rejectedAccessToken, CancellationToken cancellationToken = default)
    {
        CloudAccount account = Usable(accountId);
        if (string.Equals(rejectedAccessToken, CurrentToken(account.Id), StringComparison.Ordinal))
        {
            if (Offline)
            {
                throw new CloudAccountException(CloudAccountError.ProviderUnavailable, "offline");
            }

            Renewals++;
            _generation[account.Id]++;
        }

        return ValueTask.FromResult(CurrentToken(account.Id));
    }

    /// <inheritdoc />
    public ValueTask<CloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        Reconnections++;
        if (!_accounts.TryGetValue(accountId, out CloudAccount? account))
        {
            throw new CloudAccountException(CloudAccountError.AccountNotFound, accountId);
        }

        if (NextReconnectFails is { } error)
        {
            NextReconnectFails = null;
            throw new CloudAccountException(error, $"sign-in: {error}");
        }

        account = account with { State = CloudAccountState.Connected };
        _accounts[accountId] = account;
        _generation[accountId]++;
        Changed?.Invoke();
        return ValueTask.FromResult(account);
    }

    private CloudAccount Usable(string accountId)
    {
        if (!_accounts.TryGetValue(accountId, out CloudAccount? account))
        {
            throw new CloudAccountException(CloudAccountError.AccountNotFound, accountId);
        }

        return account.State == CloudAccountState.NeedsReconnect
            ? throw new CloudAccountException(CloudAccountError.ReconnectRequired, $"{accountId} must sign in again")
            : account;
    }
}
