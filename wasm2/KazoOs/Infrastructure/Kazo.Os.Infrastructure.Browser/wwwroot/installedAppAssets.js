// The wwwroot/ files of apps installed by kazopkg (ADR 0206), the page half of
// Apps/BrowserInstalledAppAssets.cs, imported by that class (never by a host, AGENTS.md).
//
// The OS copies each verified file into the Cache Storage cache below, under the address a
// built-in app uses (_content/<assembly>/<path>); the service worker (installedAppAssets.sw.js,
// imported by both service-worker files of the host) answers those addresses from it. An <img>, a
// <script src>, an import(), a CSS url(), a new Worker(...) and the importScripts of that worker
// then all find the file, with no change to the app.

const cacheName = 'kazoos-installed-apps';
// index.html registers no service worker on these hosts (development); the OS then registers one
// that only serves installed apps (no release cache), recognised by this query.
const installedAppsOnlyQuery = 'mode=installed-apps';
// ADR 0237: a worker downloads and checks the whole release before it is installed (P2-PWA-003).
const backgroundInstallTimeoutMs = 180000;

function isDevelopmentHost() {
    const host = window.location.hostname;
    return host.includes('localhost') || host.includes('127.0.0.1');
}

function absolute(url) {
    return new URL(url, document.baseURI).href;
}

/**
 * Makes sure a service worker controls the page and returns whether one does. On a development
 * host, registers the "installed apps only" worker first. Waits at most timeoutMs for it to take
 * control (clients.claim on activation).
 *
 * A page loaded with Ctrl+F5 is not controlled even though a worker is active (the browser bypasses
 * it for that load). The page then asks that worker to take control (ADR 0225), which it does only
 * when it caches the release the page runs. When it caches an older release (a version was deployed
 * since), the worker of the page's release is activated instead (ADR 0237): the pages of the older
 * release restart onto it, as when the user restarts to update (ADR 0223).
 */
export async function prepare(timeoutMs) {
    if (!('serviceWorker' in navigator) || !('caches' in window)) {
        return false;
    }

    const container = navigator.serviceWorker;
    if (container.controller) {
        return true;
    }

    const controlled = new Promise(resolve => {
        const timer = setTimeout(() => resolve(!!container.controller), timeoutMs);
        container.addEventListener('controllerchange', () => { clearTimeout(timer); resolve(true); }, { once: true });
    });
    let registration = null;
    if (isDevelopmentHost()) {
        try {
            registration = await container.register(`service-worker.js?${installedAppsOnlyQuery}`, { updateViaCache: 'none' });
        } catch (error) {
            console.warn('Kazo OS: the service worker of installed apps could not be registered', error);
            return false;
        }
    } else {
        registration = await container.getRegistration().catch(() => null);
    }

    // An active worker never activates again, so nothing else would make it claim this page.
    if (registration && registration.active && !container.controller) {
        const version = isDevelopmentHost() ? null : await serverReleaseVersion();
        const answer = await askToClaim(registration.active, version, timeoutMs);
        if (!answer.claimed) {
            // ADR 0237: the active worker caches an older release than this page. The worker of this
            // page's release takes over now; the pages of the older release restart onto it.
            const activated = await activateNewerWorker(registration, version, timeoutMs);
            if (!activated.activating) {
                console.warn('Kazo OS: the service worker does not serve this page yet, installed apps get their wwwroot/ files once it does', { claim: answer, activation: activated });
                return !!container.controller;
            }
        }
    }

    return await controlled;
}

/**
 * ADR 0241: called once the OS starts. A page loaded with Ctrl+F5 runs the release the server
 * delivers now, while the site's active worker may still cache an older one (its successor waits
 * until no tab of the site is left, which a reload never achieves). Without this, the next F5 lands
 * on the older release again. The page asks the active worker to take it, or activates the worker of
 * its own release, exactly as for an installed app's files (prepare). Nothing on a development host
 * (no release cache there) or when a worker already controls the page.
 * @param {number} timeoutMs How long each step waits for the workers.
 * @returns {Promise<boolean>} Whether a worker controls the page afterwards.
 */
export async function adoptAtBoot(timeoutMs) {
    if (!('serviceWorker' in navigator) || !('caches' in window) || isDevelopmentHost()) {
        return false;
    }

    if (navigator.serviceWorker.controller) {
        return true;
    }

    const registration = await navigator.serviceWorker.getRegistration().catch(() => null);
    if (!registration || !registration.active) {
        // First visit: index.html registers the worker, which claims the page when it activates.
        return false;
    }

    return await prepare(timeoutMs);
}

/**
 * ADR 0237: finds the worker of this page's release (waiting, installing, or fetched now), and once
 * it is installed asks it to activate. Resolves to its answer; { activating: false, reason } when there
 * is none in time. A worker still installing after timeoutMs is asked when it has installed, so a
 * later launch finds the page controlled.
 */
async function activateNewerWorker(registration, version, timeoutMs) {
    if (!version) {
        return { activating: false, reason: 'no-version' };
    }

    if (!registration.waiting && !registration.installing) {
        try {
            await registration.update();
        } catch (error) {
            return { activating: false, reason: 'update-failed', message: String(error) };
        }
    }

    const installed = await waitUntilInstalled(registration, timeoutMs);
    if (!installed) {
        if (registration.installing) {
            waitUntilInstalled(registration, backgroundInstallTimeoutMs)
                .then(worker => worker && askToActivate(worker, version, timeoutMs))
                .catch(() => { });
        }

        return { activating: false, reason: registration.installing ? 'installing' : 'no-newer-worker' };
    }

    const answer = await askToActivate(installed, version, timeoutMs);
    if (!answer.activating) {
        return answer;
    }

    // The new worker claims every page when it activates (onActivate): wait for this one.
    const container = navigator.serviceWorker;
    if (!container.controller) {
        await new Promise(resolve => {
            const timer = setTimeout(resolve, timeoutMs);
            container.addEventListener('controllerchange', () => { clearTimeout(timer); resolve(); }, { once: true });
        });
    }

    return { ...answer, activating: !!container.controller };
}

/** Resolves to the registration's waiting worker once there is one, or null after timeoutMs. */
function waitUntilInstalled(registration, timeoutMs) {
    if (registration.waiting) {
        return Promise.resolve(registration.waiting);
    }

    const worker = registration.installing;
    if (!worker) {
        return Promise.resolve(null);
    }

    return new Promise(resolve => {
        const timer = setTimeout(() => finish(null), timeoutMs);
        function finish(result) {
            clearTimeout(timer);
            worker.removeEventListener('statechange', check);
            resolve(result);
        }
        function check() {
            if (registration.waiting) return finish(registration.waiting);
            if (worker.state === 'redundant' || worker.state === 'activated') return finish(null);
        }
        worker.addEventListener('statechange', check);
        check();
    });
}

/** Asks a waiting worker of this page's release to activate; resolves to its answer. */
function askToActivate(worker, version, timeoutMs) {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => resolve({ activating: false, reason: 'no-answer' }), timeoutMs);
        channel.port1.onmessage = event => {
            clearTimeout(timer);
            const answer = event.data || { activating: false };
            if (!answer.activating && window.kazoOsClaimForInstalledApps === 'pending') {
                window.kazoOsClaimForInstalledApps = undefined;
            }
            resolve(answer);
        };
        // index.html reloads a page whose controller changes; this page already runs that release.
        window.kazoOsClaimForInstalledApps = 'pending';
        worker.postMessage({ type: 'KAZOOS_ACTIVATE_FOR_INSTALLED_APPS', version }, [channel.port2]);
    });
}

/** The version of the release the server delivers now, the one a page loaded with Ctrl+F5 runs (ADR 0225). */
async function serverReleaseVersion() {
    try {
        const response = await fetch('service-worker-assets.js', { cache: 'no-store' });
        const match = response.ok ? /"version"\s*:\s*"([^"]+)"/.exec(await response.text()) : null;
        return match ? match[1] : null;
    } catch {
        return null;
    }
}

/** Asks a worker to take control of this page; resolves to its answer, { claimed: false } on silence. */
function askToClaim(worker, version, timeoutMs) {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => resolve({ claimed: false, reason: 'no-answer' }), timeoutMs);
        channel.port1.onmessage = event => {
            clearTimeout(timer);
            const answer = event.data || { claimed: false };
            if (!answer.claimed && window.kazoOsClaimForInstalledApps === 'pending') {
                window.kazoOsClaimForInstalledApps = undefined;
            }
            resolve(answer);
        };
        // index.html reloads a page whose controller changes; this change is expected (same release).
        window.kazoOsClaimForInstalledApps = 'pending';
        worker.postMessage({ type: 'KAZOOS_CLAIM_FOR_INSTALLED_APPS', version }, [channel.port2]);
    });
}

/** Lists the cached addresses, relative to the page's base (for example _content/Kazo.Sample/x.js). */
export async function list() {
    const cache = await caches.open(cacheName);
    const base = new URL('.', document.baseURI).href;
    return JSON.stringify((await cache.keys())
        .map(request => request.url)
        .filter(url => url.startsWith(base))
        .map(url => url.substring(base.length)));
}

/** Stores one file under its address, with its content type. */
export async function put(url, bytes, contentType) {
    const cache = await caches.open(cacheName);
    await cache.put(absolute(url), new Response(new Uint8Array(bytes), {
        headers: {
            'Content-Type': contentType,
            // Same-origin files may be embedded, so a worker or frame under COEP require-corp loads them.
            'Cross-Origin-Resource-Policy': 'same-origin',
            'Cache-Control': 'no-cache'
        }
    }));
}

/** Removes one address; true when it was cached. */
export async function remove(url) {
    const cache = await caches.open(cacheName);
    return await cache.delete(absolute(url));
}

/**
 * Links a style sheet the cache serves (an installed app's scoped CSS, ADR 0221 §2) at the end of the
 * page's <head>, as Blazor's own styles.css links a built-in app's. Once per address: false when it
 * was already linked.
 */
export function linkStyleSheet(url) {
    const href = absolute(url);
    const linked = [...document.head.querySelectorAll('link[data-kazoos-installed-app]')];
    if (linked.some(link => link.href === href)) {
        return false;
    }

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.dataset.kazoosInstalledApp = '';
    document.head.appendChild(link);
    return true;
}
