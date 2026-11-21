// Shared end-to-end harness of the Kazo OS apps (ADR 0281): runs an app the way users get it, on
// the published trimmed OS (kazoos-wasm-<version>.zip of the Kazo OS release), installed by
// `sudo kazopkg install <repository>` from the package the app's CI built with KazoOsPack.
//
// An app reaches this module through its Kazo OS submodule
// (external/KazoOs/wasm2/KazoOs/build/app-e2e/kazoos-app-e2e.mjs), so the harness always has the
// version of the OS it tests. Its only dependency is `playwright`, which the app's tests install.
//
//   import { fromEnvironment, startTrimmedOs, newAppContext, bootFirstAccount, installApp, openApp }
//       from '../../external/KazoOs/wasm2/KazoOs/build/app-e2e/kazoos-app-e2e.mjs';
//   const setup = fromEnvironment();           // null outside the CI step of ADR 0281 §5
//   const os = await startTrimmedOs(setup);    // serves the OS on 127.0.0.1
//   const context = await newAppContext(browser, setup);
//   const page = await context.newPage();
//   await bootFirstAccount(page, os.baseUrl);
//   await installApp(page, setup);
//   await openApp(page, 'My App');
//
// The same calls work on a phone: a context made with a phone device (for example
// newAppContext(browser, setup, { ...devices['Pixel 7'] })) runs the mobile shell, and the harness
// then uses its app drawer instead of the desktop launcher (see SHELLS).
//
// No request leaves the machine for the package: the addresses kazopkg reads on
// raw.githubusercontent.com for the repository are answered from the packages folder.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';

/** The administrator the harness creates on the first boot. */
export const ACCOUNT = Object.freeze({ login: 'neo', displayName: 'Neo', password: 'matrix123' });

/**
 * The shells this harness drives: an app's tests read it to know whether this Kazo OS version can
 * install and open the app on a phone too (since 3.6.0), or only on the desktop.
 */
export const SHELLS = Object.freeze(['desktop', 'phone']);

/** Time a step may take: the first WebAssembly boot downloads and compiles the whole OS. */
export const STEP_TIMEOUT_MS = 180_000;

/**
 * Console errors that mean the trimmed OS lacks something the app needs (ADR 0242, 0277, 0281).
 * A test should fail on any of them, even when the app looks fine.
 */
export const TRIMMED_RUNTIME_ERRORS = [
    /MissingMethodException/,
    /MissingFieldException/,
    /TypeLoadException/,
    /FileNotFoundException/,
    /uses APIs this OS does not have/,
];

const TYPES = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
    '.json': 'application/json', '.wasm': 'application/wasm', '.svg': 'image/svg+xml', '.png': 'image/png',
    '.webmanifest': 'application/manifest+json', '.dat': 'application/octet-stream', '.blat': 'application/octet-stream',
    '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.txt': 'text/plain',
};

/**
 * Reads the setup the CI step gives (ADR 0281 §5): KAZOOS_E2E_WWWROOT (the published OS),
 * KAZOOS_E2E_PACKAGES (the folder KazoOsPack wrote) and KAZOOS_E2E_REPOSITORY (owner/repository).
 * @returns {{ wwwroot: string, packages: string, repository: string } | null} null when unset:
 *   the app's tests then use their own host (`dotnet run`).
 */
export function fromEnvironment(env = process.env) {
    const { KAZOOS_E2E_WWWROOT: wwwroot, KAZOOS_E2E_PACKAGES: packages, KAZOOS_E2E_REPOSITORY: repository } = env;
    if (!wwwroot || !packages || !repository) {
        return null;
    }
    return { wwwroot: path.resolve(wwwroot), packages: path.resolve(packages), repository };
}

/**
 * Serves the published OS with a plain static server, as a release host does: no COOP/COEP, no
 * cache, index.html for a folder.
 * @param {{ wwwroot: string }} setup The published OS (the content of kazoos-wasm-<version>.zip).
 * @returns {Promise<{ baseUrl: string, stop: () => Promise<void> }>}
 */
export async function startTrimmedOs({ wwwroot }) {
    const root = path.resolve(wwwroot);
    await stat(path.join(root, 'index.html'));
    const server = createServer(async (request, response) => {
        const relative = decodeURIComponent(new URL(request.url, 'http://host').pathname);
        let file = path.join(root, relative);
        if (file !== root && !file.startsWith(root + path.sep)) {
            response.writeHead(403).end();
            return;
        }
        try {
            if ((await stat(file)).isDirectory()) {
                file = path.join(file, 'index.html');
            }
            const body = await readFile(file);
            response.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
            response.end(body);
        } catch {
            response.writeHead(404).end();
        }
    });
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    const baseUrl = `http://127.0.0.1:${server.address().port}/`;
    return { baseUrl, stop: () => new Promise(resolve => server.close(() => resolve())) };
}

/**
 * Answers the addresses kazopkg reads for the repository
 * (https://raw.githubusercontent.com/<repository>/kazoos-packages/...) from the packages folder.
 * @param {import('playwright').BrowserContext} context A fresh browser context.
 * @param {{ packages: string, repository: string }} setup The CI setup.
 */
export async function routePackages(context, { packages, repository }) {
    const root = path.resolve(packages);
    const escaped = repository.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await context.route(new RegExp(`^https://raw\\.githubusercontent\\.com/${escaped}/kazoos-packages/`, 'i'), async route => {
        const relative = decodeURIComponent(new URL(route.request().url()).pathname).split('/kazoos-packages/')[1] ?? '';
        const file = path.join(root, relative);
        const headers = { 'Access-Control-Allow-Origin': '*' };
        try {
            if (!file.startsWith(root + path.sep)) {
                throw new Error('outside the packages folder');
            }
            await route.fulfill({ status: 200, body: await readFile(file), headers: { ...headers, 'Content-Type': 'application/octet-stream' } });
        } catch {
            await route.fulfill({ status: 404, headers });
        }
    });
}

/**
 * A new browser context whose package addresses are answered locally (see routePackages).
 * @param {import('playwright').Browser} browser The browser.
 * @param {{ packages: string, repository: string }} setup The CI setup.
 * @param {import('playwright').BrowserContextOptions} [options] Viewport, device, etc.
 */
export async function newAppContext(browser, setup, options = { viewport: { width: 1280, height: 800 } }) {
    const context = await browser.newContext(options);
    await routePackages(context, setup);
    return context;
}

/**
 * Collects what makes a run fail on the trimmed OS: page errors and console errors naming
 * TRIMMED_RUNTIME_ERRORS. Call it before the first navigation.
 * @param {import('playwright').Page} page The page.
 * @returns {string[]} The list, filled while the page runs.
 */
export function watchFailures(page) {
    const failures = [];
    page.on('pageerror', error => failures.push(`page error: ${error.message}`));
    page.on('console', message => {
        const text = message.text();
        if (TRIMMED_RUNTIME_ERRORS.some(pattern => pattern.test(text))) {
            failures.push(`console ${message.type()}: ${text}`);
        }
    });
    return failures;
}

/** The desktop shell's launcher button. */
const desktopLauncher = page => page.getByRole('button', { name: 'App launcher' });

/** The mobile shell's button that opens the app drawer (dock). */
const phoneDrawerButton = page => page.getByRole('button', { name: 'All applications', exact: true });

/** The mobile shell's Home button (navigation bar), always on screen, even over a running app. */
const phoneHomeButton = page => page.getByRole('button', { name: 'Home', exact: true });

// The shell each page booted into (bootFirstAccount): it does not change while the page runs.
const bootedShells = new WeakMap();

/**
 * Whether the page runs the mobile shell (a phone device: touch, small screen) rather than the
 * desktop. The shell is chosen at boot: bootFirstAccount records it; for a page booted otherwise,
 * the shell is read from what shows (the desktop launcher, or the home screen's drawer button).
 * @param {import('playwright').Page} page The booted page.
 */
export async function isPhone(page) {
    if (!bootedShells.has(page)) {
        await desktopLauncher(page).or(phoneDrawerButton(page)).first().waitFor({ timeout: STEP_TIMEOUT_MS });
        bootedShells.set(page, await desktopLauncher(page).count() > 0 ? 'desktop' : 'phone');
    }
    return bootedShells.get(page) === 'phone';
}

/**
 * First boot of a fresh profile: creates the administrator, then skips the welcome wizard
 * (ADR 0276) when this version has it. Waits for the desktop, or the home screen on a phone.
 * @param {import('playwright').Page} page A page of a new context.
 * @param {string} baseUrl The OS address.
 */
export async function bootFirstAccount(page, baseUrl, account = ACCOUNT) {
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    await page.getByRole('heading', { name: 'Create administrator' }).waitFor({ timeout: STEP_TIMEOUT_MS });
    await page.getByLabel('Login name').fill(account.login);
    await page.getByLabel('Display name').fill(account.displayName);
    await page.getByLabel('Password', { exact: true }).fill(account.password);
    await page.getByLabel('Confirm password').fill(account.password);
    await page.getByRole('button', { name: 'Create local profile' }).click();
    const skip = page.locator('.welcome-skip');
    const shell = desktopLauncher(page).or(phoneDrawerButton(page)).first();
    await skip.or(shell).first().waitFor({ timeout: STEP_TIMEOUT_MS });
    if (await skip.isVisible()) {
        await skip.click();
    }
    await shell.waitFor({ timeout: STEP_TIMEOUT_MS });
    bootedShells.set(page, await desktopLauncher(page).count() > 0 ? 'desktop' : 'phone');
}

/**
 * Opens an app from the launcher's search on the desktop, or from the app drawer's search on a
 * phone.
 * @param {import('playwright').Page} page The booted page.
 * @param {string} name The app's name as the launcher shows it (manifest "name").
 */
export async function openApp(page, name) {
    if (await isPhone(page)) {
        // The drawer opens from the home screen: go Home first when an app fills the screen.
        if (!(await phoneDrawerButton(page).isVisible())) {
            await phoneHomeButton(page).tap({ timeout: STEP_TIMEOUT_MS });
        }
        await phoneDrawerButton(page).tap({ timeout: STEP_TIMEOUT_MS });
        const drawer = page.getByRole('dialog', { name: 'All applications' });
        await drawer.getByRole('searchbox', { name: 'Search applications' }).fill(name);
        // Each app of the drawer is a button named after the app.
        await drawer.getByRole('button', { name, exact: true }).first().tap({ timeout: STEP_TIMEOUT_MS });
        return;
    }
    await desktopLauncher(page).click({ timeout: STEP_TIMEOUT_MS });
    await page.getByRole('combobox', { name: 'Search applications' }).fill(name);
    // The button's name is "Launch <name>", followed by ", <description>" when the app has one.
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await page.getByRole('button', { name: new RegExp(`^Launch ${escaped}(,|$)`) }).first().click({ timeout: STEP_TIMEOUT_MS });
}

/**
 * Waits until the Terminal shows text matching the predicate.
 * @returns {Promise<string>} The Terminal's text.
 */
async function waitForTerminal(rows, predicate, what) {
    const deadline = Date.now() + STEP_TIMEOUT_MS;
    while (Date.now() < deadline) {
        const text = (await rows.innerText()).replace(/ /g, ' ');
        if (predicate(text)) {
            return text;
        }
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    throw new Error(`timed out waiting for ${what}`);
}

/**
 * Installs the app as the Store does: `sudo kazopkg install <repository>` in the Terminal, as the
 * administrator, the install-time API check included (ADR 0182 §5.3), on the desktop or on a phone.
 * Closes the Terminal after.
 * Throws with the Terminal's text when kazopkg refuses the package.
 * @param {import('playwright').Page} page The booted page (its context made by newAppContext).
 * @param {{ repository: string }} setup The CI setup.
 */
export async function installApp(page, { repository }, account = ACCOUNT) {
    await openApp(page, 'Terminal');
    const panel = page.locator('[data-terminal-panel]').last();
    const rows = panel.locator('.xterm-rows').first();
    await waitForTerminal(rows, text => text.includes('$'), 'the shell prompt');
    const screen = panel.locator('.xterm-screen').first();
    if (await isPhone(page)) {
        await screen.tap();
    } else {
        await screen.click();
    }
    await page.keyboard.type(`sudo kazopkg install ${repository}`);
    await page.keyboard.press('Enter');
    const prompt = page.locator('.credential-prompt-dialog, .aaa-window').first();
    await prompt.waitFor({ state: 'visible', timeout: STEP_TIMEOUT_MS });
    const login = prompt.locator('input[type=text]');
    if (await login.count() > 0 && !(await login.inputValue())) {
        await login.fill(account.login);
    }
    await prompt.locator('input[type=password]').fill(account.password);
    await prompt.locator('input[type=password]').press('Enter');
    const output = await waitForTerminal(rows, text => text.includes('available now') || /does not have|refused|error/i.test(text), 'the end of the installation');
    if (!output.includes('available now')) {
        throw new Error(`kazopkg did not install ${repository}:\n${output}`);
    }
    await page.keyboard.type('exit\n');
    return output;
}

/**
 * Approves, as the administrator, a dangerous capability the app asks for at its first launch,
 * then waits for something the app shows.
 * @param {import('playwright').Page} page The page.
 * @param {import('playwright').Locator} app Something the app shows once it runs.
 */
export async function approveIfAsked(page, app, account = ACCOUNT) {
    const approval = page.getByText('Administrator authorization required', { exact: false });
    await approval.or(app).first().waitFor({ timeout: STEP_TIMEOUT_MS });
    if (await approval.isVisible()) {
        await page.locator('input[type="password"]').last().fill(account.password);
        await page.getByRole('button', { name: 'Allow', exact: true }).click();
    }
    await app.first().waitFor({ timeout: STEP_TIMEOUT_MS });
}

/**
 * What the OS shows when an app fails: its failure panel in the window (ADR 0238) or the Blazor
 * error banner.
 * @param {import('playwright').Page} page The page.
 * @returns {Promise<string | null>} The failure text, or null.
 */
export async function shownFailure(page) {
    const panel = page.locator('[data-testid="app-failure"]');
    if (await panel.count() > 0 && await panel.first().isVisible()) {
        return `app failure panel: ${(await panel.first().innerText()).replace(/\s+/g, ' ').trim()}`;
    }
    const banner = page.locator('#blazor-error-ui');
    if (await banner.count() > 0 && await banner.isVisible()) {
        return `Blazor error banner: ${(await banner.innerText()).replace(/\s+/g, ' ').trim()}`;
    }
    return null;
}
