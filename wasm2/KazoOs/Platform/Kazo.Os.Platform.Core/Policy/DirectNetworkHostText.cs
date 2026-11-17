using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Formats the direct-network hosts an app declares (ADR 0282 §4, ADR 0284) for the approval of
/// <c>network.direct.access</c> and <c>drives.provide</c>: the secure-desktop window and the
/// terminal's text mode show the same list, so the administrator sees which web services the app
/// will reach without the server.
/// </summary>
public static class DirectNetworkHostText
{
    /// <summary>
    /// Returns the hosts <paramref name="capability"/> lets the app reach as
    /// <c>api.example.com, *.example.org</c>: its <c>directNetworkHosts</c> for
    /// <c>network.direct.access</c>, the hosts of its drive providers for <c>drives.provide</c>
    /// (ADR 0284 §1); <see langword="null"/> when there are none.
    /// </summary>
    /// <param name="manifest">The app asking for approval.</param>
    /// <param name="capability">The capability the line belongs to.</param>
    public static string? For(AppManifest manifest, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        IReadOnlyList<string> hosts = DangerousCapabilityEvaluator.DirectHostsFor(manifest, capability);
        return hosts.Count == 0 ? null : string.Join(", ", hosts);
    }

    /// <summary>
    /// Returns the labels of the "server address" fields of the app's drive providers, as
    /// <c>Server address</c>, for the line of <c>drives.provide</c> that says the provider reaches
    /// the server each user types there (ADR 0284 §2); <see langword="null"/> when there are none or
    /// <paramref name="capability"/> is another one.
    /// </summary>
    /// <param name="manifest">The app asking for approval.</param>
    /// <param name="capability">The capability the line belongs to.</param>
    public static string? TypedServersFor(AppManifest manifest, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(capability, AppCapabilities.DrivesProvide, StringComparison.Ordinal))
        {
            return null;
        }

        string[] labels = [.. (manifest.DriveProviders ?? [])
            .Select(static provider => AppDriveProviders.ServerAddressField(provider?.Connection)?.Label)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];
        return labels.Length == 0 ? null : string.Join(", ", labels);
    }
}
