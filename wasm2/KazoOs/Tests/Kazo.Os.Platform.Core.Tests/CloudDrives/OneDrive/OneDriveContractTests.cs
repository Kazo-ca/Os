using System.Net;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives.OneDrive;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.OneDrive;

/// <summary>The OneDrive connector keeps the contract of every cloud connector, against <see cref="FakeGraphServer"/>.</summary>
public sealed class OneDriveContractTests : CloudHostFolderContractTests
{
    /// <inheritdoc />
    protected override ICloudDriveContractService CreateService(FakeCloudAccountTokens tokens) => new OneDriveContractService(tokens);

    /// <summary>The fake Graph seen through the contract.</summary>
    internal sealed class OneDriveContractService : ICloudDriveContractService
    {
        public OneDriveContractService(FakeCloudAccountTokens tokens, CloudAccessScope scope = CloudAccessScope.Full)
        {
            tokens.Add(AccountId, CloudOAuthProvider.OneDriveId, scope, "alice@example.test");
            Server = new FakeGraphServer(() => tokens.CurrentToken(AccountId)) { QuotaTotal = 5_000_000 };
            Source = new OneDriveDriveSource(tokens, new StaticCatalog(), new HttpClient(Server));
        }

        public string AccountId => "od-alice";

        public FakeGraphServer Server { get; }

        public OneDriveDriveSource Source { get; }

        public HostFolderQuota? ExpectedQuota => new(Server.QuotaTotal, Server.QuotaTotal - Server.Used);

        public bool Online
        {
            get => Server.Online;
            set => Server.Online = value;
        }

        public async ValueTask<IHostFolder> OpenAsync() =>
            (await Source.CreateAsync(new NetworkDriveRequest(AccountId)))!.Folder;

        public void SeedFolder(string path) => Server.Folder(Split(path));

        public void SeedFile(string path, byte[] content) => Server.Put(content, Split(path));

        public byte[]? ReadRemote(string path) => Server.Find(Split(path)) is { IsFolder: false } node ? node.Content : null;

        public bool ExistsRemote(string path) => Server.Find(Split(path)) is not null;

        public void RevokeCurrentToken() => Server.RevokeCurrentToken();

        public void FailNext(int count, int status) => Server.FailNext(count, (HttpStatusCode)status);

        private static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A catalog with OneDrive configured, or nothing.</summary>
    internal sealed class StaticCatalog(bool configured = true) : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>(
                configured ? [CloudOAuthProvider.OneDrive("fake-client", null)!] : []);
    }
}
