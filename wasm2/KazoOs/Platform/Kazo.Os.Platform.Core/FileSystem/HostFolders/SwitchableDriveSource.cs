using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.HostFolders;

/// <summary>
/// A drive source that an administrator can switch off without publishing the OS again (ADR 0282
/// section 5): <c>KazoOs:CloudDrives:Disabled</c> in <c>appsettings.json</c> or the <c>disabled</c>
/// key of <c>/etc/kazoos/cloud-drives.config</c>. While the switch is off the type is not offered:
/// <see cref="IsAvailableAsync"/> answers <see langword="false"/>, so the Explorer, Settings and
/// <c>mount -t</c> leave it out and <see cref="NetworkDriveService"/> refuses a new drive of it.
/// </summary>
/// <remarks>
/// The switch is asked on every call because the system file can change while the OS runs. The
/// wrapped source keeps its own rules (an OAuth provider still needs an app id). Disposing this
/// source disposes the wrapped one, which the service container no longer sees.
/// </remarks>
public sealed class SwitchableDriveSource : INetworkDriveSource, IDisposable
{
    private readonly INetworkDriveSource _inner;
    private readonly Func<CancellationToken, ValueTask<bool>> _isEnabled;

    /// <summary>Initializes the source.</summary>
    /// <param name="inner">The connector.</param>
    /// <param name="isEnabled">Answers whether the type is switched on now.</param>
    public SwitchableDriveSource(INetworkDriveSource inner, Func<CancellationToken, ValueTask<bool>> isEnabled)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    /// <summary>Gets the wrapped connector.</summary>
    public INetworkDriveSource Inner => _inner;

    /// <inheritdoc />
    public string Type => _inner.Type;

    /// <inheritdoc />
    public event Action? AvailabilityChanged
    {
        add => _inner.AvailabilityChanged += value;
        remove => _inner.AvailabilityChanged -= value;
    }

    /// <summary>
    /// Gets whether the type is switched on now. While it is off, <see cref="NetworkDriveService"/>
    /// keeps the user's drives of this type in their list without mounting them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read of the system file.</param>
    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => _isEnabled(cancellationToken);

    /// <summary>Available when the switch is on and the connector itself is available.</summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await _isEnabled(cancellationToken).ConfigureAwait(false)
        && await _inner.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The type is switched off on this machine.</exception>
    public async ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _isEnabled(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{Type}: this drive type is disabled on this machine.");
        }

        return await _inner.CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default) =>
        _inner.OpenAsync(reference, cancellationToken);

    /// <inheritdoc />
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default) =>
        _inner.ForgetAsync(reference, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
