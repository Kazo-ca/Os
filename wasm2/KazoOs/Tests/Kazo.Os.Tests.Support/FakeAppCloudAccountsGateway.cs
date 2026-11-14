using Kazo.Os.AppSdk.CloudAccounts;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// In-memory <see cref="IAppCloudAccountsGateway"/> (ADR 0270): providers and accounts chosen by the
/// test, a sign-in that succeeds at once. Never reaches a real provider.
/// </summary>
public sealed class FakeAppCloudAccountsGateway : IAppCloudAccountsGateway
{
    private readonly List<KazoCloudAccount> _accounts = [];

    /// <summary>Gets the providers offered.</summary>
    public List<KazoCloudProvider> Providers { get; } = [];

    /// <summary>Gets the accounts.</summary>
    public IReadOnlyList<KazoCloudAccount> Accounts => _accounts;

    /// <summary>Gets how many sign-ins <see cref="ConnectAsync"/> ran.</summary>
    public int SignIns { get; private set; }

    /// <summary>Adds a provider with its scopes.</summary>
    public FakeAppCloudAccountsGateway WithProvider(string id, string name, params KazoCloudAccessScope[] scopes)
    {
        Providers.Add(new KazoCloudProvider(id, name, scopes.Length == 0 ? [KazoCloudAccessScope.Full] : scopes));
        return this;
    }

    /// <summary>Adds an account.</summary>
    public FakeAppCloudAccountsGateway WithAccount(
        string id, string providerId, string displayName,
        KazoCloudAccessScope scope = KazoCloudAccessScope.Full,
        KazoCloudAccountState state = KazoCloudAccountState.Connected)
    {
        _accounts.Add(new KazoCloudAccount(id, providerId, scope, displayName, state, DateTimeOffset.UnixEpoch));
        return this;
    }

    public ValueTask<bool> CanConnectAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public ValueTask PrepareAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<KazoCloudProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoCloudProvider>>([.. Providers]);

    public ValueTask<IReadOnlyList<KazoCloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoCloudAccount>>([.. _accounts]);

    public ValueTask<KazoCloudAccount> ConnectAsync(string providerId, KazoCloudAccessScope scope, CancellationToken cancellationToken = default)
    {
        SignIns++;
        KazoCloudAccount account = new($"acc-{_accounts.Count + 1}", providerId, scope, $"user@{providerId}", KazoCloudAccountState.Connected, DateTimeOffset.UnixEpoch);
        _accounts.Add(account);
        return ValueTask.FromResult(account);
    }

    public ValueTask<KazoCloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        int index = Find(accountId);
        _accounts[index] = _accounts[index] with { State = KazoCloudAccountState.Connected };
        return ValueTask.FromResult(_accounts[index]);
    }

    public ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        _accounts.RemoveAt(Find(accountId));
        return ValueTask.CompletedTask;
    }

    private int Find(string accountId)
    {
        int index = _accounts.FindIndex(account => account.Id == accountId);
        return index >= 0
            ? index
            : throw new KazoCloudAccountException(KazoCloudAccountError.AccountNotFound, $"{accountId}: no such account");
    }
}
