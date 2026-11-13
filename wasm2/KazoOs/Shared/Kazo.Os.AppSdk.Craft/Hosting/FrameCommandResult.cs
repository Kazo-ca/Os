using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// A Craft app's answer to one command of its page API (<c>window.&lt;app&gt;.request</c>, the
/// <c>craft-web-api</c> contract), sent by the window through the iframe proxy (message
/// <c>command</c>, answered by <c>result</c>; ADR 0247).
/// </summary>
/// <param name="Ok">Whether the app ran the command.</param>
/// <param name="Value">What the command returned (an undefined element when it failed).</param>
/// <param name="Error">Why it failed: the app's own error, a timeout, or a lost connection.</param>
public sealed record FrameCommandResult(bool Ok, JsonElement Value, string? Error)
{
    /// <summary>A failed command.</summary>
    /// <param name="error">Why it failed.</param>
    /// <returns>The result.</returns>
    public static FrameCommandResult Failure(string error) => new(false, default, error);
}
