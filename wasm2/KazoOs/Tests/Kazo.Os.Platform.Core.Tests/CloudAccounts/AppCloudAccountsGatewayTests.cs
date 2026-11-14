using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// ADR 0270: the SDK's cloud accounts gateway is reserved to apps holding
/// <c>cloud-accounts.manage</c>, carries references only, and reports refusals with the SDK's own
/// reasons; mounting a cloud drive needs the same capability, a folder of the computer does not.
/// </summary>
public sealed class AppCloudAccountsGatewayTests
{
    private readonly SettingsFilesHarness _harness = new(withKeyring: true);
    private readonly FakeOAuthServer _server = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Without_the_capability_only_CanConnect_answers()
    {
        await _harness.LoginAsync(_harness.Alice);
        AppCloudAccountsGateway gateway = new(CreateService, new Granted());

        Assert.True(await gateway.CanConnectAsync());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetAccountsAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetProvidersAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.ConnectAsync("dropbox", KazoCloudAccessScope.Full).AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.RemoveAsync("x").AsTask());
        Assert.Empty(_server.AuthorizationRequests);
    }

    [Fact]
    public async Task With_the_capability_an_app_connects_lists_and_removes_accounts()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService service = CreateService();
        AppCloudAccountsGateway gateway = new(() => service, new Granted(AppCapabilities.CloudAccountsManage));

        KazoCloudProvider provider = Assert.Single(await gateway.GetProvidersAsync());
        KazoCloudAccount account = await gateway.ConnectAsync(provider.Id, KazoCloudAccessScope.AppFolder);
        IReadOnlyList<KazoCloudAccount> listed = await gateway.GetAccountsAsync();
        await gateway.RemoveAsync(account.Id);

        Assert.Equal("dropbox", provider.Id);
        Assert.Contains(KazoCloudAccessScope.AppFolder, provider.Scopes);
        Assert.Equal(("dropbox", KazoCloudAccessScope.AppFolder, "alice@example.test", KazoCloudAccountState.Connected),
            (account.ProviderId, account.Scope, account.DisplayName, account.State));
        Assert.Equal(account, Assert.Single(listed));
        Assert.Empty(await gateway.GetAccountsAsync());
    }

    [Fact]
    public async Task A_refusal_carries_the_sdk_reason()
    {
        await _harness.LoginAsync(_harness.Alice);
        AppCloudAccountsGateway gateway = new(CreateService, new Granted(AppCapabilities.CloudAccountsManage));

        KazoCloudAccountException unknown = await Assert.ThrowsAsync<KazoCloudAccountException>(
            () => gateway.ConnectAsync("box", KazoCloudAccessScope.Full).AsTask());
        KazoCloudAccountException missing = await Assert.ThrowsAsync<KazoCloudAccountException>(
            () => gateway.ReconnectAsync("nope").AsTask());

        Assert.Equal(KazoCloudAccountError.ProviderNotConfigured, unknown.Error);
        Assert.Equal(KazoCloudAccountError.AccountNotFound, missing.Error);
    }

    [Fact]
    public void Every_platform_reason_has_an_sdk_reason_of_the_same_name()
    {
        foreach (CloudAccountError error in Enum.GetValues<CloudAccountError>())
        {
            Assert.Equal(error.ToString(), AppCloudAccountsGateway.ToSdk(error).ToString());
        }
    }

    [Fact]
    public async Task Mounting_a_cloud_drive_needs_the_capability_and_a_folder_of_the_computer_does_not()
    {
        FakeAppDriveGateway inner = new() { NetworkTypes = { "dropbox", "hostfs" } };
        CapabilityCheckedDriveGateway plain = new(inner, new Granted());
        CapabilityCheckedDriveGateway system = new(inner, new Granted(AppCapabilities.CloudAccountsManage));

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => plain.MountNetworkDriveAsync(new KazoNetworkDriveRequest("dropbox", "a1"), null).AsTask());
        Assert.NotNull(await plain.MountNetworkDriveAsync(new KazoNetworkDriveRequest("hostfs"), "/mnt/h"));
        Assert.NotNull(await system.MountNetworkDriveAsync(new KazoNetworkDriveRequest("dropbox", "a1"), "/mnt/d"));
        Assert.Equal(["dropbox", "hostfs"], await plain.GetNetworkDriveTypesAsync());
    }

    private CloudAccountService CreateService() =>
        new(
            _harness.FileSystem,
            _harness.Clock,
            _harness.Keyring!,
            new ManagedKeyringCipher(),
            new FixedCatalog(_server.Provider),
            _server,
            new CloudOAuthTokenClient(new HttpClient(_server), _time),
            () => _harness.Session.CurrentPrincipal,
            () => new NoDrives(),
            timeProvider: _time);

    private sealed class FixedCatalog(CloudOAuthProvider provider) : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>([provider]);
    }

    private sealed class NoDrives : ICloudAccountDrives
    {
        public ValueTask ReleaseAsync(string accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Granted(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
