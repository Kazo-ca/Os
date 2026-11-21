using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.DragDrop;

/// <summary>
/// The files and folders of a Kazo OS drag (ADR 0287 §1), as the File Explorer, the desktop and
/// <c>KazoFileDragSource</c> write them under <see cref="KazoFileDrag.MimeType"/>.
/// </summary>
/// <param name="Files">The dragged files.</param>
/// <param name="Folders">The dragged folders.</param>
public sealed record KazoFileDragData(
    IReadOnlyList<VirtualFileDragPayload> Files,
    IReadOnlyList<VirtualFolderDragPayload> Folders)
{
    /// <summary>Gets a drag with nothing in it.</summary>
    public static KazoFileDragData Empty { get; } = new([], []);

    /// <summary>Gets whether the drag holds no file and no folder.</summary>
    public bool IsEmpty => Files.Count == 0 && Folders.Count == 0;
}

/// <summary>
/// The public format of a Kazo OS file drag (ADR 0287 §1): the <c>DataTransfer</c> type and its
/// JSON, the same one the File Explorer has always written, so an app can receive files dragged
/// from the File Explorer or the desktop, and give files to them.
/// </summary>
/// <remarks>
/// A Blazor app uses the <c>KazoFileDropZone</c> and <c>KazoFileDragSource</c> components of
/// <c>Kazo.Os.AppSdk.Blazor</c>; an app that handles the browser's <c>drop</c> or <c>dragstart</c>
/// in its own JavaScript reads or writes <see cref="MimeType"/> and goes through
/// <see cref="TryParse"/> and <see cref="Serialize"/>. A drop gives paths, never rights: the app
/// reads them with its own capabilities (ADR 0287 §4).
/// </remarks>
public static class KazoFileDrag
{
    /// <summary>The <c>DataTransfer</c> type of a Kazo OS file drag.</summary>
    public const string MimeType = "application/vnd.kazoos.file-drag+json";

    /// <summary>The most files and folders one drag may carry; a larger drag is refused as a whole.</summary>
    public const int MaxItems = 1000;

    /// <summary>
    /// Reads the JSON of a drag. Never throws: invalid JSON, a path that is not absolute, an empty
    /// name or more than <see cref="MaxItems"/> items give <see langword="false"/>.
    /// </summary>
    /// <param name="json">The text read from the <c>DataTransfer</c> under <see cref="MimeType"/>.</param>
    /// <param name="data">The drag, or <see cref="KazoFileDragData.Empty"/> when refused.</param>
    /// <returns>Whether the text is a valid drag.</returns>
    public static bool TryParse(string? json, out KazoFileDragData data)
    {
        data = KazoFileDragData.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        WireEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(json, KazoFileDragJsonContext.Default.WireEnvelope);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null)
        {
            return false;
        }

        List<VirtualFileDragPayload> files = envelope.Files ?? [];
        List<VirtualFolderDragPayload> folders = envelope.Folders ?? [];
        if (files.Count + folders.Count > MaxItems
            || files.Exists(file => file is null || !IsValid(file.FilePath, file.FileName))
            || folders.Exists(folder => folder is null || !IsValid(folder.FolderPath, folder.FolderName)))
        {
            return false;
        }

        data = new KazoFileDragData(files, folders);
        return true;
    }

    /// <summary>Writes the JSON of a drag, to put in the <c>DataTransfer</c> under <see cref="MimeType"/>.</summary>
    /// <param name="data">The drag.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(KazoFileDragData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(
            new WireEnvelope([.. data.Files], [.. data.Folders]), KazoFileDragJsonContext.Default.WireEnvelope);
    }

    /// <summary>
    /// Prepares the drag of Kazo OS paths (ADR 0287 §5): reads the size, name and MIME type of each one
    /// through the app's file gateway, with its capabilities. A path the app cannot read, a missing
    /// one or a path that is not valid is left out of the drag; nothing throws for it.
    /// </summary>
    /// <param name="files">Paths of the files to drag, resolved against the ambient context.</param>
    /// <param name="folders">Paths of the folders to drag.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The drag, with the ambient app as the source; at most <see cref="MaxItems"/> items.</returns>
    public static async Task<KazoFileDragData> CreateAsync(
        IEnumerable<string>? files, IEnumerable<string>? folders, CancellationToken cancellationToken = default)
    {
        string sourceAppId = KazoExecutionContext.CurrentOrNull?.Manifest.Id ?? string.Empty;
        List<VirtualFileDragPayload> dragFiles = [];
        List<VirtualFolderDragPayload> dragFolders = [];
        foreach (string path in files ?? [])
        {
            if (dragFiles.Count >= MaxItems)
            {
                break;
            }

            if (await TryGetAsync(() => KazoFileInfo.GetAsync(path, cancellationToken)).ConfigureAwait(false) is { Exists: true } file)
            {
                dragFiles.Add(new VirtualFileDragPayload(sourceAppId, file.FullName, file.Name, file.Length, file.MediaType));
            }
        }

        foreach (string path in folders ?? [])
        {
            if (dragFiles.Count + dragFolders.Count >= MaxItems)
            {
                break;
            }

            if (await TryGetAsync(() => KazoDirectoryInfo.GetAsync(path, cancellationToken)).ConfigureAwait(false) is { Exists: true } folder
                && folder.FullName != "/")
            {
                // As the File Explorer writes it: the count is decorative, no drop reads it.
                dragFolders.Add(new VirtualFolderDragPayload(sourceAppId, folder.FullName, folder.Name, ItemCount: -1));
            }
        }

        return new KazoFileDragData(dragFiles, dragFolders);
    }

    /// <summary>Reads one entry, or <see langword="null"/> when the path is invalid or the app cannot read it.</summary>
    private static async Task<T?> TryGetAsync<T>(Func<ValueTask<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Left out of the drag (ADR 0287 §5): an app gives only what it can read itself.
            return null;
        }
    }

    private static bool IsValid(string? path, string? name) =>
        !string.IsNullOrWhiteSpace(path) && path[0] == '/' && !string.IsNullOrWhiteSpace(name);

    /// <summary>The JSON shape on the wire: <c>{"files":[…],"folders":[…]}</c>; a missing list reads as empty.</summary>
    internal sealed record WireEnvelope(List<VirtualFileDragPayload>? Files, List<VirtualFolderDragPayload>? Folders);
}

/// <summary>Source-generated (trim-safe) serializer of <see cref="KazoFileDrag"/>'s JSON, camelCase as the File Explorer always wrote it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KazoFileDrag.WireEnvelope))]
internal sealed partial class KazoFileDragJsonContext : JsonSerializerContext;
