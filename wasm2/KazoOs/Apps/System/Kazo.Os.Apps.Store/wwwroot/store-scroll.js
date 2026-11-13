// Browser-side helper of the Kazo OS Store (StoreScrollMemory.cs). The only thing C# cannot do: move
// the page's scroll bar. Every view of the Store is drawn in the same scrolling <main class="store-body">,
// so without this a page opened from a scrolled list would open in the middle (or at the bottom).
//
// Each body remembers the position of every view it showed; a new view opens at the top, and the
// list an app's page was opened from comes back where it was left.

const states = new WeakMap();

function stateOf(body) {
    let state = states.get(body);
    if (!state) {
        state = { key: null, positions: new Map() };
        // Read at event time: the position is filed under the view on screen now.
        body.addEventListener("scroll", () => {
            if (state.key !== null) {
                state.positions.set(state.key, body.scrollTop);
            }
        }, { passive: true });
        states.set(body, state);
    }

    return state;
}

/**
 * Called after each render of the Store whose view changed.
 * @param {HTMLElement} marker the hidden marker the component draws as the first child of the body.
 * @param {string} key the view now on screen ("home", "installed", "detail:<appId>"…).
 * @param {boolean} restore true to go back to where this view was left, false to start at the top.
 */
export function showView(marker, key, restore) {
    const body = marker && marker.parentElement;
    if (!body) {
        return;
    }

    const state = stateOf(body);
    if (state.key === key) {
        return;
    }

    state.key = key;
    body.scrollTop = restore ? (state.positions.get(key) ?? 0) : 0;
}
