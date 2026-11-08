// ADR 0240: « Copier les détails » of the error page. Kept apart from the OS clipboard services,
// which may be the part that failed.

/**
 * Writes text to the real clipboard; falls back to a hidden text area and execCommand when the
 * Clipboard API is missing or refused (an insecure origin, no user activation).
 * @param {string} text The failure report.
 * @returns {Promise<boolean>} Whether the text reached the clipboard.
 */
export async function copyText(text) {
    try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        // Fall through to the legacy path.
    }

    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    // Out of sight but selectable: a dynamic element gets no scoped CSS.
    area.style.position = 'fixed';
    area.style.top = '0';
    area.style.left = '0';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    try {
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        area.remove();
    }
}
