// The proxy in a Craft app's iframe (ADR 0259, module Kazo.Os.AppSdk.Craft; taken from Kazo Word's
// word-proxy.js).
//
// The app's iframe page (its own `host/index.html`) has one canvas that describes the app:
//
//   <canvas id="gridcraft_canvas" data-host="page"
//           data-craft-app="gridcraft" data-craft-build="../gridcraft/gridcraft.json"></canvas>
//   <script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
//
// `data-craft-app` gives the prefix of the `craft-web-host` messages (`gridcraft:ready`) and the
// page API object (`window.gridcraft`, `craft-web-api`); `data-craft-build` gives the build's
// provenance file (the wasm-bindgen glue and the .wasm), relative to the page.
//
// The proxy starts the Craft app in page host mode on that canvas, and relays, without
// interpreting them, its canvas events (`<app>:ready`, `failed`, `save`, `dirty`) to the window and
// the window's messages (`open`, `saved`) to the app. The window hands over one MessagePort with its
// first message, `init`; messages that happen before it are kept and sent once the port is there.
// A click anywhere in the page sends `focus`, so the Kazo window comes to the front. A `command`
// from the window calls the page API (`window.<app>.request`) and is answered by a `result` with
// the same id. The file picker goes to the window (`pick`, `picked`; file-picker.js), the chords the
// window reserves go back to it (`key`; reserved-keys.js), and the app's localStorage holds the
// Kazo user's settings (`setting`; settings-store.js). For the settings to be there when the app
// reads them, the app is downloaded and compiled at once but started only once `init` arrived.
//
// The proxy speaks version "1" of craft-web-host; a `version` it does not know is read as "1".
// It decides nothing: every decision is in the window's C#.

import { interceptFilePicker } from "./file-picker.js";
import { forwardReservedKeys } from "./reserved-keys.js";
import { installStorage, MemoryStorage } from "./settings-store.js";

/** Names of the messages on the port (internal to the module, ADR 0259 section 3). */
export const MessageTypes = Object.freeze({
    init: "init",
    ready: "ready",
    failed: "failed",
    open: "open",
    save: "save",
    saved: "saved",
    export: "export",
    dirty: "dirty",
    focus: "focus",
    command: "command",
    result: "result",
    pick: "pick",
    picked: "picked",
    key: "key",
    setting: "setting",
});

/** How long a `command` may wait for the Craft app when the window gives no time, in milliseconds. */
export const DefaultCommandTimeoutMs = 10_000;

/**
 * The form of an app id: the `craft-web-host` prefix and the name of a `window` property
 * (`wordcraft`, `gridcraft`, `pdfcraft`...).
 */
export const AppIdPattern = /^[a-z][a-z0-9_]{0,63}$/;

/**
 * Reads the app the page describes: the canvas that carries `data-craft-app`.
 * @param {{ querySelector(selector: string): Element | null, baseURI: string }} doc The iframe page.
 * @returns {{ canvas: HTMLCanvasElement, app: string, buildUrl: URL }}
 * @throws {Error} When the page has no such canvas, or its attributes are missing or malformed.
 */
export function readAppDescription(doc) {
    const canvas = doc.querySelector("canvas[data-craft-app]");
    if (!canvas) {
        throw new Error("craft-proxy: the page has no canvas with data-craft-app");
    }

    const app = canvas.getAttribute("data-craft-app") ?? "";
    if (!AppIdPattern.test(app)) {
        throw new Error(`craft-proxy: data-craft-app "${app}" is not an app id`);
    }

    const build = canvas.getAttribute("data-craft-build") ?? "";
    if (build.trim() === "") {
        throw new Error("craft-proxy: the canvas has no data-craft-build");
    }

    return { canvas, app, buildUrl: new URL(build, doc.baseURI) };
}

/**
 * Runs one `command` of the window on the page API and builds its `result`. The page API resolves
 * with the command's value and rejects with the app's error (a refused command, an unknown method,
 * no document open).
 * @param {{ request(method: string, params: object): Promise<unknown> } | undefined} api `window.<app>`.
 * @param {{ id: number, method: string, params?: object, timeoutMs?: number }} command
 * @returns {Promise<{ type: string, id: number, ok: boolean, value?: unknown, error?: string }>}
 */
export async function runCommand(api, command) {
    const answer = (ok, field) => ({ type: MessageTypes.result, id: command.id, ok, ...field });
    if (typeof api?.request !== "function") {
        return answer(false, { error: "the Craft app is not ready" });
    }

    const timeoutMs = Number.isFinite(command.timeoutMs) && command.timeoutMs > 0
        ? command.timeoutMs
        : DefaultCommandTimeoutMs;
    let timer;
    const timeout = new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`${command.method}: no answer after ${timeoutMs} ms`)), timeoutMs);
    });
    try {
        const value = await Promise.race([api.request(String(command.method), command.params ?? {}), timeout]);
        return answer(true, { value: value ?? null });
    } catch (error) {
        return answer(false, { error: String(error?.message ?? error) });
    } finally {
        clearTimeout(timer);
    }
}

/**
 * Downloads and compiles the Craft app without starting it: reads the provenance file, imports the
 * wasm-bindgen glue it names, and compiles the .wasm while the window gets ready.
 * @param {URL} manifestUrl Address of the provenance file (`<app>.json`: `{ glue, wasm }`).
 * @returns {Promise<{ glue: { default(options: object): Promise<unknown> }, module: WebAssembly.Module }>}
 */
export async function prepareApp(manifestUrl) {
    const response = await fetch(manifestUrl);
    if (!response.ok) {
        throw new Error(`${manifestUrl.pathname}: HTTP ${response.status}`);
    }

    const build = await response.json();
    if (typeof build?.glue !== "string" || typeof build?.wasm !== "string") {
        throw new Error(`${manifestUrl.pathname}: no glue or wasm`);
    }

    const wasmUrl = new URL(build.wasm, manifestUrl);
    const [glue, module] = await Promise.all([
        import(new URL(build.glue, manifestUrl).href),
        compileWasm(wasmUrl),
    ]);
    return { glue, module };
}

// compileStreaming needs application/wasm; a server that sends another type gets the slower path.
async function compileWasm(url) {
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error(`${url.pathname}: HTTP ${response.status}`);
    }

    if (response.headers.get("Content-Type")?.startsWith("application/wasm")) {
        return WebAssembly.compileStreaming(response);
    }

    return WebAssembly.compile(await response.arrayBuffer());
}

/**
 * Starts the Craft app from what prepareApp gave. The app finds its canvas itself (its own id,
 * `data-host="page"`).
 * @param {Promise<{ glue: { default(options: object): Promise<unknown> }, module: WebAssembly.Module }>} prepared
 * @returns {Promise<void>} Settles when the app's init returned.
 */
export async function runApp(prepared) {
    const { glue, module } = await prepared;
    await glue.default({ module_or_path: module });
}

/**
 * Starts the proxy.
 * @param {object} options
 * @param {string} options.app The app id: prefix of the canvas events (`<app>:ready`).
 * @param {EventTarget} options.page The page's window: receives `init`, and clicks.
 * @param {EventTarget} options.canvas The app's canvas, where its events arrive.
 * @param {(init: object) => Promise<void>} options.start Starts the app once `init` arrived (runApp in the browser).
 * @param {string} options.origin The only origin `init` is accepted from (this page's own).
 * @param {object|null} [options.parent] The window allowed to send `init` (the page's parent).
 * @param {() => object | undefined} [options.api] Gives the page API (`window.<app>`).
 * @param {(send: Function) => { answer(message: object): boolean, catchDownloads?(): void }} [options.filePicker]
 *        Intercepts the app's file picker, and its downloads when `init.exports` asks
 *        (interceptFilePicker in the browser; none in the tests of the port).
 * @returns {{ stopped: Promise<void> }} `stopped` settles when the start attempt ended.
 */
export function startProxy({ app, page, canvas, start, origin, parent = null, api = () => undefined, filePicker = null }) {
    if (!AppIdPattern.test(String(app ?? ""))) {
        throw new Error(`craft-proxy: "${app}" is not an app id`);
    }

    const event = (kind) => `${app}:${kind}`;
    let port = null;
    let settled = false;
    const outbox = [];
    let resolveInit;
    const initReceived = new Promise((resolve) => {
        resolveInit = resolve;
    });

    const send = (message, transfer = []) => {
        if (port) {
            port.postMessage(message, transfer);
        } else {
            outbox.push([message, transfer]);
        }
    };

    // Optional craft-web-host fields go along only when the app gave them.
    const withId = (message, detail) => (detail?.id === undefined || detail?.id === null
        ? message
        : { ...message, id: detail.id });

    // App → window. Added before the app starts: it announces `ready` during its init.
    canvas.addEventListener(event("ready"), (e) => {
        settled = true;
        send({ type: MessageTypes.ready, appVersion: String(e.detail?.appVersion ?? "") });
    });
    canvas.addEventListener(event("failed"), (e) => {
        settled = true;
        send(withId({ type: MessageTypes.failed, error: String(e.detail?.error ?? "unknown error") }, e.detail));
    });
    canvas.addEventListener(event("dirty"), (e) => {
        send({ type: MessageTypes.dirty, dirty: e.detail?.dirty === true });
    });
    canvas.addEventListener(event("save"), (e) => {
        const { name, saveAs, bytes } = e.detail ?? {};
        const transfer = bytes instanceof ArrayBuffer ? [bytes] : [];
        send(withId({ type: MessageTypes.save, name: String(name ?? ""), saveAs: saveAs === true, bytes }, e.detail), transfer);
    });

    const picker = filePicker?.(send) ?? null;

    // Window → app.
    const onPortMessage = (e) => {
        const message = e.data;
        switch (message?.type) {
            case MessageTypes.open:
                canvas.dispatchEvent(new CustomEvent(event("open"), {
                    detail: { name: message.name, bytes: message.bytes },
                }));
                break;
            case MessageTypes.saved:
                canvas.dispatchEvent(new CustomEvent(event("saved"), { detail: { id: message.id } }));
                break;
            case MessageTypes.command:
                runCommand(api(), message).then((result) => send(result));
                break;
            case MessageTypes.picked:
                picker?.answer(message);
                break;
            default:
                break;
        }
    };

    // The first message from the window, with the port. Any other sender, or a second `init`, is
    // ignored: the port is this window's alone.
    page.addEventListener("message", (e) => {
        if (port || e.data?.type !== MessageTypes.init || e.origin !== origin) {
            return;
        }

        if (parent !== null && e.source !== parent) {
            return;
        }

        const [received] = e.ports ?? [];
        if (!received) {
            return;
        }

        port = received;
        port.onmessage = onPortMessage;
        const init = e.data;
        // The window takes the app's downloads as exports (ADR 0264); it asks before the app starts.
        if (init.exports === true) {
            picker?.catchDownloads?.();
        }

        if (Array.isArray(init.reservedKeys) && init.reservedKeys.length > 0) {
            forwardReservedKeys({ page, chords: init.reservedKeys, send });
        }

        // Settings first: the app reads its preferences as it starts.
        if (init.settings !== null && typeof init.settings === "object") {
            installStorage(page, new MemoryStorage(init.settings, (key, value) => {
                send({ type: MessageTypes.setting, key, value });
            }));
        }

        resolveInit(init);
        for (const [message, transfer] of outbox.splice(0)) {
            port.postMessage(message, transfer);
        }
    });

    // A click in the app: the Kazo window comes to the front (capture: the app may stop it).
    page.addEventListener("pointerdown", () => send({ type: MessageTypes.focus }), true);

    const stopped = initReceived
        .then((init) => start(init))
        .catch((error) => {
            // The app reports its own start errors with <app>:failed; this one is ours (the
            // provenance file, the glue or the .wasm could not be loaded).
            if (!settled) {
                settled = true;
                send({ type: MessageTypes.failed, error: String(error?.message ?? error) });
            }
        });

    return { stopped };
}

/**
 * Starts the proxy in an iframe page: the app downloads and compiles at once, while the window
 * gets ready, and starts once the window sent `init`. A page that describes no app gets a proxy
 * that only reports the error to the window.
 * @param {Window} page The iframe page's window.
 * @returns {{ stopped: Promise<void> }}
 */
export function startInPage(page) {
    let description;
    try {
        description = readAppDescription(page.document);
    } catch (error) {
        // Still answer the window, so it shows the error instead of waiting.
        return startProxy({
            app: "craft",
            page,
            canvas: new EventTarget(),
            start: () => Promise.reject(error),
            origin: page.location.origin,
            parent: page.parent,
        });
    }

    const { canvas, app, buildUrl } = description;
    const prepared = prepareApp(buildUrl);
    prepared.catch(() => {
        // Reported by runApp, which awaits the same promise once `init` arrived.
    });
    return startProxy({
        app,
        page,
        canvas,
        start: () => runApp(prepared),
        origin: page.location.origin,
        parent: page.parent,
        api: () => page[app],
        filePicker: (send) => interceptFilePicker({ page, send }),
    });
}

// In an iframe page (not in the tests, which import the functions).
if (typeof document !== "undefined" && typeof window !== "undefined" && window.parent !== window) {
    startInPage(window);
}
