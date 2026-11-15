using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// The <see cref="IAppDriveGateway"/> one app instance receives (ADR 0270): the shared
/// <see cref="AppDriveGateway"/>, plus the check of <c>cloud-accounts.manage</c> before a cloud
/// drive is mounted. A folder of the computer needs no capability, as before (ADR 0181).
/// </summary>
/// <param name="inner">The shared gateway.</param>
/// <param name="capabilities">The app's capability checker.</param>
internal sealed class CapabilityCheckedDriveGateway(IAppDriveGateway inner, ICapabilityChecker capabilities) : IAppDriveGateway
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
        inner.GetDrivesAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) =>
        inner.IsHostFolderSupportedAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<KazoDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default) =>
        inner.MountHostFolderAsync(mountPoint, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        inner.ReconnectAsync(mountPoint, cancellationToken);

    /// <inheritdoc />
    public ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        inner.UnmountAsync(mountPoint, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetNetworkDriveTypesAsync(CancellationToken cancellationToken = default) =>
        inner.GetNetworkDriveTypesAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<KazoDriveSnapshot?> MountNetworkDriveAsync(
        KazoNetworkDriveRequest request,
        string? mountPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Type, HostFolderFileSystemProvider.FileSystemType, StringComparison.Ordinal))
        {
            capabilities.Require(AppCapabilities.CloudAccountsManage);
        }

        return inner.MountNetworkDriveAsync(request, mountPoint, cancellationToken);
    }
}
