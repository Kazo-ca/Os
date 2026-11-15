using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// The formats one Craft app opens and writes, and what a file name says about them; with the path
/// helpers every Craft app window needs (OS paths always use <c>/</c>).
/// </summary>
/// <remarks>
/// The Craft app decides the format of every file it saves: the name it hands over carries the
/// extension. The window only needs the extension to tell a document (bound to its file) from a
/// copy (an export that leaves the binding alone), and to build the OS dialogs' filters and the
/// manifest's file handlers.
/// </remarks>
public sealed class CraftDocumentFormats
{
    /// <summary>Creates the list of an app's formats.</summary>
    /// <param name="formats">Every format the app knows, its own documents first.</param>
    /// <exception cref="ArgumentException">An extension does not start with a dot, or comes twice.</exception>
    public CraftDocumentFormats(IEnumerable<CraftDocumentFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        List<CraftDocumentFormat> list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CraftDocumentFormat format in formats)
        {
            ArgumentNullException.ThrowIfNull(format, nameof(formats));
            string extension = format.Extension?.ToLowerInvariant() ?? string.Empty;
            if (extension.Length < 2 || extension[0] != '.')
            {
                throw new ArgumentException($"'{format.Extension}' is not an extension with its dot.", nameof(formats));
            }

            if (!seen.Add(extension))
            {
                throw new ArgumentException($"'{extension}' is listed twice.", nameof(formats));
            }

            list.Add(format with { Extension = extension });
        }

        All = list;
        OpenExtensions = [.. list.Where(format => format.Opens).Select(format => format.Extension)];
        FileHandlers =
        [
            .. list
                .Where(format => format.Opens)
                .GroupBy(format => format.MediaType, StringComparer.Ordinal)
                .Select(group => new FileHandler(
                    group.Key,
                    [.. group.Select(format => format.Extension)],
                    group.First().IsDefault ? ["open", "edit"] : ["open"])),
        ];
    }

    /// <summary>Gets every known format, in the given order, extensions lowercase.</summary>
    public IReadOnlyList<CraftDocumentFormat> All { get; }

    /// <summary>Gets the extensions the app opens, for the Open dialog's filter.</summary>
    public IReadOnlyList<string> OpenExtensions { get; }

    /// <summary>
    /// Gets the manifest's file handlers: one per media type, <c>open</c> and <c>edit</c> for the
    /// app's own documents, <c>open</c> alone for the formats offered in "Open with" only.
    /// </summary>
    public IReadOnlyList<FileHandler> FileHandlers { get; }

    /// <summary>Finds the format of a file name, or <see langword="null"/> for an unknown extension.</summary>
    /// <param name="fileName">A file name or a path.</param>
    /// <returns>The format.</returns>
    public CraftDocumentFormat? Find(string fileName)
    {
        string extension = ExtensionOf(fileName);
        return All.FirstOrDefault(format => format.Extension == extension);
    }

    /// <summary>Gets whether the app writes this file as a copy (an export).</summary>
    /// <param name="fileName">The name the app gave the file.</param>
    /// <returns><see langword="true"/> for a copy.</returns>
    public bool IsCopy(string fileName) => Find(fileName)?.IsCopy == true;

    /// <summary>Gets the extension of a file name or path, lowercase with its dot; empty when it has none.</summary>
    /// <param name="fileName">A file name or a path.</param>
    /// <returns>The extension, such as <c>.docx</c>.</returns>
    public static string ExtensionOf(string fileName)
    {
        string name = FileNameOf(fileName);
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>Gets the last segment of a path (the whole text when it has no <c>/</c>).</summary>
    /// <param name="path">A path or a file name.</param>
    /// <returns>The file name.</returns>
    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Gets the folder of an absolute path, or <see langword="null"/> for a bare name.</summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>The folder, <c>/</c> for a file at the root.</returns>
    public static string? FolderOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.LastIndexOf('/');
        return slash switch
        {
            < 0 => null,
            0 => "/",
            _ => path[..slash],
        };
    }

    /// <summary>
    /// Keeps the extension the app chose: the bytes are in that format, so a path picked in the save
    /// dialog with another extension (or none) gets it appended.
    /// </summary>
    /// <param name="chosenPath">The path the user picked.</param>
    /// <param name="name">The name the app gave the file.</param>
    /// <returns>The path to write.</returns>
    public static string WithExtensionOf(string chosenPath, string name)
    {
        ArgumentNullException.ThrowIfNull(chosenPath);
        string wanted = ExtensionOf(name);
        return wanted.Length == 0 || ExtensionOf(chosenPath) == wanted ? chosenPath : chosenPath + wanted;
    }
}
