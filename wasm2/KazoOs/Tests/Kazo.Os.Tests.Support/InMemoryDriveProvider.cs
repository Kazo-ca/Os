using System.Collections.Concurrent;
using System.Globalization;
using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// A third-party drive provider for tests (ADR 0282, work items 4, 6 and 7): a
/// <see cref="KazoDriveProvider"/> whose service is an <see cref="InMemoryDriveService"/>, found by
/// the <see cref="ServiceOption"/> value of the drive's options (the non-secret values the OS keeps
/// in the account). It is created by reflection like a real provider, with no parameter.
/// </summary>
public sealed class InMemoryDriveProvider : KazoDriveProvider
{
    /// <summary>The option that names the <see cref="InMemoryDriveService"/> of a drive.</summary>
    public const string ServiceOption = "service";

    /// <summary>The <c>Name, Assembly</c> form a manifest declares this class with.</summary>
    public static readonly string ManifestName = $"{typeof(InMemoryDriveProvider).FullName}, {typeof(InMemoryDriveProvider).Assembly.GetName().Name}";

    /// <inheritdoc />
    public override async ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        InMemoryDriveService service = request.Options.TryGetValue(ServiceOption, out string? key) && InMemoryDriveService.Find(key) is { } found
            ? found
            : throw new KazoDriveException(KazoDriveErrorKind.Other, "No in-memory service is named in the drive's options.");
        service.CountOpen();
        if (service.OpenGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (service.OpenFailure is { } failure)
        {
            throw failure;
        }

        return new InMemoryDriveFolder(service, request.Context);
    }
}

/// <summary>
/// The fake service behind <see cref="InMemoryDriveProvider"/>: a tree of folders and files with a
/// revision per file, a quota, an access token it accepts, and switches for the network and the
/// service's failures. Paths are <c>/a/b</c> from the root.
/// </summary>
public sealed class InMemoryDriveService : IDisposable
{
    private static readonly ConcurrentDictionary<string, InMemoryDriveService> s_services = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new Node(true, [], 0) };
    private readonly Queue<KazoDriveErrorKind> _failures = new();
    private long _revision;
    private int _opened;
    private int _closed;

    /// <summary>Creates a service and makes it findable by its <see cref="Key"/>.</summary>
    /// <param name="totalBytes">The size the service reports.</param>
    public InMemoryDriveService(long totalBytes = 1_000_000)
    {
        TotalBytes = totalBytes;
        s_services[Key] = this;
    }

    /// <summary>Gets the value of <see cref="InMemoryDriveProvider.ServiceOption"/> that names this service.</summary>
    public string Key { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the size the service reports.</summary>
    public long TotalBytes { get; }

    /// <summary>Gets or sets whether the network works; offline, every operation is unavailable.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Gets or sets the access token the service accepts; <see langword="null"/> asks for none.</summary>
    public Func<string>? AcceptedToken { get; set; }

    /// <summary>Gets or sets a gate <see cref="InMemoryDriveProvider.OpenAsync"/> waits for (a slow provider).</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    /// <summary>Gets or sets what <see cref="InMemoryDriveProvider.OpenAsync"/> throws (a broken provider).</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>Gets how many times a drive of this service was opened.</summary>
    public int Opened => Volatile.Read(ref _opened);

    /// <summary>Gets how many folders of this service were closed.</summary>
    public int Closed => Volatile.Read(ref _closed);

    /// <summary>Gets the service of <paramref name="key"/>, or <see langword="null"/>.</summary>
    /// <param name="key">The service's <see cref="Key"/>.</param>
    public static InMemoryDriveService? Find(string key) => s_services.GetValueOrDefault(key);

    /// <summary>The next <paramref name="count"/> operations fail with <paramref name="kind"/>.</summary>
    /// <param name="count">How many operations fail.</param>
    /// <param name="kind">The error they fail with.</param>
    public void FailNext(int count, KazoDriveErrorKind kind)
    {
        lock (_gate)
        {
            for (int index = 0; index < count; index++)
            {
                _failures.Enqueue(kind);
            }
        }
    }

    /// <summary>Creates a folder and its parents, as another device would.</summary>
    /// <param name="path">The folder, <c>/a/b</c>.</param>
    public void SeedFolder(string path)
    {
        lock (_gate)
        {
            EnsureFolders(Normalize(path));
        }
    }

    /// <summary>Creates or replaces a file and its parents, as another device would.</summary>
    /// <param name="path">The file, <c>/a/b.txt</c>.</param>
    /// <param name="content">Its content.</param>
    public void SeedFile(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string normalized = Normalize(path);
        lock (_gate)
        {
            EnsureFolders(Parent(normalized));
            _nodes[normalized] = new Node(false, [.. content], ++_revision);
        }
    }

    /// <summary>Reads a file; <see langword="null"/> when no file is there.</summary>
    /// <param name="path">The file, <c>/a/b.txt</c>.</param>
    public byte[]? ReadRemote(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(Normalize(path), out Node? node) && !node.IsFolder ? [.. node.Content] : null;
        }
    }

    /// <summary>Gets whether anything is at <paramref name="path"/>.</summary>
    /// <param name="path">The entry, <c>/a/b</c>.</param>
    public bool ExistsRemote(string path)
    {
        lock (_gate)
        {
            return _nodes.ContainsKey(Normalize(path));
        }
    }

    /// <inheritdoc />
    public void Dispose() => s_services.TryRemove(Key, out _);

    internal void CountOpen() => Interlocked.Increment(ref _opened);

    internal void CountClose() => Interlocked.Increment(ref _closed);

    /// <summary>Checks the network, the queued failures and the token, as each request would.</summary>
    internal async ValueTask CheckAsync(IKazoDriveProviderContext context, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "The network is down.");
        }

        lock (_gate)
        {
            if (_failures.TryDequeue(out KazoDriveErrorKind kind))
            {
                throw new KazoDriveException(kind, $"The service answered {kind}.");
            }
        }

        if (AcceptedToken is { } accepted)
        {
            string token = await context.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(token, accepted(), StringComparison.Ordinal))
            {
                throw new KazoDriveException(KazoDriveErrorKind.SignInRequired, "The service refused the access token.");
            }
        }
    }

    internal KazoDriveEntry Stat(KazoDrivePath path)
    {
        lock (_gate)
        {
            return Describe(KeyOf(path), Require(path));
        }
    }

    internal IReadOnlyList<KazoDriveEntry> List(KazoDrivePath path)
    {
        lock (_gate)
        {
            string key = KeyOf(path);
            if (!Require(path).IsFolder)
            {
                throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, $"{path} is a file.");
            }

            return [.. _nodes.Where(pair => pair.Key != "/" && Parent(pair.Key) == key).Select(static pair => Describe(pair.Key, pair.Value))];
        }
    }

    internal byte[] Read(KazoDrivePath path)
    {
        lock (_gate)
        {
            Node node = Require(path);
            return node.IsFolder ? throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, $"{path} is a folder.") : [.. node.Content];
        }
    }

    internal void Write(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision)
    {
        string key = KeyOf(path);
        lock (_gate)
        {
            RequireParent(key);
            if (_nodes.TryGetValue(key, out Node? existing))
            {
                if (existing.IsFolder)
                {
                    throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, $"{path} is a folder.");
                }

                if (ifRevision is not null && ifRevision != existing.Revision.ToString(CultureInfo.InvariantCulture))
                {
                    throw new KazoDriveException(KazoDriveErrorKind.RevisionConflict, $"{path} changed elsewhere.");
                }
            }
            else if (ifRevision is not null)
            {
                throw new KazoDriveException(KazoDriveErrorKind.RevisionConflict, $"{path} is gone.");
            }

            long used = _nodes.Where(pair => pair.Key != key).Sum(static pair => (long)pair.Value.Content.Length);
            if (used + content.Length > TotalBytes)
            {
                throw new KazoDriveException(KazoDriveErrorKind.QuotaExceeded, "The storage is full.");
            }

            _nodes[key] = new Node(false, content.ToArray(), ++_revision);
        }
    }

    internal void CreateFolder(KazoDrivePath path)
    {
        string key = KeyOf(path);
        lock (_gate)
        {
            RequireParent(key);
            if (_nodes.ContainsKey(key))
            {
                throw new KazoDriveException(KazoDriveErrorKind.Exists, $"{path} exists.");
            }

            _nodes[key] = new Node(true, [], ++_revision);
        }
    }

    internal void Delete(KazoDrivePath path, bool recursive)
    {
        string key = KeyOf(path);
        lock (_gate)
        {
            Require(path);
            string[] children = [.. _nodes.Keys.Where(other => other.StartsWith(key + "/", StringComparison.Ordinal))];
            if (children.Length > 0 && !recursive)
            {
                throw new KazoDriveException(KazoDriveErrorKind.NotEmpty, $"{path} is not empty.");
            }

            foreach (string child in children)
            {
                _nodes.Remove(child);
            }

            _nodes.Remove(key);
        }
    }

    internal void Move(KazoDrivePath from, KazoDrivePath to)
    {
        string source = KeyOf(from);
        string destination = KeyOf(to);
        lock (_gate)
        {
            Require(from);
            RequireParent(destination);
            if (_nodes.ContainsKey(destination))
            {
                throw new KazoDriveException(KazoDriveErrorKind.Exists, $"{to} exists.");
            }

            foreach (string key in _nodes.Keys.Where(key => key == source || key.StartsWith(source + "/", StringComparison.Ordinal)).ToArray())
            {
                _nodes[destination + key[source.Length..]] = _nodes[key];
                _nodes.Remove(key);
            }
        }
    }

    /// <summary>Gets the size the service reports and the space its files leave free.</summary>
    public KazoDriveQuota Quota()
    {
        lock (_gate)
        {
            long used = _nodes.Values.Sum(static node => (long)node.Content.Length);
            return new KazoDriveQuota(TotalBytes, Math.Max(0, TotalBytes - used));
        }
    }

    private Node Require(KazoDrivePath path) =>
        _nodes.TryGetValue(KeyOf(path), out Node? node) ? node : throw new KazoDriveException(KazoDriveErrorKind.NotFound, $"{path} does not exist.");

    private void RequireParent(string key)
    {
        if (!_nodes.TryGetValue(Parent(key), out Node? parent) || !parent.IsFolder)
        {
            throw new KazoDriveException(KazoDriveErrorKind.NotFound, $"The folder of {key} does not exist.");
        }
    }

    private void EnsureFolders(string path)
    {
        if (path == "/" || _nodes.ContainsKey(path))
        {
            return;
        }

        EnsureFolders(Parent(path));
        _nodes[path] = new Node(true, [], ++_revision);
    }

    private static KazoDriveEntry Describe(string key, Node node) =>
        new(key == "/" ? string.Empty : key[(key.LastIndexOf('/') + 1)..],
            node.IsFolder ? KazoDriveEntryKind.Folder : KazoDriveEntryKind.File,
            node.Content.Length,
            DateTimeOffset.UnixEpoch)
        {
            Revision = node.IsFolder ? null : node.Revision.ToString(CultureInfo.InvariantCulture)
        };

    private static string KeyOf(KazoDrivePath path) => path.Segments.Count == 0 ? "/" : "/" + string.Join('/', path.Segments);

    private static string Normalize(string path) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static string Parent(string key)
    {
        int separator = key.LastIndexOf('/');
        return separator <= 0 ? "/" : key[..separator];
    }

    private sealed record Node(bool IsFolder, byte[] Content, long Revision);
}

/// <summary>A drive of <see cref="InMemoryDriveProvider"/>.</summary>
internal sealed class InMemoryDriveFolder(InMemoryDriveService service, IKazoDriveProviderContext context) : KazoDriveFolder
{
    public override ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(service.Online ? KazoDriveState.Ready : KazoDriveState.Unavailable);

    public override async ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        return service.Stat(path);
    }

    public override async ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        return service.List(path);
    }

    public override async ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        return service.Read(path);
    }

    public override async ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        service.Write(path, content, ifRevision);
    }

    public override async ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        service.CreateFolder(path);
    }

    public override async ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        service.Delete(path, recursive);
    }

    public override async ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        service.Move(from, to);
        return true;
    }

    public override async ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        await service.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        return service.Quota();
    }

    public override ValueTask DisposeAsync()
    {
        service.CountClose();
        return base.DisposeAsync();
    }
}
