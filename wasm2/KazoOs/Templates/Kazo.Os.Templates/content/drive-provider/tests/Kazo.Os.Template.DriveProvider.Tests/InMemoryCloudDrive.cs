using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Template.DriveProvider.Tests;

/// <summary>
/// A service kept in memory that behaves as the real one answers: the tests never reach a real
/// service. Every drive opened on it sees the same files, as two devices on one account do.
/// </summary>
internal sealed class InMemoryCloudDrive
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new(true, [], 0, null) };
    private int _revisions;

    /// <summary>Gets or sets whether the service answers; <see langword="false"/> stands for no network.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Gets or sets the access token the service accepts; another one is refused as an expired sign-in.</summary>
    public string ValidToken { get; set; } = "token-1";

    /// <summary>Gets or sets the size of the account's storage, or <see langword="null"/> when the service does not say.</summary>
    public long? QuotaBytes { get; set; } = 50_000_000;

    /// <summary>Gives the API one drive sees, signed with the token <paramref name="context"/> lends.</summary>
    public ICloudDriveApi Connect(IKazoDriveProviderContext context) => new Api(this, context);

    /// <summary>Places a file of <paramref name="length"/> bytes without holding its content (a file too large to read).</summary>
    public void PlaceFile(string path, long length)
    {
        lock (_gate)
        {
            _nodes[path] = new(false, [], length, NextRevision());
        }
    }

    private string NextRevision() => "rev-" + Interlocked.Increment(ref _revisions);

    private static string ParentOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static KazoDriveException Error(KazoDriveErrorKind kind, string message) => new(kind, message);

    private sealed record Node(bool IsFolder, byte[] Content, long Size, string? Revision);

    private sealed class Api(InMemoryCloudDrive service, IKazoDriveProviderContext context) : ICloudDriveApi
    {
        public async Task<CloudDriveItem> GetItemAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                return ItemOf(path, Find(path));
            }
        }

        public async Task<IReadOnlyList<CloudDriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                if (!Find(path).IsFolder)
                {
                    throw Error(KazoDriveErrorKind.TypeMismatch, "not_a_folder");
                }

                return [.. service._nodes.Where(pair => pair.Key != "/" && ParentOf(pair.Key) == path).Select(pair => ItemOf(pair.Key, pair.Value))];
            }
        }

        public async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                Node node = Find(path);
                if (node.IsFolder)
                {
                    throw Error(KazoDriveErrorKind.TypeMismatch, "is_a_folder");
                }

                return node.Size > KazoDriveLimits.MaxFileBytes
                    ? throw Error(KazoDriveErrorKind.FileTooLarge, "too large")
                    : [.. node.Content];
            }
        }

        public async Task<CloudDriveItem> UploadAsync(string path, ReadOnlyMemory<byte> content, CloudDriveUploadMode mode, string? revision, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                RequireParentFolder(path);
                if (service._nodes.TryGetValue(path, out Node? existing))
                {
                    if (existing.IsFolder || mode == CloudDriveUploadMode.Create)
                    {
                        throw Error(existing.IsFolder && mode == CloudDriveUploadMode.Overwrite ? KazoDriveErrorKind.TypeMismatch : KazoDriveErrorKind.Exists, "exists");
                    }

                    if (revision is not null && revision != existing.Revision)
                    {
                        throw Error(KazoDriveErrorKind.RevisionConflict, "conflict");
                    }
                }
                else if (revision is not null)
                {
                    throw Error(KazoDriveErrorKind.RevisionConflict, "conflict");
                }

                Node written = new(false, content.ToArray(), content.Length, service.NextRevision());
                service._nodes[path] = written;
                return ItemOf(path, written);
            }
        }

        public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                RequireParentFolder(path);
                if (!service._nodes.TryAdd(path, new(true, [], 0, null)))
                {
                    throw Error(KazoDriveErrorKind.Exists, "exists");
                }
            }
        }

        public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                Find(path);
                string[] inside = [.. service._nodes.Keys.Where(key => key.StartsWith(path + "/", StringComparison.Ordinal))];
                if (inside.Length > 0 && !recursive)
                {
                    throw Error(KazoDriveErrorKind.NotEmpty, "not_empty");
                }

                foreach (string key in inside.Append(path))
                {
                    service._nodes.Remove(key);
                }
            }
        }

        public async Task MoveAsync(string from, string to, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                Find(from);
                RequireParentFolder(to);
                if (service._nodes.ContainsKey(to))
                {
                    throw Error(KazoDriveErrorKind.Exists, "exists");
                }

                foreach (string key in service._nodes.Keys.Where(key => key == from || key.StartsWith(from + "/", StringComparison.Ordinal)).ToArray())
                {
                    Node node = service._nodes[key];
                    service._nodes.Remove(key);
                    service._nodes[to + key[from.Length..]] = node;
                }
            }
        }

        public async Task<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            lock (service._gate)
            {
                if (service.QuotaBytes is not { } total)
                {
                    return null;
                }

                long used = service._nodes.Values.Sum(static node => node.Size);
                return new KazoDriveQuota(total, Math.Max(0, total - used));
            }
        }

        /// <summary>What every request meets first: the network, then the sign-in.</summary>
        private async Task EnterAsync(CancellationToken cancellationToken)
        {
            string token = await context.GetAccessTokenAsync(cancellationToken);
            if (!service.Online)
            {
                throw Error(KazoDriveErrorKind.Unavailable, "The service cannot be reached.");
            }

            if (token != service.ValidToken)
            {
                throw Error(KazoDriveErrorKind.SignInRequired, "The service refused the token.");
            }
        }

        private Node Find(string path) =>
            service._nodes.TryGetValue(path, out Node? node) ? node : throw Error(KazoDriveErrorKind.NotFound, "not found");

        private void RequireParentFolder(string path)
        {
            if (!service._nodes.TryGetValue(ParentOf(path), out Node? parent))
            {
                throw Error(KazoDriveErrorKind.NotFound, "The parent folder does not exist.");
            }

            if (!parent.IsFolder)
            {
                throw Error(KazoDriveErrorKind.TypeMismatch, "not_a_folder");
            }
        }

        private static CloudDriveItem ItemOf(string path, Node node) =>
            new(path == "/" ? string.Empty : NameOf(path), node.IsFolder, node.Size, DateTimeOffset.UnixEpoch, node.Revision);
    }
}
