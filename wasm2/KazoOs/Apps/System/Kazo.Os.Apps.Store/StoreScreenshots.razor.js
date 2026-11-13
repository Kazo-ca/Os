// Browser-side helper of an app's screenshots in the Kazo OS Store (StoreScreenshots.razor). Only
// what C# cannot see (ADR 0266 §8): the size of each screenshot, which the catalog does not give,
// and whether the Store is as narrow as a phone. The order itself is decided in C#
// (StoreScreenshotOrder.cs).

// The width under which the Store counts as a phone: the same as the container queries of
// StoreWindow.razor.css.
const PhoneWidth = 640;

/**
 * Starts watching a strip of screenshots.
 * @param {HTMLElement} strip the list of thumbnails.
 * @param {any} component the .NET object told the sizes and the width (OnScreenshotsMeasured).
 * @param {string[]} uris the screenshots' addresses, in the catalog's order.
 * @returns {{ measure: (uris: string[]) => void, dispose: () => void }} measures another list, stops watching.
 */
export function watch(strip, component, uris) {
    const root = strip && strip.closest(".store-root");
    if (!root) {
        return { measure() { }, dispose() { } };
    }

    let disposed = false;
    let narrow = root.clientWidth <= PhoneWidth;
    let current = { uris: [], widths: [], heights: [] };

    const tell = () => {
        if (!disposed) {
            component.invokeMethodAsync("OnScreenshotsMeasured", current.uris, current.widths, current.heights, narrow)
                .catch(() => { });
        }
    };

    const measure = async list => {
        const asked = [...list];
        // An image that cannot be read counts as 0 × 0: its size stays unknown (landscape).
        const sizes = await Promise.all(asked.map(sizeOf));
        if (disposed) {
            return;
        }

        current = { uris: asked, widths: sizes.map(size => size[0]), heights: sizes.map(size => size[1]) };
        tell();
    };

    // A window resized across the phone width changes the order; other resizes do not.
    const sizer = new ResizeObserver(() => {
        const now = root.clientWidth <= PhoneWidth;
        if (now !== narrow) {
            narrow = now;
            tell();
        }
    });
    sizer.observe(root);
    measure(uris || []);

    return {
        measure(list) {
            measure(list || []);
        },
        dispose() {
            disposed = true;
            sizer.disconnect();
        }
    };
}

/**
 * Reads an image's own size. The thumbnails load lazily, so a screenshot far down the strip may not
 * be loaded yet: a separate image is loaded (the browser's cache serves the thumbnail's bytes).
 * @param {string} uri the image's address.
 * @returns {Promise<[number, number]>} its width and height, or [0, 0] when it cannot be read.
 */
function sizeOf(uri) {
    return new Promise(resolve => {
        const image = new Image();
        image.onload = () => resolve([image.naturalWidth || 0, image.naturalHeight || 0]);
        image.onerror = () => resolve([0, 0]);
        image.src = uri;
    });
}
