// A Craft app's settings, per Kazo user (ADR 0247, module Kazo.Os.AppSdk.Craft; taken from Kazo Word).
//
// A Craft app keeps its preferences in `localStorage` (WordCraft's `wordcraft.ui`: theme, recent
// files, author, AutoCorrect...). The iframe has the OS page's origin, so that storage
// would be shared by every Kazo user of the browser, and by the OS itself. Instead, the iframe's
// `localStorage` is replaced, before the Craft app starts, by a store in memory filled from the user's
// file (`~/.config/<appId>/web-storage.json`, sent with `init`); each write is sent to the
// window (`setting`), which writes the file.

/**
 * A `Storage` in memory: the methods a Craft app calls (getItem, setItem, removeItem, clear, key,
 * length). Values are strings, as in the browser.
 */
export class MemoryStorage {
    #entries;
    #onChange;

    /**
     * @param {Record<string, string>} entries The starting values.
     * @param {(key: string, value: string|null) => void} onChange Told each write; null for a removal.
     */
    constructor(entries, onChange) {
        this.#entries = new Map(Object.entries(entries ?? {}).map(([key, value]) => [key, String(value)]));
        this.#onChange = onChange;
    }

    /** @returns {number} */
    get length() {
        return this.#entries.size;
    }

    /** @param {number} index @returns {string|null} */
    key(index) {
        return [...this.#entries.keys()][index] ?? null;
    }

    /** @param {string} key @returns {string|null} */
    getItem(key) {
        return this.#entries.get(String(key)) ?? null;
    }

    /** @param {string} key @param {string} value */
    setItem(key, value) {
        const name = String(key);
        const text = String(value);
        if (this.#entries.get(name) === text) {
            return;
        }

        this.#entries.set(name, text);
        this.#onChange(name, text);
    }

    /** @param {string} key */
    removeItem(key) {
        const name = String(key);
        if (this.#entries.delete(name)) {
            this.#onChange(name, null);
        }
    }

    clear() {
        for (const name of [...this.#entries.keys()]) {
            this.removeItem(name);
        }
    }
}

/**
 * Puts `storage` in place of the page's `localStorage`. A window attribute is an own, configurable
 * property of the window object, so it can be redefined for this page only.
 * @param {object} page The iframe page's window.
 * @param {MemoryStorage} storage
 */
export function installStorage(page, storage) {
    Object.defineProperty(page, "localStorage", { configurable: true, enumerable: true, get: () => storage });
}
