using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// Trusted <see cref="IAppCloudAccountsGateway"/> behind <see cref="KazoCloudAccounts"/> (ADR 0270):
/// one per app instance, over the session's <see cref="CloudAccountService"/>. Every call but
/// <see cref="CanConnectAsync"/> checks the dangerous capability <c>cloud-accounts.manage</c>.
/// </summary>
/// <remarks>
/// Only references cross this gateway: an app never receives a token or a key. The service's
/// <see cref="CloudAccountException"/> becomes the SDK's <see cref="KazoCloudAccountException"/>
/// with the same reason, so the app shows its own translated text.
/// </remarks>
internal sealed class AppCloudAccountsGateway(Func<CloudAccountService> accounts, ICapabilityChecker capabilities) : IAppCloudAccountsGateway
{
    private readonly Func<CloudAccountService> _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly ICapabilityChecker _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    /// <inheritdoc />
    public ValueTask<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_accounts().CanConnect);

    /// <inheritdoc />
    public async ValueTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        await Translate(async () =>
        {
            await _accounts().PrepareAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoCloudProvider>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        IReadOnlyList<CloudProviderInfo> providers = await Translate(() => _accounts().GetProvidersAsync(cancellationToken)).ConfigureAwait(false);
        return [.. providers.Select(static provider => new KazoCloudProvider(provider.Id, provider.DisplayName, [.. provider.Scopes.Select(ToSdk)]))];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoCloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        IReadOnlyList<CloudAccount> accounts = await Translate(() => _accounts().GetAccountsAsync(cancellationToken)).ConfigureAwait(false);
        return [.. accounts.Select(ToSdk)];
    }

    /// <inheritdoc />
    public async ValueTask<KazoCloudAccount> ConnectAsync(string providerId, KazoCloudAccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        CloudAccessScope platformScope = scope == KazoCloudAccessScope.AppFolder ? CloudAccessScope.AppFolder : CloudAccessScope.Full;
        return ToSdk(await Translate(() => _accounts().ConnectAsync(providerId, platformScope, cancellationToken)).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<KazoCloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        return ToSdk(await Translate(() => _accounts().ReconnectAsync(accountId, cancellationToken)).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _capabilities.Require(AppCapabilities.CloudAccountsManage);
        await Translate(async () =>
        {
            await _accounts().RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>The SDK's shape of an account: references only.</summary>
    internal static KazoCloudAccount ToSdk(CloudAccount account) =>
        new(account.Id, account.ProviderId, ToSdk(account.Scope), account.DisplayName,
            account.State == CloudAccountState.NeedsReconnect ? KazoCloudAccountState.NeedsReconnect : KazoCloudAccountState.Connected,
            account.ConnectedAtUtc);

    private static KazoCloudAccessScope ToSdk(CloudAccessScope scope) =>
        scope == CloudAccessScope.AppFolder ? KazoCloudAccessScope.AppFolder : KazoCloudAccessScope.Full;

    /// <summary>The SDK's reason for a refused operation; the two enums share their member names.</summary>
    internal static KazoCloudAccountError ToSdk(CloudAccountError error) =>
        Enum.TryParse(error.ToString(), out KazoCloudAccountError mapped) ? mapped : KazoCloudAccountError.Other;

    private static async ValueTask<T> Translate<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (CloudAccountException exception)
        {
            throw new KazoCloudAccountException(ToSdk(exception.Error), exception.Message);
        }
    }
}
