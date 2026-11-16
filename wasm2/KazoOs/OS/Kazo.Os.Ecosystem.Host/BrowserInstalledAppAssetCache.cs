using System.Runtime.Versioning;
using Kazo.Os.Infrastructure.Browser.Apps;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The browser's <see cref="IInstalledAppAssetCache"/> (ADR 0206, ADR 0274): the <c>servedFiles</c>
/// store of the OS database, which the host's service worker reads to answer
/// <c>_content/&lt;assembly&gt;/&lt;path&gt;</c> from the file system's content
/// (<see cref="IndexedDbServedFiles"/>); the service worker itself through
/// <see cref="BrowserInstalledAppAssets"/>.
/// </summary>
/// <param name="runtime">JavaScript runtime of the OS database.</param>
[SupportedOSPlatform("browser")]
public sealed class BrowserInstalledAppAssetCache(IJSRuntime runtime) : IInstalledAppAssetCache
{
    private readonly IndexedDbServedFiles _table = new(runtime);

    /// <summary>How long a launch waits for a newly registered service worker to take control.</summary>
    public static readonly TimeSpan ServiceWorkerTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async ValueTask<bool> PrepareAsync(CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.PrepareAsync(ServiceWorkerTimeout).WaitAsync(cancellationToken).ConfigureAwait(true);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        _table.ListAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ServedFileResult>> ServeAsync(string appId, IReadOnlyList<ServedFileRequest> files, CancellationToken cancellationToken) =>
        _table.ServeAsync(appId, files, cancellationToken);

    /// <inheritdoc />
    public ValueTask RemoveAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken) =>
        _table.RemoveAsync(urls, cancellationToken);

    /// <inheritdoc />
    public async ValueTask LinkStyleSheetAsync(string url, CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.LinkStyleSheetAsync(url).WaitAsync(cancellationToken).ConfigureAwait(true);
}
