export async function getStorageEstimate() {
    ensureStorageManager();
    const [estimate, isPersisted] = await Promise.all([
        navigator.storage.estimate(),
        navigator.storage.persisted()
    ]);

    // Chromium details the usage per kind of storage (ADR 0250 §3); other browsers do not.
    const details = estimate.usageDetails ?? null;
    return {
        usageBytes: Math.trunc(estimate.usage ?? 0),
        quotaBytes: Math.trunc(estimate.quota ?? 0),
        isPersisted,
        indexedDbBytes: details ? Math.trunc(details.indexedDB ?? 0) : null,
        cacheBytes: details ? Math.trunc(details.caches ?? 0) : null,
        fileSystemBytes: details ? Math.trunc(details.fileSystem ?? 0) : null
    };
}

// ADR 0250 §2: the boot marker lives next to kazoos.osVersion (ADR 0230), in localStorage, which
// survives an eviction of IndexedDB in some browsers and tells the OS it had booted here before.
const bootMarkerKey = "kazoos.storage.marker";

export function readBootMarker() {
    try {
        return window.localStorage.getItem(bootMarkerKey);
    } catch {
        return null;
    }
}

export function writeBootMarker(marker) {
    try {
        window.localStorage.setItem(bootMarkerKey, marker);
    } catch {
        // A browser that refuses localStorage cannot detect an eviction; the boot goes on.
    }
}

// Whether a database exists, without opening it (indexedDB.open would create it). Null when the
// browser cannot list its databases (Firefox before 126, older Safari).
export async function databaseExists(name) {
    if (typeof indexedDB?.databases !== "function") {
        return null;
    }

    try {
        const databases = await indexedDB.databases();
        return databases.some(database => database.name === name);
    } catch {
        return null;
    }
}

// Whether a file of the Origin Private File System exists. Null when the browser has none.
export async function fileExists(path) {
    if (typeof navigator.storage?.getDirectory !== "function") {
        return null;
    }

    const parts = String(path).split("/").filter(part => part.length > 0);
    if (parts.length === 0) {
        return false;
    }

    try {
        let directory = await navigator.storage.getDirectory();
        for (const part of parts.slice(0, -1)) {
            directory = await directory.getDirectoryHandle(part);
        }

        await directory.getFileHandle(parts[parts.length - 1]);
        return true;
    } catch (error) {
        // NotFoundError and TypeMismatchError mean the file is not there; anything else is unknown.
        return error?.name === "NotFoundError" || error?.name === "TypeMismatchError" ? false : null;
    }
}

export async function requestPersistence() {
    ensureStorageManager();
    return navigator.storage.persist();
}

function ensureStorageManager() {
    if (!navigator.storage?.estimate || !navigator.storage?.persist || !navigator.storage?.persisted) {
        throw new DOMException("Browser storage management is unavailable.", "NotSupportedError");
    }
}