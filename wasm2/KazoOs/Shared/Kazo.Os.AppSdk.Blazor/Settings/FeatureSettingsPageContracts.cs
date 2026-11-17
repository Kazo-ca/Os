using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.Settings;

/// <summary>
/// The pages features add to Settings ▸ Features (ADR 0249 §1 and §2), implemented by the platform
/// and injected in the Settings app. The OS answers only Settings: any other host gets no page.
/// </summary>
/// <remarks>
/// The list is read from the catalog, live (ADR 0214 §1): a feature installed, enabled, disabled or
/// removed, or a role granted or withdrawn, raises <see cref="Changed"/>. A page is listed only when
/// its feature and the page are enabled, the user holds the role the page requires (ADR 0247 §2:
/// every administrator holds them all), and, on a phone, the page shows on mobile.
/// </remarks>
public interface IFeatureSettingsPages
{
    /// <summary>Raised, outside any Blazor event, when the list of pages may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the pages the user of <paramref name="host"/> sees, by feature name, then order, then title.</summary>
    /// <param name="host">The Settings window's context.</param>
    /// <returns>The visible pages; empty for any host other than Settings.</returns>
    IReadOnlyList<FeatureSettingsPageInfo> GetPages(IAppExecutionContext host);

    /// <summary>
    /// Starts the process of a page, as a child of <paramref name="host"/>'s process, and gives the
    /// content Settings draws. The page runs in its own context, never in Settings'.
    /// </summary>
    /// <param name="host">The Settings window's context.</param>
    /// <param name="pageId">The id of the page's entry point, for example <c>ca.kazo.x86.admin</c>.</param>
    /// <param name="cancellationToken">Cancels the load of the feature's assembly.</param>
    /// <returns>The running page, or why it cannot be shown.</returns>
    Task<FeatureSettingsPageStart> OpenAsync(IAppExecutionContext host, string pageId, CancellationToken cancellationToken = default);
}

/// <summary>One page a feature adds to Settings ▸ Features (ADR 0249 §1).</summary>
/// <param name="Id">The id of the page's entry point (<c>ca.kazo.x86.admin</c>), also its <c>--page=</c> argument.</param>
/// <param name="FeatureId">The id of its feature.</param>
/// <param name="FeatureName">The name of its feature, in the user's language, shown before the title in the menu.</param>
/// <param name="Title">The page's title, in the user's language.</param>
/// <param name="Order">Its place among the pages of its feature.</param>
public sealed record FeatureSettingsPageInfo(string Id, string FeatureId, string FeatureName, string Title, int Order)
{
    /// <summary>Gets the URL of the page's icon, or <see langword="null"/> when it declares none.</summary>
    public string? IconUrl { get; init; }
}

/// <summary>Outcome of <see cref="IFeatureSettingsPages.OpenAsync"/>.</summary>
/// <param name="Session">The running page, when it started.</param>
/// <param name="Error">Why it did not start, for the user.</param>
/// <param name="IsDenied">Whether it was refused because the page is not open to this user (role, disabled, unknown).</param>
public sealed record FeatureSettingsPageStart(IFeatureSettingsPageSession? Session, string? Error, bool IsDenied = false)
{
    /// <summary>Gets whether the page started.</summary>
    public bool IsStarted => Session is not null;

    /// <summary>Creates a started outcome.</summary>
    /// <param name="session">The running page.</param>
    /// <returns>The outcome.</returns>
    public static FeatureSettingsPageStart Started(IFeatureSettingsPageSession session) => new(session, null);

    /// <summary>Creates an outcome for a page that is not open to the user.</summary>
    /// <param name="error">Why, for the user.</param>
    /// <returns>The outcome.</returns>
    public static FeatureSettingsPageStart Denied(string error) => new(null, error, IsDenied: true);

    /// <summary>Creates an outcome for a page that failed to start.</summary>
    /// <param name="error">Why, for the user.</param>
    /// <returns>The outcome.</returns>
    public static FeatureSettingsPageStart Failed(string error) => new(null, error);
}

/// <summary>
/// The running process of a page of Settings ▸ Features (ADR 0249 §2): a child of Settings' process,
/// with the page's id and capabilities. It ends when Settings leaves the page or closes, or when the
/// OS stops it (task manager, <c>kill</c>).
/// </summary>
public interface IFeatureSettingsPageSession : IAsyncDisposable
{
    /// <summary>Gets the page.</summary>
    FeatureSettingsPageInfo Page { get; }

    /// <summary>
    /// Gets what Settings draws in its page area: the page's component in the page's context, inside
    /// the OS error barrier (ADR 0238), so an exception of the page stays in that area.
    /// </summary>
    RenderFragment Content { get; }

    /// <summary>Gets the page's execution context.</summary>
    IAppExecutionContext Context { get; }

    /// <summary>Gets whether the process ended.</summary>
    bool HasExited { get; }

    /// <summary>Raised once when the process ends, from outside any Blazor event.</summary>
    event Action? Exited;

    /// <summary>Ends the page's process: Settings left the page.</summary>
    /// <returns>A task that completes when the process stopped.</returns>
    Task StopAsync();
}
