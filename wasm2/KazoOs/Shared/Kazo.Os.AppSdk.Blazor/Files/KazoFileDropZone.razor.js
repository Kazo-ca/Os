// Drop zone of an app (ADR 0287 §2 and §3). Blazor gives no DataTransfer.getData in C#, so this
// module reads the drop: the Kazo OS files and folders (KazoFileDrag.MimeType, the JSON written by the
// File Explorer, the desktop and KazoFileDragSource) and the files dragged from the real computer.
// The computer's File objects stay here, under a token, until the component releases them: .NET reads
// their content only when the app asks for it (an IJSStreamReference).

const overClass = 'kazo-file-drop-over';
const hostFileStore = new Map();
let nextToken = 1;

// Whether the drag can bring something to the zone: Kazo OS files or computer files.
function carriesFiles(transfer, mimeType) {
    const types = Array.from(transfer?.types || []);
    return types.includes(mimeType) || types.includes('Files');
}

// Listens to the drag events of `element` and hands each drop to `component.OnDropAsync(json, token,
// filesJson)`: the Kazo OS drag text (or null), the token of the computer's files and their
// `[{ name, size, type }]`.
export function attach(element, component, mimeType) {
    if (!element || element.__kazoFileDropZone) {
        return;
    }

    // dragenter and dragleave fire for each child: count them so the class stays while inside.
    let depth = 0;
    const clear = () => {
        depth = 0;
        element.classList.remove(overClass);
    };

    const onDragEnter = e => {
        if (!carriesFiles(e.dataTransfer, mimeType)) {
            return;
        }

        depth++;
        element.classList.add(overClass);
    };

    const onDragOver = e => {
        const transfer = e.dataTransfer;
        if (!carriesFiles(transfer, mimeType)) {
            return;
        }

        // A drop into an app is always a copy (ADR 0287 §2): the source keeps its files.
        e.preventDefault();
        transfer.dropEffect = 'copy';
    };

    const onDragLeave = e => {
        if (depth > 0 && --depth === 0) {
            element.classList.remove(overClass);
        }
    };

    const onDrop = e => {
        const transfer = e.dataTransfer;
        if (!carriesFiles(transfer, mimeType)) {
            return;
        }

        e.preventDefault();
        // An outer zone (the File Explorer behind the app, the desktop) must not take it too.
        e.stopPropagation();
        clear();

        const json = transfer.getData(mimeType) || null;
        // A drag from the File Explorer also lists no computer file; keep only real ones.
        const files = json ? [] : Array.from(transfer.files || []);
        let token = 0;
        if (files.length > 0) {
            token = nextToken++;
            hostFileStore.set(token, files);
        }

        // As JSON text: .NET reads it with a generated (trim-safe) serializer.
        const metas = JSON.stringify(files.map(f => ({ name: f.name, size: f.size, type: f.type || '' })));
        // The app reads or saves the computer's files before its handler returns (ADR 0287 §3).
        component.invokeMethodAsync('OnDropAsync', json, token, metas)
            .finally(() => hostFileStore.delete(token));
    };

    element.addEventListener('dragenter', onDragEnter);
    element.addEventListener('dragover', onDragOver);
    element.addEventListener('dragleave', onDragLeave);
    element.addEventListener('drop', onDrop);
    element.__kazoFileDropZone = { onDragEnter, onDragOver, onDragLeave, onDrop };
}

// Removes the listeners installed by attach.
export function detach(element) {
    const handlers = element?.__kazoFileDropZone;
    if (!handlers) {
        return;
    }

    element.removeEventListener('dragenter', handlers.onDragEnter);
    element.removeEventListener('dragover', handlers.onDragOver);
    element.removeEventListener('dragleave', handlers.onDragLeave);
    element.removeEventListener('drop', handlers.onDrop);
    element.classList.remove(overClass);
    delete element.__kazoFileDropZone;
}

// The File (a Blob) of a dropped computer file; it comes back to .NET as an IJSStreamReference.
export function openFile(token, index) {
    const files = hostFileStore.get(token);
    if (!files || index < 0 || index >= files.length) {
        throw new Error('The dropped file is no longer available.');
    }

    return files[index];
}
