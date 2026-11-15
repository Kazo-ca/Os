// Binary content chunks crossing the JSON adapter (ADR 0272 §5). Since database version 5 a chunk
// of the fsContent store holds its bytes as `data` (a Uint8Array) with their SHA-256 as
// `chunkHash`. The few callers that still read or write whole stores as JSON (backup and restore,
// orphan cleanup, the diagnostic snapshot, the repair of the 3.0.0 rename) see the chunk in the
// form JSON can carry, `dataBase64`, and ask for a write in the binary form with `binaryChunk`.
// The content repository itself never goes through JSON: see fileContent.js.

/**
 * Returns the SHA-256 of the bytes, in lowercase hexadecimal.
 * @param {Uint8Array} bytes
 * @returns {Promise<string>}
 */
export async function sha256Hex(bytes) {
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    let text = "";
    for (let index = 0; index < digest.length; index++) {
        text += digest[index].toString(16).padStart(2, "0");
    }
    return text;
}

/**
 * Decodes base64 into bytes.
 * @param {unknown} text
 * @returns {Uint8Array|null} null when the text is not base64.
 */
export function decodeBase64(text) {
    if (typeof text !== "string") {
        return null;
    }
    try {
        if (typeof Uint8Array.fromBase64 === "function") {
            return Uint8Array.fromBase64(text);
        }
        const binary = atob(text);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) {
            bytes[index] = binary.charCodeAt(index);
        }
        return bytes;
    } catch {
        return null;
    }
}

/**
 * Encodes bytes in base64.
 * @param {Uint8Array} bytes
 * @returns {string}
 */
export function encodeBase64(bytes) {
    if (typeof bytes.toBase64 === "function") {
        return bytes.toBase64();
    }
    let binary = "";
    const step = 0x8000;
    for (let offset = 0; offset < bytes.length; offset += step) {
        binary += String.fromCharCode.apply(null, bytes.subarray(offset, offset + step));
    }
    return btoa(binary);
}

function isBinaryChunk(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value) && value.data instanceof Uint8Array;
}

/**
 * Returns a value as JSON can carry it: a binary chunk, alone or in an array, becomes
 * { ...record, dataBase64 } without `data`. Anything else is returned as it is.
 * @param {unknown} value A result of an IndexedDB request.
 * @returns {unknown}
 */
export function toJsonForm(value) {
    if (Array.isArray(value)) {
        return value.some(isBinaryChunk) ? value.map(toJsonForm) : value;
    }
    if (!isBinaryChunk(value)) {
        return value;
    }
    const { data, ...rest } = value;
    return { ...rest, dataBase64: encodeBase64(data) };
}

/**
 * Turns a chunk written as JSON ({ dataBase64 }, with or without a chunkHash) into its binary form.
 * A chunkHash that does not match the decoded bytes is refused: the chunk was altered.
 * @param {object} value The record to write.
 * @returns {Promise<object>}
 */
export async function toBinaryForm(value) {
    if (value === null || typeof value !== "object" || typeof value.dataBase64 !== "string") {
        throw new Error("A binary chunk write needs a record with dataBase64.");
    }
    const data = decodeBase64(value.dataBase64);
    if (data === null) {
        throw new DOMException("indexeddb.chunk-malformed: the chunk data is not base64.", "DataError");
    }
    const chunkHash = await sha256Hex(data);
    if (typeof value.chunkHash === "string" && value.chunkHash !== chunkHash) {
        throw new DOMException("indexeddb.chunk-hash-mismatch: the chunk does not match its hash.", "DataError");
    }
    const { dataBase64, ...rest } = value;
    return { ...rest, data, chunkHash };
}
