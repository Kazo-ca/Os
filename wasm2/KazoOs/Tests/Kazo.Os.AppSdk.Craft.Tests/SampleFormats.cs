namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>
/// The formats of a word processor, as Kazo Word declares them: the example list the tests give the
/// module (the module itself holds no app's list, ADR 0247 §1).
/// </summary>
internal static class SampleFormats
{
    public static CraftDocumentFormats Word { get; } = new(
    [
        new(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", IsDefault: true, IsCopy: false),
        new(".doc", "application/msword", IsDefault: true, IsCopy: false),
        new(".dot", "application/msword", IsDefault: true, IsCopy: false),
        new(".odt", "application/vnd.oasis.opendocument.text", IsDefault: true, IsCopy: false),
        new(".rtf", "application/rtf", IsDefault: true, IsCopy: false),
        new(".md", "text/markdown", IsDefault: false, IsCopy: true),
        new(".txt", "text/plain", IsDefault: false, IsCopy: true),
        new(".html", "text/html", IsDefault: false, IsCopy: true),
        new(".htm", "text/html", IsDefault: false, IsCopy: true),
        new(".tex", "application/x-tex", IsDefault: false, IsCopy: false),
        new(".pdf", "application/pdf", IsDefault: false, IsCopy: true, Opens: false),
        new(".png", "image/png", IsDefault: false, IsCopy: true, Opens: false),
    ]);
}
