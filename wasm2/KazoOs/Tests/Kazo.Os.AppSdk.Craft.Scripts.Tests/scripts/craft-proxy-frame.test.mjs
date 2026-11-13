// The frame services of the proxy (ADR 0247; taken from Kazo Word's tests): reserved keys, the
// user's settings in place of localStorage, the app started only once `init` arrived, and the
// file picker's hand-over. The picker's DOM interception is covered by the end-to-end journey (work item 4).
import { test } from "node:test";
import assert from "node:assert/strict";
import { MessageTypes, startProxy } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-proxy.js";
import { forwardReservedKeys, matchesChord } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/reserved-keys.js";
import { MemoryStorage } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/settings-store.js";
import { deliverFiles } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/file-picker.js";

const origin = "https://os.example";
const parent = { name: "the OS window" };

const altF4 = { key: "F4", code: null, ctrl: false, alt: true, shift: false, meta: false, target: "os" };
const altBackquote = { key: null, code: "Backquote", ctrl: false, alt: true, shift: false, meta: false, target: "os" };
const altQ = { key: "q", code: "KeyQ", ctrl: false, alt: true, shift: false, meta: false, target: "os" };
const ctrlN = { key: "n", code: "KeyN", ctrl: true, alt: false, shift: false, meta: false, target: "app" };

/** A keyboard event as the browser makes it, with what the listeners did to it. */
function keyEvent(type, { key, code = "", ctrl = false, alt = false, shift = false, meta = false, repeat = false }) {
    const event = Object.assign(new Event(type, { cancelable: true }), {
        key, code, ctrlKey: ctrl, altKey: alt, shiftKey: shift, metaKey: meta, repeat,
    });
    return event;
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 20));

/** A proxy whose `init` carries `extra`, and the window's end of its port. */
function connected(extra = {}, start = () => new Promise(() => {})) {
    const page = new EventTarget();
    const canvas = new EventTarget();
    const proxy = startProxy({ app: "fakecraft", page, canvas, origin, parent, start });
    const channel = new MessageChannel();
    const received = [];
    channel.port1.onmessage = (event) => received.push(event.data);
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, author: "Neo", language: "en", reservedKeys: [], ...extra },
        origin,
        source: parent,
        ports: [channel.port2],
    }));
    return { page, canvas, proxy, port: channel.port1, received, close: () => channel.port1.close() };
}

test("a chord matches with exactly its modifiers, by key ignoring case, else by physical key", () => {
    assert.equal(matchesChord(altF4, keyEvent("keydown", { key: "F4", alt: true })), true);
    assert.equal(matchesChord(altF4, keyEvent("keydown", { key: "F4", alt: true, shift: true })), false);
    assert.equal(matchesChord(altF4, keyEvent("keydown", { key: "F4" })), false);
    assert.equal(matchesChord(altQ, keyEvent("keydown", { key: "Q", alt: true })), true);
    assert.equal(matchesChord(altQ, keyEvent("keydown", { key: "œ", code: "KeyQ", alt: true })), true, "macOS Option+Q");
    assert.equal(matchesChord(altBackquote, keyEvent("keydown", { key: "`", code: "Backquote", alt: true })), true);
});

test("a reserved chord is kept from the fake Craft app and sent to the window, the rest goes to the fake Craft app", () => {
    const page = new EventTarget();
    const sent = [];
    const reachedApp = [];
    forwardReservedKeys({ page, chords: [altF4, ctrlN], send: (message) => sent.push(message) });
    page.addEventListener("keydown", (event) => reachedApp.push(event.key));

    const reserved = keyEvent("keydown", { key: "F4", code: "F4", alt: true });
    page.dispatchEvent(reserved);
    page.dispatchEvent(keyEvent("keydown", { key: "b", code: "KeyB", ctrl: true }));
    page.dispatchEvent(keyEvent("keydown", { key: "n", code: "KeyN", ctrl: true }));

    assert.equal(reserved.defaultPrevented, true);
    assert.deepEqual(reachedApp, ["b"]);
    assert.deepEqual(sent.map((message) => [message.type, message.phase, message.target, message.key]), [
        ["key", "down", "os", "F4"],
        ["key", "down", "app", "n"],
    ]);
});

test("an app reserves its own chords for the window: F12 and Ctrl+Shift+S go to it, Ctrl+S stays with the app", () => {
    // GridCraft's saves never ask "Save as": its window takes Save As (F12, Ctrl+Shift+S) itself.
    const f12 = { key: "F12", code: "F12", ctrl: false, alt: false, shift: false, meta: false, target: "app" };
    const ctrlShiftS = { key: "s", code: "KeyS", ctrl: true, alt: false, shift: true, meta: false, target: "app" };
    const page = new EventTarget();
    const sent = [];
    const reachedApp = [];
    forwardReservedKeys({ page, chords: [f12, ctrlShiftS], send: (message) => sent.push(message) });
    page.addEventListener("keydown", (event) => reachedApp.push(event.key));

    page.dispatchEvent(keyEvent("keydown", { key: "F12", code: "F12" }));
    page.dispatchEvent(keyEvent("keydown", { key: "S", code: "KeyS", ctrl: true, shift: true }));
    page.dispatchEvent(keyEvent("keydown", { key: "s", code: "KeyS", ctrl: true }));
    page.dispatchEvent(keyEvent("keyup", { key: "Control", code: "ControlLeft" }));

    assert.deepEqual(reachedApp, ["s"]);
    assert.deepEqual(sent.map((message) => [message.phase, message.target, message.key, message.shift]), [
        ["down", "app", "F12", false],
        ["down", "app", "S", true],
    ]);
});

test("while an OS chord is held its key releases go to the window too, not to the fake Craft app", () => {
    const page = new EventTarget();
    const sent = [];
    const reachedApp = [];
    forwardReservedKeys({ page, chords: [altBackquote], send: (message) => sent.push(message) });
    page.addEventListener("keyup", (event) => reachedApp.push(event.key));

    page.dispatchEvent(keyEvent("keydown", { key: "`", code: "Backquote", alt: true }));
    page.dispatchEvent(keyEvent("keyup", { key: "`", code: "Backquote", alt: true }));
    page.dispatchEvent(keyEvent("keyup", { key: "Alt", code: "AltLeft" }));
    page.dispatchEvent(keyEvent("keyup", { key: "b", code: "KeyB" }));

    assert.deepEqual(sent.map((message) => [message.phase, message.key]), [["down", "`"], ["up", "`"], ["up", "Alt"]]);
    assert.deepEqual(reachedApp, ["b"], "after Alt is released, keys are the fake Craft app's again");
});

test("the chords of init are reserved", async () => {
    const { page, received, close } = connected({ reservedKeys: [altF4] });

    page.dispatchEvent(keyEvent("keydown", { key: "F4", code: "F4", alt: true }));
    await tick();

    assert.equal(received.length, 1);
    assert.equal(received[0].type, "key");
    assert.equal(received[0].target, "os");
    close();
});

test("the fake Craft app starts only once init arrived, with init", async () => {
    let started = null;
    const page = new EventTarget();
    startProxy({ app: "fakecraft", page, canvas: new EventTarget(), origin, parent, start: async (init) => { started = init; } });
    await tick();
    assert.equal(started, null);

    const channel = new MessageChannel();
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, author: "Neo", reservedKeys: [] },
        origin,
        source: parent,
        ports: [channel.port2],
    }));
    await tick();

    assert.equal(started?.author, "Neo");
    channel.port1.close();
});

test("the user's settings replace the page's localStorage before the fake Craft app starts, and its writes reach the window", async () => {
    let seenAtStart = null;
    const { page, received, close } = connected(
        { settings: { "fakecraft.ui": "{\"theme\":\"light\"}" } },
        async () => { seenAtStart = page.localStorage.getItem("fakecraft.ui"); });
    await tick();

    page.localStorage.setItem("fakecraft.ui", "{\"theme\":\"dark\"}");
    page.localStorage.setItem("fakecraft.ui", "{\"theme\":\"dark\"}");
    page.localStorage.removeItem("fakecraft.ui");
    await tick();

    assert.equal(seenAtStart, "{\"theme\":\"light\"}");
    assert.deepEqual(received, [
        { type: "setting", key: "fakecraft.ui", value: "{\"theme\":\"dark\"}" },
        { type: "setting", key: "fakecraft.ui", value: null },
    ]);
    close();
});

test("without settings in init the page keeps its own storage", async () => {
    const { page, close } = connected();
    await tick();

    assert.equal(Object.getOwnPropertyDescriptor(page, "localStorage"), undefined);
    close();
});

test("the memory storage behaves as the browser's", () => {
    const changes = [];
    const storage = new MemoryStorage({ a: "1", b: 2 }, (key, value) => changes.push([key, value]));

    assert.equal(storage.length, 2);
    assert.equal(storage.getItem("b"), "2");
    assert.equal(storage.getItem("missing"), null);
    assert.equal(storage.key(0), "a");
    assert.equal(storage.key(5), null);
    storage.clear();

    assert.equal(storage.length, 0);
    assert.deepEqual(changes, [["a", null], ["b", null]]);
});

test("picked files are handed to the input and change fires; none (cancelled) still fires change", () => {
    const fired = [];
    const input = new EventTarget();
    input.addEventListener("change", () => fired.push(input.files.length));
    const createTransfer = () => {
        const list = [];
        return { items: { add: (file) => list.push(file) }, files: list };
    };

    deliverFiles(input, [new File([new Uint8Array([1, 2])], "photo.png", { type: "image/png" })], createTransfer);
    deliverFiles(input, [], createTransfer);

    assert.deepEqual(fired, [1, 0]);
});

test("picked from the window reaches the file picker", async () => {
    const answers = [];
    const page = new EventTarget();
    startProxy({
        app: "fakecraft",
        page,
        canvas: new EventTarget(),
        origin,
        parent,
        start: () => new Promise(() => {}),
        filePicker: () => ({ answer: (message) => { answers.push(message); return true; } }),
    });
    const channel = new MessageChannel();
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, reservedKeys: [] }, origin, source: parent, ports: [channel.port2],
    }));

    channel.port1.postMessage({ type: "picked", id: 3, cancelled: true });
    await tick();

    assert.deepEqual(answers, [{ type: "picked", id: 3, cancelled: true }]);
    channel.port1.close();
});
