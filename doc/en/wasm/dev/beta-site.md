> [Version française](../../../fr/wasm/dev/beta-site.md)

# Beta site: os-beta.kazo.ca

Work item 2 of the `release-channels` series ([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §3 and §6,
[plan](plans/release-channels-plan.md)). Every push of the `beta` branch publishes the OS to
`https://os-beta.kazo.ca`, a separate origin hosted by Cloudflare Pages;
`https://os.kazo.ca/beta` redirects there.

## Why a separate origin

On the stable site's origin, the beta would open the user's `kazoos` IndexedDB database: a beta
that upgrades the schema would leave the stable OS unable to open it again, the two service
workers would delete each other's caches, and the worker of `/` would serve its own page for
`/beta/`. On `os-beta.kazo.ca` the beta has its own database, caches and worker; nothing is
copied from one site to the other.

## What is in place

| Piece | Where | Role |
| --- | --- | --- |
| **Deploy Kazo OS beta site** workflow | `.github/workflows/deploy-beta-site.yml` | Push on `beta` (`wasm2/KazoOs/**` files or this workflow) and manual run from `beta`: checks the channel (`check-release-channel.sh beta`, the version must carry a suffix), publishes the Ecosystem in Release like `deploy-to-pages.yml` (without `legacy/`), checks the Cloudflare limits, reads the Cloudflare Pages project's production branch and deploys with `wrangler pages deploy`. A PR that changes this workflow or its scripts builds and checks without deploying. |
| "Kazo OS Beta" name | `build/tools/brand-beta-site.sh` | Before the publish, renames the PWA (`name`, `short_name` of `manifest.webmanifest`) and the tab (`<title>` of `index.html`). Before, not after: the service worker's asset manifest hashes the renamed files. Fails when the expected text is gone. |
| Host limits | `build/tools/check-static-site-limits.sh` | At most 20,000 files and 25 MiB per file. Measured 2026-11-14: **913 files, the largest 8.3 MiB** (`_framework/Kazo.Os.AppSdk.Icons.*.wasm`), 82 MiB in all. |
| "Beta" badge | `KazoOsVersionLabel` (Kazo.Os.AppSdk.Blazor) | Sign-in screen and Settings ▸ About: a version with a suffix (`3.4.0-preview.1`) shows the badge. |
| Links between channels | `KazoOsChannelLink` (Kazo.Os.AppSdk.Blazor), Settings ▸ About | On a beta: "Back to the stable version" to `https://os.kazo.ca/`. On the stable version served by `os.kazo.ca`: "Try the beta" to `https://os-beta.kazo.ca/`. Elsewhere (Kazo OS server, local build): nothing. |
| `os.kazo.ca/beta` | `build/pages/beta-redirect.html`, copied by `assemble-pages.ps1` to `beta/index.html` | Immediate redirect to `os-beta.kazo.ca`, keeping the query (`?autolaunch=<id>`). The service worker leaves `beta/` to the network like `legacy/`. |
| Hosts that redirect `/index.html` | `service-worker.published.js` (`withoutRedirectFlag`) | Cloudflare Pages answers `/index.html` with a redirect to `/`; the cached shell is then a "redirected" response, which a browser refuses for a navigation. The worker serves a copy without that flag. |

The channel comes from the version (`KazoOsSdk.Version`): nothing to configure in the OS.
`version.json` and `HostReleaseMonitor` (ADR 0174, 0236) work unchanged; each site reads its own.

## Setup by Benoit (once)

1. **Create the project.** Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages**
   tab → **Upload assets** (direct upload, no Git connection). Project name: `kazo-os-beta`.
   Drop a folder holding a plain `index.html` (any content) → **Deploy site**.
2. **Domain.** In the project → **Custom domains** → **Set up a custom domain** →
   `os-beta.kazo.ca` → **Continue** → **Activate domain**. The `kazo.ca` zone is already on
   Cloudflare: the DNS record (CNAME to `kazo-os-beta.pages.dev`) is created automatically.
3. **API token.** Avatar → **My Profile** → **API Tokens** → **Create Token** → **Create Custom
   Token**. Name: `Kazo OS beta site`. Permission: **Account** · **Cloudflare Pages** · **Edit**.
   Account Resources: **Include** · the account. **Continue to summary** → **Create Token**; copy
   the token (shown only once).
4. **Account ID.** Account home (Workers & Pages): **Account ID** in the right column; copy it.
5. **GitHub secrets.** Repository `Kazo-ca/Os` → **Settings** → **Secrets and variables** →
   **Actions** → **New repository secret**: `CLOUDFLARE_API_TOKEN` (the token), then
   `CLOUDFLARE_ACCOUNT_ID` (the ID).
6. Optional: if the project has another name, repository variable `BETA_SITE_PROJECT`
   (**Variables** tab) with that name.

The workflow runs only once the `beta` branch exists (work item 8). Without the secrets it fails
with a message pointing to this page.

## Checked

- `BetaSiteScriptTests` (Kazo.Os.Ecosystem.Tests): name changed in the real `wwwroot`, refusal of
  a file without the expected text, file-count and size limits.
- `KazoOsChannelLinkTests` (Kazo.Os.AppSdk.Blazor.Tests): channel read from the version, link
  target by host (`os.kazo.ca`, a lookalike domain, `localhost`).
- Chromium run of a `3.4.0-preview.1` publish passed through `brand-beta-site.sh`: "Kazo OS Beta"
  tab, BETA badge on the sign-in screen, Settings ▸ About with the badge and "Back to the stable
  version".

## Tasks

- [x] Measure the published site against the Cloudflare Pages limits.
- [x] `deploy-beta-site.yml` workflow, `brand-beta-site.sh` and `check-static-site-limits.sh` scripts.
- [x] `os.kazo.ca/beta` redirect, excluded from the service worker.
- [x] "Beta" badge, "Try the beta" and "Back to the stable version" links.
- [x] Cloudflare's "redirected" response served without the flag by the service worker.
- [ ] Cloudflare project, domain and secrets (Benoit, steps above).
- [ ] First real deployment (work item 8, when `beta` is created).
