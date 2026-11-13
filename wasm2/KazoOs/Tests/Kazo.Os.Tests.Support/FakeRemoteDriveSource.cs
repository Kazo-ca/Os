using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// An in-memory cloud drive source (ADR 0267 §3) for tests: no network, no provider. Each account
/// has its own storage, with a quota, service revisions (<c>r1</c>, <c>r2</c>…), conditional
/// writes, and a network that a test can cut (<see cref="Online"/>) or an account whose sign-in a
/// test can expire (<see cref="Expire"/>).
/// </summary>
/// <param name="type">Filesystem type, e.g. <c>fakecloud</c>.</param>
/// <param name="defaultMountName">Default mount point name under <c>/mnt</c>.</param>
public sealed class FakeRemoteDriveSource(string type = "fakecloud", string defaultMountName = "FakeCloud") : INetworkDriveSource
{
    private readonly Dictionary<string, FakeRemoteStorage> _accounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private bool _online = true;

    /// <inheritdoc />
    public string Type { get; } = type;

    /// <summary>Gets or sets whether a new drive can be created (the provider is configured).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Gets the references the service was asked to forget.</summary>
    public List<NetworkDriveReference> Forgotten { get; } = [];

    /// <summary>Gets the folders the source handed out, in order.</summary>
    public List<FakeRemoteFolder> Opened { get; } = [];

    /// <summary>
    /// Gets or sets whether the network works. Setting it back to <see langword="true"/> raises
    /// <see cref="AvailabilityChanged"/>, as the browser's <c>online</c> event would.
    /// </summary>
    public bool Online
    {
        get => _online;
        set
        {
            bool back = value && !_online;
            _online = value;
            if (back)
            {
                AvailabilityChanged?.Invoke();
            }
        }
    }

    /// <inheritdoc />
    public event Action? AvailabilityChanged;

    /// <summary>Gets the storage of an account, created empty with <paramref name="totalBytes"/> of quota.</summary>
    public FakeRemoteStorage Account(string account, long totalBytes = 1_000_000)
    {
        if (!_accounts.TryGetValue(account, out FakeRemoteStorage? storage))
        {
            storage = new FakeRemoteStorage(totalBytes);
            _accounts[account] = storage;
        }

        return storage;
    }

    /// <summary>Makes the account's sign-in expire: its drives ask for a reconnection with a gesture.</summary>
    public void Expire(string account) => _expired.Add(account);

    /// <summary>Signs the account in again and raises <see cref="AvailabilityChanged"/>.</summary>
    public void SignIn(string account)
    {
        _expired.Remove(account);
        AvailabilityChanged?.Invoke();
    }

    /// <inheritdoc />
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Available);

    /// <inheritdoc />
    public ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Available)
        {
            throw new NotSupportedException($"{Type}: not configured");
        }

        if (string.IsNullOrEmpty(request.Account))
        {
            return ValueTask.FromResult<NetworkDriveCreation?>(null);
        }

        if (!_accounts.ContainsKey(request.Account))
        {
            throw new ArgumentException($"{request.Account}: unknown account", nameof(request));
        }

        NetworkDriveReference reference = new(request.Account, Account: request.Account, Root: request.Root);
        return ValueTask.FromResult<NetworkDriveCreation?>(new NetworkDriveCreation(Open(reference), reference, defaultMountName));
    }

    /// <inheritdoc />
    public ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ValueTask.FromResult<IHostFolder?>(
            reference.Account is { } account && _accounts.ContainsKey(account) ? Open(reference) : null);
    }

    /// <inheritdoc />
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        Forgotten.Add(reference);
        return ValueTask.CompletedTask;
    }

    /// <summary>The state of the connection of an account, as the folder reports it.</summary>
    internal HostFolderPermission State(string account) =>
        !_online ? HostFolderPermission.Unavailable
        : _expired.Contains(account) ? HostFolderPermission.Prompt
        : HostFolderPermission.Granted;

    private FakeRemoteFolder Open(NetworkDriveReference reference)
    {
        string account = reference.Account ?? reference.Name;
        string[] root = reference.Root is { } path ? path.Split('/', StringSplitOptions.RemoveEmptyEntries) : [];
        FakeRemoteFolder folder = new(this, account, _accounts[account], root);
        Opened.Add(folder);
        return folder;
    }
}

/// <summary>
/// One drive of <see cref="FakeRemoteDriveSource"/>: an <see cref="IHostFolder"/> with a quota and
/// conditional writes, as a cloud connector is (ADR 0267 §4).
/// </summary>
public sealed class FakeRemoteFolder : IHostFolder, IHostFolderQuota, IConditionalHostFolder
{
    private readonly FakeRemoteDriveSource _source;
    private readonly string _account;
    private readonly FakeRemoteStorage _storage;
    private readonly string[] _root;

    internal FakeRemoteFolder(FakeRemoteDriveSource source, string account, FakeRemoteStorage storage, string[] root)
    {
        _source = source;
        _account = account;
        _storage = storage;
        _root = root;
    }

    /// <summary>The account, which <c>/proc/mounts</c> shows as the source.</summary>
    public string Name => _account;

    /// <summary>Gets the number of conditional writes the service received.</summary>
    public int ConditionalWrites { get; private set; }

    /// <summary>Gets or sets an action run just before the next conditional write, to change the file in between.</summary>
    public Action? BeforeNextConditionalWrite { get; set; }

    /// <summary>Gets whether the folder was closed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_source.State(_account));

    /// <summary>Stands for the provider's sign-in window: signs the account in again when the network works.</summary>
    public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        if (_source.Online)
        {
            _source.SignIn(_account);
        }

        return QueryPermissionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.FromResult(_storage.Stat(Full(path)));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.FromResult(_storage.List(Full(path)));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.FromResult(_storage.Read(Full(path)));
    }

    /// <inheritdoc />
    public ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default)
    {
        Check();
        _storage.Create(Full(path), kind);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default)
    {
        Check();
        _storage.Write(Full(path), content, expectedRevision: null);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteIfMatchAsync(
        IReadOnlyList<string> path,
        byte[] content,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Check();
        ConditionalWrites++;
        Action? before = BeforeNextConditionalWrite;
        BeforeNextConditionalWrite = null;
        before?.Invoke();
        _storage.Write(Full(path), content, expectedRevision);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default)
    {
        Check();
        _storage.Delete(Full(path), recursive);
        return ValueTask.CompletedTask;
    }

    /// <summary>The fake service has no move: the caller copies then deletes.</summary>
    public ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask<HostFolderQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        Check();
        return ValueTask.FromResult<HostFolderQuota?>(new HostFolderQuota(_storage.TotalBytes, _storage.TotalBytes - _storage.UsedBytes));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private string[] Full(IReadOnlyList<string> path) => [.. _root, .. path];

    /// <summary>A request that cannot reach the service fails as a connector's would.</summary>
    private void Check()
    {
        if (_source.State(_account) != HostFolderPermission.Granted)
        {
            throw new HostFolderException(HostFolderErrorKind.Unavailable, $"{_source.Type}: the service cannot be reached");
        }
    }
}

/// <summary>The storage of one account of <see cref="FakeRemoteDriveSource"/>: files with service revisions.</summary>
/// <param name="totalBytes">Size of the storage.</param>
public sealed class FakeRemoteStorage(long totalBytes)
{
    private readonly Node _root = new(string.Empty, null);
    private long _revisions;

    /// <summary>Gets the size of the storage.</summary>
    public long TotalBytes { get; } = totalBytes;

    /// <summary>Gets the bytes the files take.</summary>
    public long UsedBytes => Sum(_root);

    /// <summary>Puts a file, as another device of the user would; folders are created.</summary>
    public void Put(string text, params string[] path)
    {
        Node parent = _root;
        foreach (string segment in path[..^1])
        {
            if (!parent.Children!.TryGetValue(segment, out Node? child))
            {
                child = new Node(segment, null);
                parent.Children[segment] = child;
            }

            parent = child;
        }

        parent.Children![path[^1]] = new Node(path[^1], System.Text.Encoding.UTF8.GetBytes(text)) { Revision = NextRevision() };
    }

    /// <summary>The text of a file, or <see langword="null"/> when there is none.</summary>
    public string? Text(params string[] path)
    {
        try
        {
            return Find(path).Content is { } content ? System.Text.Encoding.UTF8.GetString(content) : null;
        }
        catch (HostFolderException)
        {
            return null;
        }
    }

    /// <summary>The service revision of a file.</summary>
    public string? RevisionOf(params string[] path) => Find(path).Revision;

    internal HostFolderEntry Stat(string[] path) => Describe(Find(path));

    internal IReadOnlyList<HostFolderEntry> List(string[] path) => [.. Folder(path).Children!.Values.Select(Describe)];

    internal byte[] Read(string[] path) =>
        Find(path).Content is { } content ? [.. content] : throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a file");

    internal void Create(string[] path, HostFolderEntryKind kind)
    {
        Node parent = Folder(path[..^1]);
        if (parent.Children!.ContainsKey(path[^1]))
        {
            throw new HostFolderException(HostFolderErrorKind.AlreadyExists, $"{path[^1]} exists");
        }

        parent.Children[path[^1]] = kind == HostFolderEntryKind.Directory
            ? new Node(path[^1], null)
            : new Node(path[^1], []) { Revision = NextRevision() };
    }

    internal void Write(string[] path, byte[] content, string? expectedRevision)
    {
        Node node = Find(path);
        if (node.Content is null)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a file");
        }

        if (expectedRevision is not null && expectedRevision != node.Revision)
        {
            throw new HostFolderException(HostFolderErrorKind.RevisionConflict, $"{path[^1]} changed");
        }

        if (UsedBytes - node.Content.LongLength + content.LongLength > TotalBytes)
        {
            throw new HostFolderException(HostFolderErrorKind.NoSpace, "quota exceeded");
        }

        node.Content = [.. content];
        node.Revision = NextRevision();
    }

    internal void Delete(string[] path, bool recursive)
    {
        Node node = Find(path);
        if (node.Children is { Count: > 0 } && !recursive)
        {
            throw new HostFolderException(HostFolderErrorKind.NotEmpty, "not empty");
        }

        Folder(path[..^1]).Children!.Remove(path[^1]);
    }

    private string NextRevision() => $"r{++_revisions}";

    private Node Find(string[] path)
    {
        Node current = _root;
        foreach (string segment in path)
        {
            if (current.Children is null)
            {
                throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a folder");
            }

            if (!current.Children.TryGetValue(segment, out Node? child))
            {
                throw new HostFolderException(HostFolderErrorKind.NotFound, $"{segment} not found");
            }

            current = child;
        }

        return current;
    }

    private Node Folder(string[] path)
    {
        Node node = Find(path);
        return node.Children is null ? throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "not a folder") : node;
    }

    private static long Sum(Node node) =>
        node.Content?.LongLength ?? node.Children!.Values.Sum(static child => Sum(child));

    private static HostFolderEntry Describe(Node node) => node.Content is { } content
        ? new HostFolderEntry(node.Name, HostFolderEntryKind.File, content.LongLength, DateTimeOffset.UnixEpoch) { Revision = node.Revision }
        : new HostFolderEntry(node.Name, HostFolderEntryKind.Directory, 0, null);

    private sealed class Node(string name, byte[]? content)
    {
        public string Name { get; } = name;

        public byte[]? Content { get; set; } = content;

        public string? Revision { get; set; }

        public Dictionary<string, Node>? Children { get; } = content is null ? new(StringComparer.Ordinal) : null;
    }
}
