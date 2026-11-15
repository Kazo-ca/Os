// ADR 0272 §3: waiting screen of the one-time conversion of the stored file content to the binary
// form, at the first boot of the release that brings it, before the OS opens a session. The page
// has no OS language yet: the text follows the browser's language (French or English). Styles in
// css/app.css (#kazoos-storage-conversion).

const french = {
    title: "Mise à jour du stockage",
    explanation: "Kazo OS convertit vos fichiers à son nouveau format de stockage. Cela n'arrive qu'une fois ; ne fermez pas cet onglet. S'il est fermé, la conversion reprendra au prochain démarrage.",
    waiting: "Un autre onglet de Kazo OS convertit vos fichiers. Celui-ci démarrera quand ce sera fini.",
    progress: (percent, looked, total) => `${percent} % (${looked.toLocaleString("fr-CA")} blocs sur ${total.toLocaleString("fr-CA")})`,
    failed: "La conversion de vos fichiers a échoué. Vos fichiers n'ont pas été modifiés au-delà de ce qui était déjà converti. Réessayez ; si le problème continue, faites une sauvegarde depuis une autre version de Kazo OS.",
    retry: "Réessayer"
};

const english = {
    title: "Storage update",
    explanation: "Kazo OS is converting your files to its new storage format. This happens once; do not close this tab. If it is closed, the conversion resumes at the next start.",
    waiting: "Another Kazo OS tab is converting your files. This one starts when it is done.",
    progress: (percent, looked, total) => `${percent}% (${looked.toLocaleString("en")} of ${total.toLocaleString("en")} chunks)`,
    failed: "The conversion of your files failed. Your files were not changed beyond what was already converted. Retry; if the problem persists, make a backup from another version of Kazo OS.",
    retry: "Retry"
};

function texts() {
    const language = (navigator.language || "en").toLowerCase();
    return language.startsWith("fr") ? french : english;
}

function screen() {
    let section = document.getElementById("kazoos-storage-conversion");
    if (section) {
        return section;
    }

    const words = texts();
    section = document.createElement("section");
    section.id = "kazoos-storage-conversion";
    section.setAttribute("role", "status");
    section.setAttribute("aria-live", "polite");
    section.setAttribute("aria-labelledby", "kazoos-storage-conversion-title");
    section.lang = words === french ? "fr" : "en";

    const title = document.createElement("h1");
    title.id = "kazoos-storage-conversion-title";
    title.textContent = words.title;
    const explanation = document.createElement("p");
    explanation.className = "storage-conversion-explanation";
    explanation.textContent = words.explanation;
    const bar = document.createElement("progress");
    bar.className = "storage-conversion-bar";
    bar.max = 100;
    bar.setAttribute("aria-labelledby", "kazoos-storage-conversion-title");
    const detail = document.createElement("p");
    detail.className = "storage-conversion-detail";
    const retry = document.createElement("button");
    retry.type = "button";
    retry.className = "storage-conversion-retry";
    retry.textContent = words.retry;
    retry.hidden = true;
    retry.addEventListener("click", () => location.reload());

    section.append(title, explanation, bar, detail, retry);
    document.body.append(section);
    document.documentElement.classList.add("kazoos-storage-converting");
    return section;
}

/**
 * Shows the progress: chunks looked at out of chunks to look at.
 * @param {number} looked
 * @param {number} total
 */
export function showProgress(looked, total) {
    const section = screen();
    const percent = total > 0 ? Math.min(100, Math.floor((looked * 100) / total)) : 100;
    section.dataset.state = "converting";
    section.querySelector(".storage-conversion-bar").value = percent;
    section.querySelector(".storage-conversion-detail").textContent = texts().progress(percent, looked, total);
}

/** Says that another tab is converting. */
export function showWaiting() {
    const section = screen();
    section.dataset.state = "waiting";
    section.querySelector(".storage-conversion-bar").removeAttribute("value");
    section.querySelector(".storage-conversion-detail").textContent = texts().waiting;
}

/**
 * Says that the conversion failed and offers to retry (a reload resumes it).
 * @param {string} reason The technical reason, shown under the message.
 */
export function showFailure(reason) {
    const section = screen();
    section.dataset.state = "failed";
    section.querySelector(".storage-conversion-explanation").textContent = texts().failed;
    section.querySelector(".storage-conversion-detail").textContent = reason;
    section.querySelector(".storage-conversion-retry").hidden = false;
    section.setAttribute("role", "alert");
}

/** Removes the screen once the conversion is over. */
export function hide() {
    document.getElementById("kazoos-storage-conversion")?.remove();
    document.documentElement.classList.remove("kazoos-storage-converting");
}
