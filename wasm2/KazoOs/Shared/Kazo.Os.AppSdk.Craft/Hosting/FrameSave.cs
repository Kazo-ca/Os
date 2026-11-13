namespace Kazo.Os.AppSdk.Craft;

/// <summary>A file the Craft app saves (its <c>&lt;app&gt;:save</c> message).</summary>
/// <param name="Name">The file name the app gave; its extension tells the format.</param>
/// <param name="SaveAs">The app's <c>saveAs</c>: the file becomes the document's file.</param>
/// <param name="Bytes">The file's content.</param>
public sealed record FrameSave(string Name, bool SaveAs, byte[] Bytes);
