// Keys a Craft app's iframe gives back (ADR 0247, module Kazo.Os.AppSdk.Craft; taken from Kazo Word):
// a key pressed in an iframe never reaches the OS page, so Alt+F4, Alt+Tab and the other window
// manager shortcuts would stop working while the caret is in the Craft app. The window sends the list
// of chords it reserves with `init` (`reservedKeys`); a reserved chord is stopped here and sent to
// the window as `key`, everything else goes to the Craft app.
//
// While a chord meant for the OS is held, its key releases are sent too (Alt+Tab opens the window
// switcher on Alt down and picks on Alt up) and kept from the Craft app, which would otherwise show its
// ribbon key tips on the Alt release.

const MODIFIER_KEYS = new Set(["Alt", "AltGraph", "Control", "Shift", "Meta", "OS", "Super", "Hyper"]);

/**
 * Same rule as the OS (KeyChord.Matches, WindowManagerOverlays.razor.js): modifiers exact, then the
 * key ignoring case, else the physical key (macOS Option turns Alt+Q into "œ").
 * @param {{ key?: string|null, code?: string|null, ctrl: boolean, alt: boolean, shift: boolean, meta: boolean }} chord
 * @param {KeyboardEvent} event
 * @returns {boolean}
 */
export function matchesChord(chord, event) {
    if (chord.ctrl !== event.ctrlKey || chord.alt !== event.altKey
        || chord.shift !== event.shiftKey || chord.meta !== event.metaKey) {
        return false;
    }

    if (chord.key) {
        if (event.key.toLowerCase() === chord.key.toLowerCase()) {
            return true;
        }

        if (chord.key.length === 1 && event.key.length === 1 && event.key.charCodeAt(0) > 127) {
            return !!chord.code && event.code === chord.code;
        }

        return false;
    }

    return !!chord.code && event.code === chord.code;
}

/** The message describing one key event. */
const keyMessage = (event, phase, target) => ({
    type: "key",
    phase,
    target,
    key: event.key,
    code: event.code,
    ctrl: event.ctrlKey,
    alt: event.altKey,
    shift: event.shiftKey,
    meta: event.metaKey,
    repeat: event.repeat === true,
});

/**
 * Starts forwarding reserved keys.
 * @param {object} options
 * @param {EventTarget} options.page The iframe page's window (capture listeners, before the Craft app).
 * @param {{ key?: string|null, code?: string|null, ctrl: boolean, alt: boolean, shift: boolean, meta: boolean, target: string }[]} options.chords
 *        The reserved chords; `target` is "os" (replayed on the OS page) or "app" (the window acts).
 * @param {(message: object) => void} options.send Sends a message to the window.
 * @returns {{ stop(): void }}
 */
export function forwardReservedKeys({ page, chords, send }) {
    let holding = false;

    const onKeyDown = (event) => {
        const chord = chords.find((candidate) => matchesChord(candidate, event));
        if (!chord) {
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
        holding = holding || chord.target === "os";
        send(keyMessage(event, "down", chord.target));
    };

    const onKeyUp = (event) => {
        if (!holding) {
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
        send(keyMessage(event, "up", "os"));
        // The chord is over once every modifier is released.
        if (MODIFIER_KEYS.has(event.key) && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
            holding = false;
        }
    };

    page.addEventListener("keydown", onKeyDown, true);
    page.addEventListener("keyup", onKeyUp, true);
    return {
        stop() {
            page.removeEventListener("keydown", onKeyDown, true);
            page.removeEventListener("keyup", onKeyUp, true);
        },
    };
}
