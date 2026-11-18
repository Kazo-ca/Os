// Short test of an app on the published trimmed OS (ADR 0281 §4), run by the shared CI of every
// app (kazoos-ci.yml of Kazo-ca/Os-AppTemplate), with or without end-to-end tests of its own:
//
//   node external/KazoOs/wasm2/KazoOs/build/app-e2e/smoke.mjs \
//       --wwwroot <published OS> --packages <KazoOsPack output> --repository <owner/repository> \
//       --manifest <app folder>/manifest.json [--seconds 15]
//
// First boot, `sudo kazopkg install <repository>` (the install-time API check included), launch
// from the launcher by the manifest's "name" (window apps), then the window must stay open for
// --seconds with no page error, no console error naming a missing member or type, no app failure
// panel. A service or command package is only installed. Exit code 0 when it passes, 1 otherwise.
// `playwright` is resolved from the app repository (its node_modules).
import { readFile } from 'node:fs/promises';
import { parseArgs } from 'node:util';
import { chromium } from 'playwright';
import {
    approveIfAsked, bootFirstAccount, installApp, newAppContext, openApp, shownFailure, startTrimmedOs, watchFailures,
} from './kazoos-app-e2e.mjs';

const { values } = parseArgs({
    options: {
        wwwroot: { type: 'string' },
        packages: { type: 'string' },
        repository: { type: 'string' },
        manifest: { type: 'string' },
        seconds: { type: 'string', default: '15' },
    },
});
for (const name of ['wwwroot', 'packages', 'repository', 'manifest']) {
    if (!values[name]) {
        console.error(`--${name} is required.`);
        process.exit(2);
    }
}

const log = message => console.log(`[smoke] ${message}`);
const manifest = JSON.parse((await readFile(values.manifest, 'utf8')).replace(/^﻿/, ''));
const seconds = Number.parseInt(values.seconds, 10);
const setup = { wwwroot: values.wwwroot, packages: values.packages, repository: values.repository };

const os = await startTrimmedOs(setup);
const browser = await chromium.launch();
let failed = false;
let page;
let failures = [];
try {
    const context = await newAppContext(browser, setup);
    page = await context.newPage();
    failures = watchFailures(page);
    log(`Kazo OS served at ${os.baseUrl}`);
    await bootFirstAccount(page, os.baseUrl);
    log('first account created');
    await installApp(page, setup);
    log(`kazopkg installed ${manifest.id} ${manifest.version}`);

    if (manifest.kind === 'window') {
        // The app's window carries its name in its title bar.
        const appWindow = page.locator('[data-window-id]', { hasText: manifest.name });
        await openApp(page, manifest.name);
        // The window, or the approval of a dangerous capability first (granted by the administrator).
        await approveIfAsked(page, appWindow);
        log(`${manifest.name} opened; watching it for ${seconds} s`);
        const deadline = Date.now() + seconds * 1000;
        while (Date.now() < deadline && failures.length === 0) {
            const shown = await shownFailure(page);
            if (shown) {
                failures.push(shown);
                break;
            }
            await page.waitForTimeout(500);
        }
        if (await appWindow.count() === 0) {
            failures.push(`the window of ${manifest.name} closed by itself`);
        }
    } else {
        log(`${manifest.kind} package: installed only`);
    }
} catch (error) {
    failures.push(error instanceof Error ? error.message : String(error));
} finally {
    if (failures.length > 0) {
        failed = true;
        console.error(`[smoke] FAILED on the trimmed Kazo OS:\n- ${failures.join('\n- ')}`);
        await page?.screenshot({ path: 'kazoos-smoke-failure.png' }).catch(() => {});
    } else {
        log('passed');
    }
    await browser.close();
    await os.stop();
}
process.exit(failed ? 1 : 0);
