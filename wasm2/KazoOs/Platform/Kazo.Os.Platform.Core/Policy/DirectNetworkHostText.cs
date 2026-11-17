using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Formats the direct-network hosts an app declares (ADR 0282 §4) for the approval of
/// <c>network.direct.access</c>: the secure-desktop window and the terminal's text mode show the
/// same list, so the administrator sees which web services the app will reach without the server.
/// </summary>
public static class DirectNetworkHostText
{
    /// <summary>
    /// Returns the declared hosts as <c>api.example.com, *.example.org</c>, or <see langword="null"/>
    /// when the app declares none or <paramref name="capability"/> is not <c>network.direct.access</c>.
    /// </summary>
    /// <param name="manifest">The app asking for approval.</param>
    /// <param name="capability">The capability the line belongs to.</param>
    public static string? For(AppManifest manifest, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(capability, AppCapabilities.NetworkDirectAccess, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<string> hosts = DirectNetworkHosts.ParseValid(manifest.DirectNetworkHosts);
        return hosts.Count == 0 ? null : string.Join(", ", hosts);
    }
}
