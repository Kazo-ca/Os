using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// ADR 0272 §3: the waiting screen of the one-time conversion of the stored file content, the C#
/// half of the host's <c>storageConversion.js</c> (imported here, no host page references it).
/// The calls are synchronous: the conversion reports its progress from JavaScript.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserStorageConversionScreen
{
    private const string ModuleName = "kazoos-storage-conversion";
    private const string ModulePath = "_content/Kazo.Os.Ecosystem.Host/storageConversion.js";
    private static Task? _import;

    /// <summary>Imports the module once per page.</summary>
    public static Task ImportAsync() => _import ??= ImportCoreAsync();

    private static async Task ImportCoreAsync()
    {
        string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
        await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
    }

    /// <summary>Shows the progress: chunks looked at out of chunks to look at.</summary>
    /// <param name="looked">Chunks looked at.</param>
    /// <param name="total">Chunks to look at.</param>
    [JSImport("showProgress", ModuleName)]
    public static partial void ShowProgress(double looked, double total);

    /// <summary>Says that another tab is converting.</summary>
    [JSImport("showWaiting", ModuleName)]
    public static partial void ShowWaiting();

    /// <summary>Says that the conversion failed and offers to retry.</summary>
    /// <param name="reason">The technical reason.</param>
    [JSImport("showFailure", ModuleName)]
    public static partial void ShowFailure(string reason);

    /// <summary>Removes the screen.</summary>
    [JSImport("hide", ModuleName)]
    public static partial void Hide();
}
