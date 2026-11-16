// The network state the browser reports, for the probes of the OS (version.json, the page, app
// catalogs). With no network at all (navigator.onLine false), a probe that still called fetch would
// fail with "Failed to load resource: net::ERR_FAILED" in the console, like a broken resource, while
// the OS already knows the answer: nothing is reachable. A real machine does not dial out with its
// cable unplugged either.

/** True when the browser reports no network connection at all. Never throws. */
export function isOffline() {
    return typeof navigator !== 'undefined' && navigator.onLine === false;
}
