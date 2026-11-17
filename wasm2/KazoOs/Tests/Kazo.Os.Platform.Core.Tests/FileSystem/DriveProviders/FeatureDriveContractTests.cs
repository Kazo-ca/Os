using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Platform.Core.FileSystem.DriveProviders;
using Kazo.Os.Platform.Core.Tests.CloudDrives;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>
/// A drive of a type a feature provides keeps the contract of the shipped connectors (ADR 0282 §2):
/// the suite of <see cref="CloudHostFolderContractTests"/>, run on <see cref="InMemoryDriveProvider"/>
/// through <see cref="FeatureDriveSource"/>, loaded in the feature's context like a real provider.
/// </summary>
public sealed class FeatureDriveContractTests : CloudHostFolderContractTests, IDisposable
{
    private readonly List<IDisposable> _owned = [];

    /// <inheritdoc />
    protected override ICloudDriveContractService CreateService(FakeCloudAccountTokens tokens)
    {
        MemoryContractService service = new(tokens);
        _owned.Add(service);
        return service;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }
    }

    /// <summary>The in-memory service seen through the contract, its drive opened by the adapter.</summary>
    private sealed class MemoryContractService : ICloudDriveContractService, IDisposable
    {
        private readonly FakeCloudAccountTokens _tokens;
        private readonly InMemoryDriveService _service = new(totalBytes: 5_000);
        private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
        private readonly DriveProviderFixtures.Page _page;

        public MemoryContractService(FakeCloudAccountTokens tokens)
        {
            _tokens = tokens;
            tokens.Add(AccountId, DriveProviderFixtures.Type, displayName: "alice@example.test");
            _service.AcceptedToken = () =>
            {
                string current = tokens.CurrentToken(AccountId);
                return _revoked.Contains(current) ? "(revoked)" : current;
            };
            _page = new DriveProviderFixtures.Page(tokens, _ => _service, DriveProviderFixtures.Feature());
        }

        public string AccountId => "mem-alice";

        public HostFolderQuota? ExpectedQuota
        {
            get
            {
                KazoDriveQuota quota = _service.Quota();
                return new HostFolderQuota(quota.TotalBytes, quota.FreeBytes);
            }
        }

        public bool Online
        {
            get => _service.Online;
            set => _service.Online = value;
        }

        public async ValueTask<IHostFolder> OpenAsync()
        {
            IHostFolder folder = (await _page.Source.CreateAsync(new NetworkDriveRequest(AccountId)))!.Folder;
            // The provider is loaded at the first operation, which waits for it (bounded).
            await folder.StatAsync([]);
            return folder;
        }

        public void SeedFolder(string path) => _service.SeedFolder(path);

        public void SeedFile(string path, byte[] content) => _service.SeedFile(path, content);

        public byte[]? ReadRemote(string path) => _service.ReadRemote(path);

        public bool ExistsRemote(string path) => _service.ExistsRemote(path);

        public void RevokeCurrentToken() => _revoked.Add(_tokens.CurrentToken(AccountId));

        public void FailNext(int count, int status) =>
            _service.FailNext(count, status >= 500 ? KazoDriveErrorKind.Unavailable : KazoDriveErrorKind.Other);

        public void Dispose()
        {
            _page.Dispose();
            _service.Dispose();
        }
    }
}
