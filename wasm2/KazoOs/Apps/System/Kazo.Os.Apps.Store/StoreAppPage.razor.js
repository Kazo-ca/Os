// Browser-side helper of an app's page in the Kazo OS Store (StoreAppPage.razor). Only what C#
// cannot see: whether the page's header has scrolled out of the Store's scrolling body (the sticky
// bar then shows the app's icon, name and button), the height of that body (the facts panel that
// follows the scroll never grows taller than it), and the address the OS is served from (for the
// « Try it » link of ADR 0258).

/**
 * Starts watching an app's page.
 * @param {HTMLElement} head the page's header (icon, name, buttons).
 * @param {HTMLElement} bar the sticky bar at the top of the page.
 * @param {any} page the .NET object told when the header leaves or comes back (OnHeadVisibilityChanged).
 * @returns {{ dispose: () => void }} stops watching.
 */
export function watch(head, bar, page) {
    const body = head && head.closest(".store-body");
    if (!body || !bar) {
        return { dispose() { } };
    }

    let visible = true;
    const observer = new IntersectionObserver(entries => {
        const entry = entries[entries.length - 1];
        // The header counts as gone once it is fully under the sticky bar.
        if (entry && entry.isIntersecting !== visible) {
            visible = entry.isIntersecting;
            page.invokeMethodAsync("OnHeadVisibilityChanged", visible).catch(() => { });
        }
    }, { root: body, rootMargin: `-${Math.round(bar.offsetHeight)}px 0px 0px 0px`, threshold: 0 });
    observer.observe(head);

    const sizer = new ResizeObserver(() => {
        body.style.setProperty("--store-body-height", `${body.clientHeight}px`);
        body.style.setProperty("--store-bar-height", `${bar.offsetHeight}px`);
    });
    sizer.observe(body);
    sizer.observe(bar);

    return {
        dispose() {
            observer.disconnect();
            sizer.disconnect();
        }
    };
}

/** The address of the page the OS runs in, as the browser shows it. */
export function pageAddress() {
    return window.location.href;
}
