using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Apps.Store;

/// <summary>What the Store shows for an app or a pack it is working on (ADR 0266 §7).</summary>
internal enum StoreOperationStage
{
    /// <summary>Another install, update or removal runs: this one waits its turn.</summary>
    Queued,

    /// <summary>The OS reads the sources and the index (<see cref="KazoPackageInstallStage.Preparing"/>).</summary>
    Preparing,

    /// <summary>The OS downloads the package (<see cref="KazoPackageInstallStage.Downloading"/>).</summary>
    Downloading,

    /// <summary>The OS checks the package (<see cref="KazoPackageInstallStage.Verifying"/>).</summary>
    Verifying,

    /// <summary>An administrator is asked in the OS's window (<see cref="KazoPackageInstallStage.AwaitingApproval"/>).</summary>
    AwaitingApproval,

    /// <summary>The OS writes the app (<see cref="KazoPackageInstallStage.Installing"/>).</summary>
    Installing,

    /// <summary>An action that reports no steps: a removal, a rollback, a theme pack, a launch.</summary>
    Working
}

/// <summary>
/// One running or waiting action of the Store, immutable: each report replaces it.
/// </summary>
/// <param name="Id">The app ID or the pack ID.</param>
/// <param name="Stage">Where it stands.</param>
/// <param name="BytesReceived">Bytes downloaded, during <see cref="StoreOperationStage.Downloading"/>.</param>
/// <param name="TotalBytes">Size of the package: the host's, or else the catalog's.</param>
internal sealed record StoreOperation(string Id, StoreOperationStage Stage, long? BytesReceived = null, long? TotalBytes = null)
{
    /// <summary>Gets the downloaded share from 0 to 1, or <see langword="null"/> when it cannot be known.</summary>
    public double? Fraction => Stage == StoreOperationStage.Downloading && BytesReceived is { } received && TotalBytes is > 0 and { } total
        ? Math.Clamp((double)received / total, 0, 1)
        : null;

    /// <summary>Gets the downloaded share as a whole percent, or <see langword="null"/>.</summary>
    public int? Percent => Fraction is { } fraction ? (int)Math.Floor(fraction * 100) : null;

    /// <summary>Maps a step the OS reports to the Store's.</summary>
    public static StoreOperationStage ToStage(KazoPackageInstallStage stage) => stage switch
    {
        KazoPackageInstallStage.Preparing => StoreOperationStage.Preparing,
        KazoPackageInstallStage.Downloading => StoreOperationStage.Downloading,
        KazoPackageInstallStage.Verifying => StoreOperationStage.Verifying,
        KazoPackageInstallStage.AwaitingApproval => StoreOperationStage.AwaitingApproval,
        KazoPackageInstallStage.Installing => StoreOperationStage.Installing,
        _ => StoreOperationStage.Working
    };
}

/// <summary>
/// The Store's actions on apps and packs (ADR 0266 §7). Each app has its own state, so only its
/// buttons are disabled; the actions that change what is installed run one at a time, like a package
/// manager holding its lock, and the next ones show « Waiting » until their turn. A launch does not
/// wait.
/// </summary>
internal sealed class StoreOperations
{
    private readonly Dictionary<string, StoreOperation> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _packageLock = new(1, 1);
    private readonly Action _changed;

    /// <summary>Creates the tracker.</summary>
    /// <param name="changed">Called after each change, to redraw the window.</param>
    public StoreOperations(Action changed)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>Gets whether any action runs or waits.</summary>
    public bool Any => _running.Count > 0;

    /// <summary>Gets whether an action runs or waits on <paramref name="id"/>.</summary>
    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>Gets the action on <paramref name="id"/>, or <see langword="null"/>.</summary>
    public StoreOperation? Find(string id) => _running.GetValueOrDefault(id);

    /// <summary>
    /// Runs <paramref name="action"/> on <paramref name="id"/>. Does nothing when an action already
    /// runs on it (a double click).
    /// </summary>
    /// <param name="id">The app ID or the pack ID.</param>
    /// <param name="firstStage">Shown once its turn comes.</param>
    /// <param name="exclusive">Whether it changes what is installed and waits for the others.</param>
    /// <param name="action">The action; it may report steps through <see cref="ProgressFor"/>.</param>
    /// <returns>Whether the action ran.</returns>
    public async Task<bool> RunAsync(string id, StoreOperationStage firstStage, bool exclusive, Func<Task> action)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(action);
        if (_running.ContainsKey(id))
        {
            return false;
        }

        bool waits = exclusive && _packageLock.CurrentCount == 0;
        Set(new StoreOperation(id, waits ? StoreOperationStage.Queued : firstStage));
        bool locked = false;
        try
        {
            if (exclusive)
            {
                await _packageLock.WaitAsync();
                locked = true;
                if (waits)
                {
                    Set(new StoreOperation(id, firstStage));
                }
            }

            await action();
            return true;
        }
        finally
        {
            if (locked)
            {
                _packageLock.Release();
            }

            _running.Remove(id);
            _changed();
        }
    }

    /// <summary>
    /// Receives the steps of <see cref="KazoAppPackages.InstallAsync(string, KazoPackageScope, IProgress{KazoPackageInstallProgress}?, CancellationToken)"/>
    /// for <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The app being installed.</param>
    /// <param name="catalogBytes">The package size the catalog announces, used when the host sends none.</param>
    public IProgress<KazoPackageInstallProgress> ProgressFor(string id, long? catalogBytes) => new Reporter(this, id, catalogBytes);

    private void Report(string id, KazoPackageInstallProgress progress, long? catalogBytes)
    {
        if (!_running.TryGetValue(id, out StoreOperation? current))
        {
            return;
        }

        StoreOperation next = progress.Stage == KazoPackageInstallStage.Downloading
            ? new StoreOperation(id, StoreOperationStage.Downloading, progress.BytesReceived ?? 0, progress.TotalBytes ?? catalogBytes)
            : new StoreOperation(id, StoreOperation.ToStage(progress.Stage));
        if (current == next)
        {
            return;
        }

        Set(next);
    }

    private void Set(StoreOperation operation)
    {
        _running[operation.Id] = operation;
        _changed();
    }

    /// <summary>
    /// Applies a report at once: the Store runs on Blazor's single WebAssembly thread, so the OS's
    /// call reaches it there, and a <see cref="Progress{T}"/> would only delay it.
    /// </summary>
    private sealed class Reporter(StoreOperations owner, string id, long? catalogBytes) : IProgress<KazoPackageInstallProgress>
    {
        public void Report(KazoPackageInstallProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            owner.Report(id, value, catalogBytes);
        }
    }
}
