// What the module adds to Kazo Word's proxy (ADR 0247): the app id comes from the page
// (`data-craft-app`, `data-craft-build`), only that app's events are relayed, and the optional
// craft-web-host fields (`id` on save and failed, `saved` back to the app) go through.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
    AppIdPattern,
    MessageTypes,
    readAppDescription,
    startInPage,
    startProxy,
} from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-proxy.js";

const origin = "https://os.example";
const parent = { name: "the OS window" };
const tick = () => new Promise((resolve) => setTimeout(resolve, 20));

/** A proxy for `app` on a fresh page and canvas, already given its port. */
function connected(app = "fakecraft") {
    const page = new EventTarget();
    const canvas = new EventTarget();
    startProxy({ app, page, canvas, origin, parent, start: () => new Promise(() => {}) });
    const channel = new MessageChannel();
    const received = [];
    channel.port1.onmessage = (event) => received.push(event.data);
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, reservedKeys: [] }, origin, source: parent, ports: [channel.port2],
    }));
    return { page, canvas, port: channel.port1, received };
}

/** A page whose canvas carries the given attributes (none: no canvas). */
function fakeDocument(attributes, baseURI = "https://os.example/_content/Kazo.Fake/host/index.html") {
    const canvas = new EventTarget();
    canvas.getAttribute = (name) => attributes?.[name] ?? null;
    return {
        baseURI,
        canvas,
        querySelector: (selector) => (attributes && selector === "canvas[data-craft-app]" ? canvas : null),
    };
}

test("the events of another app on the canvas are not relayed", async () => {
    const { canvas, port, received } = connected("fakecraft");

    canvas.dispatchEvent(new CustomEvent("othercraft:ready", { detail: { appVersion: "9" } }));
    canvas.dispatchEvent(new CustomEvent("fakecraft:ready", { detail: { appVersion: "1.2.0" } }));
    await tick();

    assert.deepEqual(received, [{ type: "ready", appVersion: "1.2.0" }]);
    port.close();
});

test("a version the module does not know is read as version 1: ready still goes through", async () => {
    const { canvas, port, received } = connected();

    canvas.dispatchEvent(new CustomEvent("fakecraft:ready", { detail: { version: "0.14.0" } }));
    await tick();

    assert.deepEqual(received, [{ type: "ready", appVersion: "" }]);
    port.close();
});

test("a save's id goes to the window; a save without one carries none", async () => {
    const { canvas, port, received } = connected();

    canvas.dispatchEvent(new CustomEvent("fakecraft:save", { detail: { name: "a.fake", bytes: new ArrayBuffer(2), id: 7 } }));
    canvas.dispatchEvent(new CustomEvent("fakecraft:save", { detail: { name: "b.fake", bytes: new ArrayBuffer(1) } }));
    await tick();

    assert.equal(received.length, 2);
    assert.equal(received[0].id, 7);
    assert.equal(received[0].name, "a.fake");
    assert.equal("id" in received[1], false);
    port.close();
});

test("a failure about one save carries that save's id", async () => {
    const { canvas, port, received } = connected();

    canvas.dispatchEvent(new CustomEvent("fakecraft:failed", { detail: { error: "disk full", id: 4 } }));
    await tick();

    assert.deepEqual(received, [{ type: "failed", error: "disk full", id: 4 }]);
    port.close();
});

test("saved from the window reaches the app's canvas with the save's id", async () => {
    const { canvas, port } = connected();
    const saved = new Promise((resolve) => canvas.addEventListener("fakecraft:saved", (event) => resolve(event.detail)));

    port.postMessage({ type: "saved", id: 7 });

    assert.deepEqual(await saved, { id: 7 });
    port.close();
});

test("an app id that is not one is refused", () => {
    for (const app of ["", "Word", "a b", "x:y", "../x", "1craft", "a".repeat(65)]) {
        assert.equal(AppIdPattern.test(app), false, app);
        assert.throws(() => startProxy({
            app, page: new EventTarget(), canvas: new EventTarget(), origin, start: async () => {},
        }), /not an app id/);
    }

    assert.equal(AppIdPattern.test("gridcraft"), true);
});

test("the page describes the app: its id, and its build relative to the page", () => {
    const doc = fakeDocument({ "data-craft-app": "fakecraft", "data-craft-build": "../fakecraft/fakecraft.json" });

    const description = readAppDescription(doc);

    assert.equal(description.app, "fakecraft");
    assert.equal(description.canvas, doc.canvas);
    assert.equal(description.buildUrl.href, "https://os.example/_content/Kazo.Fake/fakecraft/fakecraft.json");
});

test("a page without a described canvas, or with a bad one, is an error", () => {
    assert.throws(() => readAppDescription(fakeDocument(null)), /no canvas with data-craft-app/);
    assert.throws(() => readAppDescription(fakeDocument({ "data-craft-app": "Fake Craft", "data-craft-build": "x.json" })), /not an app id/);
    assert.throws(() => readAppDescription(fakeDocument({ "data-craft-app": "fakecraft" })), /no data-craft-build/);
});

test("a page that describes no app still tells the window, once init arrived", async () => {
    const page = new EventTarget();
    Object.assign(page, { document: fakeDocument(null), location: { origin }, parent });
    const proxy = startInPage(page);
    const channel = new MessageChannel();
    const received = [];
    channel.port1.onmessage = (event) => received.push(event.data);

    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, reservedKeys: [] }, origin, source: parent, ports: [channel.port2],
    }));
    await proxy.stopped;
    await tick();

    assert.equal(received.length, 1);
    assert.equal(received[0].type, "failed");
    assert.match(received[0].error, /no canvas with data-craft-app/);
    channel.port1.close();
});
