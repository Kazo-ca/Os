// The callback page of the cloud accounts (ADR 0267 section 5): the provider redirects the sign-in
// window here with ?code=…&state=… (or #code=… with response_mode=fragment, or ?error=…). The
// page hands them to Kazo OS on a BroadcastChannel (see oauthWindow.js), removes them from the
// address bar and closes the window. It never starts Kazo OS and keeps nothing.

(function () {
    const query = new URLSearchParams(window.location.search);
    const fragment = new URLSearchParams(window.location.hash.startsWith('#') ? window.location.hash.slice(1) : '');
    const read = name => query.get(name) ?? fragment.get(name);

    const message = {
        type: 'kazoos-oauth-callback',
        state: read('state'),
        code: read('code'),
        error: read('error'),
        errorDescription: read('error_description')
    };

    // The code must not stay in the history or be seen over the user's shoulder.
    try {
        window.history.replaceState(null, '', window.location.pathname);
    } catch {
        // A page that cannot rewrite its address still works.
    }

    const status = document.getElementById('status');
    if (!message.state) {
        status.textContent = 'This page is only used by Kazo OS to finish a sign-in. / Cette page sert seulement à Kazo OS pour finir une connexion.';
        return;
    }

    const channel = new BroadcastChannel('kazoos-oauth-callback');
    channel.postMessage(message);
    channel.close();

    status.textContent = message.error
        ? 'The sign-in did not succeed. You can close this window. / La connexion n’a pas abouti. Vous pouvez fermer cette fenêtre.'
        : 'Signed in. You can close this window. / Connecté. Vous pouvez fermer cette fenêtre.';
    setTimeout(() => window.close(), 150);
})();
