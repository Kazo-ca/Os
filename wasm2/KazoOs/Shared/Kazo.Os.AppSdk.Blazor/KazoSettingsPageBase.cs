namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Base class of a page a feature adds to Settings ▸ Features (ADR 0249 §2): the component of an
/// entry point of kind <c>SettingsPage</c>. Settings draws it in its own window, in the same frame,
/// navigation and theme (the <c>--kos-*</c> styles), but the page runs in the execution context of
/// its entry point: its id, its capabilities, its process in <c>ps</c>. It receives nothing from
/// Settings, and Settings receives nothing from it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KazoComponentBase.AppContext"/> is the page's own context, so the <c>Kazo*</c> facades
/// called from the page act with the page's rights. Opening the page asks for no authentication; a
/// sensitive operation asks for it through <c>KazoFeatureAuthentication.AuthenticateAsync</c>
/// (ADR 0247 §3), whose OS window shows over Settings.
/// </para>
/// <para>
/// An exception thrown by the page stays in the page's area ("&lt;Feature&gt; ran into a problem",
/// Retry, ADR 0238); Settings goes on.
/// </para>
/// </remarks>
public abstract class KazoSettingsPageBase : KazoComponentBase
{
    /// <summary>Gets the page's title, its entry point's name as Settings shows it in its menu.</summary>
    protected string PageTitle => AppContext?.Manifest.Name ?? string.Empty;
}
