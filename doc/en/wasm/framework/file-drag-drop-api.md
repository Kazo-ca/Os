> [Version française](../../../fr/wasm/framework/file-drag-drop-api.md)

# Drag and drop files: `KazoFileDropZone`, `KazoFileDragSource`, `KazoFileDrag`

An app can receive files dragged from the File Explorer, the desktop, another app or the real
computer, and give its own files to the File Explorer and the desktop
([ADR 0287](../dev/adr/0287-drop-files-into-an-app.md)). A drop gives **paths**, never rights: the
app reads the files with its own capabilities, as on Windows or Linux. A drop into an app is
**always a copy**: the source keeps its files.

## Receiving files (`KazoFileDropZone`)

```razor
@using Kazo.Os.AppSdk.Blazor.Files

<KazoFileDropZone Accept="@(["audio/*", ".m3u"])" FilesDropped="OnFilesDroppedAsync">
    <PlaylistView />
</KazoFileDropZone>

@code {
    private async Task OnFilesDroppedAsync(KazoFilesDroppedEventArgs drop)
    {
        foreach (VirtualFileDragPayload file in drop.Files)
        {
            await using KazoFileStream stream = await (await KazoFileInfo.GetAsync(file.FilePath)).OpenReadAsync();
            // … read the track
        }

        // Files from the computer: read them, or write them into the Kazo OS file system.
        await KazoFileOperations.ImportAsync(drop.HostFiles, "/home/benoit/Music");
    }
}
```

| Parameter | Role |
|---|---|
| `Accept` | MIME types (`audio/mpeg`, `audio/*`) or extensions (`.m3u`). On drop, the files that match none are removed; a folder always matches. Empty or absent: any file. |
| `FilesDropped` | Receives `KazoFilesDroppedEventArgs` when something is left after `Accept`. |
| `Class`, other attributes | Passed to the zone's element. |

`KazoFilesDroppedEventArgs`:

- `Files`: the Kazo OS files (`VirtualFileDragPayload`: path, name, size, MIME type, source app);
- `Folders`: the folders (`VirtualFolderDragPayload`); the app decides whether to look into them;
- `HostFiles`: the computer's files (`KazoHostFile`: name, size, MIME type, `OpenReadStreamAsync(maxBytes, ct)`).
  To write them into Kazo OS like the File Explorer, `KazoFileOperations.ImportAsync(drop.HostFiles, folder)`
  ([`file-operations-api.md`](file-operations-api.md)): copy suffix for a name already taken, the app's
  rights (`filesystem.user-home.write` for `/home/<user>`).

The computer's files are released when the `FilesDropped` handler ends: the app reads or saves them
before returning.

While hovering, the element has the `kazo-file-drop-over` class (a dashed outline in the accent color
by default), which the app may restyle in its CSS.

## Giving files (`KazoFileDragSource`)

```razor
<KazoFileDragSource Files="@SelectedTrackPaths">
    <TrackRow />
</KazoFileDragSource>
```

- `Files` and `Folders`: the Kazo OS paths the element drags.
- The browser asks for the data during `dragstart`, with no wait possible: the component prepares the
  drag whenever `Files` or `Folders` change (`KazoFileDrag.CreateAsync`). Sizes, names and types come
  from the app's file gateway, with its capabilities: a path the app cannot read is left out of the
  drag. With nothing readable, the element is not draggable.
- `Drag` gives the prepared drag.
- The File Explorer, the desktop and another `KazoFileDropZone` receive this drag like one from another
  explorer, but **always copy** (`effectAllowed = 'copy'`): an app does not empty a user's folder
  through a drag.

## The format (`KazoFileDrag`, app written in JavaScript)

The `DataTransfer` type is `KazoFileDrag.MimeType` (`application/vnd.kazoos.file-drag+json`); the JSON is
the one the File Explorer has always written:

```json
{"files":[{"sourceAppId":"…","filePath":"/home/benoit/a.mp3","fileName":"a.mp3","sizeBytes":12,"mimeType":"audio/mpeg"}],
 "folders":[{"sourceAppId":"…","folderPath":"/home/benoit/Music","folderName":"Music","itemCount":-1}]}
```

- `KazoFileDrag.TryParse(json, out KazoFileDragData data)` reads that text and never throws: invalid
  JSON, a path that is not absolute, an empty name or more than `KazoFileDrag.MaxItems` (1,000) items
  give `false`.
- `KazoFileDrag.Serialize(data)` writes the text, to put in the `DataTransfer` during `dragstart`.
- `KazoFileDrag.CreateAsync(files, folders)` prepares a drag from paths, as `KazoFileDragSource` does.

An app that handles `drop` itself reads `event.dataTransfer.getData('application/vnd.kazoos.file-drag+json')`
and passes the text to `TryParse`.

## Outside this document

Dropping into a Craft app (iframe) and into an app rendered from a Web Worker are tracked by
[#634](https://github.com/Kazo-ca/Os/issues/634) and [#635](https://github.com/Kazo-ca/Os/issues/635).

## Tasks

See the task list of [ADR 0287](../dev/adr/0287-drop-files-into-an-app.md#tasks).
