using System.Text.Json;

namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// Reads the version of Kazo OS the origin serves now (ADR 0174): the <c>version.json</c> file the
/// build publishes next to the page. The browser host fetches it past every cache.
/// </summary>
public interface IHostReleaseProbe
{
    /// <summary>Gets the content of the served <c>version.json</c>, or <see langword="null"/> when unreachable.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks every few minutes whether the origin serves another version of Kazo OS than the one
/// running (ADR 0174), like an update notifier: it only says a newer release waits; the host shows
/// it and offers the restart. Each check also asks <see cref="HostBuildUpdateMonitor"/> whether the
/// build changed under the page with the same version (ADR 0162). ADR 0236: it also compares the
/// build numbers and keeps a <see cref="Status"/> (up to date, outdated, unreachable) for the
/// sign-in screen, which checks as soon as it opens.
/// </summary>
public sealed class HostReleaseMonitor : IAsyncDisposable
{
    /// <summary>The default time between two checks: five minutes.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly IHostReleaseProbe _probe;
    private readonly HostBuildUpdateMonitor? _buildMonitor;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task? _loop;
    private string? _availableVersion;
    private int? _availableBuild;
    private HostReleaseStatus _status;

    /// <summary>Creates the monitor.</summary>
    /// <param name="probe">Reads the served <c>version.json</c>.</param>
    /// <param name="runningVersion">The version of the running OS (<c>KazoOsSdk.Version</c>).</param>
    /// <param name="buildMonitor">Also checked at each tick, when given.</param>
    public HostReleaseMonitor(IHostReleaseProbe probe, string runningVersion, HostBuildUpdateMonitor? buildMonitor = null)
        : this(probe, runningVersion, runningBuild: null, buildMonitor)
    {
    }

    /// <summary>Creates the monitor with the build number of the running OS (ADR 0236).</summary>
    /// <param name="probe">Reads the served <c>version.json</c>.</param>
    /// <param name="runningVersion">The version of the running OS (<c>KazoOsSdk.Version</c>).</param>
    /// <param name="runningBuild">The build number of the running OS (<see cref="HostBuildNumber"/>), <see langword="null"/> for a local build.</param>
    /// <param name="buildMonitor">Also checked at each tick, when given.</param>
    public HostReleaseMonitor(IHostReleaseProbe probe, string runningVersion, int? runningBuild, HostBuildUpdateMonitor? buildMonitor = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        ArgumentException.ThrowIfNullOrWhiteSpace(runningVersion);
        RunningVersion = runningVersion;
        RunningBuild = runningBuild is > 0 ? runningBuild : null;
        _buildMonitor = buildMonitor;
    }

    /// <summary>Gets the version of the running OS.</summary>
    public string RunningVersion { get; }

    /// <summary>Gets the build number of the running OS, or <see langword="null"/> for a local build (ADR 0236).</summary>
    public int? RunningBuild { get; }

    /// <summary>Gets the other version the origin serves, or <see langword="null"/> while none was found.</summary>
    public string? AvailableVersion => Volatile.Read(ref _availableVersion);

    /// <summary>Gets the build number the origin serves when it is an update, or <see langword="null"/> (ADR 0236).</summary>
    public int? AvailableBuild
    {
        get
        {
            lock (_gate)
            {
                return _availableBuild;
            }
        }
    }

    /// <summary>Gets whether the running OS is the one the origin serves, as far as the last check knows (ADR 0236).</summary>
    public HostReleaseStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised each time the origin is found serving a version not reported yet.</summary>
    public event Action<string>? ReleaseAvailable;

    /// <summary>Raised when <see cref="Status"/> or <see cref="AvailableBuild"/> changes (ADR 0236).</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// Reads the served version once. An unreachable origin or an unreadable file is not an
    /// update: the OS keeps running offline.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// <see langword="true"/> when the origin serves another version or another build than the
    /// running one, or when the files changed under the page.
    /// </returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        bool filesChanged = false;
        if (_buildMonitor is not null)
        {
            filesChanged = await _buildMonitor.CheckAsync(cancellationToken).ConfigureAwait(false);
        }

        string? document;
        try
        {
            document = await _probe.GetServedVersionDocumentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            document = null;
        }

        string? served = ParseVersion(document);
        int? servedBuild = ParseBuild(document);
        bool otherVersion = served is not null && !string.Equals(served, RunningVersion, StringComparison.Ordinal);
        // ADR 0236: the same version with another number is another build (a Pages deployment
        // without a version change); a local build or an origin without a number compares the
        // versions only.
        bool otherBuild = served is not null && !otherVersion
            && RunningBuild is not null && servedBuild is not null && servedBuild != RunningBuild;

        HostReleaseStatus next = otherVersion || otherBuild || filesChanged
            ? HostReleaseStatus.UpdateAvailable
            : served is null ? HostReleaseStatus.Unreachable : HostReleaseStatus.UpToDate;
        bool raiseStatus = UpdateStatus(next, otherVersion || otherBuild ? servedBuild : null);

        if (otherVersion
            && !string.Equals(Interlocked.Exchange(ref _availableVersion, served), served, StringComparison.Ordinal))
        {
            ReleaseAvailable?.Invoke(served!);
        }

        if (raiseStatus)
        {
            StatusChanged?.Invoke();
        }

        return Status == HostReleaseStatus.UpdateAvailable;
    }

    /// <summary>Starts checking at <paramref name="interval"/>, the first check after one interval. A second call does nothing.</summary>
    /// <param name="interval">Time between two checks; <see cref="DefaultInterval"/> in the browser host.</param>
    public void Start(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _loop ??= RunAsync(interval, _stopping.Token);
    }

    /// <summary>
    /// Returns the <c>version</c> of a <c>version.json</c> document, or <see langword="null"/> when
    /// the document is missing, malformed or has no version.
    /// </summary>
    /// <param name="document">The JSON text: <c>{ "product": "Kazo OS", "version": "2.1.0" }</c>.</param>
    public static string? ParseVersion(string? document)
    {
        using JsonDocument? json = TryParse(document);
        return json is not null
            && json.RootElement.TryGetProperty("version", out JsonElement version)
            && version.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(version.GetString())
            ? version.GetString()!.Trim()
            : null;
    }

    /// <summary>
    /// Returns the <c>build</c> of a <c>version.json</c> document (ADR 0236): a positive number,
    /// written as a JSON number or string. A missing, zero or textual build is no number.
    /// </summary>
    /// <param name="document">The JSON text: <c>{ "product": "Kazo OS", "version": "3.2.1", "build": 1234 }</c>.</param>
    public static int? ParseBuild(string? document)
    {
        using JsonDocument? json = TryParse(document);
        if (json is null || !json.RootElement.TryGetProperty("build", out JsonElement build))
        {
            return null;
        }

        return build.ValueKind switch
        {
            JsonValueKind.Number when build.TryGetInt32(out int number) && number > 0 => number,
            JsonValueKind.String => HostBuildNumber.Parse(build.GetString()),
            _ => null,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is the expected end of the loop.
            }
        }

        _stopping.Dispose();
    }

    private static JsonDocument? TryParse(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return null;
        }

        try
        {
            JsonDocument json = JsonDocument.Parse(document);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                return json;
            }

            json.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the result of a check. An outdated page stays outdated (its files do not change
    /// until the restart), whatever a later check finds.
    /// </summary>
    /// <returns><see langword="true"/> when the status or the available build changed.</returns>
    private bool UpdateStatus(HostReleaseStatus next, int? availableBuild)
    {
        lock (_gate)
        {
            if (_status == HostReleaseStatus.UpdateAvailable)
            {
                if (next != HostReleaseStatus.UpdateAvailable || availableBuild is null || availableBuild == _availableBuild)
                {
                    return false;
                }

                _availableBuild = availableBuild;
                return true;
            }

            if (_status == next)
            {
                return false;
            }

            _status = next;
            _availableBuild = availableBuild;
            return true;
        }
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
