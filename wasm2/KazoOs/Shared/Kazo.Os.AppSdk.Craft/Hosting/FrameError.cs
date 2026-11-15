namespace Kazo.Os.AppSdk.Craft;

/// <summary>An error the running Craft app reported (message <see cref="FrameMessages.Error"/>).</summary>
/// <param name="Message">The app's error text.</param>
/// <param name="Id">The id of the request it concerns (a save), or <see langword="null"/>.</param>
public sealed record FrameError(string Message, string? Id);
