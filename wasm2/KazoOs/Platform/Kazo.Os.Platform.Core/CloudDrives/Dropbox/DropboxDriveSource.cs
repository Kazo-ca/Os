using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.Dropbox;

/// <summary>
/// The <c>dropbox</c> drive source (ADR 0267 §6): a drive of a connected Dropbox account, the
/// whole storage or the <c>Apps/Kazo OS</c> folder depending on the app the account signed in
/// with (§8: Dropbox fixes the scope per app). The default mount point is <c>/mnt/Dropbox</c>.
/// </summary>
public sealed class DropboxDriveSource : CloudDriveSource
{
    /// <summary>Filesystem type of a Dropbox drive.</summary>
    public const string FileSystemType = "dropbox";

    /// <summary>Initializes the source.</summary>
    /// <param name="tokens">The cloud accounts.</param>
    /// <param name="catalog">The configured providers: available when Dropbox has an app key.</param>
    /// <param name="http">The HTTP client (<c>fetch</c> in the browser).</param>
    /// <param name="activity">The page's activity, to try a disconnected drive again; <see langword="null"/> in tests.</param>
    public DropboxDriveSource(ICloudAccountTokens tokens, ICloudProviderCatalog catalog, HttpClient http, IHostActivitySignal? activity = null)
        : base(tokens, catalog, http, activity)
    {
    }

    /// <inheritdoc />
    public override string Type => FileSystemType;

    /// <inheritdoc />
    public override string ProviderId => CloudOAuthProvider.DropboxId;

    /// <inheritdoc />
    public override string DefaultMountName => "Dropbox";

    /// <inheritdoc />
    protected override IHostFolder CreateFolder(CloudDriveConnection connection, CloudAccount account, string? root) =>
        new DropboxFolder(connection, account.DisplayName, root);

    /// <summary>
    /// A Dropbox path: <c>/Documents</c>, with or without the leading or trailing slash;
    /// <see langword="null"/>, empty or <c>/</c> is the whole storage the account reaches.
    /// </summary>
    /// <exception cref="ArgumentException">An empty, <c>.</c> or <c>..</c> segment, or a backslash.</exception>
    protected override string? NormalizeRoot(string? root) => DropboxPaths.NormalizeRoot(root);
}
