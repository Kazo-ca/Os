using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// The servers users typed in the "server address" field of a drive provider's form (ADR 0284 §2),
/// allowed to the feature that provides the drive while the drive is open. A drive takes a lease
/// with <see cref="Allow"/> when it opens and disposes it when it closes; two drives on the same
/// server keep it allowed until the second one closes. The network gateway of the feature reads
/// <see cref="IsAllowed"/> before a direct request. One instance per page, shared by the drive
/// sources and the gateways.
/// </summary>
public sealed class DriveServerHosts
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string AppId, string Host), int> _leases = [];

    /// <summary>
    /// Allows <paramref name="host"/> to <paramref name="appId"/> until the returned lease is disposed.
    /// </summary>
    /// <param name="appId">The feature that provides the drive.</param>
    /// <param name="host">The host of the typed address (<see cref="AppDriveProviders.TryGetServerHost"/>).</param>
    /// <returns>The lease; disposing it twice has no further effect.</returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is not a host the OS may allow.</exception>
    public IDisposable Allow(string appId, string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!TryCanonical(host, out string canonical))
        {
            throw new ArgumentException($"'{host}' is not a server host the OS may allow (ADR 0284 §2).", nameof(host));
        }

        (string, string) key = (appId, canonical);
        lock (_gate)
        {
            _leases[key] = _leases.GetValueOrDefault(key) + 1;
        }

        return new Lease(this, key);
    }

    /// <summary>Determines whether a drive of <paramref name="appId"/> open now was typed with <paramref name="host"/>.</summary>
    /// <param name="appId">The app sending a direct request.</param>
    /// <param name="host">The request's host.</param>
    /// <returns><see langword="true"/> while at least one lease holds the pair.</returns>
    public bool IsAllowed(string appId, string host)
    {
        if (string.IsNullOrEmpty(appId) || !TryCanonical(host, out string canonical))
        {
            return false;
        }

        lock (_gate)
        {
            return _leases.ContainsKey((appId, canonical));
        }
    }

    /// <summary>The same rules as a typed address: an exact name, no IP address, no <c>localhost</c>.</summary>
    private static bool TryCanonical(string? host, out string canonical)
    {
        canonical = string.Empty;
        return !string.IsNullOrWhiteSpace(host)
            && Uri.CheckHostName(host) == UriHostNameType.Dns
            && AppDriveProviders.TryGetServerHost($"https://{host}/", out canonical);
    }

    private void Release((string AppId, string Host) key)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(key, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                _leases.Remove(key);
            }
            else
            {
                _leases[key] = count - 1;
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly DriveServerHosts _owner;
        private readonly (string AppId, string Host) _key;
        private int _disposed;

        public Lease(DriveServerHosts owner, (string AppId, string Host) key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_key);
            }
        }
    }
}
