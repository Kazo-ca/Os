// The iframe proxy with a fake Craft app (ADR 0247; taken from Kazo Word's tests):
// it relays ready and failed to the window's port, keeps what happens before the port arrives,
// accepts the port only from its parent and its origin, and sends focus on a click.
import { test } from "node:test";
import assert from "node:assert/strict";
import { MessageTypes, startProxy } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-proxy.js";

const origin = "https://os.example";
const parent = { name: "the OS window" };

/** A page, a canvas, and a the fake Craft app whose start the test controls. */
function setup({ start } = {}) {
    const page = new EventTarget();
    const canvas = new EventTarget();
    const proxy = startProxy({ app: "fakecraft", page, canvas, origin, parent, start: start ?? (() => new Promise(() => {})) });
    return { page, canvas, proxy };
}

/** Hands the proxy a port, as craft-host.js does, and returns the window's end of it. */
function connect(page, { from = parent, at = origin } = {}) {
    const channel = new MessageChannel();
    const received = [];
    channel.port1.onmessage = (event) => received.push(event.data);
    // A plain Event with the fields of a window's MessageEvent: node's MessageEvent only takes a
    // MessagePort as its source, and here the source is a window.
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, author: "Neo", language: "en", reservedKeys: [] },
        origin: at,
        source: from,
        ports: [channel.port2],
    }));
    return { port: channel.port1, received, close: () => channel.port1.close() };
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 20));

test("ready from the fake Craft app reaches the window with the fake Craft app's version", async () => {
    const { page, canvas } = setup();
    const window = connect(page);

    canvas.dispatchEvent(new CustomEvent("fakecraft:ready", { detail: { version: "1", appVersion: "0.18.1", file: false } }));
    await tick();

    assert.deepEqual(window.received, [{ type: "ready", appVersion: "0.18.1" }]);
    window.close();
});

test("failed from the fake Craft app reaches the window with its error", async () => {
    const { page, canvas } = setup();
    const window = connect(page);

    canvas.dispatchEvent(new CustomEvent("fakecraft:failed", { detail: { error: "no WebGPU nor WebGL2" } }));
    await tick();

    assert.deepEqual(window.received, [{ type: "failed", error: "no WebGPU nor WebGL2" }]);
    window.close();
});

test("what happens before the port arrives is sent once it is there", async () => {
    const { page, canvas } = setup();

    canvas.dispatchEvent(new CustomEvent("fakecraft:ready", { detail: { appVersion: "0.18.1" } }));
    const window = connect(page);
    await tick();

    assert.deepEqual(window.received, [{ type: "ready", appVersion: "0.18.1" }]);
    window.close();
});

test("a start that throws (the .wasm is missing) is reported as failed", async () => {
    const { page, proxy } = setup({ start: () => Promise.reject(new Error("wasm: HTTP 404")) });
    const window = connect(page);

    await proxy.stopped;
    await tick();

    assert.deepEqual(window.received, [{ type: "failed", error: "wasm: HTTP 404" }]);
    window.close();
});

test("a start that throws after the fake Craft app reported failed sends one failure only", async () => {
    let fail;
    const { page, canvas, proxy } = setup({ start: () => new Promise((_, reject) => { fail = reject; }) });
    const window = connect(page);

    await tick(); // the proxy calls start on the next turn
    canvas.dispatchEvent(new CustomEvent("fakecraft:failed", { detail: { error: "runner" } }));
    fail(new Error("init"));
    await proxy.stopped;
    await tick();

    assert.deepEqual(window.received, [{ type: "failed", error: "runner" }]);
    window.close();
});

test("a the fake Craft app that never answers sends nothing: the window's timeout decides", async () => {
    const { page } = setup();
    const window = connect(page);

    await tick();

    assert.deepEqual(window.received, []);
    window.close();
});

test("init from another origin or another window is ignored", async () => {
    const { page, canvas } = setup();
    const stranger = connect(page, { at: "https://elsewhere.example" });
    const sibling = connect(page, { from: { name: "another frame" } });
    const window = connect(page);

    canvas.dispatchEvent(new CustomEvent("fakecraft:ready", { detail: { appVersion: "0.18.1" } }));
    await tick();

    assert.deepEqual(stranger.received, []);
    assert.deepEqual(sibling.received, []);
    assert.deepEqual(window.received, [{ type: "ready", appVersion: "0.18.1" }]);
    [stranger, sibling, window].forEach((end) => end.close());
});

test("a second init cannot take the port over", async () => {
    const { page, canvas } = setup();
    const window = connect(page);
    const intruder = connect(page);

    canvas.dispatchEvent(new CustomEvent("fakecraft:dirty", { detail: { dirty: true } }));
    await tick();

    assert.deepEqual(window.received, [{ type: "dirty", dirty: true }]);
    assert.deepEqual(intruder.received, []);
    window.close();
    intruder.close();
});

test("a click in the page asks the window to come to the front", async () => {
    const { page } = setup();
    const window = connect(page);

    page.dispatchEvent(new Event("pointerdown"));
    await tick();

    assert.deepEqual(window.received, [{ type: "focus" }]);
    window.close();
});

test("open from the window reaches the fake Craft app's canvas", async () => {
    const { page, canvas } = setup();
    const window = connect(page);
    const opened = new Promise((resolve) => canvas.addEventListener("fakecraft:open", (event) => resolve(event.detail)));

    const bytes = new Uint8Array([80, 75, 3, 4]).buffer;
    window.port.postMessage({ type: "open", name: "Report.docx", bytes });

    const detail = await opened;
    assert.equal(detail.name, "Report.docx");
    assert.deepEqual([...new Uint8Array(detail.bytes)], [80, 75, 3, 4]);
    window.close();
});

test("a save from the fake Craft app reaches the window with its bytes", async () => {
    const { page, canvas } = setup();
    const window = connect(page);

    canvas.dispatchEvent(new CustomEvent("fakecraft:save", {
        detail: { name: "Report.docx", saveAs: false, bytes: new Uint8Array([1, 2, 3]).buffer },
    }));
    await tick();

    assert.equal(window.received.length, 1);
    const [save] = window.received;
    assert.equal(save.type, "save");
    assert.equal(save.name, "Report.docx");
    assert.equal(save.saveAs, false);
    assert.deepEqual([...new Uint8Array(save.bytes)], [1, 2, 3]);
    window.close();
});
