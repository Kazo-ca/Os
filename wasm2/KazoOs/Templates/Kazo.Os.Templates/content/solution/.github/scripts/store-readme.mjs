// Copies the Kazo OS Store listing (store/) to the top of README.md and readme-fr.md, so the
// repository shows the same description and screenshots as the Store.
//
//   node .github/scripts/store-readme.mjs           rewrite the listing block of both READMEs
//   node .github/scripts/store-readme.mjs --check   fail (exit 1) when a README differs from store/
//
// The block sits between two HTML comments: "<!-- Store listing" ... "<!-- /Store listing -->" in
// README.md, "<!-- Fiche du Store" ... "<!-- /Fiche du Store -->" in readme-fr.md. Everything outside
// the block is left as written. Only Node.js is needed (no npm package).
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = process.cwd();
const check = process.argv.includes('--check');

const readmes = [
  {
    file: 'README.md',
    description: 'store/description.en.md',
    start: '<!-- Store listing: generated from store/ by .github/scripts/store-readme.mjs. Edit store/, then run it. -->',
    startPrefix: '<!-- Store listing',
    end: '<!-- /Store listing -->',
    screenshots: 'Screenshots',
    alt: (name, i) => `${name}, screenshot ${i}`,
  },
  {
    file: 'readme-fr.md',
    description: 'store/description.fr.md',
    start: '<!-- Fiche du Store : générée depuis store/ par .github/scripts/store-readme.mjs. Modifiez store/, puis lancez-le. -->',
    startPrefix: '<!-- Fiche du Store',
    end: '<!-- /Fiche du Store -->',
    screenshots: "Captures d'écran",
    alt: (name, i) => `${name}, capture ${i}`,
  },
];

/** Builds the listing block of one README from store/. */
function listing(readme) {
  const description = readFileSync(join(root, readme.description), 'utf8').replace(/\r\n/g, '\n').trimEnd();
  const name = (description.match(/^#\s+(.+)$/m)?.[1] ?? 'App').trim();
  const shotsDir = join(root, 'store', 'screenshots');
  // Screenshots in name order, the order the Store shows them (1.png, 2.png, ...).
  const shots = existsSync(shotsDir)
    ? readdirSync(shotsDir)
        .filter((f) => /\.(png|jpe?g|webp)$/i.test(f))
        .sort((a, b) => a.localeCompare(b, 'en', { numeric: true }))
    : [];
  const lines = [readme.start, ''];
  if (existsSync(join(root, 'store', 'icon.png'))) {
    lines.push('<img src="store/icon.png" alt="" width="96" align="right">', '');
  }
  lines.push(description);
  if (shots.length > 0) {
    lines.push('', `## ${readme.screenshots}`, '', '<p>');
    shots.forEach((f, i) => lines.push(`  <img src="store/screenshots/${f}" alt="${readme.alt(name, i + 1)}" height="240">`));
    lines.push('</p>');
  }
  lines.push('', readme.end);
  return lines.join('\n');
}

let failed = false;
for (const readme of readmes) {
  const path = join(root, readme.file);
  if (!existsSync(path)) continue;
  const text = readFileSync(path, 'utf8').replace(/\r\n/g, '\n');
  const lines = text.split('\n');
  const startLine = lines.findIndex((l) => l.startsWith(readme.startPrefix));
  const endLine = lines.findIndex((l) => l.trim() === readme.end);
  if (startLine < 0 || endLine < startLine) {
    console.error(`${readme.file}: no "${readme.startPrefix} ..." / "${readme.end}" block.`);
    failed = true;
    continue;
  }
  const updated = [...lines.slice(0, startLine), listing(readme), ...lines.slice(endLine + 1)].join('\n');
  if (updated === text) continue;
  if (check) {
    console.error(`${readme.file} does not show the Store listing of store/: run "node .github/scripts/store-readme.mjs" and commit.`);
    failed = true;
  } else {
    writeFileSync(path, updated);
    console.log(`${readme.file}: Store listing updated.`);
  }
}
process.exit(failed ? 1 : 0);
