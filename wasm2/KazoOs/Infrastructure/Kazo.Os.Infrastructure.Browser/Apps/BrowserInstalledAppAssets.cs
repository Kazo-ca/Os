using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Kazo.Os.Infrastructure.Browser.Apps;

/// <summary>
/// The page's side of the service worker that serves the <c>wwwroot/</c> files of installed apps
/// (ADR 0206): the C# half of <c>wwwroot/installedAppAssets.js</c>, which this class imports itself
/// (no host references it, AGENTS.md). The worker answers <c>_content/&lt;assembly&gt;/&lt;path&gt;</c>
/// from the file system's content, through the <c>servedFiles</c> table
/// (<see cref="FileSystem.IndexedDbServedFiles"/>, ADR 0274), so an installed app finds its files at
/// the address a built-in app uses.
/// </summary>
/// <remarks>Every address is relative to the page's base, for example <c>_content/Kazo.Sample/x.js</c>.</remarks>
[SupportedOSPlatform("browser")]
public static partial class BrowserInstalledAppAssets
{
    private const string ModuleName = "kazoos-installed-app-assets";
    private const string ModulePath = "_content/Kazo.Os.Infrastructure.Browser/installedAppAssets.js";
    private static Task? _import;

    /// <summary>
    /// Makes sure a service worker controls the page (on a development host, registers the one that
    /// only serves installed apps) and returns whether one does. It also deletes the Cache Storage
    /// copy a release before ADR 0274 kept (<c>kazoos-installed-apps</c>).
    /// </summary>
    /// <param name="timeout">How long to wait for a new worker to take control.</param>
    public static async Task<bool> PrepareAsync(TimeSpan timeout)
    {
        await ImportAsync().ConfigureAwait(true);
        return await Prepare((int)timeout.TotalMilliseconds).ConfigureAwait(true);
    }

    /// <summary>
    /// ADR 0241: once the OS starts, makes the site's service worker the one of this page's release
    /// when the page was loaded with Ctrl+F5 while an older worker is still active, so the next
    /// reload does not go back to the older release. Does nothing on a development host, on a first
    /// visit, or when a worker already controls the page.
    /// </summary>
    /// <param name="timeout">How long each step waits for the workers.</param>
    /// <returns>Whether a worker controls the page afterwards.</returns>
    public static async Task<bool> AdoptAtBootAsync(TimeSpan timeout)
    {
        await ImportAsync().ConfigureAwait(true);
        return await AdoptAtBoot((int)timeout.TotalMilliseconds).ConfigureAwait(true);
    }

    /// <summary>
    /// Links a served style sheet to the page (an installed app's scoped CSS, ADR 0221 §2), once per
    /// address; <see langword="true"/> when the link was added.
    /// </summary>
    /// <param name="url">The address, relative to the page's base.</param>
    public static async Task<bool> LinkStyleSheetAsync(string url)
    {
        await ImportAsync().ConfigureAwait(true);
        return LinkStyleSheet(url);
    }

    private static Task ImportAsync() => _import ??= ImportCoreAsync();

    private static async Task ImportCoreAsync()
    {
        string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
        await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
    }

    [JSImport("prepare", ModuleName)]
    private static partial Task<bool> Prepare(int timeoutMs);

    [JSImport("adoptAtBoot", ModuleName)]
    private static partial Task<bool> AdoptAtBoot(int timeoutMs);

    [JSImport("linkStyleSheet", ModuleName)]
    private static partial bool LinkStyleSheet(string url);
}
