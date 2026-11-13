// The OS side of a Craft app's window (ADR 0247, module Kazo.Os.AppSdk.Craft; taken from Kazo
// Word's word-host.js).
//
// It creates the iframe that runs the Craft app (the app's own `host/index.html`, which loads
// craft-proxy.js), hands it one MessagePort when it has loaded, and passes the proxy's messages to
// the window's C# (CraftFrame). It decides nothing. One call to `create` = one iframe and one port:
// two windows never share a channel. `request` sends a `command` to the proxy and settles with its
// `result` (the Craft app's page API). The chords C# reserves are replayed on the OS page (target
// "os") or passed to C# (target "app"); file picks and setting writes go to C#; the OS color scheme
// is watched.
//
// The messages on the port are internal to this module: the OS side and the proxy always come from
// the same OS (ADR 0247, section 3).

/**
 * Builds the iframe address: `page` is the app's iframe page, relative to `base` (the OS
 * document's base address); `parameters` go on its query, in order (an empty value is kept as
 * `name=`, which the Craft apps read as a flag, such as WordCraft's `webgl`).
 * @param {string} page Address of the iframe page, e.g. `_content/Kazo.Word/host/index.html`.
 * @param {string} base The OS document's base address.
 * @param {Record<string, string> | null} [parameters]
 * @returns {string}
 */
export function frameUrl(page, base, parameters = null) {
    if (typeof page !== "string" || page.trim() === "") {
        throw new Error("craft-host: options.page names no iframe page");
    }

    const url = new URL(page, base);
    for (const [name, value] of Object.entries(parameters ?? {})) {
        url.searchParams.set(String(name), value === null || value === undefined ? "" : String(value));
    }

    return url.href;
}

/**
 * Creates the iframe in `container` and connects it.
 * @param {HTMLElement} container Element of the window that holds the iframe.
 * @param {{ invokeMethodAsync(name: string, ...args: unknown[]): Promise<unknown> }} dotnet The CraftFrame.
 * @param {{ page: string, title?: string, language?: string, parameters?: Record<string, string> | null,
 *           reservedKeys?: object[], settings?: Record<string, string> | null }} options
 * @returns {{ focus(): void, open(name: string, bytes: Uint8Array): boolean,
 *             request(method: string, params?: object, timeoutMs?: number): Promise<unknown>,
 *             answerPick(id: number, files: { name: string, type: string, bytes: Uint8Array }[] | null): void,
 *             colorScheme(): string, dispose(): void }}
 */
export function create(container, dotnet, options = {}) {
    const iframe = document.createElement("iframe");
    iframe.className = "craft-frame-iframe";
    iframe.title = options.title ?? "Craft app";
    iframe.allow = "fullscreen; clipboard-read; clipboard-write";
    iframe.src = frameUrl(options.page, document.baseURI, options.parameters);

    let port = null;
    let disposed = false;
    // Whether the Craft app of the current page load said `ready`: a `failed` after it is a
    // document's or a save's error ("error"), before it the start failed ("failed").
    let started = false;
    let nextCommandId = 1;
    // Commands sent and not answered yet, by id: { resolve, reject }.
    const pending = new Map();
    // The user's settings as the Craft app last wrote them: a reloaded page starts from them.
    const settings = options.settings ? { ...options.settings } : null;

    // Every command still waiting fails: the page reloaded (its Craft app is a new one) or the
    // window is closing.
    const abandonCommands = (reason) => {
        for (const { reject } of pending.values()) {
            reject(new Error(reason));
        }

        pending.clear();
    };

    const deliver = (type, value) => {
        if (!disposed) {
            dotnet.invokeMethodAsync("OnFrameMessage", type, value ?? null).catch(() => {
                // The window is closing: nobody listens any more.
            });
        }
    };

    // The bytes cross to .NET as a byte[] (Blazor's binary interop), without base64. When the
    // Craft app gave the save an id and C# answers true (the file is written), the app hears
    // `saved` and clears its changes; without an id it treats the save as done once posted.
    const save = (message, from) => {
        if (disposed) {
            return;
        }

        const bytes = message.bytes instanceof ArrayBuffer ? new Uint8Array(message.bytes) : new Uint8Array(0);
        dotnet.invokeMethodAsync("OnFrameSave", String(message.name ?? ""), message.saveAs === true, bytes)
            .then((stored) => {
                const hasId = message.id !== null && message.id !== undefined;
                if (stored === true && hasId && port === from && !disposed) {
                    from.postMessage({ type: "saved", id: message.id });
                }
            })
            .catch(() => {
                // The window is closing: the save is lost with it, as the Craft app is.
            });
    };

    const onMessage = (event) => {
        const message = event.data ?? {};
        switch (message.type) {
            case "ready":
                started = true;
                deliver("ready", message.appVersion);
                break;
            case "failed":
                if (started) {
                    deliver("error", JSON.stringify({ error: String(message.error ?? ""), id: message.id ?? null }));
                } else {
                    deliver("failed", message.error);
                }

                break;
            case "dirty":
                deliver("dirty", message.dirty ? "true" : "false");
                break;
            case "save":
                save(message, event.target);
                break;
            case "focus":
                // The window's frame activates it on pointerdown; a click inside the iframe never
                // reaches the OS document, so the same event is replayed on the iframe element.
                iframe.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, cancelable: true }));
                break;
            case "result": {
                const waiting = pending.get(message.id);
                if (waiting) {
                    pending.delete(message.id);
                    if (message.ok) {
                        waiting.resolve(message.value ?? null);
                    } else {
                        waiting.reject(new Error(String(message.error ?? "unknown error")));
                    }
                }

                break;
            }
            case "key":
                if (message.target === "os") {
                    replayKey(iframe, message);
                } else if (message.phase === "down") {
                    deliver("key", JSON.stringify(message));
                }

                break;
            case "pick":
                deliver("pick", JSON.stringify({ id: message.id, accept: message.accept, multiple: message.multiple }));
                break;
            case "setting":
                if (settings) {
                    if (message.value === null) {
                        delete settings[message.key];
                    } else {
                        settings[message.key] = message.value;
                    }
                }

                deliver("setting", JSON.stringify({ key: message.key, value: message.value }));
                break;
            default:
                // A message this OS side does not know: ignored.
                break;
        }
    };

    // Each load of the page gets a new port. The page loads once, unless the iframe element is
    // moved in the document (a moved iframe reloads): the Craft app then starts again and is
    // connected again, and the old port is closed so nothing late arrives from it.
    iframe.addEventListener("load", () => {
        if (disposed) {
            return;
        }

        port?.close();
        started = false;
        abandonCommands("the Craft app restarted");
        const channel = new MessageChannel();
        port = channel.port1;
        port.onmessage = onMessage;
        iframe.contentWindow.postMessage({
            type: "init",
            language: options.language ?? "en",
            reservedKeys: options.reservedKeys ?? [],
            settings,
        }, window.location.origin, [channel.port2]);
    });

    container.replaceChildren(iframe);

    // The OS theme: its color scheme is a CSS variable of the theme the window sits in. A theme
    // change rewrites data-theme (or data-kos-theme) on an ancestor; C# hears the new scheme.
    let scheme = readColorScheme(container);
    const themeObserver = new MutationObserver(() => {
        const next = readColorScheme(container);
        if (next !== scheme) {
            scheme = next;
            deliver("scheme", next);
        }
    });
    themeObserver.observe(document.documentElement, {
        attributes: true,
        subtree: true,
        attributeFilter: ["data-theme", "data-kos-theme"],
    });

    return {
        focus() {
            iframe.contentWindow?.focus();
        },
        /**
         * Sends a document to the Craft app (`open`); its bytes are transferred, not copied again.
         * @param {string} name The file name the Craft app gives the document.
         * @param {Uint8Array} bytes The file's content, from .NET.
         * @returns {boolean} false when the Craft app is not connected.
         */
        open(name, bytes) {
            if (!port || disposed) {
                return false;
            }

            const buffer = bytes.byteOffset === 0 && bytes.byteLength === bytes.buffer.byteLength
                ? bytes.buffer
                : bytes.slice().buffer;
            port.postMessage({ type: "open", name, bytes: buffer }, [buffer]);
            return true;
        },
        request(method, params = {}, timeoutMs = 0) {
            if (!port || disposed) {
                return Promise.reject(new Error("the Craft app is not connected"));
            }

            const id = nextCommandId++;
            return new Promise((resolve, reject) => {
                pending.set(id, { resolve, reject });
                port.postMessage({ type: "command", id, method, params, timeoutMs });
            });
        },
        answerPick(id, files) {
            if (!port || disposed) {
                return;
            }

            if (!files) {
                port.postMessage({ type: "picked", id, cancelled: true });
                return;
            }

            // Each file's bytes are copied once into their own buffer, then moved to the iframe.
            const sent = files.map((file) => ({ name: file.name, type: file.type, bytes: file.bytes.slice().buffer }));
            port.postMessage({ type: "picked", id, files: sent }, sent.map((file) => file.bytes));
        },
        colorScheme() {
            return scheme;
        },
        dispose() {
            disposed = true;
            themeObserver.disconnect();
            abandonCommands("the window closed");
            port?.close();
            iframe.remove();
        },
    };
}

/**
 * The color scheme of the OS theme around `element`: "light" or "dark" (Kazo OS themes set
 * --kos-color-scheme; dark when none is set, as the OS default theme).
 * @param {Element} element
 * @returns {string}
 */
export function readColorScheme(element) {
    const value = getComputedStyle(element).getPropertyValue("--kos-color-scheme").trim().toLowerCase();
    return value === "light" ? "light" : "dark";
}

/**
 * Replays a key the iframe gave back on the OS page, where the window manager listens (capture on
 * the document). Sent from the iframe element, so it starts inside the window that owns it.
 * @param {HTMLIFrameElement} iframe
 * @param {{ phase: string, key: string, code: string, ctrl: boolean, alt: boolean, shift: boolean, meta: boolean, repeat: boolean }} message
 */
export function replayKey(iframe, message) {
    iframe.dispatchEvent(new KeyboardEvent(message.phase === "up" ? "keyup" : "keydown", {
        key: message.key,
        code: message.code,
        ctrlKey: message.ctrl,
        altKey: message.alt,
        shiftKey: message.shift,
        metaKey: message.meta,
        repeat: message.repeat,
        bubbles: true,
        cancelable: true,
        composed: true,
    }));
}
