> [Version française](../../../../fr/wasm/dev/plans/release-channels-plan.md)

# Plan: beta channel and automatic app updates

**Proposed on 2026-11-14**, waiting for Benoit's answers to the questions of
[ADR 0271](../adr/0271-beta-channel-and-automatic-app-updates.md). Series
`release-channels/base`. Nothing is coded.

## Goal

When a beta or stable version of Kazo OS ships, each app rebuilds, tests and
offers its update with no manual step; a beta of the OS is online at
`os-beta.kazo.ca` (and through `os.kazo.ca/beta`); merges into `main` stay
with Benoit.

## The intended path

1. An agent merges a series into `beta` and raises
   `3.5.0-preview.1` → `3.5.0-preview.2` in `build/Versions.props`.
2. `Verify Kazo OS WASM` passes on `beta`; `Release Kazo OS` pushes the
   prerelease packages to nuget.org, creates the tag `v3.5.0-preview.2` and
   the "pre-release" GitHub Release; the beta site is deployed.
3. `release.yml` notifies the app repositories. Each one moves its `beta`
   branch to the prerelease, builds, tests, and submits `x.y.z-beta.n` to the
   catalog's beta channel; the "rebuild only" submission is merged by the
   automation, compiled and signed by the catalog.
4. On `os-beta.kazo.ca`, the Store offers the beta versions.
5. Benoit merges `beta` → `main` with version `3.5.0`. Each app gets a PR
   "Kazo OS 3.5.0"; once green, it is published according to Q4 and Q5.

## Work items

| # | Branch | Repository | Content | Depends on |
| --- | --- | --- | --- | --- |
| 0 | `release-channels/0-plan` | Os | This evaluation: ADR 0271, plan, questions. | nothing |
| 1 | `release-channels/1-prerelease-from-beta` | Os | `beta` branch; `release.yml` and `deploy-wasm.yml` on `beta`; guards (suffix required on `beta`, refused on `main`); prerelease on nuget.org; `releases.md`, `ci-workflows.md`. | 0 |
| 2 | `release-channels/2-beta-site` | Os | Deployment of the beta site on every push of `beta`; `os.kazo.ca/beta` redirect; PWA name and "Beta" badge; "Try the beta" and "Back to the stable version" links. Benoit creates the hosting project, the DNS and the token. | 1, Q1 |
| 3 | `release-channels/3-os-channel` | Os | The OS knows its channel: official catalog address by channel (`catalog-beta.json`), beta versions offered and followed by the Store and `kazopkg` on a prerelease OS; the Store and `kazopkg` pick the newest version **compatible** with the OS's SDK. Public API to declare, detail ADR if needed. | 1 |
| 4 | `release-channels/4-catalog-beta` | Os-AppMarketPlace | `beta` branch, `catalog-beta.json`, retention of beta versions, "rebuild only" check in the tool, merge by the automation, documentation. | 3 (format) |
| 5 | `release-channels/5-template-workflows` | Os then Os-AppTemplate | Reusable workflows (`ci`, `publish-to-kazoos-store`, `publish-packages`, `tag-release`, new `sdk-update`), version script, `beta` branch of apps; `kazoos-solution` template; template regeneration; `app-repository.md`. | 1 |
| 6 | `release-channels/6-notify-apps` | Os | `build/app-repositories.txt`, `repository_dispatch` sent by `release.yml`, organization token. | 1, 5, Q6 |
| 7 | `release-channels/7-<app>` | each app repository | Align the repository on the reusable workflows: Chess (workflows only), Webamp, IRC, EmulatorJS, Word, Spreadsheets, Pdf, Fig, Photo, then Deck, Design and Vector when they exist. One thread per repository, in parallel. | 4, 5, 6 |
| 8 | `release-channels/8-trial-and-close` | Os | Full trial: a prerelease, the beta site, an app in beta, then the stable version; `features.md`, memory, closing report. | 1 to 7 |

Work items 2, 3 and 5 can move in parallel after 1; 4 starts when the format
of `catalog-beta.json` is fixed by 3.

## What Benoit must do himself

- Answer questions Q1 to Q6.
- Depending on Q1: create the beta site (Cloudflare Pages project, domain
  `os-beta.kazo.ca`) and the deployment token, as a secret of the `Os`
  repository.
- Depending on Q6: create the GitHub App (or the token) and the organization
  secret.
- Check that nuget.org's Trusted Publishing policy accepts runs of
  `release.yml` from the `beta` branch.
- Protect the `beta` branch of `Os` (pull request required, green check).
- Merge the series into `main` when he decides to.

## Risks

| Risk | Answer |
| --- | --- |
| A broken prerelease blocks the 11 apps | A red app CI only opens an issue; nothing is published, the app's `main` is not touched. |
| Noise: 11 PRs per stable version | Q4 (publish only for a minor or a major) and Q5 (the automation merges rebuild-only submissions). |
| An automation signs unreviewed code | "Rebuild only" check: only the submodule and the version change, same capabilities; otherwise human review. |
| Cloudflare Pages limits (25 MiB per file, 20,000 files) | To measure in work item 2 on the `kazoos-wasm-<version>.zip` archive; fallback: a second GitHub Pages repository. |
| `beta` drifts away from `main` | `main` is merged into `beta` after each stable version; when in doubt, `beta` is created again from `main`. |

## Out of the plan

- Points 2 and 3 of issue #456 (check the installed apps before an OS update,
  keeping older versions).
- Copying a user's data between the stable site and the beta site.
- Aucun jeu sur ce repository.

## Tasks

- [x] Work item 0: evaluation, ADR 0271, plan.
- [ ] Work item 1: prerelease from `beta`.
- [ ] Work item 2: beta site.
- [ ] Work item 3: channel in the OS.
- [ ] Work item 4: catalog beta channel.
- [ ] Work item 5: reusable workflows and template.
- [ ] Work item 6: notify the apps.
- [ ] Work item 7: align each app repository.
- [ ] Work item 8: full trial and closing report.
