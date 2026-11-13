// Commands of the window on a Craft app's page API (craft-web-api, ADR 0247): `command` calls
// window.<app>.request, `result` answers it. Taken from Kazo Word's proxy tests.
import { test } from "node:test";
import assert from "node:assert/strict";
import { MessageTypes, runCommand, startProxy } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-proxy.js";

const origin = "https://os.example";
const parent = { name: "the OS window" };

/** A proxy over a fake the fake Craft app page API, connected to a window port. */
function setup(api) {
    const page = new EventTarget();
    const canvas = new EventTarget();
    startProxy({ app: "fakecraft", page, canvas, origin, parent, start: () => new Promise(() => {}), api: () => api });
    const channel = new MessageChannel();
    const received = [];
    channel.port1.onmessage = (event) => received.push(event.data);
    page.dispatchEvent(Object.assign(new Event("message"), {
        data: { type: MessageTypes.init, author: "Neo", language: "en", reservedKeys: [] },
        origin,
        source: parent,
        ports: [channel.port2],
    }));
    return { port: channel.port1, received, close: () => channel.port1.close() };
}

const until = async (condition) => {
    for (let i = 0; i < 100 && !condition(); i++) {
        await new Promise((resolve) => setTimeout(resolve, 5));
    }
};

test("a command runs on the fake Craft app's page API and its value comes back with the same id", async () => {
    const calls = [];
    const api = { request: async (method, params) => { calls.push([method, params]); return { documents: [] }; } };
    const window = setup(api);

    window.port.postMessage({ type: "command", id: 7, method: "ui.inspect", params: {} });
    await until(() => window.received.length > 0);

    assert.deepEqual(calls, [["ui.inspect", {}]]);
    assert.deepEqual(window.received, [{ type: "result", id: 7, ok: true, value: { documents: [] } }]);
    window.close();
});

test("a command the fake Craft app refuses comes back with its error", async () => {
    const window = setup({ request: async () => { throw new Error("unsaved changes"); } });

    window.port.postMessage({ type: "command", id: 2, method: "file.close", params: {} });
    await until(() => window.received.length > 0);

    assert.deepEqual(window.received, [{ type: "result", id: 2, ok: false, error: "unsaved changes" }]);
    window.close();
});

test("a command before the fake Craft app installed its page API fails at once", async () => {
    assert.deepEqual(
        await runCommand(undefined, { id: 1, method: "ui.theme", params: { value: "dark" } }),
        { type: "result", id: 1, ok: false, error: "the Craft app is not ready" });
});

test("a command the fake Craft app never answers fails after its time", async () => {
    const result = await runCommand({ request: () => new Promise(() => {}) }, { id: 3, method: "ui.inspect", timeoutMs: 30 });

    assert.equal(result.ok, false);
    assert.match(result.error, /ui\.inspect: no answer after 30 ms/);
});

test("a page API that throws is a failed command, not a lost one", async () => {
    const result = await runCommand({ request: () => { throw new Error("boom"); } }, { id: 4, method: "x" });

    assert.deepEqual(result, { type: "result", id: 4, ok: false, error: "boom" });
});

test("a result without a value carries null", async () => {
    const result = await runCommand({ request: async () => undefined }, { id: 5, method: "file.save" });

    assert.deepEqual(result, { type: "result", id: 5, ok: true, value: null });
});
