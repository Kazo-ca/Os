// Sign-in window of the cloud accounts (ADR 0267 section 5), the page half of
// CloudAccounts/BrowserOAuthAuthorizationWindow.cs, which imports it (never a host page, AGENTS.md).
//
// Kazo OS is served with "Cross-Origin-Opener-Policy: same-origin" (ADR 0131): a window opened
// towards a provider is put in another browsing context group, so the OS cannot read it and it
// cannot reach window.opener. The provider sends the user back to oauth-callback.html, a page of
// the same origin, which posts what it received on the BroadcastChannel below and closes itself.
// BroadcastChannel joins every page of one origin, whatever their browsing context group.
//
// The window is opened at the very start of authorize(), synchronously: it must still run inside
// the click that asked for it, or the browser's pop-up blocker refuses it.

const channelName = 'kazoos-oauth-callback';
const pending = new Map();
let channel = null;

function ensureChannel() {
    if (channel) {
        return;
    }

    channel = new BroadcastChannel(channelName);
    channel.onmessage = event => {
        const data = event.data;
        if (!data || data.type !== 'kazoos-oauth-callback' || typeof data.state !== 'string') {
            return;
        }

        const waiter = pending.get(data.state);
        if (!waiter) {
            // Another sign-in, another tab of Kazo OS, or a replayed page: not ours.
            return;
        }

        finish(data.state, data.error
            ? { status: 'failed', error: String(data.error), errorDescription: data.errorDescription ? String(data.errorDescription) : null }
            : data.code
                ? { status: 'authorized', code: String(data.code) }
                : { status: 'failed', error: 'no_code', errorDescription: null });
    };
}

function finish(state, result) {
    const waiter = pending.get(state);
    if (!waiter) {
        return;
    }

    pending.delete(state);
    clearTimeout(waiter.timer);
    waiter.resolve(result);
}

/** Opens the channel ahead of the click, so that authorize() waits on nothing before window.open. */
export function prepare() {
    ensureChannel();
}

/**
 * Opens the provider's sign-in page and resolves with what the callback page reports for this
 * state: { status: 'authorized', code } | { status: 'failed', error, errorDescription } |
 * { status: 'blocked' } | { status: 'timedOut' }.
 */
export function authorize(url, state, timeoutMs) {
    ensureChannel();
    const features = 'popup=yes,width=520,height=720';
    // No "noopener": it makes window.open return null, which would read as "blocked".
    const opened = window.open(url, 'kazoos-oauth', features);
    if (!opened) {
        return Promise.resolve({ status: 'blocked' });
    }

    return new Promise(resolve => {
        const timer = setTimeout(() => finish(state, { status: 'timedOut' }), timeoutMs);
        pending.set(state, { resolve, timer });
    });
}

/** Stops waiting for a sign-in (the OS cancelled); a late answer is then ignored. */
export function cancel(state) {
    finish(state, { status: 'cancelled' });
}
