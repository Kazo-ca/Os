> [Version française](../../../../fr/wasm/dev/adr/0287-drop-files-into-an-app.md)

# ADR 0287: drop files from the File Explorer into an app

## Status

Accepted on 2026-11-20: Benoit answered Q3 = A (the app may also write the
computer's files into the file system, like the File Explorer), Q4 = B (dragging
from an app is part of this ADR) and Q5 = A; Q1 and Q2 left blank, so the
recommendation (A). Series `todo-wins/base`, for issue
[#380](https://github.com/Kazo-ca/Os/issues/380) (`TODO`).

- **Completes:** [ADR 0058](0058-base-app-features.md) §1 (drag and drop of the
  File Explorer between its windows) and [ADR 0160](0160-desktop-implementation-adjustments.md)
  §3 (drag from the desktop).
- **Does not change:** the file rights model of
  [ADR 0231](0231-network-drives-count-as-personal-files.md) (capability by
  path), if question 1 keeps the recommendation.

## Context

- The File Explorer (`FileView`) and the desktop (`FileViewDesktop`) write what
  is dragged into the browser's `DataTransfer` under the type
  `application/vnd.kazoos.file-drag+json`. The content is `FileViewDragEnvelope`,
  internal to `Kazo.Os.Apps.FileExplorer`: `{"files":[…],"folders":[…]}`, with
  the SDK's `VirtualFileDragPayload` and `VirtualFolderDragPayload`
  (`sourceAppId`, path, name, size, MIME type or item count).
- An installed app cannot receive such a drop properly:
  - the MIME type and the envelope are written nowhere in the SDK;
  - Blazor does not give the `DataTransfer`'s `getData` in C#: `FileView` reads
    the drop in its own JavaScript (`FileView.razor.js`, a capture-phase `drop`
    listener), which an app cannot reuse;
  - nothing says which rights the app has on the dropped files.
- Benoit (2026-11-06, Webamp design, question 15): no drag and drop in the first
  version of Webamp; this issue keeps the public contract for later (drop tracks
  into the playlist). Any editor that opens files benefits too.
- In a real OS, dropping a file on a window gives its path to the app
  (`WM_DROPFILES` / `IDropTarget` on Windows, `text/uri-list` on X11 and Wayland,
  the drag pasteboard on macOS). The app then reads the file with its own rights;
  only the macOS sandbox also grants a right on the dropped file.

## Decision

### 1. The format becomes public

- `Kazo.Os.AppSdk.DragDrop.KazoFileDrag` carries the MIME type
  (`KazoFileDrag.MimeType = "application/vnd.kazoos.file-drag+json"`) and reads
  or writes the envelope `KazoFileDragData(Files, Folders)`, serialized with a
  generated JSON context (trim-safe), camelCase names: **the same JSON** as
  today.
- `KazoFileDrag.TryParse(string? json, out KazoFileDragData data)` refuses,
  without throwing, invalid JSON, a path that is not absolute, or more than
  1,000 items.
- The File Explorer and the desktop use this public type instead of
  `FileViewDragEnvelope`, which goes away. A drag between two explorers does
  not change.
- An app written in JavaScript (Webamp) can read the same MIME type in its own
  `drop` and pass the text to `KazoFileDrag.TryParse`.

### 2. A drop zone for Blazor apps

`Kazo.Os.AppSdk.Blazor` gains the `KazoFileDropZone` component:

```razor
<KazoFileDropZone Accept="@(["audio/*", ".m3u"])" FilesDropped="OnFilesDropped">
    <PlaylistView />
</KazoFileDropZone>
```

- Its JavaScript (`KazoFileDropZone.razor.js`, loaded by the component) listens
  to `dragover`, `dragleave` and `drop` on its element, reads the MIME type of
  §1 and, depending on question 3, the computer's files.
- `Accept` (optional): MIME types (`audio/*`) or extensions (`.m3u`). While
  hovering, the zone says `copy` if at least one item matches, `none`
  otherwise; on drop, the files that do not match are removed. A folder always
  matches (the app decides whether to look into it).
- `FilesDropped` receives `KazoFilesDroppedEventArgs`: `Files`
  (`KazoDroppedFile`: path, name, size, MIME type, source app), `Folders`
  (`KazoDroppedFolder`: path, name) and, depending on question 3, `HostFiles`.
- While hovering, the element gets the `kazo-file-drop-over` class, which the
  app styles in its CSS.
- A drop into an app is **always a copy**: the zone sets `dropEffect = 'copy'`
  and the File Explorer removes nothing from the source.

### 3. Files from the computer (question 3: A)

The zone also receives the files dragged from the real computer, under
`HostFiles` (`KazoDroppedHostFile`: name, size, MIME type), like a drop into the
File Explorer today. The zone writes nothing by itself; the app chooses:

- `OpenReadStream(maxBytes)`: it reads the content (a track played, a document
  opened without saving it);
- `SaveToAsync(string path, bool overwrite = false)`: it writes it into the
  Kazo OS file system, as the File Explorer does with a drop. The path goes
  through the app's file gateway, with its capabilities
  (`filesystem.user-home.write` for `/home/<user>`); a name already taken gives
  `IOException` unless `overwrite`.

The files are released when the `FilesDropped` handler ends: the app reads or
saves them before returning.

### 4. Rights on the dropped files (question 1)

Recommendation A: **no new right**. The app receives paths and reads them with
its capabilities, as in a real desktop OS (ADR 0231): an app with
`filesystem.user-home.read` reads what is dropped from `/home/<user>` or from a
network drive of the user; without it, the read answers "Permission denied". An
app that places a drop zone has nothing more to declare.

### 5. Dragging from an app (question 4: B)

`Kazo.Os.AppSdk.Blazor` also gains the `KazoFileDragSource` component:

```razor
<KazoFileDragSource Files="@SelectedTracks">
    <TrackRow />
</KazoFileDragSource>
```

- `Files` and `Folders`: the Kazo OS paths the element drags. The component
  prepares the JSON of §1 at each render: the browser asks for the data
  **during** `dragstart`, with no wait possible.
- Its JavaScript sets the `draggable` attribute, writes the MIME type of §1 on
  `dragstart` and sets `effectAllowed = 'copy'`.
- The component fills in sizes, names and MIME types through the app's file
  gateway, with its capabilities: a path the app cannot read is not put in the
  drag.
- The File Explorer and the desktop receive this drag like one from another
  explorer, but **always copy** (an app's `effectAllowed` does not allow a
  move): an app does not empty a user's folder through a drag.
- An app written in JavaScript calls `KazoFileDrag.Serialize(data)` and writes
  the text itself in its `dragstart`.

### 6. Outside this ADR (question 5: A)

Each becomes a `TODO` issue:

- dropping into a Craft app (its page is in an iframe, the drop must go through
  `craft-proxy.js`, ADR 0259) and dragging from it;
- dropping into an app rendered from a Web Worker and dragging from it.

## Questions asked to Benoit

Benoit's answers (2026-11-20): "9. A mais l'application peut écrire dans le
système de fichier (Comme l'explorateur de fichier)" (A, but the app can write
into the file system, like the File Explorer), "10. b", "11. A" (questions 3, 4
and 5 of this ADR); questions 1 and 2 left blank, so A.

1. Which rights does an app have on the files dropped into it?
   - A) No new right: it reads the paths with its capabilities
     (`filesystem.user-home.read` for the home folder and the network drives).
     Simple, like Windows and Linux. ✨(Recommended, chosen)
   - B) The drop grants a read right on those files only
     (`filesystem.user-selected.read`, until the process ends): an app without
     `filesystem.user-home.read` can read them, like the macOS sandbox. More
     code in the file gateway, and a dropped folder raises the question of its
     content.
   - C) A now, B in a later ADR together with the file picker (same right for a
     file chosen in the dialog).
2. Which shape for the API?
   - A) Both: the public format in `Kazo.Os.AppSdk` (`KazoFileDrag`) and the
     `KazoFileDropZone` component in `Kazo.Os.AppSdk.Blazor`. ✨(Recommended, chosen)
   - B) The format only: each app writes its own drop JavaScript.
   - C) The component only: the format stays internal.
3. Files dragged from the real computer?
   - A) The zone gives them too (`HostFiles`, read as streams, nothing is
     written into the file system). ✨(Recommended, chosen, with writing by the
     app: §3)
   - B) Kazo OS files only; a file from the computer is refused while hovering.
4. Dragging **from** an app to the File Explorer or the desktop?
   - A) Not in this ADR: a `TODO` issue. ✨(Recommended)
   - B) In this ADR (`KazoFileDragSource`, §5). **(Chosen)**
5. Craft apps (iframe) and apps rendered from a Web Worker?
   - A) Not in this ADR: one `TODO` issue for each. ✨(Recommended, chosen)
   - B) Craft apps in this ADR (relay through `craft-proxy.js`).

## Consequences

- Webamp and any editor can receive files from the File Explorer and the
  desktop with one component, and give files with another; an issue follows in
  [Os-App-Webamp](https://github.com/Kazo-ca/Os-App-Webamp) once an OS version
  carries the API.
- New public API (`PublicAPI.Unshipped.txt` of `Kazo.Os.AppSdk` and
  `Kazo.Os.AppSdk.Blazor`); an app that uses it requires the SDK version that
  carries it.
- The drag JSON does not change: an explorer of an earlier version and an app of
  this version understand each other.

## Tasks

- [x] `KazoFileDrag`, `KazoFileDragData`, JSON context, parsing tests (valid,
  invalid, relative path, too many items).
- [x] The File Explorer and the desktop move to `KazoFileDragData`; the
  `FileViewDragDropTests` stay green.
- [x] `KazoFileDropZone` (component, JS, CSS), `Accept`, computer files
  (`OpenReadStream`, `SaveToAsync`); bUnit tests.
- [x] `KazoFileDragSource`, `KazoFileDrag.Serialize`; the File Explorer and the
  desktop copy an app's drag; tests.
- [ ] A Playwright test: drag from the File Explorer into a test app, and from
  the app to the File Explorer.
- [x] `framework/` docs (fr and en), `features.md`, change entry.
- [ ] `TODO` issues of §6 ([#634](https://github.com/Kazo-ca/Os/issues/634),
  [#635](https://github.com/Kazo-ca/Os/issues/635)), issue in Os-App-Webamp.

## Implementation notes

These notes give the shape of the code without changing the decisions:

- The drop arguments reuse the SDK's types: `Files` is a list of
  `VirtualFileDragPayload`, `Folders` of `VirtualFolderDragPayload` and
  `HostFiles` of `KazoHostFile` (name, size, MIME type,
  `OpenReadStreamAsync(maxBytes, ct)`), instead of new `KazoDropped*` types.
- Writing a computer file into Kazo OS (§3) goes through
  `KazoFileOperations.ImportAsync(drop.HostFiles, folder)`, the one the File
  Explorer uses (copy suffix, size cap, the app's rights), instead of a
  per-file `SaveToAsync`.
- `Accept` applies at drop: while hovering, the browser does not give the
  drag's content, so the zone says `copy` as soon as a drag carries files.
- `KazoFileDrag.CreateAsync(files, folders)` prepares the drag of
  `KazoFileDragSource`; an app written in JavaScript can call it too.
- Guide: [`framework/file-drag-drop-api.md`](../../framework/file-drag-drop-api.md).
