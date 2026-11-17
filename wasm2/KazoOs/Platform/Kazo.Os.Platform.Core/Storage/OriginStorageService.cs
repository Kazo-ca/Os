using System.Globalization;
using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Storage;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Storage;

/// <summary>What the OS remembers of one feature's own storage (ADR 0250 §2, §3).</summary>
/// <param name="FeatureId">The feature.</param>
/// <param name="Name">Its name when it last wrote here.</param>
/// <param name="MarkerPath">Its marker in the Origin Private File System, or <see langword="null"/>.</param>
/// <param name="UsageBytes">The bytes it declared, or <see langword="null"/>.</param>
/// <param name="ReportedAtUtc">When it declared them, or <see langword="null"/>.</param>
public sealed record FeatureStorageRecord(string FeatureId, string Name, string? MarkerPath, long? UsageBytes, DateTimeOffset? ReportedAtUtc);

/// <summary>
/// The browser storage of the page's origin as the OS manages it (ADR 0250): the estimate apps read,
/// the one <c>navigator.storage.persist()</c> of the origin, and the features' markers and declared
/// usage, kept in <c>/var/lib/kazoos/storage/features.json</c> (a document of the OS's IndexedDB). At
/// boot it checks each marker; a marker gone means the browser erased that feature's storage: a
/// <c>storage.evicted</c> line in <c>kern.log</c>, a notification at the next login, and
/// <see cref="KazoStorage.Changed"/> to the feature at its next launch.
/// </summary>
public sealed class OriginStorageService
{
    /// <summary>The file of the features' markers and usage.</summary>
    public static VirtualPath StatePath { get; } = VirtualPath.Parse("/var/lib/kazoos/storage/features.json");

    /// <summary>The folder of <see cref="StatePath"/>.</summary>
    public static VirtualPath StateDirectory { get; } = VirtualPath.Parse("/var/lib/kazoos/storage");

    /// <summary>The app id the eviction notifications come from (the same as the low-space warning).</summary>
    public const string NotificationSourceAppId = StorageSpaceMonitor.NotificationSourceAppId;

    /// <summary>Settings, which shows the storage page.</summary>
    public const string SettingsAppId = "org.kazoos.settings";

    /// <summary>Longest marker path kept, in characters.</summary>
    public const int MaximumMarkerPathLength = 512;

    private readonly IOriginStorage _storage;
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly StorageEvictionDetector? _detector;
    private readonly INotificationQueue? _notifications;
    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly KernelLog? _kernelLog;
    private readonly TimeProvider _time;
    private readonly Func<CultureInfo> _language;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Lock _evictionGate = new();
    private readonly Dictionary<string, KazoStorageChangedEventArgs> _pendingChanges = new(StringComparer.Ordinal);
    private readonly List<FeatureStorageRecord> _toNotify = [];

    /// <summary>Creates the service.</summary>
    /// <param name="storage">The origin's storage.</param>
    /// <param name="fileSystem">The file system the state file is written to, as the system.</param>
    /// <param name="clock">The simulation clock of the file system's authorizations.</param>
    /// <param name="detector">The boot's eviction check, for <see cref="KazoStorageEstimate.PersistenceLost"/>; optional.</param>
    /// <param name="notifications">Where the eviction notifications go; none without it.</param>
    /// <param name="currentUser">The session user the notifications are for.</param>
    /// <param name="kernelLog">Where <c>storage.evicted</c> is written; optional.</param>
    /// <param name="time">The real clock; the system clock by default.</param>
    /// <param name="language">The language of the notifications; the session's.</param>
    public OriginStorageService(
        IOriginStorage storage,
        IFileSystemService fileSystem,
        ISimulationClock clock,
        StorageEvictionDetector? detector = null,
        INotificationQueue? notifications = null,
        Func<AuthenticatedPrincipal?>? currentUser = null,
        KernelLog? kernelLog = null,
        TimeProvider? time = null,
        Func<CultureInfo>? language = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _detector = detector;
        _notifications = notifications;
        _currentUser = currentUser ?? (static () => null);
        _kernelLog = kernelLog;
        _time = time ?? TimeProvider.System;
        _language = language ?? (static () => CultureInfo.InvariantCulture);
    }

    /// <summary>Gets how many times this service called <c>navigator.storage.persist()</c>; for the tests and Diagnostic.</summary>
    public int PersistenceRequests { get; private set; }

    /// <summary>Reads the origin's estimate, with what <paramref name="featureId"/> declared.</summary>
    /// <param name="featureId">The calling feature, or <see langword="null"/> for an app.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<KazoStorageEstimate> GetEstimateAsync(string? featureId, CancellationToken cancellationToken = default)
    {
        OriginStorageStatus? status = await _storage.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return KazoStorageEstimate.Unknown;
        }

        long? declared = null;
        if (featureId is not null)
        {
            declared = (await ReadStateAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(record => string.Equals(record.FeatureId, featureId, StringComparison.Ordinal))?.UsageBytes;
        }

        return new KazoStorageEstimate(
            status.UsageBytes,
            status.QuotaBytes,
            status.IsPersisted,
            status.QuotaBytes > 0 && StorageSpacePolicy.IsLow(new StorageCapacity(status.UsageBytes, status.QuotaBytes)))
        {
            IndexedDbBytes = status.IndexedDbBytes,
            CacheBytes = status.CacheBytes,
            FileSystemBytes = status.FileSystemBytes,
            DeclaredUsageBytes = declared,
            PersistenceLost = _detector?.PersistenceLost == true && !status.IsPersisted,
        };
    }

    /// <summary>
    /// ADR 0250 §1: asks the browser to protect the origin. The OS asks for the whole origin, one call at
    /// a time, and not at all while the origin is protected: a feature and Settings never ask twice. The
    /// state is read again at each call, since the browser may withdraw its protection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the origin is protected.</returns>
    public async ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _storage.GetStatusAsync(cancellationToken).ConfigureAwait(false) is { IsPersisted: true })
            {
                return true;
            }

            PersistenceRequests++;
            bool granted = await _storage.RequestPersistenceAsync(cancellationToken).ConfigureAwait(false);
            _kernelLog?.Write(
                KernelLogLevel.Notice,
                granted ? "storage: persistence granted by the browser" : "storage: persistence refused by the browser");
            return granted;
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>Records the marker of a feature (ADR 0250 §2).</summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="name">Its name.</param>
    /// <param name="path">The marker's path from the root of the Origin Private File System.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">
    /// The path is empty, too long, absolute or leaves the root; or the file is not in the Origin Private
    /// File System yet (a marker registered before it is written would read as an eviction at the next boot).
    /// </exception>
    public async ValueTask RegisterMarkerAsync(string featureId, string name, string path, CancellationToken cancellationToken = default)
    {
        string marker = NormalizeMarkerPath(path);
        if (await _storage.FileExistsAsync(marker, cancellationToken).ConfigureAwait(false) == false)
        {
            throw new ArgumentException($"'{marker}' is not in the Origin Private File System: write the marker first, then register it.", nameof(path));
        }

        await UpdateAsync(featureId, record => record with { Name = name, MarkerPath = marker }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the bytes a feature declared (ADR 0250 §3).</summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="name">Its name.</param>
    /// <param name="bytes">The bytes, zero or more.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask ReportUsageAsync(string featureId, string name, long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        DateTimeOffset now = _time.GetUtcNow();
        return UpdateAsync(featureId, record => record with { Name = name, UsageBytes = bytes, ReportedAtUtc = now }, cancellationToken);
    }

    /// <summary>Reads what the features recorded, by feature id.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<IReadOnlyList<FeatureStorageRecord>> GetFeaturesAsync(CancellationToken cancellationToken = default) =>
        [.. (await ReadStateAsync(cancellationToken).ConfigureAwait(false)).OrderBy(static record => record.FeatureId, StringComparer.Ordinal)];

    /// <summary>
    /// At boot, once the file system is ready: checks every feature's marker. A marker the Origin
    /// Private File System no longer has means the browser erased that storage; the marker is forgotten
    /// (the feature registers a new one with its new storage), <c>kern.log</c> and <c>syslog</c> get
    /// <c>storage.evicted</c>, and the feature and the user are told. Never throws.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The features whose storage was erased.</returns>
    public async ValueTask<IReadOnlyList<string>> CheckMarkersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<FeatureStorageRecord> evicted = [];
            foreach (FeatureStorageRecord record in await ReadStateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (record.MarkerPath is { } path
                    && await _storage.FileExistsAsync(path, cancellationToken).ConfigureAwait(false) == false)
                {
                    evicted.Add(record);
                }
            }

            if (evicted.Count == 0)
            {
                return [];
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (FeatureStorageRecord record in evicted)
            {
                _kernelLog?.Write(KernelLogLevel.Warning, $"storage.evicted: the browser erased the storage of {record.FeatureId} (marker {record.MarkerPath} is gone)");
                lock (_evictionGate)
                {
                    _pendingChanges[record.FeatureId] = new KazoStorageChangedEventArgs(KazoStorageChangeKind.Evicted, record.FeatureId, record.MarkerPath, now);
                    _toNotify.Add(record);
                }

                // Forget only the marker found missing: one the feature registered meanwhile stays.
                await UpdateAsync(
                        record.FeatureId,
                        current => string.Equals(current.MarkerPath, record.MarkerPath, StringComparison.Ordinal) ? current with { MarkerPath = null } : current,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            NotifyEvictions();
            return [.. evicted.Select(static record => record.FeatureId)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _kernelLog?.Write(KernelLogLevel.Warning, $"storage: the features' markers could not be checked: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// Posts, for the session user, the notification of each feature whose storage the browser erased
    /// (ADR 0250 §2). Called at boot and again once a user is signed in: a notification needs a user.
    /// </summary>
    public void NotifyEvictions()
    {
        if (_notifications is null || _currentUser() is not { } user)
        {
            return;
        }

        FeatureStorageRecord[] records;
        lock (_evictionGate)
        {
            records = [.. _toNotify];
            _toNotify.Clear();
        }

        Microsoft.Extensions.Localization.IStringLocalizer text = Strings.For(_language());
        DateTimeOffset now = _time.GetUtcNow();
        foreach (FeatureStorageRecord record in records)
        {
            _notifications.Enqueue(new Notification(
                NotificationId.FromGuid(Guid.NewGuid()),
                user.UserId,
                NotificationSourceAppId,
                NotificationSeverity.Warning,
                text["Storage.Evicted.Title"],
                text["Storage.Evicted.Message", record.Name],
                [NotificationAction.LaunchApp(SettingsAppId, text["Storage.Evicted.OpenSettings"])],
                now,
                expiresAtUtc: null));
        }
    }

    /// <summary>Takes the change found for <paramref name="featureId"/> at boot, once: the feature receives it at its next launch.</summary>
    /// <param name="featureId">The feature.</param>
    public KazoStorageChangedEventArgs? TakePendingChange(string featureId)
    {
        lock (_evictionGate)
        {
            return _pendingChanges.Remove(featureId, out KazoStorageChangedEventArgs? change) ? change : null;
        }
    }

    /// <summary>
    /// Normalizes a marker path: relative to the root of the Origin Private File System, with <c>/</c>,
    /// no empty, <c>.</c> or <c>..</c> segment.
    /// </summary>
    /// <param name="path">The path the feature gave.</param>
    /// <exception cref="ArgumentException">The path is empty, too long, absolute or leaves the root.</exception>
    public static string NormalizeMarkerPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string trimmed = path.Trim().Replace('\\', '/');
        string[] parts = trimmed.Split('/');
        if (trimmed.Length > MaximumMarkerPathLength
            || trimmed.StartsWith('/')
            || parts.Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException($"'{path}' is not a relative path inside the Origin Private File System.", nameof(path));
        }

        return string.Join('/', parts);
    }

    private async ValueTask UpdateAsync(string featureId, Func<FeatureStorageRecord, FeatureStorageRecord> change, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAppFileSystemGateway system = CreateSystemWriter();
            List<FeatureStorageRecord> records = [.. await ReadStateCoreAsync(system, cancellationToken).ConfigureAwait(false)];
            int index = records.FindIndex(record => string.Equals(record.FeatureId, featureId, StringComparison.Ordinal));
            FeatureStorageRecord current = index >= 0 ? records[index] : new FeatureStorageRecord(featureId, featureId, null, null, null);
            FeatureStorageRecord updated = change(current);
            if (index >= 0)
            {
                records[index] = updated;
            }
            else
            {
                records.Add(updated);
            }

            Ensure(await SettingsTextFile.EnsureDirectoryAsync(system, StateDirectory, cancellationToken).ConfigureAwait(false));
            Ensure(await SettingsTextFile.EditAsync(system, StatePath, _ => Format(records), createIfMissing: true, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<FeatureStorageRecord>> ReadStateAsync(CancellationToken cancellationToken) =>
        await ReadStateCoreAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);

    private static async ValueTask<IReadOnlyList<FeatureStorageRecord>> ReadStateCoreAsync(IAppFileSystemGateway system, CancellationToken cancellationToken)
    {
        SettingsFileReadResult file = await SettingsTextFile.ReadAsync(system, StatePath, cancellationToken).ConfigureAwait(false);
        return Parse(file.Content);
    }

    /// <summary>The system writer: the OS keeps this file for the features, which never write it themselves.</summary>
    private AppFileSystemGateway CreateSystemWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

    private static void Ensure(FileSystemError? error)
    {
        if (error is not null)
        {
            throw new IOException($"{error.Path?.Value ?? StatePath.Value}: {error.Code}.");
        }
    }

    /// <summary>Reads the state file; a broken file or entry is skipped, never fatal.</summary>
    /// <param name="content">The file's content, or <see langword="null"/>.</param>
    public static IReadOnlyList<FeatureStorageRecord> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("features", out JsonElement features) || features.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            List<FeatureStorageRecord> records = [];
            foreach (JsonProperty feature in features.EnumerateObject())
            {
                JsonElement value = feature.Value;
                if (value.ValueKind != JsonValueKind.Object || feature.Name.Length == 0)
                {
                    continue;
                }

                string name = value.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? feature.Name
                    : feature.Name;
                string? marker = value.TryGetProperty("marker", out JsonElement markerElement) && markerElement.ValueKind == JsonValueKind.String
                    ? markerElement.GetString()
                    : null;
                long? usage = value.TryGetProperty("usageBytes", out JsonElement usageElement) && usageElement.TryGetInt64(out long bytes) && bytes >= 0
                    ? bytes
                    : null;
                DateTimeOffset? reportedAt = value.TryGetProperty("reportedAtUtc", out JsonElement dateElement)
                    && dateElement.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(dateElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset date)
                        ? date
                        : null;
                records.Add(new FeatureStorageRecord(feature.Name, name, marker, usage, reportedAt));
            }

            return records;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the state file: <c>{"features":{"&lt;id&gt;":{"name":…,"marker":…,"usageBytes":…,"reportedAtUtc":…}}}</c>.</summary>
    /// <param name="records">The records.</param>
    public static string Format(IEnumerable<FeatureStorageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("features");
            foreach (FeatureStorageRecord record in records.OrderBy(static record => record.FeatureId, StringComparer.Ordinal))
            {
                writer.WriteStartObject(record.FeatureId);
                writer.WriteString("name", record.Name);
                if (record.MarkerPath is { } marker)
                {
                    writer.WriteString("marker", marker);
                }

                if (record.UsageBytes is { } usage)
                {
                    writer.WriteNumber("usageBytes", usage);
                }

                if (record.ReportedAtUtc is { } reportedAt)
                {
                    writer.WriteString("reportedAtUtc", reportedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
