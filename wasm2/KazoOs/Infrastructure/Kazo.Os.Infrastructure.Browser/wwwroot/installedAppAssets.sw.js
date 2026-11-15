// The wwwroot/ files of apps installed by kazopkg (ADR 0206), the service worker half of
// installedAppAssets.js. A classic script, loaded with importScripts by both service-worker files of
// the host (service-worker.js in development, service-worker.published.js once published).
//
// ADR 0274: the files are not copied anywhere. The OS writes one row per address a built-in app
// would use (_content/<assembly>/<path>) in the servedFiles store of its database, pointing at the
// content of the file in the app's folder; this script answers those addresses by streaming the
// content's chunks from the fsContent store, and lets every other request through. The query of a
// request is ignored (a worker script carries ?kazoos-worker=N, ADR 0131 §4).

self.kazoOsInstalledAppAssets = (() => {
    const databaseName = 'kazoos';
    const servedStoreName = 'servedFiles';
    const contentStoreName = 'fsContent';
    // How many chunks one read transaction loads: enough to keep the stream busy, few enough that a
    // large file never sits whole in the worker's memory.
    const chunksPerRead = 8;
    // Registered by the OS on a development host, where index.html registers no worker: then only
    // installed apps are served, the release cache is never filled (installedAppAssets.js).
    const installedAppsOnly = new URL(self.location.href).searchParams.get('mode') === 'installed-apps';
    let pageIsolation = null;

    function contentPath() {
        return new URL('_content/', self.registration.scope).pathname;
    }

    /** Whether the request may be an installed app's file; synchronous, for respondWith. */
    function handles(request) {
        if (request.method !== 'GET') {
            return false;
        }

        const url = new URL(request.url);
        return url.origin === self.location.origin && url.pathname.startsWith(contentPath());
    }

    function requestResult(request) {
        return new Promise((resolve, reject) => {
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    /**
     * Opens the OS database without a version: the page owns its schema. A database that does not
     * exist yet is not created (the upgrade is aborted), and the connection closes as soon as a
     * page asks for a newer version, so this worker never blocks the page's upgrade (ADR 0274 §5).
     * Resolves to null when there is nothing to serve from.
     */
    async function openDatabase() {
        let created = false;
        const request = indexedDB.open(databaseName);
        request.onupgradeneeded = () => {
            created = true;
            request.transaction.abort();
        };
        let database;
        try {
            database = await requestResult(request);
        } catch {
            return null;
        }

        database.onversionchange = () => database.close();
        if (created || !database.objectStoreNames.contains(servedStoreName) ||
            !database.objectStoreNames.contains(contentStoreName)) {
            database.close();
            return null;
        }

        return database;
    }

    /** The address of the request relative to the worker's scope, as the OS writes it. */
    function relativeAddresses(request) {
        const url = new URL(request.url);
        const scope = new URL(self.registration.scope).pathname;
        const relative = url.pathname.substring(scope.length);
        let decoded = relative;
        try {
            decoded = decodeURIComponent(relative);
        } catch {
            // A malformed escape is looked up as written.
        }
        return decoded === relative ? [relative] : [relative, decoded];
    }

    async function readRow(database, request) {
        const transaction = database.transaction([servedStoreName], 'readonly');
        const store = transaction.objectStore(servedStoreName);
        for (const address of relativeAddresses(request)) {
            const row = await requestResult(store.get(address));
            if (row) {
                return row;
            }
        }
        return null;
    }

    /** Parses a single `bytes=` range; null for none, 'invalid' when it cannot be satisfied. */
    function parseRange(header, length) {
        if (!header) {
            return null;
        }

        const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
        if (!match || (match[1] === '' && match[2] === '')) {
            return 'invalid';
        }

        let start;
        let end;
        if (match[1] === '') {
            const suffix = Number(match[2]);
            start = Math.max(0, length - suffix);
            end = length - 1;
        } else {
            start = Number(match[1]);
            end = match[2] === '' ? length - 1 : Math.min(Number(match[2]), length - 1);
        }

        return start > end || start >= length ? 'invalid' : { start, end };
    }

    function toHex(buffer) {
        return Array.from(new Uint8Array(buffer), byte => byte.toString(16).padStart(2, '0')).join('');
    }

    /** Reads chunks [first, last] of a content, checked one by one (ADR 0272 §4). */
    async function readChunks(database, contentHash, first, last) {
        const transaction = database.transaction([contentStoreName], 'readonly');
        const records = await requestResult(transaction.objectStore(contentStoreName)
            .getAll(IDBKeyRange.bound([contentHash, first], [contentHash, last])));
        if (records.length !== last - first + 1) {
            throw new Error(`Chunks of ${contentHash} are missing.`);
        }

        for (let index = 0; index < records.length; index++) {
            const record = records[index];
            if (record.chunkIndex !== first + index || !(record.data instanceof Uint8Array)) {
                throw new Error(`Chunk ${first + index} of ${contentHash} is damaged.`);
            }

            if (typeof record.chunkHash === 'string' &&
                toHex(await crypto.subtle.digest('SHA-256', record.data)) !== record.chunkHash) {
                throw new Error(`Chunk ${first + index} of ${contentHash} does not match its hash.`);
            }
        }

        return records.map(record => record.data);
    }

    /**
     * The bytes [start, end] of a content, as a stream that reads its chunks a few at a time. The
     * database connection closes when the stream ends, fails or is cancelled.
     */
    function contentStream(database, row, start, end) {
        const chunkSize = row.chunkSize;
        const lastChunk = row.length === 0 ? 0 : Math.floor(end / chunkSize);
        let next = row.length === 0 ? 0 : Math.floor(start / chunkSize);
        const close = () => database.close();
        return new ReadableStream({
            async pull(controller) {
                try {
                    if (row.length === 0 || next > lastChunk) {
                        close();
                        controller.close();
                        return;
                    }

                    const last = Math.min(lastChunk, next + chunksPerRead - 1);
                    const chunks = await readChunks(database, row.contentHash, next, last);
                    for (let index = 0; index < chunks.length; index++) {
                        const offset = (next + index) * chunkSize;
                        const from = Math.max(0, start - offset);
                        const to = Math.min(chunks[index].length, end - offset + 1);
                        if (to > from) {
                            controller.enqueue(chunks[index].subarray(from, to));
                        }
                    }
                    next = last + 1;
                } catch (error) {
                    close();
                    controller.error(error);
                }
            },
            cancel() {
                close();
            }
        });
    }

    /**
     * The response for an installed app's file read from the file system's content (ADR 0274 §5),
     * or null when the address is not one. A `Range` request gets a 206 with only the chunks it
     * covers.
     */
    async function match(request) {
        const database = await openDatabase();
        if (!database) {
            return null;
        }

        let row;
        try {
            row = await readRow(database, request);
        } catch {
            row = null;
        }

        if (!row || typeof row.contentHash !== 'string' || !(row.length >= 0) ||
            !(row.chunkSize > 0)) {
            database.close();
            return null;
        }

        const headers = new Headers({
            'Content-Type': row.contentType || 'application/octet-stream',
            // Same-origin files may be embedded, so a worker or frame under COEP require-corp loads them.
            'Cross-Origin-Resource-Policy': 'same-origin',
            'Cache-Control': 'no-cache',
            'ETag': `"${row.contentHash}"`,
            'Accept-Ranges': 'bytes'
        });
        const range = parseRange(request.headers.get('Range'), row.length);
        if (range === 'invalid') {
            database.close();
            headers.set('Content-Range', `bytes */${row.length}`);
            headers.delete('Content-Type');
            return new Response(null, { status: 416, statusText: 'Range Not Satisfiable', headers });
        }

        const start = range ? range.start : 0;
        const end = range ? range.end : row.length - 1;
        headers.set('Content-Length', String(row.length === 0 ? 0 : end - start + 1));
        if (range) {
            headers.set('Content-Range', `bytes ${start}-${end}/${row.length}`);
        }

        return new Response(contentStream(database, row, start, end), {
            status: range ? 206 : 200,
            statusText: range ? 'Partial Content' : 'OK',
            headers
        });
    }

    // In "installed apps only" mode nothing else adds COOP/COEP: the files take the page's own, read
    // once from the server, so a worker started by an isolated page is allowed to run.
    async function readPageIsolation() {
        if (!pageIsolation) {
            try {
                const page = await fetch(self.registration.scope, { method: 'HEAD', cache: 'no-store' });
                pageIsolation = {
                    opener: page.headers.get('Cross-Origin-Opener-Policy'),
                    embedder: page.headers.get('Cross-Origin-Embedder-Policy')
                };
            } catch {
                pageIsolation = {};
            }
        }

        return pageIsolation;
    }

    async function withPageIsolation(response) {
        const isolation = await readPageIsolation();
        if (!isolation.opener && !isolation.embedder) {
            return response;
        }

        const headers = new Headers(response.headers);
        if (isolation.opener) headers.set('Cross-Origin-Opener-Policy', isolation.opener);
        if (isolation.embedder) headers.set('Cross-Origin-Embedder-Policy', isolation.embedder);
        return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
    }

    /** Answers a request handles() accepted: the installed app's file, otherwise the network. */
    async function respond(request) {
        const served = await match(request);
        if (!served) {
            return fetch(request);
        }

        return installedAppsOnly ? withPageIsolation(served) : served;
    }

    /**
     * ADR 0225: whether this worker may take control of a page that has none (a page loaded with
     * Ctrl+F5 bypasses the worker). Always in "installed apps only" mode and in development, where the
     * worker keeps no release cache. Otherwise only when the page runs the release this worker caches
     * (pageVersion is the version of the server's service-worker-assets.js): a page of another release
     * must not receive this worker's cached files.
     */
    function mayClaim(pageVersion) {
        const workerVersion = self.assetsManifest && self.assetsManifest.version;
        return installedAppsOnly || !workerVersion || pageVersion === workerVersion;
    }

    // ADR 0225: the OS asks for control before it serves an installed app's files (installedAppAssets.js).
    self.addEventListener('message', event => {
        if (!event.data || event.data.type !== 'KAZOOS_CLAIM_FOR_INSTALLED_APPS') {
            return;
        }

        const port = event.ports && event.ports[0];
        const workerVersion = (self.assetsManifest && self.assetsManifest.version) || null;
        if (!mayClaim(event.data.version)) {
            if (port) port.postMessage({ claimed: false, workerVersion });
            return;
        }

        event.waitUntil(self.clients.claim()
            .then(() => ({ claimed: true, workerVersion }))
            .catch(error => ({ claimed: false, workerVersion, message: String(error) }))
            .then(result => port && port.postMessage(result)));
    });

    // ADR 0237: the page runs a newer release than the active worker, and this worker (waiting) caches
    // that release. It activates at once; its activate handler claims every page of the site, and the
    // pages of the previous release restart onto this one (ADR 0223). Same version check as above:
    // a worker of another release never activates for this reason.
    self.addEventListener('message', event => {
        if (!event.data || event.data.type !== 'KAZOOS_ACTIVATE_FOR_INSTALLED_APPS') {
            return;
        }

        const port = event.ports && event.ports[0];
        const workerVersion = (self.assetsManifest && self.assetsManifest.version) || null;
        if (!mayClaim(event.data.version)) {
            if (port) port.postMessage({ activating: false, workerVersion });
            return;
        }

        event.waitUntil(self.skipWaiting()
            .then(() => ({ activating: true, workerVersion }))
            .catch(error => ({ activating: false, workerVersion, message: String(error) }))
            .then(result => port && port.postMessage(result)));
    });

    return { installedAppsOnly, handles, match, respond, mayClaim, parseRange };
})();
