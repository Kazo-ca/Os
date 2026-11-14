using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives.Dropbox;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.Dropbox;

/// <summary>The Dropbox connector keeps the contract of every cloud connector, against <see cref="FakeDropboxServer"/>.</summary>
public sealed class DropboxContractTests : CloudHostFolderContractTests
{
    /// <inheritdoc />
    protected override ICloudDriveContractService CreateService(FakeCloudAccountTokens tokens) => new DropboxContractService(tokens);

    /// <summary>The fake Dropbox seen through the contract.</summary>
    internal sealed class DropboxContractService : ICloudDriveContractService
    {
        private readonly FakeCloudAccountTokens _tokens;

        public DropboxContractService(FakeCloudAccountTokens tokens, CloudAccessScope scope = CloudAccessScope.Full)
        {
            _tokens = tokens;
            tokens.Add(AccountId, CloudOAuthProvider.DropboxId, scope, "alice@example.test");
            Server = new FakeDropboxServer(() => tokens.CurrentToken(AccountId));
            Source = new DropboxDriveSource(tokens, new StaticCatalog(), new HttpClient(Server));
        }

        public string AccountId => "dbx-alice";

        public FakeDropboxServer Server { get; }

        public DropboxDriveSource Source { get; }

        public HostFolderQuota? ExpectedQuota => new(Server.Allocated, Server.Allocated - Server.Used);

        public bool Online
        {
            get => Server.Online;
            set => Server.Online = value;
        }

        public async ValueTask<IHostFolder> OpenAsync() =>
            (await Source.CreateAsync(new NetworkDriveRequest(AccountId)))!.Folder;

        public void SeedFolder(string path) => Server.PutFolder(path);

        public void SeedFile(string path, byte[] content) => Server.PutFile(path, content);

        public byte[]? ReadRemote(string path) => Server.Read(path);

        public bool ExistsRemote(string path) => Server.Exists(path);

        public void RevokeCurrentToken() => Server.RevokeCurrentToken();

        public void FailNext(int count, int status) => Server.FailNext(count, status);
    }

    /// <summary>A catalog with both Dropbox apps configured.</summary>
    internal sealed class StaticCatalog(bool configured = true) : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>(
                configured ? [CloudOAuthProvider.Dropbox("full-key", "app-folder-key")!] : []);
    }
}
