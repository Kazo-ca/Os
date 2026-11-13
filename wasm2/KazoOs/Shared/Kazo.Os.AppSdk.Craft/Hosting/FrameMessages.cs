namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// Names of the messages <c>craft-host.js</c> passes to <see cref="CraftFrame.OnFrameMessage"/>,
/// each with a text value (ADR 0247 §3). They stay inside the module: the OS side and the proxy
/// always come from the same OS.
/// </summary>
public static class FrameMessages
{
    /// <summary>The Craft app is listening; the value is its version.</summary>
    public const string Ready = "ready";

    /// <summary>The Craft app did not start; the value is the error.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The running Craft app reported an error (its <c>&lt;app&gt;:failed</c> after <c>ready</c>, for
    /// example a save it could not finish); the value is <c>{ error, id }</c>.
    /// </summary>
    public const string Error = "error";

    /// <summary>The shown document has changes, or not; the value is <c>true</c> or <c>false</c>.</summary>
    public const string Dirty = "dirty";

    /// <summary>A reserved chord meant for the window was typed in the app; the value is the key's JSON.</summary>
    public const string Key = "key";

    /// <summary>The app opened its file picker; the value is <c>{ id, accept, multiple }</c>.</summary>
    public const string Pick = "pick";

    /// <summary>The app wrote a setting; the value is <c>{ key, value }</c> (value null: removed).</summary>
    public const string Setting = "setting";

    /// <summary>The OS theme's color scheme changed; the value is <c>light</c> or <c>dark</c>.</summary>
    public const string Scheme = "scheme";
}
