namespace Kazo.Os.AppSdk.Craft;

/// <summary>What a Craft app window does with a file the app saves.</summary>
public enum SaveAction
{
    /// <summary>The document's name is bound to a file and the last write there succeeded: write that file again.</summary>
    Rewrite,

    /// <summary>A new file (Save As, an untitled document, or the last write failed): the OS save dialog, then the document is bound to the chosen file.</summary>
    AskAndBind,

    /// <summary>A copy (an export): the OS save dialog; the document stays bound to its own file.</summary>
    AskForCopy,
}

/// <summary>The decision for one <c>save</c> message of the Craft app (<see cref="DocumentSession.Decide"/>).</summary>
/// <param name="Action">What to do.</param>
/// <param name="Name">The file name the Craft app gave (its document's name, or the copy's name).</param>
/// <param name="Path">The file to write again, for <see cref="SaveAction.Rewrite"/>.</param>
/// <param name="Folder">The folder the save dialog opens in: the document's own, or <see langword="null"/> for the user's default.</param>
public sealed record SaveDecision(SaveAction Action, string Name, string? Path, string? Folder)
{
    /// <summary>Gets whether the user picks the file in the OS save dialog.</summary>
    public bool AsksForPath => Action != SaveAction.Rewrite;

    /// <summary>Gets whether the outcome changes the document's saved state (a copy does not).</summary>
    public bool SavesTheDocument => Action != SaveAction.AskForCopy;
}
