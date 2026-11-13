> [Version française](../../../fr/wasm/dev/trimmed-publish.md)

# Trimmed Release publication

## Purpose

The Release publication of the Ecosystem host (`OS/Kazo.Os.Ecosystem`) is
trimmed by ILLink (work item `v3/14-trimming`, ADR 0106 and 0113): the .NET
framework code that Kazo OS does not use is removed from the WebAssembly
files. The first load downloads less, and each Web Worker (ADR 0070) starts a
smaller runtime.

| Ecosystem publication | `.wasm` files | Size | Brotli |
|---|---|---|---|
| before (`PublishTrimmed=false`) | 273 | 50.9 MiB | 13.5 MiB |
| after (trimmed) | 134 | 26.5 MiB | 8.0 MiB |

Measured on 2026-10-17 with `build/publish-trimmed.sh --compare` (files of the
`fr/` satellites included). AOT is out of scope.

## Architecture

```
Ecosystem.csproj            PublishTrimmed=true, TrimMode=partial
        │
        ├─ CollectKazoOsBuiltInApplications (build/Kazo.Os.Apps.targets)
        │     ManifestGenerator aggregate --trimmer-roots
        │       → obj/.../BuiltInApplications.json
        │       → obj/.../Kazo.Os.TrimmerRoots.xml  ──► TrimmerRootDescriptor
        │
        └─ _KazoOsRootKazoOsAssemblies (avant PrepareForILLink)
              chaque Kazo.Os.*.dll publiée ──► TrimmerRootAssembly
                                    │
                              ILLink (publish)
            System.Private.CoreLib: whole (ADR 0242)
            rest of the framework and trimmable libraries: trimmed
            Kazo OS and app assemblies: whole
            non-trimmable packages (MudBlazor…): whole (partial)
```

- **Why roots.** The OS never calls an app's code directly: it loads its
  assembly on demand (`BlazorWebAssemblyLazyLoad`) and finds its entry point,
  its embeddable components and a site's Blazor root through the manifest names
  (`Assembly.GetType`, `Activator.CreateInstance`, loading by name in the
  workers). Without a root, ILLink removes these assemblies.
- **Why whole assemblies.** Kazo OS code passes its records to JavaScript
  interop, which serializes them by reflection without the trimming analysis
  seeing it. Member-by-member trimming breaks startup (storage "unavailable")
  for only 0.4 MiB of Brotli gained (ADR 0113 §1).
- **`Kazo.Os.TrimmerRoots.xml`** (`TrimmerRootDescriptorWriter` of the manifest
  generator): one `<assembly fullname="…" preserve="all" />` line per assembly
  that a manifest names (entry point, per-platform entries, components,
  `web.blazor`). It covers apps installed as packages, even when their name does
  not start with `Kazo.Os.`.
- **Apps installed after the publish (ADR 0242).** An app from the Store or
  GitHub is not a root: it finds the framework the OS kept. So
  `System.Private.CoreLib` stays whole (`TrimmerRootAssembly` in
  `_KazoOsRootKazoOsAssemblies`, +0.6 MiB in Brotli; `KazoOsKeepCoreLib=false`
  trims it as before). For the rest of the framework, `kazopkg` compares the
  full signature of every method the package references with the loaded
  runtime, and refuses the package with the missing overload
  (`apps.package.missing-api`). Before ADR 0242 only the parameter count was
  compared: IRC 1.0.0 passed the check and stopped on
  `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)`.
- **Warning suppressions.** The remaining `UnconditionalSuppressMessage`
  attributes (app discovery, workers, embeddable components, Blazor site router,
  MVC model binding) cite the root that makes them safe.

## Usage

### Publishing and testing

```bash
wasm2/KazoOs/build/publish-trimmed.sh            # publie, mesure, sert, teste
wasm2/KazoOs/build/publish-trimmed.sh --compare  # publie aussi sans trimming et compare
wasm2/KazoOs/build/publish-trimmed.sh --skip-build
```

The script publishes to `artifacts/publish-trimmed/publish`, fails on any `IL…`
warning, serves the publication on `127.0.0.1` and `127.0.0.2`, then runs
`Tests/Kazo.Os.TrimmedPublish.E2E.Tests`:

| Test | What it checks |
|---|---|
| `Trimmed_publish_runs_every_app_a_worker_command_and_both_sample_sites` | profile creation; each windowed app of the Start menu (read from `BuiltInApplications.json`, administrator approval included) opens its window without error; `seq 30 \| grep 1 \| wc -l` and `/proc/kazoos/workers` in the terminal; the Intranet (`.cshtml` view, script, REST API, Blazor table) and the sample Blazor site (counter, link, API) in the browser |
| `Trimmed_publish_boots_offline_after_the_first_load` | on `127.0.0.2` (the service worker is not registered on `127.0.0.1`): version caching, reload without network, login, launch of a never-loaded app |
| `The_calculator_rendered_in_a_worker_works_like_on_the_page` | ADR 0133 trial, setting enabled by rewriting `appsettings.json`: the Calculator in a worker computes with mouse and keyboard, keeps its CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, dedicated worker released on close ([`remote-window-rendering.md`](remote-window-rendering.md)) |
| `Measures_the_calculator_on_the_page_and_in_a_worker` | ADR 0133 measurements (opening, latency, memory, batches), written to `remote-window-measurements.json` in the artifacts folder |
| `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts` | ADR 0206: the test package `Tests/fixtures/installed-app` (packed by the script with `KazoOsPack`, answered by Playwright in place of `raw.githubusercontent.com`) is installed with `sudo kazopkg install`, the OS restarts, the app imports its module, starts its Web Worker (relative `importScripts`), shows its image and calls CoreLib overloads the OS does not use itself (ADR 0242); on `127.0.0.1`, through the `installed-apps` mode service worker |
| `The_published_service_worker_serves_installed_app_files_to_a_worker` | ADR 0206 on `127.0.0.2`: the published service worker answers files of the `kazoos-installed-apps` cache to a Web Worker under cross-origin isolation |
| `The_published_service_worker_serves_an_installed_app_page_to_an_iframe` | ADR 0243 on `127.0.0.2`: an HTML page copied into the `kazoos-installed-apps` cache opens in an iframe with its query (`?author=kazo&webgl`), isolated like the OS, online then offline, instead of the OS's `index.html` |
| `The_installed_apps_worker_serves_an_installed_app_page_to_an_iframe` | ADR 0243 on `127.0.0.1`: the same page, served by the worker of the `installed-apps` mode |
| `Trimmed_publish_shows_a_new_notification_as_a_bubble_above_the_taskbar` | ADR 0197: the what's new notification posted at a new account's sign-in shows as a bubble above the taskbar (source "Kazo OS", title, "View" button), leaves by itself after about 6 s and stays in the clock panel ([`notification-toasts.md`](notification-toasts.md)) |
| `Trimmed_publish_shows_unread_notifications_next_to_the_clock` | ADR 0196: a new account gets the what's new notification (`whats-new.json` served); clock dot and tooltip, no dot on Applications, opened panel = read, **View** opens Settings ▸ What's new and dismisses the notification; screenshots `trimmed-clock-dot.png` and `trimmed-whats-new-page.png` |
| `A_direct_link_with_no_account_starts_the_guest_installs_and_launches_the_app` | ADR 0258: `?autolaunch=<id>` with no account starts the guest with no click, installs the test package from the official catalog served by Playwright (signed by the run's key) and opens it; the address loses the parameter; a reload starts nothing again; a second visit continues as the guest and opens the app with no new download ([`app-launch-link.md`](app-launch-link.md)) |
| `A_direct_link_with_an_account_opens_the_store_at_the_app_page` | ADR 0258: with an account, the sign-in notice, then the Store at the app's page; nothing downloaded |
| `A_direct_link_reports_a_missing_app_and_can_be_cancelled` | ADR 0258: app missing from the official catalog → notification whose button opens the Store; **Cancel** during the download → no app and no notification |

The script requires all thirteen tests to pass (`EXPECTED_TESTS`). For the three direct link tests, it
creates a P-256 key on every run (`openssl`, in `artifacts/publish-trimmed/test-catalog-signing/`) and
publishes with `-p:KazoOsAppSigningKeys=catalog:official=<public key>`: the publish under test accepts
that key for the official catalog, next to the real one.

No console error is allowed. Without `KAZOOS_TRIMMED_URL`, the suite is
skipped (a `dotnet test` of the solution does not run it).

This test is part of the local merge gate of `v3/14` and of the following work
items (ADR 0106 §5). `build/verify-hosts.sh` also publishes the Ecosystem in
Release: its startup test therefore also covers the trimmed publication.

### A host from another repository

The `kazoos-host` template publishes trimmed in the same way. The
`Kazo.Os.App.Build` targets generate the roots and keep the `Kazo.Os.*`
packages whole; app templates start with `IsTrimmable=true`, and the package
enables the trimming analysis in an app project that does not set it.

### Pitfall: successive publications

The SDK's WebCIL conversion is incremental by file dates. A non-trimmed
publication (`-p:PublishTrimmed=false`) made after a trimmed publication in the
same `obj/` folder may pick up the trimmed assemblies. Before comparing, delete
`OS/Kazo.Os.Ecosystem/obj/Release/net10.0/linked`, `webcil` and `tmp-webcil`
(the script does it).

## Key decisions

- `TrimMode=partial` (ADR 0106 §1): packages that do not declare themselves
  trimmable stay whole.
- Kazo OS and app assemblies kept whole; only the framework is trimmed
  (ADR 0113 §1-2), except `System.Private.CoreLib`, kept whole for installed
  apps (ADR 0242).
- No generated table for the `[Route]` pages of a Blazor site: the site's
  assembly is whole (ADR 0113 §3).
- Offline test on `127.0.0.2` rather than `[::1]` (ADR 0113 §4).

## Tasks

- [x] `PublishTrimmed=true` and `TrimMode=partial` in the Ecosystem and the host template.
- [x] `Kazo.Os.TrimmerRoots.xml` generated by the aggregation and passed to ILLink.
- [x] `Kazo.Os.*` assemblies kept whole when publishing a host.
- [x] Warning suppression justifications updated.
- [x] `Kazo.Os.App.Build`: trimming analysis in app projects; templates `IsTrimmable=true`.
- [x] `build/publish-trimmed.sh` and `Tests/Kazo.Os.TrimmedPublish.E2E.Tests`.
- [x] Sizes before and after measured (here and in `building-and-testing.md`).
- [x] ADR 0113.
- [x] ADR 0242: whole CoreLib, exact signature check, test package that calls CoreLib overloads.
