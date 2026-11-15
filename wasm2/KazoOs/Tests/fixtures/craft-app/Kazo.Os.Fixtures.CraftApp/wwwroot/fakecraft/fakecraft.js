// A fake Craft app for the Kazo OS end-to-end journey of ADR 0259 (module Kazo.Os.AppSdk.Craft).
//
// It stands where a Craft app's wasm-bindgen glue stands: craft-proxy.js imports this file (named by
// fakecraft.json) and calls its default export with the compiled .wasm (an empty module). Like a
// Craft app in page host mode (`data-host="page"`), it speaks version "1" of the craft-web-host
// contract with CustomEvents on its canvas (`fakecraft:ready`, `dirty`, `save`, `failed` out;
// `open`, `saved` in) and offers the craft-web-api page object `window.fakecraft.request`.
//
// One text document. Typing appends to it; Backspace removes; Ctrl+S saves (with an `id`, so the
// window's `saved` clears the changes); Ctrl+O opens a file input (the window answers the pick);
// "!" reports an error about the document after ready. It shows its state in the page for the test.
// Its own preference (how many times it started) lives in localStorage, which the window fills
// with the Kazo user's settings. Aucun jeu sur ce repository.

const APP = "fakecraft";
const VERSION = "1";
const APP_VERSION = "0.0.1-fixture";
const EXTENSION = ".fake";

export default async function init({ module_or_path: module } = {}) {
    if (module instanceof WebAssembly.Module) {
        await WebAssembly.instantiate(module, {});
    }

    const canvas = document.getElementById(`${APP}_canvas`);
    if (!canvas || canvas.dataset.host !== "page") {
        throw new Error("fakecraft: no host canvas");
    }

    start(canvas);
}

function start(canvas) {
    const emit = (kind, detail) => canvas.dispatchEvent(new CustomEvent(`${APP}:${kind}`, { detail }));
    const doc = { id: 1, path: null, title: "Untitled", text: "", dirty: false };
    const pendingSaves = new Set();
    let nextSave = 1;
    let theme = "unset";
    let language = "unset";
    let picked = "";

    const launches = Number.parseInt(localStorage.getItem(`${APP}.launches`) ?? "0", 10) + 1;
    localStorage.setItem(`${APP}.launches`, String(launches));

    const view = document.createElement("div");
    view.className = "fakecraft-view";
    document.body.appendChild(view);
    const field = (name, tag = "p") => {
        const element = document.createElement(tag);
        element.setAttribute(`data-fake-${name}`, "");
        view.appendChild(element);
        return element;
    };
    const fields = {
        name: field("name"),
        text: field("text", "pre"),
        dirty: field("dirty"),
        theme: field("theme"),
        language: field("language"),
        launches: field("launches"),
        picked: field("picked"),
    };
    const render = () => {
        fields.name.textContent = doc.path ?? doc.title;
        fields.text.textContent = doc.text;
        fields.dirty.textContent = doc.dirty ? "modified" : "saved";
        fields.theme.textContent = theme;
        fields.language.textContent = language;
        fields.launches.textContent = `launches ${launches}`;
        fields.picked.textContent = picked;
        view.classList.toggle("light", theme === "light");
    };

    const setDirty = (dirty) => {
        if (doc.dirty !== dirty) {
            doc.dirty = dirty;
            emit("dirty", { version: VERSION, dirty });
        }

        render();
    };

    const save = () => {
        const id = `save-${nextSave++}`;
        pendingSaves.add(id);
        const bytes = new TextEncoder().encode(doc.text).buffer;
        emit("save", { version: VERSION, id, name: doc.path ?? `${doc.title}${EXTENSION}`, saveAs: false, bytes });
    };

    const pick = () => {
        const input = document.createElement("input");
        input.type = "file";
        input.accept = EXTENSION;
        input.addEventListener("change", async () => {
            const file = input.files?.[0];
            picked = file ? `picked ${file.name}: ${await file.text()}` : "pick cancelled";
            render();
        });
        input.click();
    };

    canvas.addEventListener(`${APP}:open`, (event) => {
        const { name, bytes } = event.detail ?? {};
        doc.path = String(name ?? "");
        doc.text = new TextDecoder().decode(bytes ?? new ArrayBuffer(0));
        setDirty(false);
    });
    canvas.addEventListener(`${APP}:saved`, (event) => {
        if (pendingSaves.delete(event.detail?.id)) {
            setDirty(false);
        }
    });

    window.addEventListener("keydown", (event) => {
        const plainCtrl = event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey;
        if (plainCtrl && event.key.toLowerCase() === "s") {
            event.preventDefault();
            save();
        } else if (plainCtrl && event.key.toLowerCase() === "o") {
            event.preventDefault();
            pick();
        } else if (event.key === "!") {
            event.preventDefault();
            emit("failed", { version: VERSION, id: `check-${doc.id}`, error: "fakecraft: the document failed its check" });
        } else if (event.key === "Backspace") {
            event.preventDefault();
            doc.text = doc.text.slice(0, -1);
            setDirty(true);
        } else if (event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey) {
            event.preventDefault();
            doc.text += event.key;
            setDirty(true);
        }
    });

    const commands = {
        "ui.inspect": () => ({
            title: doc.title,
            documents: [{ id: doc.id, path: doc.path, title: doc.title, dirty: doc.dirty, active: true }],
        }),
        "ui.set": (params) => {
            theme = params?.dark === false ? "light" : "dark";
            render();
            return null;
        },
        "app.language.set": (params) => {
            language = String(params?.language ?? "");
            render();
            return null;
        },
        "file.save": () => {
            save();
            return null;
        },
        "doc.rename": (params) => {
            doc.path = String(params?.name ?? "");
            render();
            return null;
        },
    };
    window[APP] = {
        async request(method, params) {
            const command = commands[method];
            if (!command) {
                throw new Error(`unknown method ${method}`);
            }

            return command(params);
        },
    };

    render();
    emit("ready", { version: VERSION, appVersion: APP_VERSION });
}
