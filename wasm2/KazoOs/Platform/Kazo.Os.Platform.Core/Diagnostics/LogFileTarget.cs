using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Identifies one log file under <c>/var/log</c> that <see cref="LogFileWriter"/> appends to, with
/// the Unix mode it is created with (ADR 0045, section 1). Every log is owned by <c>system:system</c>,
/// except a user's event log (<see cref="ForUser"/>, ADR 0254), owned by that user.
/// </summary>
public sealed record LogFileTarget
{
    /// <summary>Canonical <c>/var/log</c> directory.</summary>
    public static readonly VirtualPath LogDirectory = VirtualPath.Parse("/var/log");

    /// <summary>Canonical directory holding one log per app.</summary>
    public static readonly VirtualPath AppLogDirectory = VirtualPath.Parse("/var/log/apps");

    /// <summary>Platform log: every <c>ILogger</c> category, boot, and OS service errors. Mode <c>0644</c>.</summary>
    public static readonly LogFileTarget Syslog = new(VirtualPath.Parse("/var/log/syslog"), 0b110_100_100);

    /// <summary>Audit log: logins, <c>sudo</c>, permission approvals. Mode <c>0600</c>, like Linux.</summary>
    public static readonly LogFileTarget AuthLog = new(VirtualPath.Parse("/var/log/auth.log"), 0b110_000_000);

    /// <summary>
    /// Filesystem audit log: every protected write under <c>/etc</c>, <c>/bin</c> and <c>/var</c>, in the
    /// format of <c>auditd</c> (ADR 0100, section 3). Mode <c>0600</c>, read with <c>sudo ausearch</c>.
    /// </summary>
    public static readonly LogFileTarget AuditLog = new(VirtualPath.Parse("/var/log/audit.log"), 0b110_000_000);

    /// <summary>
    /// Kernel log: every line of the kernel's ring buffer (<c>kern.*</c>, ADR 0126), such as the OOM
    /// killer's. Mode <c>0644</c>; <c>dmesg</c> reads the ring buffer itself through <c>/dev/kmsg</c>.
    /// </summary>
    public static readonly LogFileTarget KernLog = new(VirtualPath.Parse("/var/log/kern.log"), 0b110_100_100);

    /// <summary>
    /// Canonical directory holding one directory per user, <c>/var/log/users</c> (ADR 0254, section 1).
    /// </summary>
    public static readonly VirtualPath UserLogDirectory = VirtualPath.Parse("/var/log/users");

    /// <summary>File name of a user's event log inside <c>/var/log/users/&lt;id&gt;/</c>.</summary>
    public const string UserEventsFileName = "events.log";

    /// <summary>Mode of the directories of <see cref="UserLogDirectory"/>: <c>0711</c>, traversed but never listed by others.</summary>
    public const ushort PrivateDirectoryMode = 0b111_001_001;

    /// <summary>Mode of the directories every other log lives in: <c>0755</c>.</summary>
    public const ushort PublicDirectoryMode = 0b111_101_101;

    private LogFileTarget(VirtualPath path, ushort mode, LogFileOwner? owner = null, ushort directoryMode = PublicDirectoryMode)
    {
        Path = path;
        Mode = mode;
        Owner = owner;
        DirectoryMode = directoryMode;
    }

    /// <summary>Gets the canonical path of the active log file.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the Unix permission bits the file is created with.</summary>
    public ushort Mode { get; }

    /// <summary>
    /// Gets the user who owns the file, or <see langword="null"/> for <c>system:system</c>. Only the
    /// creation of the file uses it: the writer still appends with the system context.
    /// </summary>
    public LogFileOwner? Owner { get; }

    /// <summary>
    /// Gets the mode of the directories the writer creates below <c>/var/log</c> for this log
    /// (<c>/var/log</c> itself and its ancestors are always <c>0755</c>).
    /// </summary>
    public ushort DirectoryMode { get; }

    /// <summary>Gets the directory containing the log.</summary>
    public VirtualPath Directory => VirtualPath.Parse(Path.Value[..Path.Value.LastIndexOf('/')]);

    /// <summary>
    /// Returns the per-app log <c>/var/log/apps/&lt;appId&gt;.log</c>, mode <c>0644</c>.
    /// </summary>
    /// <param name="appId">App identifier; must be a valid single file-name segment.</param>
    /// <exception cref="ArgumentException">The app ID cannot be used as a file name.</exception>
    public static LogFileTarget ForApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        FileSystemEntryName name = FileSystemEntryName.Parse($"{appId}.log");
        return new LogFileTarget(VirtualPath.Parse($"{AppLogDirectory.Value}/{name.Value}"), 0b110_100_100);
    }

    /// <summary>
    /// Returns a daemon log of a system app, <c>/var/log/&lt;directory&gt;/&lt;file&gt;.log</c>, mode
    /// <c>0644</c> (ADR 0080, section 3), for example <c>/var/log/kazoweb/docs.access.log</c>.
    /// </summary>
    /// <param name="relativePath"><c>&lt;directory&gt;/&lt;file&gt;.log</c>: two plain segments, the second ending in <c>.log</c>.</param>
    /// <exception cref="ArgumentException">
    /// The path has not exactly two segments, a segment is not a valid file name, the file does not
    /// end in <c>.log</c>, or the directory is <c>apps</c> (reserved for <see cref="ForApp"/>).
    /// </exception>
    public static LogFileTarget ForDaemon(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string[] segments = relativePath.Split('/');
        if (segments.Length != 2
            || !TryParseName(segments[0], out FileSystemEntryName directory)
            || !TryParseName(segments[1], out FileSystemEntryName file)
            || !file.Value.EndsWith(".log", StringComparison.Ordinal)
            || file.Value.Length == ".log".Length)
        {
            throw new ArgumentException($"'{relativePath}' is not a daemon log path ('<directory>/<file>.log').", nameof(relativePath));
        }

        if (string.Equals(directory.Value, "apps", StringComparison.Ordinal))
        {
            throw new ArgumentException("/var/log/apps holds the per-app logs; a daemon log uses a directory of its own.", nameof(relativePath));
        }

        return new LogFileTarget(VirtualPath.Parse($"{LogDirectory.Value}/{directory.Value}/{file.Value}"), 0b110_100_100);
    }

    /// <summary>
    /// Returns the event log of one user, <c>/var/log/users/&lt;id&gt;/events.log</c> (ADR 0254,
    /// section 1): owned by the user, mode <c>0600</c>, in a <c>system:system 0711</c> directory so
    /// other users can neither read it nor list who has one. The directory is named after the stable
    /// account identifier (ADR 0169), never the login name, which can change.
    /// </summary>
    /// <param name="userId">Stable identifier of the account.</param>
    /// <param name="primaryGroupId">Primary group of the account, the group of the file.</param>
    public static LogFileTarget ForUser(LocalUserId userId, LocalGroupId primaryGroupId) =>
        new(
            UserEventsPath(userId),
            0b110_000_000,
            new LogFileOwner(userId.ToString(), primaryGroupId.ToString()),
            PrivateDirectoryMode);

    /// <summary>Returns the path of one user's event log, <c>/var/log/users/&lt;id&gt;/events.log</c>.</summary>
    /// <param name="userId">Stable identifier of the account.</param>
    public static VirtualPath UserEventsPath(LocalUserId userId) =>
        VirtualPath.Parse($"{UserDirectory(userId).Value}/{UserEventsFileName}");

    /// <summary>Returns the directory of one user's logs, <c>/var/log/users/&lt;id&gt;</c>, deleted with the account.</summary>
    /// <param name="userId">Stable identifier of the account.</param>
    public static VirtualPath UserDirectory(LocalUserId userId) =>
        VirtualPath.Parse($"{UserLogDirectory.Value}/{userId}");

    private static bool TryParseName(string value, out FileSystemEntryName name)
    {
        try
        {
            name = FileSystemEntryName.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            name = default;
            return false;
        }
    }

    /// <summary>Returns the path of archive <paramref name="index"/> (<c>&lt;file&gt;.1</c> is the newest).</summary>
    /// <param name="index">Archive number, from 1 to <see cref="LogRotationPolicy.MaxArchives"/>.</param>
    public VirtualPath ArchivePath(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, LogRotationPolicy.MaxArchives);
        return VirtualPath.Parse($"{Path.Value}.{index}");
    }
}

/// <summary>
/// Owner of a log file that does not belong to <c>system:system</c>: the stable account identifier
/// (the owner of the files an app creates for that user) and its primary group.
/// </summary>
/// <param name="UserId">Owner, as the stable account identifier.</param>
/// <param name="GroupId">Group of the file, the user's primary group.</param>
public sealed record LogFileOwner(string UserId, string GroupId);
