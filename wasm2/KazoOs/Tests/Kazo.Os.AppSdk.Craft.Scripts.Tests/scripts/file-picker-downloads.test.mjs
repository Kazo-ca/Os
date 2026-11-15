// The page's downloads taken as exports (ADR 0264): a Craft app such as PdfCraft clicks a detached
// `<a download>` on a `blob:` address and revokes it at once; the module must send the file to the
// window instead, and leave every other link to the browser.
import { test } from "node:test";
import assert from "node:assert/strict";
import { interceptDownloads, interceptFilePicker } from "../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/file-picker.js";

/** A page with just what the interception touches: URL, Blob, links, a document and fetch. */
function fakePage() {
    let next = 1;
    const followed = [];
    const listeners = new Map();
    class HTMLAnchorElement {
        constructor() {
            this.attributes = new Map();
            this.href = "";
            this.id = "";
        }

        get download() {
            return this.attributes.get("download") ?? "";
        }

        set download(value) {
            this.attributes.set("download", String(value));
        }

        hasAttribute(name) {
            return this.attributes.has(name);
        }

        closest(selector) {
            return selector === "a[download]" && this.hasAttribute("download") ? this : null;
        }

        click() {
            followed.push(this.href);
        }
    }

    const URL = {
        createObjectURL: () => `blob:https://os.example/${next++}`,
        revokeObjectURL: () => {},
    };
    const document = {
        addEventListener: (type, listener) => listeners.set(type, listener),
        removeEventListener: (type) => listeners.delete(type),
    };
    return {
        page: { URL, Blob, HTMLAnchorElement, document, fetch: (href) => fetch(href) },
        followed,
        listeners,
        link(href, download) {
            const a = new HTMLAnchorElement();
            a.href = href;
            if (download !== undefined) {
                a.download = download;
            }

            return a;
        },
    };
}

/** Collects the messages sent to the window, and waits for the next one. */
function window() {
    const sent = [];
    let wake = () => {};
    return {
        sent,
        send: (message, transfer) => {
            sent.push({ message, transfer });
            wake();
        },
        next: () => new Promise((resolve) => {
            wake = resolve;
        }),
    };
}

test("a download of a blob the page made goes to the window as an export, even once revoked", async () => {
    const { page, followed, link } = fakePage();
    const to = window();
    interceptDownloads({ page, send: to.send });

    // What PdfCraft's download() does: make the address, click a detached link, revoke at once.
    const address = page.URL.createObjectURL(new Blob([new Uint8Array([37, 80, 68, 70])], { type: "application/pdf" }));
    const arrived = to.next();
    link(address, "report-reduced.pdf").click();
    page.URL.revokeObjectURL(address);
    await arrived;

    assert.deepEqual(followed, [], "the browser downloads nothing");
    const [{ message, transfer }] = to.sent;
    assert.equal(message.type, "export");
    assert.equal(message.name, "report-reduced.pdf");
    assert.deepEqual([...new Uint8Array(message.bytes)], [37, 80, 68, 70]);
    assert.deepEqual(transfer, [message.bytes], "the bytes are transferred, not copied");
});

test("a data: download is an export too, and a nameless one is called download", async () => {
    const { page, followed, link } = fakePage();
    const to = window();
    interceptDownloads({ page, send: to.send });

    const arrived = to.next();
    link("data:text/plain;base64,aGk=", "").click();
    await arrived;

    assert.deepEqual(followed, []);
    assert.equal(to.sent[0].message.name, "download");
    assert.equal(new TextDecoder().decode(to.sent[0].message.bytes), "hi");
});

test("other links, rfd's own link and unknown blobs keep the browser's behaviour", () => {
    const { page, followed, link } = fakePage();
    const to = window();
    interceptDownloads({ page, send: to.send });

    link("https://example.org/help").click();
    link("https://example.org/file.pdf", "file.pdf").click();
    link("blob:https://os.example/not-made-here", "x.pdf").click();
    const rfd = link(page.URL.createObjectURL(new Blob(["x"])), "doc.docx");
    rfd.id = "rfd-output";
    rfd.click();

    assert.equal(followed.length, 4);
    assert.deepEqual(to.sent, []);
});

test("a click by the user on a download link in the page is taken the same way", async () => {
    const { page, link, listeners } = fakePage();
    const to = window();
    interceptDownloads({ page, send: to.send });

    const a = link(page.URL.createObjectURL(new Blob(["csv"])), "fields.csv");
    let prevented = false;
    const arrived = to.next();
    listeners.get("click")({ target: a, preventDefault: () => { prevented = true; } });
    await arrived;

    assert.equal(prevented, true);
    assert.equal(to.sent[0].message.name, "fields.csv");
});

test("stop puts the page's functions back", () => {
    const { page, followed, link, listeners } = fakePage();
    const { createObjectURL, revokeObjectURL } = page.URL;
    const click = page.HTMLAnchorElement.prototype.click;
    const to = window();
    const downloads = interceptDownloads({ page, send: to.send });
    downloads.stop();

    assert.equal(page.URL.createObjectURL, createObjectURL);
    assert.equal(page.URL.revokeObjectURL, revokeObjectURL);
    assert.equal(page.HTMLAnchorElement.prototype.click, click);
    assert.equal(listeners.has("click"), false);
    link(page.URL.createObjectURL(new Blob(["x"])), "x.pdf").click();
    assert.equal(followed.length, 1);
    assert.deepEqual(to.sent, []);
});

test("the file picker takes downloads only once the window asked for them", () => {
    const { page } = fakePage();
    // What interceptFilePicker also needs from a page.
    Object.assign(page, {
        HTMLInputElement: class { click() {} },
        MutationObserver: class { observe() {} disconnect() {} },
    });
    page.document.body = {};
    const nativeCreate = page.URL.createObjectURL;
    const picker = interceptFilePicker({ page, send: () => {} });
    assert.equal(page.URL.createObjectURL, nativeCreate, "nothing changes before catchDownloads");

    picker.catchDownloads();
    assert.notEqual(page.URL.createObjectURL, nativeCreate);
    picker.stop();
    assert.equal(page.URL.createObjectURL, nativeCreate);
});

test("the proxy asks the file picker for downloads only when init says the window takes them", async () => {
    const { MessageTypes, startProxy } = await import("../../../Shared/Kazo.Os.AppSdk.Craft/wwwroot/craft-proxy.js");
    const origin = "https://os.example";
    const parent = { name: "the OS window" };
    for (const exports of [true, false, undefined]) {
        const page = new EventTarget();
        let asked = 0;
        startProxy({
            app: "fakecraft",
            page,
            canvas: new EventTarget(),
            origin,
            parent,
            start: () => new Promise(() => {}),
            filePicker: () => ({ answer: () => false, catchDownloads: () => { asked++; } }),
        });
        const channel = new MessageChannel();
        page.dispatchEvent(Object.assign(new Event("message"), {
            data: { type: MessageTypes.init, reservedKeys: [], exports }, origin, source: parent, ports: [channel.port2],
        }));
        assert.equal(asked, exports === true ? 1 : 0, `exports: ${exports}`);
        channel.port1.close();
    }
});
