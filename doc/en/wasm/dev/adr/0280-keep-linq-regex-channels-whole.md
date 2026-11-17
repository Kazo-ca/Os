> [Version française](../../../../fr/wasm/dev/adr/0280-keep-linq-regex-channels-whole.md)

# ADR 0280: System.Linq, System.Text.RegularExpressions and System.Threading.Channels stay whole

## Status

Accepted on 2026-11-17: Benoit chose to fix the OS rather than the app ("OS
only"). Starting point: the Store of 3.4.1 refuses to install Kazo AI 0.1.1
(`apps.package.missing-api`).

- **Adjusts:** [ADR 0242](0242-framework-api-for-installed-apps.md) §1 (three
  more framework assemblies stay whole, next to CoreLib).
- **Document:** [`trimmed-publish.md`](../trimmed-publish.md).

## Context

- ADR 0242 keeps `System.Private.CoreLib` whole and trims the rest of the
  framework. An installed app only finds what the OS and its built-in apps call.
- Kazo AI 0.1.1 (`ca.kazo.ai`) is refused by the Store of os.kazo.ca (3.4.1).
  The published `_framework` files really lack these members (checked on the
  metadata of the served `.wasm` files, with `AppPackageApiCheck` unchanged):

  | Assembly | Missing members |
  |---|---|
  | `System.Text.RegularExpressions` | `MatchEvaluator`, `Regex.Replace(string, MatchEvaluator)`, `Regex.Replace(string, string, string, RegexOptions, TimeSpan)`, also `Match.NextMatch()` and `Regex.Match(string, int)` |
  | `System.Linq` | `Enumerable.Sum(IEnumerable<double>)` |
  | `System.Threading.Channels` | `ChannelWriter<T>.WriteAsync(T, CancellationToken)` |

  These are common calls the OS does not make itself. Kazo AI had been tested
  with `dotnet run`, which keeps the whole framework.
- The same assemblies in Brotli in 3.4.1 (trimmed): Linq 34.7 KiB, Regex
  99.5 KiB, Channels 14.5 KiB.

## Decision

1. **`System.Linq`, `System.Text.RegularExpressions` and
   `System.Threading.Channels` stay whole** in a trimmed publish of a host
   (`_KazoOsRootKazoOsAssemblies` in `build/Kazo.Os.Apps.targets`, so also for
   hosts of other repositories through `Kazo.Os.App.Build`).
   `KazoOsKeepAppFrameworkLibraries=false` trims them as before.
2. **The app does not change.** Kazo AI 0.1.1 installs on an OS carrying this
   decision. The app-side fix (Os-App-AI#5) is closed.
3. **Regression test.** The test package `Tests/fixtures/installed-app` calls
   the five members Kazo AI calls; the test
   `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts` of
   `build/publish-trimmed.sh` checks their result in the window. If these
   assemblies were trimmed again, the package installation would be refused.
4. **The rest of the framework stays trimmed**; the install check (ADR 0182
   §5.3, ADR 0242) still refuses a package calling a member removed elsewhere.

## Options not taken

- **Adapt the app only** (Os-App-AI#5, Kazo AI 0.1.2 without these calls): no
  extra size, the app also installs on 3.4.1, but every app author must avoid
  common LINQ, regular expression and channel calls.
- **Both**: the same extra size as the decision, plus an app version.
- **The whole framework**: +3.8 MiB Brotli (ADR 0242).

## Consequences

- Size of the first load (Brotli, measured by `build/publish-trimmed.sh` on
  2026-11-17): +56 KiB in total.

  | Assembly | 3.4.1 (trimmed) | whole |
  |---|---|---|
  | `System.Linq` | 34.7 KiB | 57.7 KiB |
  | `System.Text.RegularExpressions` | 99.5 KiB | 127.8 KiB |
  | `System.Threading.Channels` | 14.5 KiB | 19.4 KiB |
- Kazo AI 0.1.1, unchanged, passes the check on this publish (0 missing
  members; 5 on 3.4.1).
- On OS 3.4.1 or older, Kazo AI 0.1.1 is still refused: it declares
  `sdkCompatibility.minimumVersion` 3.4.1, so the Store of 3.4.1 offers it. It
  installs once the OS is updated.
- The API surface of ADR 0277 (`todo-wins/base` series) is read from the
  publish: it will hold these three whole assemblies with no other change.
