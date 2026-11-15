// Content repository of the file system, binary side (ADR 0272). Imported by
// BrowserFileContentChunkStore with [JSImport]: the bytes of a file cross as a MemoryView (no JSON,
// no base64), the SHA-256 of the file and of each chunk are computed by WebCrypto, and each chunk
// is stored as { contentHash, chunkIndex, data: Uint8Array, chunkHash, createdUtcMs } in the
// fsContent store of the OS database.
//
// This module keeps its own connection to the database. The C# side always opens the database
// through indexedDb.js first (which applies the migration plan), so the connection here never has
// to upgrade it; when another page asks for a newer version, it closes like those of indexedDb.js.

import { decodeBase64, sha256Hex } from "./binaryChunks.js";

const contentStoreName = "fsContent";
const bookkeepingStoreName = "syncMetadata";
const conversionKey = "fileContentConversion";
const conversionLockName = "kazoos-file-content-conversion";
const connections = new Map();

function interopError(error, fallbackMessage) {
    if (error instanceof DOMException) {
        return new Error(`${error.name}: ${error.message}`);
    }
    return error instanceof Error ? error : new Error(fallbackMessage);
}

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(interopError(request.error, "IndexedDB request failed."));
    });
}

function transactionCompletion(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onabort = () => reject(interopError(transaction.error, "IndexedDB transaction was aborted."));
        transaction.onerror = () => reject(interopError(transaction.error, "IndexedDB transaction failed."));
    });
}

function abortQuietly(transaction) {
    try {
        transaction.abort();
    } catch (abortError) {
        if (abortError.name !== "InvalidStateError") {
            throw abortError;
        }
    }
}

async function database(databaseName, version) {
    const cacheKey = `${databaseName}:${version}`;
    const cached = connections.get(cacheKey);
    if (cached) {
        return cached;
    }

    const request = indexedDB.open(databaseName, version);
    // indexedDb.js creates and upgrades the database before this module runs; an upgrade here
    // would leave a database without its stores, so it is refused.
    request.onupgradeneeded = () => request.transaction?.abort();
    const opened = await requestResult(request);
    opened.onversionchange = () => {
        opened.close();
        connections.delete(cacheKey);
    };
    connections.set(cacheKey, opened);
    return opened;
}

/** Copies a MemoryView out of the .NET heap and releases it. */
function takeBytes(view) {
    try {
        return view.slice();
    } finally {
        view.dispose();
    }
}

/**
 * Returns the SHA-256 of the bytes, in lowercase hexadecimal.
 * @param {IMemoryView} view The bytes, on the .NET heap.
 * @returns {Promise<string>}
 */
export async function sha256(view) {
    return sha256Hex(takeBytes(view));
}

/**
 * Stores one batch of chunks of a file (one transaction), adding only the chunks that are not
 * already stored at their key: content is immutable and addressed by the hash of the file. An
 * existing chunk 0 gets the new creation time (ADR 0273 §2).
 * @param {string} databaseName The OS database.
 * @param {number} version Its version.
 * @param {string} contentHash SHA-256 of the whole file.
 * @param {number} firstIndex Number of the first chunk of the batch.
 * @param {IMemoryView} view The bytes of the batch.
 * @param {number} chunkSize Bytes per chunk (the last chunk of the file may be shorter).
 * @param {number} createdUtcMs Creation time stamped on the new chunks.
 * @returns {Promise<number>} How many chunks were added.
 */
export async function writeChunks(databaseName, version, contentHash, firstIndex, view, chunkSize, createdUtcMs) {
    const bytes = takeBytes(view);
    if (!(chunkSize > 0)) {
        throw new Error("The chunk size must be positive.");
    }

    // Each chunk gets its own buffer: a view onto a larger buffer would make IndexedDB store the
    // whole buffer with every chunk (a structured clone copies the buffer, not the view).
    const chunks = [];
    for (let offset = 0; offset < bytes.length || chunks.length === 0; offset += chunkSize) {
        chunks.push(bytes.slice(offset, Math.min(bytes.length, offset + chunkSize)));
    }

    // WebCrypto is not an IndexedDB request: the hashes are computed before the transaction opens,
    // or the transaction would commit while waiting for them.
    const hashes = await Promise.all(chunks.map(sha256Hex));
    const opened = await database(databaseName, version);
    const transaction = opened.transaction([contentStoreName], "readwrite");
    const completion = transactionCompletion(transaction);
    const store = transaction.objectStore(contentStoreName);
    let added = 0;
    try {
        for (let index = 0; index < chunks.length; index++) {
            const chunkIndex = firstIndex + index;
            if (chunkIndex === 0) {
                // Chunk 0 carries the age of the content (ADR 0273 §2): when the same content is
                // already stored (maybe as an orphan), it gets the new creation time, so the orphan
                // pass, which checks the age again in its delete transaction, keeps it.
                const current = await requestResult(store.get([contentHash, 0]));
                if (current !== undefined) {
                    if (current.createdUtcMs !== createdUtcMs) {
                        await requestResult(store.put({ ...current, createdUtcMs }));
                    }
                    continue;
                }
            } else if (await requestResult(store.getKey([contentHash, chunkIndex])) !== undefined) {
                continue;
            }

            await requestResult(store.add({
                contentHash,
                chunkIndex,
                data: chunks[index],
                chunkHash: hashes[index],
                createdUtcMs
            }));
            added++;
        }
        await completion;
        return added;
    } catch (error) {
        abortQuietly(transaction);
        await completion.catch(() => {});
        throw error;
    }
}

/**
 * Reads one batch of chunks into the destination and checks each chunk by its own hash, the
 * numbers that follow each other and the lengths (ADR 0272 §4). The hash of the whole file is not
 * recomputed.
 * @param {string} databaseName The OS database.
 * @param {number} version Its version.
 * @param {string} contentHash SHA-256 of the whole file.
 * @param {number} firstIndex Number of the first chunk of the batch.
 * @param {number} count How many chunks the batch has.
 * @param {IMemoryView} destination The bytes of the batch, on the .NET heap, filled on success.
 * @param {number} chunkSize Bytes per chunk.
 * @returns {Promise<string>} An empty string, or why the content is damaged.
 */
export async function readChunks(databaseName, version, contentHash, firstIndex, count, destination, chunkSize) {
    try {
        const opened = await database(databaseName, version);
        const transaction = opened.transaction([contentStoreName], "readonly");
        const completion = transactionCompletion(transaction);
        const store = transaction.objectStore(contentStoreName);
        const requests = [];
        for (let index = 0; index < count; index++) {
            requests.push(requestResult(store.get([contentHash, firstIndex + index])));
        }
        const records = await Promise.all(requests);
        await completion;

        const chunks = [];
        let length = 0;
        for (let index = 0; index < count; index++) {
            const record = records[index];
            const chunkIndex = firstIndex + index;
            if (record === undefined) {
                return `chunk ${chunkIndex} is missing`;
            }
            if (record.contentHash !== contentHash || record.chunkIndex !== chunkIndex) {
                return `chunk ${chunkIndex} is out of order`;
            }
            if (!(record.data instanceof Uint8Array) || typeof record.chunkHash !== "string") {
                return typeof record.dataBase64 === "string"
                    ? `chunk ${chunkIndex} is still in the base64 form`
                    : `chunk ${chunkIndex} has no data`;
            }
            if (record.data.length > chunkSize || (index < count - 1 && record.data.length !== chunkSize)) {
                return `chunk ${chunkIndex} has a wrong length`;
            }
            chunks.push(record.data);
            length += record.data.length;
        }
        if (length !== destination.length) {
            return "the chunks do not have the length of the file";
        }

        const hashes = await Promise.all(chunks.map(sha256Hex));
        for (let index = 0; index < count; index++) {
            if (hashes[index] !== records[index].chunkHash) {
                return `chunk ${firstIndex + index} does not match its hash`;
            }
        }

        let offset = 0;
        for (const chunk of chunks) {
            destination.set(chunk, offset);
            offset += chunk.length;
        }
        return "";
    } finally {
        destination.dispose();
    }
}

/**
 * Converts every chunk still in the base64 form ({ dataBase64 }) to the binary form, once
 * (ADR 0272 §3): batches of at most batchBytes decoded bytes, one transaction each, the position
 * recorded in syncMetadata with the batch, so a closed tab resumes there. Only one tab converts
 * (Web Locks): another one waits for the lock, then finds the work done.
 * @param {string} databaseName The OS database.
 * @param {number} version Its version.
 * @param {number} batchBytes Largest batch, in decoded bytes.
 * @param {(looked: number, total: number) => void} progress Chunks looked at and chunks to look
 * at, called before the first batch and after each one; never called when the store is empty or
 * the conversion was already done.
 * @param {() => void} waiting Called when another tab holds the conversion.
 * @returns {Promise<string>} JSON { alreadyDone, chunks, converted, bytes, damaged: [[hash, index]] }.
 */
export async function convertLegacyContent(databaseName, version, batchBytes, progress, waiting) {
    const run = () => convertCore(databaseName, version, batchBytes, progress);
    if (typeof navigator === "undefined" || !navigator.locks) {
        return JSON.stringify(await run());
    }

    // Try first without waiting, so that a page that has to wait can say so.
    let result = await navigator.locks.request(conversionLockName, { ifAvailable: true }, lock => lock ? run() : null);
    if (result === null) {
        waiting();
        result = await navigator.locks.request(conversionLockName, run);
    }
    return JSON.stringify(result);
}

async function convertCore(databaseName, version, batchBytes, progress) {
    const opened = await database(databaseName, version);
    let state;
    let total;
    {
        const transaction = opened.transaction([contentStoreName, bookkeepingStoreName], "readonly");
        const completion = transactionCompletion(transaction);
        state = await requestResult(transaction.objectStore(bookkeepingStoreName).get(conversionKey));
        total = await requestResult(transaction.objectStore(contentStoreName).count());
        await completion;
    }

    if (state?.done === true) {
        return { alreadyDone: true, chunks: 0, converted: 0, bytes: 0, damaged: [] };
    }

    let position = state?.position ?? null;
    let looked = state?.looked ?? 0;
    let converted = state?.converted ?? 0;
    let bytes = state?.bytes ?? 0;
    const damaged = [];
    if (total > 0) {
        progress(looked, Math.max(total, looked));
    }

    while (true) {
        const batch = await readBatch(opened, position, batchBytes);
        if (batch.length === 0) {
            break;
        }

        const replacements = [];
        for (const record of batch) {
            if (record.data instanceof Uint8Array) {
                continue;
            }
            const data = decodeBase64(record.dataBase64);
            if (data === null) {
                // Left as it is: its file is reported as damaged when read (ADR 0018).
                damaged.push([record.contentHash, record.chunkIndex]);
                continue;
            }
            replacements.push({ record, data });
        }

        const hashes = await Promise.all(replacements.map(item => sha256Hex(item.data)));
        const last = batch[batch.length - 1];
        position = [last.contentHash, last.chunkIndex];
        looked += batch.length;
        converted += replacements.length;
        for (const item of replacements) {
            bytes += item.data.length;
        }

        const transaction = opened.transaction([contentStoreName, bookkeepingStoreName], "readwrite");
        const completion = transactionCompletion(transaction);
        const store = transaction.objectStore(contentStoreName);
        try {
            for (let index = 0; index < replacements.length; index++) {
                const { record, data } = replacements[index];
                const { dataBase64, ...rest } = record;
                await requestResult(store.put({ ...rest, data, chunkHash: hashes[index] }));
            }
            await requestResult(transaction.objectStore(bookkeepingStoreName).put({
                key: conversionKey, done: false, position, looked, converted, bytes
            }));
            await completion;
        } catch (error) {
            abortQuietly(transaction);
            await completion.catch(() => {});
            throw error;
        }

        progress(looked, Math.max(total, looked));
    }

    {
        const transaction = opened.transaction([bookkeepingStoreName], "readwrite");
        const completion = transactionCompletion(transaction);
        await requestResult(transaction.objectStore(bookkeepingStoreName).put({
            key: conversionKey, done: true, looked, converted, bytes, completedUtcMs: Date.now()
        }));
        await completion;
    }

    return { alreadyDone: false, chunks: looked, converted, bytes, damaged };
}

/** Reads the chunks after position, up to about batchBytes of data, in one read-only transaction. */
async function readBatch(opened, position, batchBytes) {
    const transaction = opened.transaction([contentStoreName], "readonly");
    const completion = transactionCompletion(transaction);
    const store = transaction.objectStore(contentStoreName);
    const range = position === null ? undefined : IDBKeyRange.lowerBound(position, true);
    const records = [];
    await new Promise((resolve, reject) => {
        let size = 0;
        const request = store.openCursor(range);
        request.onerror = () => reject(interopError(request.error, "IndexedDB cursor failed."));
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor) {
                resolve();
                return;
            }
            const record = cursor.value;
            records.push(record);
            size += record.data instanceof Uint8Array
                ? record.data.length
                : Math.floor((record.dataBase64?.length ?? 0) * 3 / 4);
            if (size >= batchBytes) {
                resolve();
                return;
            }
            cursor.continue();
        };
    });
    await completion;
    return records;
}
