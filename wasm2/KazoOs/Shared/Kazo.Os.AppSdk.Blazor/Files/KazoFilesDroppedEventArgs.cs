using Kazo.Os.AppSdk.DragDrop;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// What was dropped on a <see cref="KazoFileDropZone"/> (ADR 0287 §2 and §3): Kazo OS files and folders
/// (paths, never rights: the app reads them with its own capabilities) and files dragged from the
/// real computer.
/// </summary>
/// <remarks>
/// The computer's files are released when the <see cref="KazoFileDropZone.FilesDropped"/> handler
/// ends: read them (<see cref="KazoHostFile.OpenReadStreamAsync"/>) or write them into the Kazo OS
/// file system (<see cref="KazoFileOperations.ImportAsync"/>, as the File Explorer does) before
/// returning.
/// </remarks>
public sealed class KazoFilesDroppedEventArgs : EventArgs
{
    /// <summary>Creates the arguments of a drop.</summary>
    /// <param name="files">The dropped Kazo OS files.</param>
    /// <param name="folders">The dropped Kazo OS folders.</param>
    /// <param name="hostFiles">The files dragged from the real computer.</param>
    public KazoFilesDroppedEventArgs(
        IReadOnlyList<VirtualFileDragPayload> files,
        IReadOnlyList<VirtualFolderDragPayload> folders,
        IReadOnlyList<KazoHostFile> hostFiles)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(hostFiles);
        Files = files;
        Folders = folders;
        HostFiles = hostFiles;
    }

    /// <summary>Gets the dropped Kazo OS files (path, name, size, MIME type, source app).</summary>
    public IReadOnlyList<VirtualFileDragPayload> Files { get; }

    /// <summary>Gets the dropped Kazo OS folders; the app decides whether to look into them.</summary>
    public IReadOnlyList<VirtualFolderDragPayload> Folders { get; }

    /// <summary>Gets the files dragged from the real computer, readable until the handler returns.</summary>
    public IReadOnlyList<KazoHostFile> HostFiles { get; }

    /// <summary>Gets whether nothing is left once <see cref="KazoFileDropZone.Accept"/> is applied.</summary>
    public bool IsEmpty => Files.Count == 0 && Folders.Count == 0 && HostFiles.Count == 0;
}
