using System.Globalization;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.Metrics;

namespace Kazo.Os.Platform.Core.Storage;

/// <summary>An eviction of Kazo OS's data, found at boot (ADR 0250 §2).</summary>
/// <param name="LastBootUtc">When the OS last started here, as the boot marker says; <see langword="null"/> when it cannot be read.</param>
/// <param name="DetectedAtUtc">When the eviction was found.</param>
public sealed record StorageEviction(DateTimeOffset? LastBootUtc, DateTimeOffset DetectedAtUtc);

/// <summary>
/// Finds out at boot whether the browser erased Kazo OS's data (ADR 0250 §2), as a journaling file
/// system notices it was not cleanly mounted: each successful boot leaves a marker in
/// <c>localStorage</c> (<c>kazoos.storage.marker</c>, next to <c>kazoos.osVersion</c>), and the OS's
/// database holds the files. A marker without the database means the browser evicted IndexedDB
/// (quota, Safari's 7 days without a visit, « clear site data » for that kind only).
/// </summary>
/// <remarks>
/// <see cref="DetectAsync"/> runs before anything opens the database: opening it would create it.
/// A browser that cannot list its databases (<see cref="IOriginStorage.DatabaseExistsAsync"/> rendering
/// <see langword="null"/>) reports no eviction. A factory reset erases <c>localStorage</c> too
/// (ADR 0227), so it is never mistaken for one.
/// </remarks>
public sealed class StorageEvictionDetector
{
    /// <summary>The OS's own IndexedDB database (<c>KazoOsIndexedDbSchema.DatabaseName</c>).</summary>
    public const string OsDatabaseName = "kazoos";

    private const string LastBootProperty = "lastBootUtc";
    private const string PersistedProperty = "persisted";

    private readonly IOriginStorage _storage;
    private readonly TimeProvider _time;
    private readonly string _databaseName;

    /// <summary>Creates the detector.</summary>
    /// <param name="storage">The origin's storage.</param>
    /// <param name="time">The real clock: the marker records wall-clock time; the system clock by default.</param>
    /// <param name="databaseName">The database whose absence means an eviction; <see cref="OsDatabaseName"/> by default.</param>
    public StorageEvictionDetector(IOriginStorage storage, TimeProvider? time = null, string databaseName = OsDatabaseName)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? TimeProvider.System;
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        _databaseName = databaseName;
    }

    /// <summary>Gets the eviction <see cref="DetectAsync"/> found at this boot, or <see langword="null"/>.</summary>
    public StorageEviction? Eviction { get; private set; }

    /// <summary>
    /// Gets whether the browser withdrew the protection it had granted: the last marker says the origin
    /// was persisted and <see cref="RecordBootAsync"/> found it no longer is (ADR 0250 §2, a warning only).
    /// </summary>
    public bool PersistenceLost { get; private set; }

    /// <summary>
    /// Compares the boot marker with the OS's database, before the database is opened. Never throws:
    /// a check that fails reports no eviction.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The eviction, or <see langword="null"/>.</returns>
    public async ValueTask<StorageEviction?> DetectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? marker = await _storage.ReadBootMarkerAsync(cancellationToken).ConfigureAwait(false);
            if (marker is null)
            {
                // Never started here, or started before ADR 0250: nothing to compare with.
                return null;
            }

            bool? exists = await _storage.DatabaseExistsAsync(_databaseName, cancellationToken).ConfigureAwait(false);
            if (exists != false)
            {
                return null;
            }

            Eviction = new StorageEviction(Read(marker).LastBootUtc, _time.GetUtcNow());
            return Eviction;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the marker of a successful boot, with whether the origin is persisted, and notes whether
    /// that protection was lost since the last boot. Never throws: the next boot then simply has no
    /// fresh marker.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async ValueTask RecordBootAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            (DateTimeOffset? _, bool wasPersisted) = Read(await _storage.ReadBootMarkerAsync(cancellationToken).ConfigureAwait(false));
            OriginStorageStatus? status = await _storage.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            bool persisted = status?.IsPersisted ?? wasPersisted;
            PersistenceLost = wasPersisted && status is { IsPersisted: false };
            await _storage.WriteBootMarkerAsync(Format(_time.GetUtcNow(), persisted), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The marker is a convenience: a page without localStorage boots all the same.
        }
    }

    /// <summary>Writes a marker: <c>{"lastBootUtc":"…","persisted":…}</c>.</summary>
    /// <param name="lastBootUtc">When the OS started.</param>
    /// <param name="persisted">Whether the origin was persisted.</param>
    public static string Format(DateTimeOffset lastBootUtc, bool persisted) =>
        "{\"" + LastBootProperty + "\":\"" + lastBootUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        + "\",\"" + PersistedProperty + "\":" + (persisted ? "true" : "false") + "}";

    /// <summary>Reads a marker; an unreadable one gives no date and no persistence.</summary>
    /// <param name="marker">The marker, or <see langword="null"/>.</param>
    public static (DateTimeOffset? LastBootUtc, bool Persisted) Read(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return (null, false);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(marker);
            JsonElement root = document.RootElement;
            DateTimeOffset? lastBoot = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(LastBootProperty, out JsonElement date)
                && date.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                    ? parsed
                    : null;
            bool persisted = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(PersistedProperty, out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;
            return (lastBoot, persisted);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }
}
