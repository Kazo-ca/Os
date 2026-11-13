using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// Finds the apps of the Store catalogs that can open a file no installed app opens (ADR 0257 §2):
/// when <see cref="Intents.FileAssociationResolver"/> answers <c>NoHandler</c>, the open path asks
/// it which catalog apps declare the file type, so the user is told what to install.
/// </summary>
/// <remarks>
/// <para>
/// Only the catalogs this OS already downloaded are read (<see cref="AppPackageInstaller.GetCachedCatalog"/>):
/// opening a file never waits for the network, and offline the list is simply empty (ADR 0257 §6).
/// </para>
/// <para>
/// For each app, the newest stable version compatible with the SDK of this OS is the one that
/// counts: it must declare the file's extension (else its media type) for the action. Apps already
/// in the running catalog, installed but not loaded yet, or blocked by <c>app-policy.config</c> are
/// left out. The official catalog comes first, then the other catalogs in the order of
/// <see cref="AppPackageInstaller.GetCatalogSearchOrder"/>; an app listed by several catalogs is
/// proposed once, from the first.
/// </para>
/// </remarks>
public sealed class CatalogAppSuggestions
{
    /// <summary>Most apps proposed for one file.</summary>
    public const int MaximumSuggestions = 8;

    private readonly AppPackageInstaller _installer;
    private readonly Func<IAppFileSystemGateway> _systemReader;
    private readonly AppCatalog _running;
    private readonly ILogger _logger;

    /// <summary>Creates the search.</summary>
    /// <param name="installer">Holds the downloaded catalogs and reads the sources, the policy and the installed apps.</param>
    /// <param name="systemReader">The OS's own reader of <c>/etc</c> and of the users' homes.</param>
    /// <param name="running">The running catalog: its apps are installed already.</param>
    /// <param name="logger">Writes the failures to <c>/var/log/syslog</c>.</param>
    public CatalogAppSuggestions(
        AppPackageInstaller installer,
        Func<IAppFileSystemGateway> systemReader,
        AppCatalog running,
        ILogger<CatalogAppSuggestions>? logger = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _systemReader = systemReader ?? throw new ArgumentNullException(nameof(systemReader));
        _running = running ?? throw new ArgumentNullException(nameof(running));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Finds the catalog apps that can open <paramref name="path"/>. Never throws for an unreadable
    /// configuration file: the open path must still answer <c>NoHandler</c>.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="mediaType">Its media type, when the caller knows it.</param>
    /// <param name="action"><c>open</c> or <c>edit</c>.</param>
    /// <param name="user">The user the file is opened for; their personal apps count as installed.</param>
    /// <param name="cancellationToken">Cancels the reads of the configuration files.</param>
    /// <returns>At most <see cref="MaximumSuggestions"/> apps; empty when none.</returns>
    public async ValueTask<IReadOnlyList<AppIntentSuggestedCatalogApp>> FindAsync(
        VirtualPath path, string? mediaType, string action, AuthenticatedPrincipal? user, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        string? extension = Intents.FileAssociationResolver.GetExtension(path)?.ToLowerInvariant();
        if (extension is null && mediaType is null)
        {
            return [];
        }

        try
        {
            IAppFileSystemGateway reader = _systemReader();
            AppSourcesConfig sources = await _installer.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
            List<(string Name, AppCatalogListing Listing)> catalogs = [];
            foreach (string name in AppPackageInstaller.GetCatalogSearchOrder(sources))
            {
                if (_installer.GetCachedCatalog(name, sources) is { } listing)
                {
                    catalogs.Add((name, listing));
                }
            }

            if (catalogs.Count == 0)
            {
                return [];
            }

            AppPolicy policy = await _installer.ReadPolicyAsync(reader, cancellationToken).ConfigureAwait(false);
            HashSet<string> installed = new(_running.Manifests.Keys, StringComparer.Ordinal);
            installed.UnionWith((await _installer.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false)).Sources.Keys);
            if (user is not null)
            {
                installed.UnionWith((await _installer.ReadPersonalInstalledAsync(reader, user.HomePath, cancellationToken).ConfigureAwait(false)).Sources.Keys);
            }

            return Select(catalogs, extension, mediaType, action, installed, policy, _installer.SdkVersion);
        }
        catch (Exception exception) when (exception is AppPackageException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning("apps: no catalog app could be proposed for {Path}: {Reason}", path.Value, exception.Message);
            return [];
        }
    }

    /// <summary>
    /// The pure part of <see cref="FindAsync"/>: walks the catalogs in order and keeps the apps
    /// whose newest compatible stable version declares the file type.
    /// </summary>
    /// <param name="catalogs">The downloaded catalogs, in search order.</param>
    /// <param name="extension">The file's extension with its dot, or <see langword="null"/>.</param>
    /// <param name="mediaType">The file's media type, or <see langword="null"/>.</param>
    /// <param name="action"><c>open</c> or <c>edit</c>.</param>
    /// <param name="installed">Apps installed already.</param>
    /// <param name="policy">The block list.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    public static IReadOnlyList<AppIntentSuggestedCatalogApp> Select(
        IReadOnlyList<(string Name, AppCatalogListing Listing)> catalogs,
        string? extension,
        string? mediaType,
        string action,
        IReadOnlySet<string> installed,
        AppPolicy policy,
        SemanticVersion sdkVersion)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(policy);
        List<AppIntentSuggestedCatalogApp> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string name, AppCatalogListing listing) in catalogs)
        {
            foreach (AppCatalogEntry app in listing.Apps)
            {
                if (app.Kind != AppCatalogEntryKind.App || installed.Contains(app.AppId) || seen.Contains(app.AppId))
                {
                    continue;
                }

                AppCatalogVersion? version = NewestCompatible(app, sdkVersion);
                if (version is null
                    || !version.HandlesFile(extension, mediaType, action)
                    || policy.FindBlock(app.AppId, app.Category, version.Capabilities) is not null)
                {
                    continue;
                }

                seen.Add(app.AppId);
                found.Add(new AppIntentSuggestedCatalogApp(app.AppId, app.Name, name));
                if (found.Count == MaximumSuggestions)
                {
                    return found;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The newest stable version that runs on <paramref name="sdkVersion"/>; a version whose
    /// catalog entry gives no SDK range is kept (the install checks the manifest again).
    /// </summary>
    private static AppCatalogVersion? NewestCompatible(AppCatalogEntry app, SemanticVersion sdkVersion) =>
        app.Versions
            .Where(static version => version.Version.PreRelease is null)
            .OrderByDescending(static version => version.Version)
            .FirstOrDefault(version => version.MinimumSdkVersion is not { } minimum
                || KazoOsSdk.IsCompatible(new AppSdkCompatibilityManifest(minimum, version.MaximumSdkVersion), sdkVersion));
}
