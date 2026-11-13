// The OS side (craft-host.js, ADR 0247): the iframe address, and how the proxy's messages reach
// the window's C# (CraftFrame). A small fake document stands in for the OS page; the test plays
// the proxy on the other end of the port.
import { test } from "node:test";
import assert from "node:assert/strict";
import { create, frameUrl } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-host.js";

const osBase = "https://os.example/";
const tick = () => new Promise((resolve) => setTimeout(resolve, 20));

test("the iframe page is relative to the OS document", () => {
    assert.equal(
        frameUrl("_content/Kazo.Fake/host/index.html", osBase),
        "https://os.example/_content/Kazo.Fake/host/index.html");
});

test("parameters go on the query, encoded; an empty value is kept as a flag", () => {
    const url = new URL(frameUrl("_content/Kazo.Fake/host/index.html", osBase, { author: "Zoé & Neo", webgl: "" }));

    assert.equal(url.search, "?author=Zo%C3%A9+%26+Neo&webgl=");
    assert.equal(url.searchParams.get("author"), "Zoé & Neo");
    assert.equal(url.searchParams.has("webgl"), true);
});

test("no iframe page is an error", () => {
    assert.throws(() => frameUrl("", osBase), /names no iframe page/);
    assert.throws(() => frameUrl(undefined, osBase), /names no iframe page/);
});

/** The globals craft-host.js uses on the OS page, faked for one test. */
function installFakePage() {
    const posted = [];
    const iframe = new EventTarget();
    Object.assign(iframe, {
        remove() {},
        contentWindow: {
            focus() {},
            postMessage(data, targetOrigin, ports) {
                posted.push({ data, targetOrigin, port: ports[0] });
            },
        },
    });
    const saved = {
        document: globalThis.document,
        window: globalThis.window,
        MutationObserver: globalThis.MutationObserver,
        getComputedStyle: globalThis.getComputedStyle,
        PointerEvent: globalThis.PointerEvent,
    };
    globalThis.document = { baseURI: osBase, documentElement: {}, createElement: () => iframe };
    globalThis.window = { location: { origin: "https://os.example" } };
    globalThis.MutationObserver = class { observe() {} disconnect() {} };
    globalThis.getComputedStyle = () => ({ getPropertyValue: () => " light " });
    globalThis.PointerEvent ??= class extends Event {};
    return {
        iframe,
        posted,
        restore: () => Object.assign(globalThis, saved),
    };
}

/** A fake CraftFrame: records every call; OnFrameSave answers `stored`. */
function fakeDotnet(stored) {
    const calls = [];
    return {
        calls,
        invokeMethodAsync(name, ...args) {
            calls.push([name, ...args]);
            return Promise.resolve(name === "OnFrameSave" ? stored : null);
        },
    };
}

/** Creates a frame, loads its page and returns the proxy's end of the port. */
function loaded(dotnet, options = {}) {
    const page = installFakePage();
    const container = { replaceChildren() {} };
    const frame = create(container, dotnet, { page: "_content/Kazo.Fake/host/index.html", ...options });
    page.iframe.dispatchEvent(new Event("load"));
    const [{ data, targetOrigin, port }] = page.posted;
    const toProxy = [];
    port.onmessage = (event) => toProxy.push(event.data);
    return { frame, page, init: data, targetOrigin, port, toProxy };
}

test("init goes to the page with the window's language, keys and settings, at the OS origin", () => {
    const { frame, page, init, targetOrigin, port } = loaded(fakeDotnet(null), {
        language: "fr", reservedKeys: [{ key: "F4", alt: true, target: "os" }], settings: { "fake.ui": "{}" },
    });
    try {
        assert.equal(targetOrigin, "https://os.example");
        assert.deepEqual(init, {
            type: "init", language: "fr", reservedKeys: [{ key: "F4", alt: true, target: "os" }], settings: { "fake.ui": "{}" },
        });
        assert.equal(page.iframe.className, "craft-frame-iframe");
        assert.equal(page.iframe.src, "https://os.example/_content/Kazo.Fake/host/index.html");
        assert.equal(frame.colorScheme(), "light");
    } finally {
        frame.dispose();
        port.close();
        page.restore();
    }
});

test("failed before ready is the start failure; after ready it is a document's error with its id", async () => {
    const dotnet = fakeDotnet(null);
    const { frame, page, port } = loaded(dotnet);
    try {
        port.postMessage({ type: "failed", error: "no WebGPU" });
        port.postMessage({ type: "ready", appVersion: "1.0.0" });
        port.postMessage({ type: "failed", error: "disk full", id: 3 });
        await tick();

        assert.deepEqual(dotnet.calls, [
            ["OnFrameMessage", "failed", "no WebGPU"],
            ["OnFrameMessage", "ready", "1.0.0"],
            ["OnFrameMessage", "error", JSON.stringify({ error: "disk full", id: 3 })],
        ]);
    } finally {
        frame.dispose();
        port.close();
        page.restore();
    }
});

test("a save with an id that C# stored is answered with saved", async () => {
    const dotnet = fakeDotnet(true);
    const { frame, page, port, toProxy } = loaded(dotnet);
    try {
        const bytes = new Uint8Array([1, 2, 3]).buffer;
        port.postMessage({ type: "save", name: "a.fake", saveAs: false, bytes, id: 9 }, [bytes]);
        await tick();

        assert.equal(dotnet.calls[0][0], "OnFrameSave");
        assert.equal(dotnet.calls[0][1], "a.fake");
        assert.equal(dotnet.calls[0][2], false);
        assert.deepEqual([...dotnet.calls[0][3]], [1, 2, 3]);
        assert.deepEqual(toProxy, [{ type: "saved", id: 9 }]);
    } finally {
        frame.dispose();
        port.close();
        page.restore();
    }
});

test("no saved without an id, or when C# did not store the file", async () => {
    for (const [stored, id] of [[true, undefined], [false, 9], [null, 9]]) {
        const { frame, page, port, toProxy } = loaded(fakeDotnet(stored));
        try {
            port.postMessage({ type: "save", name: "a.fake", saveAs: true, bytes: new ArrayBuffer(1), id });
            await tick();

            assert.deepEqual(toProxy, [], `stored ${stored}, id ${id}`);
        } finally {
            frame.dispose();
            port.close();
            page.restore();
        }
    }
});

test("a command goes to the proxy and settles with its result", async () => {
    const { frame, page, port, toProxy } = loaded(fakeDotnet(null));
    try {
        const answer = frame.request("doc.info", { all: true }, 500);
        await tick();
        assert.deepEqual(toProxy, [{ type: "command", id: 1, method: "doc.info", params: { all: true }, timeoutMs: 500 }]);

        port.postMessage({ type: "result", id: 1, ok: true, value: { pages: 2 } });
        assert.deepEqual(await answer, { pages: 2 });
    } finally {
        frame.dispose();
        port.close();
        page.restore();
    }
});

test("closing the window fails the commands still waiting", async () => {
    const { frame, page, port } = loaded(fakeDotnet(null));
    try {
        const answer = frame.request("doc.info");
        frame.dispose();
        await assert.rejects(answer, /the window closed/);
    } finally {
        port.close();
        page.restore();
    }
});
