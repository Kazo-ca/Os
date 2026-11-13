// The file picker of a Craft app's iframe (ADR 0259, module Kazo.Os.AppSdk.Craft; taken from Kazo
// Word). A Craft app asks the browser for files through the `rfd` crate, which builds a
// `<div id="rfd-overlay">` holding an `<input id="rfd-input" type="file">`, listens to its `change`
// and `cancel` events, and calls `input.click()` when the page has a user activation. This module
// stops that picker before the browser shows it, sends `pick` to the window, and hands the files
// the window answers with (`picked`) to the input, as if the user had chosen them.
//
// The same overlay holds `<a id="rfd-output" download>` when the Craft app would make the browser
// download a file (a save outside page host mode). It is sent to the window as a `save` with
// `saveAs`, so no browser download ever starts either.
//
// It decides nothing: which dialog to show, and what to do with a document, is the window's C#.

/** Id of rfd's overlay, its file input and its download link (rfd 0.17.2, src/backend/wasm.rs). */
export const RfdIds = Object.freeze({ overlay: "rfd-overlay", input: "rfd-input", output: "rfd-output" });

/**
 * Hands `files` to a file input and tells its listeners, as the browser does when the user picks
 * them. No file (cancelled) still fires `change`: rfd then reads an empty list and reports
 * "nothing chosen". Its `cancel` path is not used: it removes the overlay itself, and a Craft app
 * built with rfd 0.17 may panic there ("removeChild: not a child") and stop for good.
 * @param {HTMLInputElement} input The input rfd waits on.
 * @param {File[]} files The chosen files, none when cancelled.
 * @param {() => DataTransfer} createTransfer Builds an empty DataTransfer (the page's own).
 */
export function deliverFiles(input, files, createTransfer) {
    const transfer = createTransfer();
    for (const file of files) {
        transfer.items.add(file);
    }

    input.files = transfer.files;
    input.dispatchEvent(new Event("change"));
}

/**
 * Starts intercepting the Craft app's file picker in a page.
 * @param {object} options
 * @param {Window} options.page The iframe page's window (its own HTMLInputElement, File, DataTransfer).
 * @param {(message: object, transfer?: Transferable[]) => void} options.send Sends a message to the window.
 * @returns {{ answer(message: { id: number, files?: { name: string, type?: string, bytes: ArrayBuffer }[], cancelled?: boolean }): boolean, stop(): void }}
 *          `answer` takes the window's `picked` and reports whether a pick was waiting for it.
 */
export function interceptFilePicker({ page, send }) {
    const doc = page.document;
    const pending = new Map();
    const claimed = new WeakSet();
    let nextId = 1;

    // One pick per input: rfd may click an input the observer already claimed, or the other way
    // round, depending on the user activation.
    const claim = (input) => {
        if (claimed.has(input)) {
            return;
        }

        claimed.add(input);
        input.closest?.(`#${RfdIds.overlay}`)?.classList.add("hidden");
        const id = nextId++;
        pending.set(id, input);
        send({ type: "pick", id, accept: String(input.accept ?? ""), multiple: input.multiple === true });
    };

    // A download the Craft app hands to the browser: its bytes go to the window as "save as", then
    // rfd's Ok button closes the overlay as if the user had clicked the link.
    const forwardDownload = async (overlay, link) => {
        try {
            const response = await page.fetch(link.href);
            const bytes = await response.arrayBuffer();
            send({ type: "save", name: String(link.download ?? ""), saveAs: true, bytes }, [bytes]);
        } finally {
            overlay.querySelector(".rfd-button")?.click();
        }
    };

    // rfd calls click() on the input right after adding the overlay, before any observer runs: the
    // call itself is caught. Only this page's prototype is patched, and any other input keeps the
    // browser's behaviour.
    const prototype = page.HTMLInputElement.prototype;
    const nativeClick = prototype.click;
    prototype.click = function click() {
        if (this.type === "file") {
            claim(this);
            return;
        }

        nativeClick.call(this);
    };

    // A click by the user on a file input (rfd's card) is stopped the same way.
    const onClick = (event) => {
        if (event.target instanceof page.HTMLInputElement && event.target.type === "file") {
            event.preventDefault();
            claim(event.target);
        }
    };
    doc.addEventListener("click", onClick, true);

    // Without a user activation rfd shows its card and waits: the overlay is claimed as it is added.
    const observer = new page.MutationObserver((records) => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.id !== RfdIds.overlay) {
                    continue;
                }

                const input = node.querySelector(`#${RfdIds.input}`);
                const link = node.querySelector(`#${RfdIds.output}`);
                if (input) {
                    claim(input);
                } else if (link) {
                    node.classList.add("hidden");
                    forwardDownload(node, link);
                }
            }
        }
    });
    observer.observe(doc.body, { childList: true });

    return {
        answer(message) {
            const input = pending.get(message.id);
            if (!input) {
                return false;
            }

            pending.delete(message.id);
            const files = message.cancelled === true
                ? []
                : (message.files ?? []).map((file) => new page.File([file.bytes], file.name, { type: file.type ?? "" }));
            deliverFiles(input, files, () => new page.DataTransfer());
            return true;
        },
        stop() {
            observer.disconnect();
            doc.removeEventListener("click", onClick, true);
            prototype.click = nativeClick;
        },
    };
}
