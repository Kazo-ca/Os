> [Version française](../../../../fr/wasm/dev/adr/0242-framework-api-for-installed-apps.md)

# ADR 0242: the .NET API an installed app finds in the published OS

## Status

Proposed on 2026-11-09, with the recommendation of the question asked to
Benoit applied while waiting for his answer (option A: whole CoreLib). Starting
request: IRC, installed from the Store, stops in its window with
"MissingMethodException: Method not found: int
System.Int32.Parse(System.ReadOnlySpan`1<char>,System.IFormatProvider)".

- **Adjusts:** [ADR 0113](0113-trimming-implementation-adjustments.md) §1-2
  (only the framework is trimmed: `System.Private.CoreLib` no longer is) and
  [ADR 0182](0182-install-apps-from-github.md) §5.3 (the check now compares the
  parameter types, not their count).
- **Document:** [`trimmed-publish.md`](../trimmed-publish.md).

## Context

- The OS Release publish trims the .NET framework (`TrimMode=partial`,
  ADR 0113): ILLink removes every member that the OS and its built-in apps do
  not call. An app installed after the publish (Store, GitHub) is not a root:
  it finds what the OS kept, nothing more.
- ADR 0182 §5.3 checks every reference of a package against the loaded runtime
  at install time. Methods were compared by **name, generic arity and parameter
  count**. ILLink removes one overload and keeps another of the same length:
  `Int32.Parse(string, IFormatProvider)` is kept,
  `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)` is removed. IRC therefore
  passed the check and failed on the first message with mIRC colors.
- Exact check (full signatures) of the Store apps against the `main` publish of
  2026-11-09:

  | App | Members missing from the published OS |
  |---|---|
  | IRC 1.0.0 | `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)` (colors), `String.Compare(string, int, string, int, int, StringComparison)` (links) |
  | Webamp 1.1.0 | `String.Split(string[], StringSplitOptions)` (`.m3u`/`.pls` playlists, not hit yet) |
  | Chess 1.1.0 | none |

  All three members are in `System.Private.CoreLib`.
- Sizes of the `_framework` `.wasm` files (Brotli), same day:

  | Publish | Files | Size | Brotli |
  |---|---|---|---|
  | trimmed (before) | 111 | 30.0 MiB | 9.0 MiB |
  | whole CoreLib | 115 | 32.6 MiB | 9.6 MiB |
  | whole framework | 243 | 43.0 MiB | 12.8 MiB |

## Decision

1. **`System.Private.CoreLib` stays whole** in a trimmed publish of a host
   (`_KazoOsRootKazoOsAssemblies` in `build/Kazo.Os.Apps.targets`, so also for
   hosts of other repositories through `Kazo.Os.App.Build`). It is the core
   every app uses (base types, `string`, spans, numbers, core collections,
   tasks); it costs 0.6 MiB in Brotli. The other framework assemblies stay
   trimmed. `KazoOsKeepCoreLib=false` goes back to the previous trimming.
2. **The install check compares signatures.** `AppPackageApiCheck` decodes the
   signature of every method reference (`RuntimeTypeNames.MetadataProvider`)
   and compares it with the parameters of the loaded runtime's methods
   (`RuntimeTypeNames.Of`), in one shared text form
   (`System.ReadOnlySpan`1<System.Char>`, `!0`, `!!0`, `[]`, `&`).
   `IRuntimeApiSurface` gets a `HasMethod(…, parameterTypes)` overload; its
   default implementation keeps the old count comparison, so an existing
   implementation does not break. The `apps.package.missing-api` message names
   the exact overload. Return types are not compared.
3. **Apps avoid the missing members.** IRC 1.0.1 and Webamp 1.1.1 no longer
   call these three members and also work on the OS versions already published
   (3.2.x). IRC's `tests/installed` test sends a colored line with a link in the
   published OS.
4. **Regression test.** The test package installed by
   `build/publish-trimmed.sh` (`Tests/fixtures/installed-app`) calls the three
   members: if CoreLib were trimmed again, the check would refuse the package
   and the `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts`
   test would fail.

## Rejected options

- **B. Whole framework**: an installed app would find the whole shipped .NET
  API, but the first load goes from 9.0 to 12.8 MiB and from 111 to 243 files,
  for apps that use a small part of it.
- **C. Nothing more than the exact check**: size unchanged, but core overloads
  (`int.Parse` of a span) would be refused at install time, and every app author
  would have to know what the OS kept.

## Consequences

- An app that calls a member removed from another framework assembly
  (`System.Linq`, `System.Text.Json`…) is refused at install time with the exact
  overload, instead of failing in its window.
- An app installed before a new OS version is not checked again when the OS
  changes: a member removed by the new publish still gives an error in the
  window (ADR 0238), without taking the OS down.
- The OS fix ships with the next version (3.3.0); the app versions of
  decision 3 cover the OS versions already published.

## Question asked to Benoit

**Which part of the .NET framework does the OS keep whole for installed
apps?** A (recommended) whole CoreLib, +0.6 MiB; B whole framework, +3.8 MiB;
C nothing more, exact check only. Applied: A.
