using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.OneDrive;

/// <summary>
/// The <c>onedrive</c> drive source (ADR 0267 §3 and §6): mounts the OneDrive of a cloud account
/// the user already connected (<see cref="CloudAccountService.ConnectAsync"/>, in their click),
/// over Microsoft Graph. The account's scope chooses the top of the drive: the whole OneDrive
/// (<c>Files.ReadWrite</c>) or its app folder (<c>Files.ReadWrite.AppFolder</c>).
/// </summary>
/// <remarks>
/// <see cref="CloudDriveSource"/> does the rest: no window in <c>CreateAsync</c>, no network in
/// <c>OpenAsync</c>, and <c>AvailabilityChanged</c> when an account is reconnected or the network
/// comes back.
/// </remarks>
public sealed class OneDriveDriveSource : CloudDriveSource
{
    /// <summary>Filesystem type and provider id.</summary>
    public const string FileSystemType = CloudOAuthProvider.OneDriveId;

    /// <summary>Initializes the source.</summary>
    /// <param name="tokens">The cloud accounts (<see cref="CloudAccountService"/>).</param>
    /// <param name="catalog">The configured providers: the source is offered when OneDrive has an app id.</param>
    /// <param name="http">Client of the Graph requests (<c>fetch</c> in the browser).</param>
    /// <param name="activity">The page's activity and the network's return; <see langword="null"/> in tests.</param>
    public OneDriveDriveSource(ICloudAccountTokens tokens, ICloudProviderCatalog catalog, HttpClient http, IHostActivitySignal? activity = null)
        : base(tokens, catalog, http, activity)
    {
    }

    /// <inheritdoc />
    public override string Type => FileSystemType;

    /// <inheritdoc />
    public override string ProviderId => CloudOAuthProvider.OneDriveId;

    /// <inheritdoc />
    public override string DefaultMountName => "OneDrive";

    /// <inheritdoc />
    protected override IHostFolder CreateFolder(CloudDriveConnection connection, CloudAccount account, string? root) =>
        new OneDriveFolder(connection, Http, account.DisplayName, account.Scope, Split(root));

    /// <summary>
    /// Normalizes a folder path of OneDrive (<c>Work/2026</c>): separators <c>/</c> or <c>\</c>, no
    /// empty, <c>.</c> or <c>..</c> segment; <see langword="null"/> for the top.
    /// </summary>
    /// <exception cref="ArgumentException">A segment is <c>.</c> or <c>..</c>.</exception>
    protected override string? NormalizeRoot(string? root)
    {
        string[] segments = Split(root);
        return segments.Length == 0 ? null : string.Join('/', segments);
    }

    private static string[] Split(string? root)
    {
        string[] segments = root is null
            ? []
            : root.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(static segment => segment is "." or "..")
            ? throw new ArgumentException($"'{root}' is not a folder path of OneDrive.", nameof(root))
            : segments;
    }
}
