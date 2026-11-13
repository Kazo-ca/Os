namespace Kazo.Os.AppSdk.IO;

/// <summary>Kind of a drive, the subset of <c>System.IO.DriveType</c> Kazo OS produces.</summary>
public enum KazoDriveType
{
    /// <summary>A fixed disk: the IndexedDB disk behind <c>/</c>.</summary>
    Fixed,

    /// <summary>A network drive: a folder of the host computer (ADR 0181) or a cloud storage (ADR 0267) mounted in <c>/mnt</c>.</summary>
    Network
}

/// <summary>
/// What the OS knows about one sized mount, as an app instance receives it from
/// <see cref="IAppDriveGateway"/>: the raw <c>statfs</c> answer behind <see cref="KazoDriveInfo"/>.
/// </summary>
/// <param name="Name">Mount point, e.g. <c>/</c>.</param>
/// <param name="DriveFormat">Filesystem type from <c>/proc/mounts</c>, e.g. <c>idbfs</c>.</param>
/// <param name="VolumeLabel">Device name, e.g. <c>hda</c>.</param>
/// <param name="TotalSize">Size of the disk, in bytes.</param>
/// <param name="AvailableFreeSpace">Free bytes, never negative.</param>
public sealed record KazoDriveSnapshot(
    string Name,
    string DriveFormat,
    string VolumeLabel,
    long TotalSize,
    long AvailableFreeSpace)
{
    /// <summary>Gets the kind of drive; <see cref="KazoDriveType.Fixed"/> unless set.</summary>
    public KazoDriveType DriveType { get; init; } = KazoDriveType.Fixed;

    /// <summary>Gets whether the drive can be read; a network drive is not until the browser grants its permission.</summary>
    public bool IsReady { get; init; } = true;

    /// <summary>
    /// Gets the id of the cloud account a network drive belongs to (ADR 0270), as
    /// <see cref="CloudAccounts.KazoCloudAccount.Id"/> gives it; <see langword="null"/> for a folder
    /// of the computer and for the other drives.
    /// </summary>
    public string? Account { get; init; }
}

/// <summary>
/// What to mount as a network drive (ADR 0270), for <see cref="KazoDriveInfo.MountNetworkDriveAsync"/>.
/// </summary>
/// <param name="Type">
/// The type of drive, as <c>mount -t</c> takes it: <c>hostfs</c> (a folder of the computer),
/// <c>dropbox</c>, <c>onedrive</c> or <c>s3</c>. <see cref="KazoDriveInfo.GetNetworkDriveTypesAsync"/>
/// lists the ones this OS can mount.
/// </param>
/// <param name="Account">
/// The cloud account to use (<see cref="CloudAccounts.KazoCloudAccount.Id"/>); needed by
/// <c>dropbox</c> and <c>onedrive</c>, optional for <c>s3</c> (its keys), ignored by <c>hostfs</c>.
/// </param>
/// <param name="Root">The folder of the service the drive starts at; <see langword="null"/> for the whole storage.</param>
public sealed record KazoNetworkDriveRequest(string Type, string? Account = null, string? Root = null)
{
    /// <summary>
    /// Gets the settings the user typed, none of them secret: for <c>s3</c>, <c>endpoint</c>,
    /// <c>region</c>, <c>bucket</c>, <c>prefix</c> and <c>addressing</c> (<c>path</c> or
    /// <c>virtual</c>); <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>
    /// Gets the secrets the user typed, which the OS seals on the device and never shows again: for
    /// <c>s3</c>, <c>access-key-id</c> and <c>secret-access-key</c>. <see cref="ToString"/> never
    /// prints their values. <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }

    /// <summary>Prints the request without the values of <see cref="Secrets"/>, which must never reach a log.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Type = ").Append(Type)
            .Append(", Account = ").Append(Account)
            .Append(", Root = ").Append(Root)
            .Append(", Options = ").Append(Options is null ? "" : string.Join(";", Options.Select(static pair => pair.Key + "=" + pair.Value)))
            .Append(", Secrets = ").Append(Secrets is null ? "" : string.Join(";", Secrets.Keys.Select(static key => key + "=***")));
        return true;
    }
}

/// <summary>
/// Space of the mounted disks as one app instance sees it (ADR 0057, section 7), behind
/// <see cref="KazoDriveInfo"/>. Reached through <see cref="IAppExecutionContext.Drives"/>. Needs no
/// capability, like <c>df</c> under Linux.
/// </summary>
public interface IAppDriveGateway
{
    /// <summary>Gets every mount that has a size: <c>/</c> and the network drives.</summary>
    ValueTask<IReadOnlyList<KazoDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets whether the browser can mount a folder of the host computer (ADR 0181).</summary>
    ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>
    /// Opens the browser's folder dialog and mounts the chosen folder as a network drive;
    /// <see langword="null"/> when the user closed the dialog (ADR 0181, section 7).
    /// </summary>
    ValueTask<KazoDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");

    /// <summary>Asks the browser for its permission on a network drive again; whether it is ready now.</summary>
    ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");

    /// <summary>Unmounts a network drive.</summary>
    ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount host folders.");

    /// <summary>
    /// Gets the types of network drive a user can mount here (ADR 0270): <c>hostfs</c> when the
    /// browser has the folder dialog, <c>dropbox</c> and <c>onedrive</c> when the OS has their app
    /// ids, <c>s3</c> when the OS has the connector. None by default.
    /// </summary>
    ValueTask<IReadOnlyList<string>> GetNetworkDriveTypesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Mounts a network drive of any type (ADR 0270); <see langword="null"/> when the user closed
    /// the source's dialog. A cloud type needs the capability <c>cloud-accounts.manage</c>.
    /// </summary>
    ValueTask<KazoDriveSnapshot?> MountNetworkDriveAsync(
        KazoNetworkDriveRequest request,
        string? mountPoint,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This drive gateway cannot mount network drives.");
}

/// <summary>
/// Space and format of a mounted disk, modeled on <c>System.IO.DriveInfo</c> (ADR 0057, section 7).
/// Under Linux the space of a disk is not a file but a <c>statfs</c> call; this is its .NET-shaped
/// equivalent. No capability is needed.
/// </summary>
/// <example>
/// <code>
/// foreach (KazoDriveInfo drive in await KazoDriveInfo.GetDrivesAsync())
/// {
///     Console.WriteLine($"{drive.Name} {drive.DriveFormat} {drive.AvailableFreeSpace}/{drive.TotalSize}");
/// }
/// </code>
/// </example>
/// <remarks>
/// Unlike <c>DriveInfo</c>, the values are read once, when the instance is created: the browser
/// only answers asynchronously, so a property cannot query it. Call <see cref="GetDrivesAsync"/>
/// again for fresh numbers.
/// </remarks>
public sealed class KazoDriveInfo
{
    private KazoDriveInfo(KazoDriveSnapshot snapshot)
    {
        Name = snapshot.Name;
        DriveFormat = snapshot.DriveFormat;
        VolumeLabel = snapshot.VolumeLabel;
        TotalSize = snapshot.TotalSize;
        AvailableFreeSpace = Math.Max(0, snapshot.AvailableFreeSpace);
        DriveType = snapshot.DriveType;
        IsReady = snapshot.IsReady;
        Account = snapshot.Account;
    }

    /// <summary>
    /// Gets the id of the cloud account the drive belongs to (ADR 0270); <see langword="null"/> for
    /// a folder of the computer and for the other drives.
    /// </summary>
    public string? Account { get; }

    /// <summary>Gets the mount point, e.g. <c>/</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the filesystem type, e.g. <c>idbfs</c>.</summary>
    public string DriveFormat { get; }

    /// <summary>Gets the kind of drive: <see cref="KazoDriveType.Fixed"/> for <c>/</c>, <see cref="KazoDriveType.Network"/> for a host folder.</summary>
    public KazoDriveType DriveType { get; }

    /// <summary>Gets whether the drive is ready; a network drive is not until the browser grants its permission again.</summary>
    public bool IsReady { get; }

    /// <summary>Gets the device name, e.g. <c>hda</c>.</summary>
    public string VolumeLabel { get; }

    /// <summary>Gets the size of the drive, in bytes.</summary>
    public long TotalSize { get; }

    /// <summary>Gets the free space available to the app, in bytes; never negative.</summary>
    public long AvailableFreeSpace { get; }

    /// <summary>Gets the total free space, in bytes; Kazo OS reserves no blocks, so it equals <see cref="AvailableFreeSpace"/>.</summary>
    public long TotalFreeSpace => AvailableFreeSpace;

    /// <summary>Gets the root directory of the drive.</summary>
    public KazoDirectoryInfo RootDirectory => new(Name);

    /// <summary>Gets every mounted drive that has a size: <c>/</c> and the network drives.</summary>
    /// <exception cref="KazoExecutionContextMissingException">Called outside an app's execution context.</exception>
    public static async Task<KazoDriveInfo[]> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KazoDriveSnapshot> drives = await KazoExecutionContext.Current.Drives
            .GetDrivesAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. drives.Select(static drive => new KazoDriveInfo(drive))];
    }

    /// <summary>Gets whether this browser can mount a folder of the host computer (Chrome and Edge on a computer).</summary>
    /// <exception cref="KazoExecutionContextMissingException">Called outside an app's execution context.</exception>
    public static async Task<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) =>
        await KazoExecutionContext.Current.Drives.IsHostFolderSupportedAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the browser's folder dialog and mounts the folder the user chooses as a network drive
    /// (ADR 0181): at <c>/mnt/&lt;folder name&gt;</c>, or at <paramref name="mountPoint"/>
    /// (<c>/mnt/&lt;name&gt;</c>, an empty folder of the user). Call it from a click or a key press:
    /// the browser opens its dialog only after a user gesture.
    /// </summary>
    /// <returns>The new drive, or <see langword="null"/> when the user closed the dialog.</returns>
    /// <exception cref="NotSupportedException">The browser cannot (Firefox, Safari, mobile).</exception>
    /// <exception cref="InvalidOperationException">No recent user gesture, or nobody is signed in.</exception>
    /// <exception cref="IOException">The mount point is not usable.</exception>
    public static async Task<KazoDriveInfo?> MountHostFolderAsync(string? mountPoint = null, CancellationToken cancellationToken = default)
    {
        KazoDriveSnapshot? drive = await KazoExecutionContext.Current.Drives
            .MountHostFolderAsync(mountPoint, cancellationToken)
            .ConfigureAwait(false);
        return drive is null ? null : new KazoDriveInfo(drive);
    }

    /// <summary>
    /// Asks the browser again for its permission on a network drive that is not ready (after a
    /// reload). Call it from a click or a key press.
    /// </summary>
    /// <returns>Whether the drive is ready now.</returns>
    /// <exception cref="IOException">No network drive is mounted there.</exception>
    /// <exception cref="UnauthorizedAccessException">The drive belongs to another user.</exception>
    public static async Task<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        await KazoExecutionContext.Current.Drives.ReconnectAsync(mountPoint, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Gets the types of network drive the user can mount here (ADR 0270): <c>hostfs</c> (a folder
    /// of the computer, when the browser can), and the cloud services this OS is set up for
    /// (<c>dropbox</c>, <c>onedrive</c>, <c>s3</c>).
    /// </summary>
    /// <exception cref="KazoExecutionContextMissingException">Called outside an app's execution context.</exception>
    public static async Task<IReadOnlyList<string>> GetNetworkDriveTypesAsync(CancellationToken cancellationToken = default) =>
        await KazoExecutionContext.Current.Drives.GetNetworkDriveTypesAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Mounts a network drive of any type (ADR 0270): a folder of the computer, or a cloud storage
    /// with an account connected through <see cref="CloudAccounts.KazoCloudAccounts"/>. Call it from
    /// a click or a key press: a folder of the computer opens the browser's dialog.
    /// </summary>
    /// <param name="request">The type, the account, the root and the source's own settings.</param>
    /// <param name="mountPoint"><c>/mnt/&lt;name&gt;</c>, or <see langword="null"/> for the source's default name.</param>
    /// <param name="cancellationToken">Cancels the mount.</param>
    /// <returns>The new drive, or <see langword="null"/> when the user closed the dialog.</returns>
    /// <exception cref="NotSupportedException">The type is unknown or not set up here.</exception>
    /// <exception cref="UnauthorizedAccessException">A cloud type without the capability <c>cloud-accounts.manage</c>.</exception>
    /// <exception cref="ArgumentException">The service refused the request (an unknown account, wrong keys, a missing bucket).</exception>
    /// <exception cref="InvalidOperationException">No recent user gesture, or nobody is signed in.</exception>
    /// <exception cref="IOException">The mount point is not usable.</exception>
    public static async Task<KazoDriveInfo?> MountNetworkDriveAsync(
        KazoNetworkDriveRequest request,
        string? mountPoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        KazoDriveSnapshot? drive = await KazoExecutionContext.Current.Drives
            .MountNetworkDriveAsync(request, mountPoint, cancellationToken)
            .ConfigureAwait(false);
        return drive is null ? null : new KazoDriveInfo(drive);
    }

    /// <summary>Unmounts a network drive; the folder of the host computer is not touched.</summary>
    /// <exception cref="IOException">No network drive is mounted there.</exception>
    /// <exception cref="UnauthorizedAccessException">The drive belongs to another user and the caller is not an administrator.</exception>
    public static async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        await KazoExecutionContext.Current.Drives.UnmountAsync(mountPoint, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override string ToString() => Name;
}
