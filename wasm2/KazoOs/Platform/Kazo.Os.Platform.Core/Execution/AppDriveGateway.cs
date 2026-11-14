using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppDriveGateway"/> behind <see cref="KazoDriveInfo"/> (ADR 0057, section 7):
/// the OS's <c>statfs</c> for every mount that has a size.
/// </summary>
/// <remarks>
/// <para>
/// A mount has a size when its provider stores data, i.e. it is not one of the computed
/// <c>/proc</c> and <c>/dev</c> mounts (<see cref="IVirtualFileSystemProvider"/>). Today that is
/// <c>/</c>, the IndexedDB disk <c>hda</c>, and the network drives of ADR 0181.
/// </para>
/// <para>
/// <b>Network drives.</b> Listed from <see cref="NetworkDriveService.GetDrives"/>: the caller's own
/// drives only, with the type of their source (ADR 0267 §3). The browser gives neither the size nor
/// the free space of a host folder: both are 0; a cloud drive reports its service's quota when it
/// has one and is connected (ADR 0267 §4). Mounting, reconnecting and unmounting go to <see cref="NetworkDriveService"/>, which
/// opens the browser's folder dialog; without it (tests, hosts with no browser) they are not supported.
/// </para>
/// <para>
/// <b>Size.</b> <see cref="VirtualHardwareProfile.StorageCapacityBytes"/>, lowered to the browser
/// quota when the browser grants less. <b>Free space.</b> Size minus the bytes the deduplicated
/// persistence stores, never negative. No capability is checked, as <c>df</c> needs none.
/// </para>
/// </remarks>
public sealed class AppDriveGateway : IAppDriveGateway
{
    private readonly Func<IFileSystemMountRouter> _mounts;
    private readonly VirtualHardwareProfile _hardware;
    private readonly IStorageUsageProvider? _usage;
    private readonly string _deviceName;

    /// <summary>Initializes the gateway.</summary>
    /// <param name="mounts">Lazily resolves the mount table (it is built after the execution factory).</param>
    /// <param name="hardware">Virtual hardware supplying the disk size.</param>
    /// <param name="usage">Measures the persistent filesystem; <see langword="null"/> reports an empty disk.</param>
    /// <param name="deviceName">Device name reported as the volume label; <c>hda</c> by default.</param>
    public AppDriveGateway(
        Func<IFileSystemMountRouter> mounts,
        VirtualHardwareProfile hardware,
        IStorageUsageProvider? usage = null,
        string deviceName = "hda")
    {
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _usage = usage;
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        _deviceName = deviceName;
    }

    /// <summary>Lazily resolves the network drive service (ADR 0181); <see langword="null"/> when the host has none.</summary>
    public Func<NetworkDriveService?>? NetworkDrives { get; init; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FileSystemMount> mounts = _mounts().Mounts;
        FileSystemMount[] sized =
        [
            .. mounts.Where(static mount => mount.Provider is not (IVirtualFileSystemProvider or HostFolderFileSystemProvider))
        ];
        // ADR 0181: only the caller's own network drives (every drive for an administrator).
        List<KazoDriveSnapshot> network = [];
        if (NetworkDrives?.Invoke() is { } drives)
        {
            foreach (NetworkDrive drive in drives.GetDrives())
            {
                HostFolderQuota? driveQuota = await drives.GetQuotaAsync(drive.MountPoint, cancellationToken).ConfigureAwait(false);
                network.Add(Describe(drive, driveQuota));
            }
        }

        if (sized.Length == 0)
        {
            return network;
        }


        StorageUsage usage = _usage is null
            ? new StorageUsage(0, null)
            : await _usage.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        long total = usage.QuotaBytes is > 0 and { } quota
            ? Math.Min(_hardware.StorageCapacityBytes, quota)
            : _hardware.StorageCapacityBytes;
        long free = Math.Max(0, total - Math.Max(0, usage.UsedBytes));

        return
        [
            .. sized.Select(mount => new KazoDriveSnapshot(
                mount.Path.Value,
                MountDescription.From(mount).Type,
                _deviceName,
                total,
                free)),
            .. network
        ];
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) =>
        NetworkDrives?.Invoke() is { } drives && await drives.IsSupportedAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<KazoDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default)
    {
        NetworkDriveService drives = RequireNetworkDrives();
        try
        {
            NetworkDrive? drive = await drives.MountAsync(mountPoint, cancellationToken).ConfigureAwait(false);
            return drive is null ? null : Describe(drive, null);
        }
        catch (NetworkDriveException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RequireNetworkDrives().ReconnectAsync(mountPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (NetworkDriveException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default)
    {
        try
        {
            await RequireNetworkDrives().UnmountAsync(mountPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (NetworkDriveException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every source of the host that answers it can create a drive now (ADR 0270): <c>hostfs</c> when
    /// the browser has the folder dialog, a cloud type when its provider is configured.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> GetNetworkDriveTypesAsync(CancellationToken cancellationToken = default)
    {
        if (NetworkDrives?.Invoke() is not { } drives)
        {
            return [];
        }

        List<string> types = [];
        foreach (string type in drives.SourceTypes)
        {
            if (await drives.IsAvailableAsync(type, cancellationToken).ConfigureAwait(false))
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <inheritdoc />
    /// <remarks>
    /// No capability is checked here: this gateway is shared by every context, and the per-app
    /// <see cref="CapabilityCheckedDriveGateway"/> checks <c>cloud-accounts.manage</c> for a cloud type
    /// before it calls this method (ADR 0270).
    /// </remarks>
    public async ValueTask<KazoDriveSnapshot?> MountNetworkDriveAsync(
        KazoNetworkDriveRequest request,
        string? mountPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NetworkDriveService drives = RequireNetworkDrives();
        NetworkDriveRequest sourceRequest = new(request.Account, request.Root)
        {
            Options = request.Options,
            Secrets = request.Secrets
        };
        try
        {
            NetworkDrive? drive = await drives.MountAsync(request.Type, mountPoint, sourceRequest, cancellationToken).ConfigureAwait(false);
            return drive is null ? null : Describe(drive, await drives.GetQuotaAsync(drive.MountPoint, cancellationToken).ConfigureAwait(false));
        }
        catch (NetworkDriveException exception)
        {
            throw Translate(exception);
        }
    }

    private static KazoDriveSnapshot Describe(NetworkDrive drive, HostFolderQuota? quota) =>
        new(drive.MountPoint.Value, drive.Type, drive.Name, quota?.TotalBytes ?? 0, quota?.FreeBytes ?? 0)
        {
            DriveType = KazoDriveType.Network,
            IsReady = drive.IsConnected,
            Account = drive.Account
        };

    private NetworkDriveService RequireNetworkDrives() =>
        NetworkDrives?.Invoke() ?? throw new NotSupportedException("hostfs: not supported by this host");

    /// <summary>The SDK's exceptions for a refused network drive operation (ADR 0181, section 7).</summary>
    private static Exception Translate(NetworkDriveException exception) => exception.Error switch
    {
        NetworkDriveError.NotSupported => new NotSupportedException(exception.Message),
        NetworkDriveError.NotOwner or NetworkDriveError.PermissionDenied => new UnauthorizedAccessException(exception.Message),
        NetworkDriveError.InvalidMountPoint or NetworkDriveError.NotMounted => new IOException(exception.Message),
        // ADR 0270: the source refused what the user typed (wrong keys, a missing bucket, an unknown account).
        NetworkDriveError.InvalidRequest => new ArgumentException(exception.Message),
        _ => new InvalidOperationException(exception.Message)
    };
}
