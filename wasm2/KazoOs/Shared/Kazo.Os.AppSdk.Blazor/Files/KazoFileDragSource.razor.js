// Drag source of an app (ADR 0287 §5). The browser asks for the data during dragstart, with no wait
// possible, so the component gives the drag's JSON (KazoFileDrag.MimeType) ahead, at each render,
// and this listener only copies it into the DataTransfer.

// Installs the dragstart listener on `element`.
export function attach(element, mimeType) {
    if (!element || element.__kazoFileDragSource) {
        return;
    }

    const onDragStart = e => {
        const json = element.__kazoFileDragJson;
        if (!json || !e.dataTransfer) {
            // Nothing the app can read: no drag at all rather than an empty one.
            e.preventDefault();
            return;
        }

        // An inner source (a row inside a list that drags) wins over an outer one.
        e.stopPropagation();
        e.dataTransfer.setData(mimeType, json);
        // A drag from an app never moves a user's files (ADR 0287 §5).
        e.dataTransfer.effectAllowed = 'copy';
    };

    element.addEventListener('dragstart', onDragStart);
    element.__kazoFileDragSource = onDragStart;
}

// Sets the drag's JSON, or null when there is nothing to drag.
export function setDrag(element, json) {
    if (element) {
        element.__kazoFileDragJson = json || null;
    }
}

// Removes the listener installed by attach.
export function detach(element) {
    if (element?.__kazoFileDragSource) {
        element.removeEventListener('dragstart', element.__kazoFileDragSource);
        delete element.__kazoFileDragSource;
        delete element.__kazoFileDragJson;
    }
}
