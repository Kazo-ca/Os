> [Version française](../../../../fr/wasm/dev/plans/drive-providers-plan.md)

# Plan of the "third-party drive providers" series (`drive-providers/base`)

Approved by Benoit on 2026-11-17 (answers 1 A to 5 A). Decisions:
[ADR 0282](../adr/0282-third-party-drive-providers.md). A third-party
developer publishes in the Store a Feature that adds a drive type (like
Dropbox and OneDrive), and the connectors shipped with Kazo OS can be switched
off at publish.

**No code before Benoit's go-ahead**: this plan and the ADR are work item 0.

## Goal

- A third party writes a provider with the public SDK only, and it reaches
  os.kazo.ca through the Store, without republishing the OS.
- The OS keeps the sign-in to the service, the tokens, the mount point and the
  whole interface; the provider only talks to its service.
- Dropbox, OneDrive and S3 stay shipped with the OS, with a build property per
  connector and a run-time setting to switch them off.
- Everything works without a Kazo server.

## Work items

| # | Branch | Content | Depends on |
| --- | --- | --- | --- |
| 0 | `drive-providers/0-adr` | ADR 0282, this plan, `features.md`, memory. **Done.** | — |
| 1 | `drive-providers/1-direct-http` | Direct mode of `KazoHttpClient` (the browser's `fetch`, streamed body, no server), dangerous capability `network.direct.access`, hosts declared in the manifest and shown at approval, also works in a worker (ADR 0282 §4). Docs `sdk-network.md`, `capabilities.md`. | 0 |
| 2 | `drive-providers/2-provider-contract` | SDK: `Kazo.Os.AppSdk.Drives` (`KazoDriveProvider`, `KazoDriveFolder`, models, `KazoDriveException`, context), `FeatureDefinition.DriveProviders`, `driveProviders` of the manifest, schema, validator, generator diagnostics, capability `drives.provide`, reserved types (§1, §2). `PublicAPI.Unshipped.txt`. Nothing wired into the OS. | 0 |
| 3 | `drive-providers/3-shipped-connectors-switch` | Connectors moved to `Kazo.Os.Platform.CloudDrives`, `build/Kazo.Os.CloudDrives.targets` (`KazoOsDropboxDrives`, `KazoOsOneDriveDrives`, `KazoOsS3Drives`), run-time switches, `KazoOs:CloudDrives:Disabled` and the `disabled` key (§5). Publish test with all three `false`. | 0 |
| 4 | `drive-providers/4-platform-adapter` | Provider registry in `AppCatalog`, `FeatureDriveSource` (bridge to `INetworkDriveSource`), loading at first need in the Feature's context, dynamic sources in `NetworkDriveService` (hot add), "provider missing" lines (§3). Fake provider in `Tests.Support`. | 2, 3 |
| 5 | `drive-providers/5-declared-connection` | Declared connection: OAuth provider read from the manifest in `CloudProviderCatalog`, form with typed fields and sealed secrets, access token and secrets given only to the provider of the account, generic settings document (`client-id` per type) (§3). | 2 |
| 6 | `drive-providers/6-ui` | Explorer, Settings ▸ Cloud accounts and `mount -t` with no service name in code: name, icon, connection form and fields read through `KazoDriveInfo.GetNetworkDriveTypesAsync`; the Feature's name next to the service; greyed-out drive and "Install the provider". Texts in French and English, mobile included. | 4, 5 |
| 7 | `drive-providers/7-developer-kit` | Public conformance suite (`KazoDriveProviderTests`), `dotnet new` template of a provider, a complete sample provider, guide `framework/drive-providers.md`, `sdk-compat` fixture, E2E on the trimmed OS with an installed provider. | 1, 4, 5 |
| 8 | `drive-providers/8-store-check` | Os-AppMarketPlace repository: check of the declaration in `validate.yml` (type not reserved, `https` hosts, no bare wildcard). After an OS release that carries the contract. | 2, published release |
| 9 | `drive-providers/9-final` | Check of the series: build, every suite, trimmed publish, `features.md`, memory, minor SDK version (3.5.0). | 1 to 7 |

Work items 1, 2 and 3 run in parallel. Work items 4 and 5 run in parallel
next, then 6 and 7.

## Rules of the series

- One branch per work item, created from `drive-providers/base`, one PR to
  `drive-providers/base`. Never a merge into `main`: Benoit decides.
- Each PR adds its change entry (ADR 0186) and updates the docs in French and
  English, with the indexes.
- Merge bar: green local build and tests, E2E suites one at a time,
  `build/publish-trimmed.sh` and the `Tests/fixtures/sdk-compat` fixture when
  the work item touches them.
- A question that opens during a work item is asked to Benoit in the thread,
  with choices; the work item does not move on that point before his answer.

## Points to watch

- **Break of `Kazo.Os.Platform.Core`** (work item 3): the `CloudDrives.*`
  types change assembly. To write in the change entry and the release notes;
  the `PublicAPI.Shipped.txt` of Platform.Core loses those lines, the one of
  the new assembly gains them.
- **CORS** (work item 1): the direct mode reaches only the services that
  accept it from an isolated page (ADR 0131). The guide of work item 7 says so
  and gives the way to check it.
- **The user's sign-in** (work item 4): a provider that does not load, throws
  or is slow must never delay it; it is a required test.
- **Tokens** (work item 5): the refresh token never leaves the OS; a provider
  receives nothing from an account of another type; it is a required test.
- **The sample provider** (work item 7) must target a service that accepts
  CORS and whose app registration is free; the choice of the service is a
  question to ask Benoit at the start of that work item.

## What Benoit does himself

- Give the go-ahead to start the code.
- Decide when `drive-providers/base` goes to `main`.
- Merge the PR of work item 8 in Os-AppMarketPlace.
