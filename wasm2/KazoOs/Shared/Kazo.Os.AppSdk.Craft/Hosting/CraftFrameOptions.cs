namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// What a Craft app window gives its iframe when it creates it (ADR 0259 §3): the app's own iframe
/// page, and what the proxy passes to the Craft app.
/// </summary>
/// <param name="PagePath">
/// Address of the app's iframe page, relative to the OS document (<see cref="PagePathOf"/> builds
/// the usual one, <c>_content/&lt;assembly&gt;/host/index.html</c>, served by ADR 0243).
/// </param>
/// <param name="Title">Accessible name of the iframe.</param>
public sealed record CraftFrameOptions(string PagePath, string Title)
{
    /// <summary>Gets the user's language code, for the proxy (<see cref="CraftLanguage.Pick"/>).</summary>
    public string Language { get; init; } = CraftLanguage.English;

    /// <summary>
    /// Gets the query parameters added to the page address, which the Craft app reads at start (for
    /// WordCraft: <c>author</c>, and <c>webgl</c> with an empty value to force its WebGL 2 renderer).
    /// An empty value is kept as a bare parameter.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>Gets the chords the iframe gives back instead of the Craft app (<see cref="Craft.ReservedKeys"/>).</summary>
    public IReadOnlyList<ReservedKey>? ReservedKeys { get; init; }

    /// <summary>
    /// Gets the Craft app's browser storage for this Kazo user (<see cref="WebStorageFile.Entries"/>);
    /// <see langword="null"/> keeps the browser's own.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    /// <summary>The usual address of an app's iframe page: <c>_content/&lt;assembly&gt;/host/index.html</c>.</summary>
    /// <param name="assemblyName">The app's assembly name, which names its static web assets.</param>
    /// <returns>The address, relative to the OS document.</returns>
    public static string PagePathOf(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        return $"_content/{assemblyName}/host/index.html";
    }

    /// <summary>The form <c>craft-host.js</c>'s <c>create</c> receives.</summary>
    /// <param name="takesExports">Whether the window takes the app's downloads as exports (ADR 0264).</param>
    /// <returns><c>{ page, title, language, parameters, reservedKeys, settings, exports }</c>.</returns>
    internal Dictionary<string, object?> ToMessage(bool takesExports = false) => new()
    {
        ["page"] = PagePath,
        ["title"] = Title,
        ["language"] = Language,
        ["parameters"] = Parameters,
        ["reservedKeys"] = (ReservedKeys ?? []).Select(key => key.ToMessage()).ToArray(),
        ["settings"] = Settings,
        ["exports"] = takesExports,
    };
}
