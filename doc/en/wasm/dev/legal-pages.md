> [Version française](../../../fr/wasm/dev/legal-pages.md)

# Privacy policy and terms of use

> This document and the pages it describes are not legal advice. They describe
> what the code does; have a lawyer review them before relying on them.

## Purpose

Dropbox (production approval) and Microsoft Entra (publisher verification,
"Branding") require a public privacy policy and terms of use to approve the
cloud drives ([`cloud-drives.md`](cloud-drives.md)). They also serve users:
they say exactly what Kazo OS does with data.

## Addresses

| Page | Stable address | Languages |
| --- | --- | --- |
| Privacy policy | `https://os.kazo.ca/privacy.html` (`#fr`, `#en`) | French then English, on the same page |
| Terms of use | `https://os.kazo.ca/terms.html` (`#fr`, `#en`) | French then English, on the same page |

They are static pages of the host (`OS/Kazo.Os.Ecosystem/wwwroot/`), with no
WebAssembly and no script, styled by `legal.css`. The published service worker
keeps them offline; the beta serves them too (`os-beta.kazo.ca/privacy.html`).

## Where they are linked

| Place | Component | Link |
| --- | --- | --- |
| Footer of the sign-in and recovery screen | `OS/Kazo.Os.Ecosystem.Host/ProjectInformation.razor` (`a.project-privacy`, `a.project-terms`) | `privacy.html#fr` or `#en` after the language of the texts (`Project.LegalSection`) |
| Settings ▸ About | `Apps/System/Kazo.Os.Apps.Settings/SettingsWindow.razor` (`a.about-privacy`, `a.about-terms`) | same (`About.LegalSection`) |
| Consent screens | Dropbox console, Entra ▸ Branding | addresses above |

## Facts checked in the code (2026-11-17)

Each sentence of the pages rests on these facts. A change to one of them means
updating the pages and their "Effective" date.

| Fact | Source |
| --- | --- |
| No server of the publisher: os.kazo.ca is served by GitHub Pages, the beta by Cloudflare Pages | [`github-pages-deployment.md`](github-pages-deployment.md), [`beta-site.md`](beta-site.md), `kazoos-host.json` (`isServer: false`) |
| Accounts, files, settings in IndexedDB; hashed passwords; encrypted keyring | [`sensitive-settings.md`](sensitive-settings.md) |
| OAuth with PKCE, no secret; Graph permissions `Files.ReadWrite` or `Files.ReadWrite.AppFolder`, `User.Read`, `offline_access` | `Platform.Core/CloudAccounts/CloudOAuthProvider.cs` |
| Identity read: display name and account id (`users/get_current_account`, `/me`) | `CloudOAuthProvider.cs`, `CloudAccountStore.cs` (`DisplayName`, `ProviderAccountId`) |
| Tokens sealed AES-256-GCM on the device, key in the keyring, never synchronized | [ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md), [`cloud-accounts.md`](cloud-accounts.md) |
| Removal: unmount, erase, Dropbox revocation | `CloudAccountService.cs` (`RemoveAsync`) |
| Roboto font loaded from `fonts.googleapis.com` | `OS/Kazo.Os.Ecosystem/wwwroot/index.html` |
| Store catalog and packages on `raw.githubusercontent.com` | `Platform.Core/Apps/Packages/AppPackageDefaults.cs`, `AppPackageSource.cs` |
| Real network only with `network.real.access`, approved by an administrator | [`dangerous-capability-approval.md`](dangerous-capability-approval.md) |
| Kazo AI: key sent only to the chosen service; local model from Hugging Face | repository `Kazo-ca/Os-App-AI`, `doc/en/providers.md` |
| Optional server: home folder synchronization (so the encrypted keyring), network relay | [`synchronization.md`](synchronization.md), [`server-network-relay.md`](server-network-relay.md) |
| Code license: CC BY-NC 4.0 | `LICENSE.md` |

## Benoit's decisions (2026-11-17)

- Publisher: Benoit Robin, individual, Québec (Canada), also the person in
  charge of the protection of personal information (Law 25).
- Published contact: `confidentialite@kazo.ca` (dedicated address).
- Governing law: laws of Québec and federal laws of Canada, courts of Québec,
  subject to consumer rights.
- Google Fonts: disclosed in the policy; Roboto will be served by the site in a
  separate PR, after which the Google Fonts line leaves the policy.
- One pair of bilingual pages for every provider (choice of 2026-11-15,
  [`cloud-accounts.md`](cloud-accounts.md#public-pages-for-the-providers)).

## For Benoit to do

- Create or forward `confidentialite@kazo.ca`.
- Register both addresses in the Dropbox console (both apps) and in Entra ▸
  Branding.
- Microsoft publisher verification: a Microsoft partner program id (Microsoft
  AI Cloud Partner Program) and the `kazo.ca` domain verified in Entra.

## Tests

- `Tests/Kazo.Os.Ecosystem.Tests/ProjectInformationTests.cs`: the footer links
  and their section after the language (English, French, other).
- `Tests/Kazo.Os.Apps.Settings.Tests/SettingsWindowTests.cs`: labels of the
  About "Legal" line.
- `Tests/Kazo.Os.Pwa.E2E.Tests/PublishedPagesTests.cs`: the links on the
  published PWA (sign-in and About).
- `Tests/Kazo.Os.Pwa.E2E.Tests/CloudSignInWindowTests.cs`: both pages are
  static, without script, served and kept offline by the service worker.

## Task list

- [x] Read the code of cloud drives, the Store, the optional server and Kazo AI.
- [x] Rewrite `privacy.html` and `terms.html` (FR + EN).
- [x] Link the pages from the sign-in footer and About.
- [x] Unit and E2E tests.
- [x] FR + EN documentation and indexes.
- [ ] Serve Roboto from the site (separate PR), then remove Google Fonts from the policy.
