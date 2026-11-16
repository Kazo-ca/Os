using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The table of addresses the service worker serves installed apps' <c>wwwroot/</c> files at
/// (ADR 0206, ADR 0274). The browser implementation is <c>BrowserInstalledAppAssetCache</c> (the
/// <c>servedFiles</c> store of the OS database, read by the worker); tests use a dictionary.
/// Addresses are relative to the page's base: <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>.
/// </summary>
public interface IInstalledAppAssetCache
{
    /// <summary>Makes sure a service worker controls the page; <see langword="false"/> when none can.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    ValueTask<bool> PrepareAsync(CancellationToken cancellationToken);

    /// <summary>Lists every served address.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Serves files of an app from the content of the files in its folder, without reading or
    /// copying them (ADR 0274 §3): a file whose stored content is not the declared one is not
    /// served, and its address stops being served.
    /// </summary>
    /// <param name="appId">The installed app.</param>
    /// <param name="files">Its files, with the SHA-256 its manifest declares.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    /// <returns>One result per file.</returns>
    ValueTask<IReadOnlyList<ServedFileResult>> ServeAsync(string appId, IReadOnlyList<ServedFileRequest> files, CancellationToken cancellationToken);

    /// <summary>Stops serving addresses.</summary>
    /// <param name="urls">The addresses.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    ValueTask RemoveAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);

    /// <summary>
    /// Links a served style sheet to the page, once per address (an installed app's scoped CSS,
    /// ADR 0221 §2).
    /// </summary>
    /// <param name="url">Its address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask LinkStyleSheetAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes the <c>wwwroot/</c> files of an installed app (ADR 0206) before its first launch in the
/// session: each file declared in its manifest is served by the service worker at
/// <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>, the address Blazor gives a built-in app's files,
/// straight from the file in the app's folder (ADR 0274): the file system is the only copy.
/// </summary>
/// <remarks>
/// Like a web server pointed at the folder a package manager unpacked, nothing is copied: the
/// publication writes one row per address, after checking that the stored content of the file is
/// the one whose SHA-256 the manifest declares (ADR 0098 §5); no byte is read. It runs once per
/// session per app, at its first launch. The first publication also drops every served address
/// no installed app declares any more, neither in this page nor on disk (an app removed by
/// <c>kazopkg remove</c> or updated before the restart; ADR 0225 §2 for an app another tab installed).
/// Without a service worker controlling the page, nothing is copied and the next launch asks again
/// (ADR 0225 §1). A damaged or missing file is left out, with a <c>/var/log/syslog</c> line, and a
/// damaged one marks the app damaged in <see cref="AppAssetStore"/>; the launch goes on, as with a
/// built-in app's missing file. Once its files are served, the app's scoped CSS
/// (<c>&lt;assembly&gt;.bundle.scp.css</c>) is linked to the page (ADR 0221 §2), as the host's
/// <c>styles.css</c> links a built-in app's.
/// </remarks>
public sealed class InstalledAppAssetPublisher
{
    private readonly InstalledAppsBoot _installed;
    private readonly IInstalledAppAssetCache _cache;
    private readonly Func<AppAssetStore> _store;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private bool _served;

    /// <summary>Creates the publisher.</summary>
    /// <param name="installed">The installed apps of this session.</param>
    /// <param name="cache">The table of addresses the service worker serves.</param>
    /// <param name="store">Records a damaged file (resolved late: it comes with the OS services).</param>
    /// <param name="logger">Receives the <c>/var/log/syslog</c> lines.</param>
    public InstalledAppAssetPublisher(InstalledAppsBoot installed, IInstalledAppAssetCache cache, Func<AppAssetStore> store, ILogger<InstalledAppAssetPublisher>? logger = null)
    {
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <summary>Gets the address a file of an installed app is served at.</summary>
    /// <param name="app">The installed app.</param>
    /// <param name="assetPath">The path declared in its manifest.</param>
    /// <returns>For example <c>_content/Kazo.Sample/engine/worker.js</c>.</returns>
    public static string GetUrl(InstalledApp app, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        return StaticWebAssetAppAssetSource.RelativeUrl(app.Manifest, assetPath);
    }

    /// <summary>
    /// Gets the path of an app's scoped CSS bundle, as Blazor names it: <c>&lt;assembly&gt;.bundle.scp.css</c>
    /// at the root of its <c>_content/&lt;assembly&gt;/</c> folder (entry-point assembly).
    /// </summary>
    /// <param name="app">The installed app.</param>
    /// <returns>For example <c>Kazo.Sample.bundle.scp.css</c>.</returns>
    public static string GetScopedCssBundlePath(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        string assembly = app.Manifest.EntryPoint?.Assembly
            ?? app.Manifest.Platform?.EntryPoints.FirstOrDefault()?.Assembly
            ?? throw new InvalidOperationException($"'{app.AppId}' has no entry-point assembly.");
        string package = assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
        return $"{package}.bundle.scp.css";
    }

    /// <summary>
    /// Publishes the files of <paramref name="app"/> unless it was done in this session. Never throws
    /// for a file or table failure (it is logged); only <paramref name="cancellationToken"/> stops it.
    /// </summary>
    /// <param name="app">The installed app about to start.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    public async Task PublishAsync(InstalledApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Manifest.Assets.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_published.Contains(app.AppId))
            {
                return;
            }

            // ADR 0225: only a success is kept; without a worker the next launch asks again, since
            // the page can be taken by the worker later (Ctrl+F5 page, worker still installing).
            if (!_served)
            {
                _served = await PrepareAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_served)
            {
                await ServeAsync(app, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogWarning("apps: no service worker serves this page, the wwwroot/ files of {AppId} are not served", app.AppId);
                return;
            }

            _published.Add(app.AppId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _cache.PrepareAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await DropStaleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: the table of installed apps' wwwroot/ files cannot be used");
            return false;
        }
    }

    private async Task DropStaleAsync(CancellationToken cancellationToken)
    {
        // ADR 0225 §2: the table is shared by every tab and window of the OS. An app another tab
        // installed is on disk but not yet in this page's catalog: its files stay. The apps this page
        // runs stay too (a loaded copy keeps its version until the restart).
        HashSet<string> declared = new(StringComparer.Ordinal);
        IReadOnlyList<InstalledApp> onDisk = await _installed.ReadInstalledOnDiskAsync(cancellationToken).ConfigureAwait(false);
        foreach (InstalledApp app in _installed.Current.Installed.Concat(onDisk))
        {
            declared.UnionWith(app.Manifest.Assets.Select(asset => GetUrl(app, asset.Path)));
        }

        string[] stale = [.. (await _cache.ListAsync(cancellationToken).ConfigureAwait(false)).Where(url => !declared.Contains(url))];
        // ADR 0274 §4: once its row is gone, the content of a removed app's file is an orphan and
        // follows the ordinary retention (ADR 0273).
        await _cache.RemoveAsync(stale, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        string styles = GetScopedCssBundlePath(app);
        List<string> assetPaths = [];
        List<ServedFileRequest> requests = [];
        foreach (AssetManifest asset in app.Manifest.Assets)
        {
            if (!AppPackage.IsSafeRelativePath(asset.Path))
            {
                _logger?.LogWarning("apps: {Path} of {AppId} is not a path inside the app, it is not served", asset.Path, app.AppId);
                continue;
            }

            assetPaths.Add(asset.Path);
            requests.Add(new ServedFileRequest(
                GetUrl(app, asset.Path),
                app.GetAssetPath(asset.Path),
                asset.Sha256,
                InstalledAppAssetContentTypes.For(asset.Path)));
        }

        IReadOnlyList<ServedFileResult> results;
        try
        {
            results = await _cache.ServeAsync(app.AppId, requests, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: the wwwroot/ files of {AppId} could not be served", app.AppId);
            return;
        }

        int served = 0;
        bool stylesServed = false;
        string stylesUrl = GetUrl(app, styles);
        for (int index = 0; index < requests.Count && index < results.Count; index++)
        {
            ServedFileRequest request = requests[index];
            ServedFileResult result = results[index];
            if (result.Outcome == ServedFileOutcome.Served)
            {
                served++;
                stylesServed |= string.Equals(request.Url, stylesUrl, StringComparison.Ordinal);
                continue;
            }

            _logger?.LogWarning("apps: {Url} of {AppId} is not served ({Reason})", request.Url, app.AppId, result.Outcome);
            if (result.Outcome == ServedFileOutcome.HashMismatch)
            {
                ReportDamaged(app, request, assetPaths[index]);
            }
        }

        _logger?.LogInformation("apps: {Count} wwwroot/ file(s) of {AppId} {Version} served", served, app.AppId, app.Version);
        if (stylesServed)
        {
            await LinkStylesAsync(app, stylesUrl, cancellationToken).ConfigureAwait(false);
        }
    }

    // ADR 0098 §5: a file whose content is not the declared one marks the app damaged, as a read
    // through AppAssetStore would.
    private void ReportDamaged(InstalledApp app, ServedFileRequest request, string assetPath)
    {
        try
        {
            _store().ReportDamaged(app.AppId, assetPath, $"expected SHA-256 {request.Sha256}, the stored content differs");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: {AppId} could not be marked damaged", app.AppId);
        }
    }

    // ADR 0221 §2: the scoped CSS of the app's components, linked once it is served.
    private async Task LinkStylesAsync(InstalledApp app, string url, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.LinkStyleSheetAsync(url, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("apps: scoped CSS of {AppId} loaded from {Url}", app.AppId, url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "apps: the scoped CSS of {AppId} could not be loaded", app.AppId);
        }
    }
}

/// <summary>
/// The <c>Content-Type</c> of an installed app's file, from its extension (ADR 0206). The browser
/// needs the right one: a module script, a style sheet or <c>WebAssembly.instantiateStreaming</c>
/// refuses a wrong type.
/// </summary>
public static class InstalledAppAssetContentTypes
{
    /// <summary>The type of a file whose extension is not listed.</summary>
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".css"] = "text/css",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".json"] = "application/json",
        [".map"] = "application/json",
        [".wasm"] = "application/wasm",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain",
        [".xml"] = "application/xml",
    };

    /// <summary>Gets the type of a file.</summary>
    /// <param name="assetPath">Its declared path.</param>
    public static string For(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        return ByExtension.TryGetValue(Path.GetExtension(assetPath), out string? type) ? type : Default;
    }
}
