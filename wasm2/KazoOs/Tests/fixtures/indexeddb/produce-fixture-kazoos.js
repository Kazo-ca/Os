// Produces an IndexedDB upgrade fixture of a Kazo OS build (after the rename, database "kazoos")
// by driving its real UI -- first-run administrator, one file written in the Terminal, the Cyan
// accent in Settings (stored in ~/.config/kazoos/appearance.config) -- then dumping every object
// store to JSON. Tooling only: never shipped, never referenced by a host. The builds before the
// rename use produce-fixture.js.
//
// usage: node produce-fixture-kazoos.js <url> <commit> <out.json> <screenshotPrefix>
// env:   PLAYWRIGHT_MODULE  path of the node "playwright" package (default: "playwright")
// Full procedure: doc/en/wasm/dev/building-and-testing.md, "IndexedDB version upgrade fixtures".
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('fs');
const [url, commit, outJson, shot] = process.argv.slice(2);
const version = 4;
const admin = { loginName: 'admin', displayName: 'Fixture Admin', password: `Fixture-v${version}-pass` };
const file = { directory: '/home/admin/Documents', name: 'upgrade-note.txt',
  content: `Written by Kazo OS at IndexedDB schema v${version} (${commit}).` };
const accent = 'cyan';
async function step(page, name, fn) { console.log('step:', name); try { await fn(); } catch (e) { await page.screenshot({ path: `${shot}-fail-${name}.png` }); throw e; } }
(async () => {
  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', e => console.log('pageerror', e.message));
  page.setDefaultTimeout(30000);
  await page.goto(url);
  await step(page, 'onboarding', async () => {
    await page.getByLabel('Login name').waitFor({ timeout: 120000 });
    await page.getByLabel('Login name').fill(admin.loginName);
    await page.getByLabel('Display name').fill(admin.displayName);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByLabel('Confirm password').fill(admin.password);
    await page.getByRole('button', { name: 'Create local profile' }).click();
    await page.getByRole('button', { name: 'App launcher' }).waitFor({ timeout: 120000 });
  });
  const openApp = async (name) => {
    await page.getByRole('button', { name: 'App launcher' }).click();
    await page.getByRole('combobox', { name: 'Search applications' }).fill(name);
    await page.getByRole('button', { name: `Launch ${name}`, exact: true }).click();
  };
  await step(page, 'write-file', async () => {
    await openApp('Terminal');
    const rows = page.locator('[data-terminal-panel] .xterm-rows').first();
    const screen = page.locator('[data-terminal-panel] .xterm-screen').first();
    const waitFor = async (pred) => { for (let i = 0; i < 120; i++) { const t = (await rows.innerText()).replace(/ /g, ' '); if (pred(t)) return t; await page.waitForTimeout(250); } throw new Error('terminal timeout'); };
    await waitFor(t => /[$#]\s*$/m.test(t));
    await screen.click();
    await page.keyboard.type(`echo '${file.content}' > ${file.directory}/${file.name}`);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(1500);
    await page.keyboard.type(`cat ${file.directory}/${file.name}`);
    await page.keyboard.press('Enter');
    await waitFor(t => t.split(file.content).length >= 3);
    await page.locator('button.chrome-button-close').first().click();
  });
  await step(page, 'setting', async () => {
    await openApp('Settings');
    await page.waitForTimeout(2000);
    const appearance = page.getByRole('button', { name: /^Appearance/ }).or(page.getByRole('tab', { name: /^Appearance/ })).or(page.getByRole('link', { name: /^Appearance/ }));
    if (await appearance.count()) await appearance.first().click();
    await page.waitForTimeout(1000);
    await page.screenshot({ path: `${shot}-settings.png` });
    await page.getByRole('button', { name: accent, exact: true }).click();
    await page.waitForTimeout(3000);
  });
  await page.screenshot({ path: `${shot}-desktop.png` });
  const dump = await page.evaluate(async () => {
    const open = () => new Promise((res, rej) => { const r = indexedDB.open('kazoos'); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
    const db = await open();
    const stores = [];
    for (const name of Array.from(db.objectStoreNames)) {
      const tx = db.transaction(name, 'readonly');
      const store = tx.objectStore(name);
      const records = await new Promise((res, rej) => { const r = store.getAll(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
      const keys = await new Promise((res, rej) => { const r = store.getAllKeys(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
      const indexes = Array.from(store.indexNames).map(i => { const ix = store.index(i); return { name: ix.name, keyPath: ix.keyPath, unique: ix.unique, multiEntry: ix.multiEntry }; });
      const encode = (v) => {
        if (v instanceof ArrayBuffer) v = new Uint8Array(v);
        if (ArrayBuffer.isView(v)) { let s = ''; const b = new Uint8Array(v.buffer, v.byteOffset, v.byteLength); for (const x of b) s += String.fromCharCode(x); return { $type: v.constructor.name, base64: btoa(s) }; }
        if (v instanceof Date) return { $type: 'Date', value: v.toISOString() };
        if (Array.isArray(v)) return v.map(encode);
        if (v && typeof v === 'object') { const o = {}; for (const k of Object.keys(v)) o[k] = encode(v[k]); return o; }
        return v;
      };
      stores.push({ name, keyPath: store.keyPath, autoIncrement: store.autoIncrement, indexes, records: records.map(encode), keys: store.keyPath === null ? keys.map(encode) : undefined });
    }
    const version = db.version; db.close();
    return { version, stores };
  });
  const fixture = {
    $comment: 'IndexedDB fixture produced by booting an old Kazo OS build; see doc/en/wasm/dev/building-and-testing.md.',
    databaseName: 'kazoos', version: dump.version,
    producedFrom: { commit, method: 'booted', host: 'OS/Kazo.Os.Ecosystem (Release, trimmed publish)', producedOn: new Date().toISOString().slice(0, 10) },
    expectations: { admin, file: { path: `${file.directory}/${file.name}`, content: file.content }, setting: { document: 'appearance', key: 'accent', value: accent, path: `/home/${admin.loginName}/.config/kazoos/appearance.config` } },
    objectStores: dump.stores
  };
  if (dump.version !== version) throw new Error(`Expected DB version ${version}, got ${dump.version}`);
  fs.writeFileSync(outJson, JSON.stringify(fixture, null, 2) + '\n');
  console.log('wrote', outJson, dump.stores.map(s => `${s.name}:${s.records.length}`).join(' '));
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
