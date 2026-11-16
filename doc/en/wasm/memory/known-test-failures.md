> [Version française](../../../fr/wasm/memory/known-test-failures.md)

# Known pre-existing test failures

State as of **2026-11-16**. These tests fail or are flaky on `main` with no
link to any PR. A PR that sees them fail **names** them in its Tests section;
it does not disable, skip or quarantine them. If one of these tests passes
reliably again, remove it from this list. Causes already found and fixed are
in [`../dev/pitfalls.md`](../dev/pitfalls.md) §4.

## Fail every time (Linux cloud container)

| Suite | Test | Symptom |
| --- | --- | --- |
| `Kazo.Os.UI.E2E.Tests` | `LowStorageSpaceTests.A_full_quota_warns_once…` | "No low-space notification within 60 s". |

Fixed on 2026-11-16 by the `known-failures` series (#493, PRs #580 to #582): the
`Window_runtime_*` tests (real pen and touch gestures through CDP), the axe
`label-content-name-mismatch` tests of the taskbar, the Start menu and the
Calculator, and `Trimmed_publish_boots_offline_after_the_first_load` (no network
request any more while offline). The causes are in
[`../dev/pitfalls.md`](../dev/pitfalls.md) §4.

## Flaky (pass alone, sometimes fail under load)

- `AppInstallationService` (unit tests);
- `LocalNetworkTests` with a web worker;
- the detached-window E2E harness;
- `Kazo.Os.UI.E2E.Tests`: `FileExplorerDownload` and `BrowserHomePage` when
  the whole suite runs;
- `Kazo.Os.Platform.WorkerHost.Tests`: one timing test.

CI reruns a failed test once and reports "Flaky test" if it passes on the
second try ([`../dev/ci-workflows.md`](../dev/ci-workflows.md)).

## Recent environment tips

They complete [`../dev/pitfalls.md`](../dev/pitfalls.md) and
[`../dev/building-and-testing.md`](../dev/building-and-testing.md).

- The containers' `/usr/bin/dotnet` SDK (10.0.114 on 2026-10-30) still
  mis-compiles `ResponseBodyViewer.razor`. The Microsoft 10.0.403 SDK in
  `~/.dotnet`, first in `PATH` with `DOTNET_ROOT`, works.
- If the sandbox refuses `curl … dotnet-install.sh | bash`, read the SDK
  archive URL and its `sha512` from
  `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`,
  check the hash, then extract into `~/.dotnet`.
- In a container, headless Chromium crashes when reading an OPFS handle back
  from IndexedDB: a Playwright test of reconnecting a network drive
  (ADR 0181) is not possible there.
- To reproduce a flaky timing test, load the CPU
  (`for j in 1 2 3 4; do (timeout 300 sh -c 'while :; do :; done' &); done`)
  then rerun the test in a loop with `dotnet test --no-build --filter …`.
- Mobile home: there are two Settings buttons (`.mobile-home-screen` and the
  dock); in Playwright, take `.first()`.
