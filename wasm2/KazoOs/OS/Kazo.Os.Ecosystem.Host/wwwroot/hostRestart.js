// ADR 0195: « Redémarrer » of the Start menu's Power button reloads the OS like a hard refresh
// (Ctrl+F5), once the session has ended. The page reload itself is done by the C# power device.

const resetTimeoutMs = 120000;

/**
 * Empties the caches that would serve the old copy of the OS. With the PWA service worker, its
 * RESET_CACHE (ADR 0099 §3) downloads this release again from the server; offline, or when the
 * server cannot be reached, the worker keeps its cache so the OS still starts. Without a worker
 * (local development), the .NET boot-resource caches are dropped. Never throws.
 * @returns {Promise<string>} 'offline', 'refreshed', 'kept' or 'cleared'.
 */
export async function clearCachesForRestart() {
    try {
        if (navigator.onLine === false) {
            return 'offline';
        }

        const worker = navigator.serviceWorker && navigator.serviceWorker.controller;
        if (worker) {
            const result = await resetWorkerCache(worker);
            return result && result.ok ? 'refreshed' : 'kept';
        }

        if (self.caches) {
            const keys = await caches.keys();
            await Promise.all(keys.filter(key => key.startsWith('dotnet-resources')).map(key => caches.delete(key)));
        }

        return 'cleared';
    } catch {
        return 'kept';
    }
}

function resetWorkerCache(worker) {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => resolve({ ok: false, reason: 'timeout' }), resetTimeoutMs);
        channel.port1.onmessage = event => { clearTimeout(timer); resolve(event.data || { ok: false }); };
        worker.postMessage({ type: 'RESET_CACHE' }, [channel.port2]);
    });
}

/**
 * ADR 0239 §1: records that this tab restarts to update to target (for example 3.3.0+812). Answers
 * false when it already tried that target, so a restart that did not land on it does not loop.
 * @param {string} target The version and build the restart should land on.
 * @returns {boolean} true for the first attempt in this tab.
 */
export function claimBootUpdate(target) {
    const key = 'kazoos-boot-update';
    try {
        if (sessionStorage.getItem(key) === target) {
            return false;
        }

        sessionStorage.setItem(key, target);
        return true;
    } catch {
        // No session storage (private mode limits): never restart, a loop would be worse.
        return false;
    }
}
