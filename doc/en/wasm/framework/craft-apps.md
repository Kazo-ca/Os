> [Version française](../../../fr/wasm/framework/craft-apps.md)

# Writing a Craft app with the `Kazo.Os.AppSdk.Craft` module

Guide for the author of a Kazo OS app that shows a Craft app (a Rust app by the ArtCraft team
compiled to WebAssembly: WordCraft, GridCraft, PdfCraft…) in an iframe of its window. The
`Kazo.Os.AppSdk.Craft` module ([ADR 0259](../dev/adr/0259-shared-craft-app-host-module.md)) does
the shared work; the app only writes what is its own. The port guide
([`craft-app-port-guide.md`](../dev/craft-app-port-guide.md)) says in which order to port an app;
this guide says how to write the code.

**Complete example**: the test package
[`Tests/fixtures/craft-app/Kazo.Os.Fixtures.CraftApp`](../../../../wasm2/KazoOs/Tests/fixtures/craft-app/Kazo.Os.Fixtures.CraftApp/)
is a whole Craft window (about 650 lines of C#, comments included) around a fake Craft app. The end-to-end journey
`TrimmedPublishTests.CraftApps.cs` installs it in the trimmed published OS and drives it.

## 1. What the module provides, what the app writes

| The module (shipped and served by the OS) | The app |
| --- | --- |
| `craft-host.js` (OS document) and `craft-proxy.js` (in the iframe), with the file picker, reserved shortcuts and settings ([`craft-module-scripts.md`](../dev/craft-module-scripts.md)) | its iframe page `wwwroot/host/index.html` (a few lines) |
| `CraftFrame`: the iframe and its messages, as .NET events | its Razor window, which wires those events |
| `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest`: bound files, save rule, title, close guard | its list of formats (`CraftDocumentFormats`) |
| `FrameStartup`: loading, ready, failed, timeout, "Retry" | its strings and screens |
| `ReservedKeys`, `CraftLanguage`, `PickRequest`, `WebStorageFile` | its Craft app's commands (theme, language, rename…) |
| — | the Craft app's build (`wwwroot/<app>/`) and its manifest |

The app's package holds **neither** the `Kazo.Os.AppSdk.Craft.dll` assembly **nor** the
`_content/Kazo.Os.AppSdk.Craft/` files: the OS has them (ADR 0221, "the SDK comes with the OS").
A fix to the module therefore reaches every installed Craft app with the OS update.

## 2. The project

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <ItemGroup>
    <!-- NuGet packages in Release, projects of the OS submodule in Debug (ADR 0176). -->
    <PackageReference Include="Kazo.Os.AppSdk.Craft" Version="…" />
  </ItemGroup>
</Project>
```

- Reference the module to compile, like `Kazo.Os.AppSdk.Blazor`. In the OS repository, a project
  references `Shared/Kazo.Os.AppSdk.Craft/Kazo.Os.AppSdk.Craft.csproj`.
- **Minimum OS version**: the manifest's `sdkCompatibility.minimumVersion` = the first Kazo OS
  version that holds the module. It is not released yet (series `craft-host/base`): Benoit picks
  its number at release time. Until then, the app builds against the OS submodule on
  `craft-host/base`, and its Store publication waits for that version.
- Usual capabilities: open and save dialogs, read and write of the home and private folders,
  launching apps (new window with Ctrl+N); see `CraftFixtureApp.Capabilities`.

## 3. The iframe page

`wwwroot/host/index.html`, served at `_content/<assembly>/host/index.html` (ADR 0243), with no
inline script:

```html
<link rel="stylesheet" href="../../Kazo.Os.AppSdk.Craft/craft-page.css">
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
…
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
```

- `data-craft-app`: prefix of the events (`gridcraft:ready`) and command object
  (`window.gridcraft`), form `^[a-z][a-z0-9_]{0,63}$`.
- `data-craft-build`: the build's provenance file (`{ "glue": "….js", "wasm": "…_bg.wasm" }`),
  relative to the page.
- The canvas id stays the one the Craft app looks for.

`CraftFrameOptions.PagePathOf("<assembly>")` gives that address to the C#.

## 4. The window

A `WindowAppBase` window with three states: loading, app, error. The skeleton (the test package
shows it whole, in `CraftFixtureWindow.razor.cs`):

```csharp
CraftFrame frame = new(JavaScript);
frame.Ready += OnReadyAsync;          // FrameStartup.OnReady, then theme, language, document
frame.Failed += OnFailedAsync;        // error screen, "Retry" button
frame.DirtyChanged += OnDirtyChangedAsync;
frame.Saving += OnSavingAsync;        // returns true when the file is written
frame.ErrorReported += OnErrorAsync;  // error after ready: a notice, the app goes on
frame.KeyPressed += OnKeyPressedAsync;
frame.PickRequested += OnPickRequestedAsync;
frame.Exporting += OnExportingAsync;   // optional: exports through a download link (ADR 0264)
frame.SettingChanged += OnSettingChangedAsync;
frame.SchemeChanged += OnSchemeChangedAsync;

await frame.StartAsync(_frameHost, new CraftFrameOptions(CraftFrameOptions.PagePathOf("Kazo.Spreadsheets"), "Kazo Spreadsheets")
{
    Language = CraftLanguage.Pick(AppContext.Culture, languagesOfTheApp),
    ReservedKeys = [.. ReservedKeys.All, ReservedKeys.Parse("F12", ReservedKeyTarget.App)],
    Settings = await settingsFile.LoadAsync(AppContext.CancellationToken),
}, AppContext.CancellationToken);
```

| `CraftFrame` event | What the window does |
| --- | --- |
| `Ready(appVersion)` | `FrameStartup.OnReady`, cancels the timeout, sends the theme (`GetColorSchemeAsync`) and the language with `RequestAsync`, opens the requested file (`OpenRequest.FromArguments(LaunchArguments)`) with `OpenAsync`, then `DocumentSession.Opened`. |
| `Failed(error)` | `FrameStartup.OnFailed`: error screen. "Retry": `DisposeAsync` of the frame, `FrameStartup.Retry`, a new `CraftFrame`. |
| `DirtyChanged(dirty)` | `DocumentSession.SetFrameDirty`, then the title (`DocumentSession.Title`). |
| `Saving(save)` | `DocumentSession.Decide(name, saveAs)`: rewrite, ask then bind, or copy; the OS save dialog when needed; `KazoFile.WriteAllBytesAsync`; `Saved` or `SaveFailed`. Return `true` only once the file is written: when the app's save carried an `id`, the module answers it `saved` and it clears its changes. |
| `Exporting(export)` | Optional, to subscribe **before** `StartAsync` ([ADR 0264](../dev/adr/0264-craft-app-exports-reach-the-window.md)): a file the app exports through a download link (`<a download>` on a `blob:`, PdfCraft). `DocumentSession.DecideCopy(name)`: always a copy, whatever the extension; the OS save dialog, `KazoFile.WriteAllBytesAsync`, then a "Copy saved" message. With no subscriber, the browser downloads the file as before. |
| `ErrorReported(error)` | A notice in the window; the app keeps running. |
| `KeyPressed(key)` | The chords reserved for the window (`ReservedKeyTarget.App`): Ctrl+N = new window (`KazoApps.LaunchAsync`), F12 or Ctrl+Shift+S = Save As when the app never says it (GridCraft). |
| `PickRequested(request)` | The OS Open dialog, filtered by `request.Extensions`, then **always** `AnswerPickAsync(request.Id, files or null)`: the app waits for the answer. |
| `SettingChanged(key, value)` | `WebStorageFile.Set`, then `SaveAsync` (Kazo Word gathers writes over 500 ms). |
| `SchemeChanged(scheme)` | The app's theme command (`ui.theme`, `ui.set { dark }`…). |

`IWindowCloseGuard.ConfirmCloseAsync`: `RequestAsync("ui.inspect")`, `OpenDocument.ListFrom`,
`DocumentSession.WithUnsavedWork`; "Yes" saves each document and closes only when every write
succeeded (see the pitfall in §6).

**`ui.inspect` has two shapes.** WordCraft gives `documents[{ id, title, dirty, active, path }]`;
GridCraft gives `documents[{ title, dirty }]` and `windows[{ doc, index, active }]` (`index` = the
document's place in `documents`). `OpenDocument.ListFrom(inspect, nameOfTitle)` reads both: one
document per id (`id` or `doc`), its path when the app gives it, else `nameOfTitle(title)`, the
file name the app gives it (GridCraft: `title + ".xlsx"`), to look up in `DocumentSession`.

**Language**: `CraftLanguage.Pick(culture, languages)` returns the regional code the app lists
(WordCraft: `pt-br`, `zh-hans`, `sr-latn`), else the two-letter code when listed (GridCraft: `pt`,
`sr`), else `en`. Passing the app's exact list is enough.

**Execution context**: the events come from JavaScript (`[JSInvokable]`). Every handler that
touches the SDK (`KazoFile`, `KazoApps`) starts with
`using IDisposable scope = KazoExecutionContext.Enter(AppContext);`.

**Title**: `DocumentSession.Title(untitled)` gives `*name — <App>`. For a document with no file,
pass the name the Craft app gives it (`ui.inspect`'s `title`) rather than a text of your own, so
the OS title bar and the app say the same.

## 5. The save rule

| Case | `SaveDecision.Action` |
| --- | --- |
| Name bound to a path, `saveAs` false, last save succeeded | `Rewrite`: rewrites that path, no dialog. |
| `saveAs` true, name bound to nothing, or the last save of that name failed | `AskAndBind`: OS dialog, then binding; rename the document in the app when the name changes, then `DocumentSession.Renamed`. |
| Copy format (`CraftDocumentFormat.IsCopy`) | `AskForCopy`: dialog, the binding does not change. |
| Export (`Exporting`, `DocumentSession.DecideCopy`) | `AskForCopy`, even for a document's name or format. |

"Modified" = the app says so, **or** a save did not complete (dialog cancelled, write refused): a
Craft app whose save has no `id` clears "modified" as soon as it hands the save over.

## 6. Pitfalls

- **The Craft app says it is clean before the write.** `craft-web-host` clears "modified" as soon
  as it hands over a save's bytes, before the window has written them (dialog, disk). A close
  guard that sent the save command and then read `ui.inspect` again would think the document
  saved and close the window during the write. The guard therefore waits for **its own** writes
  to end (the `Saving` handler's `Task`, or `DocumentSession`, which moves to `Saved` only once
  the file is written) and closes only when they all succeeded; the app's "modified" state is no
  proof.
- **No window menu in the SDK.** A Kazo OS window has no menu bar the app could fill: window
  commands (new window, Save As for GridCraft) go through reserved chords
  (`ReservedKeyTarget.App`), and the rest lives in the Craft app's own UI.

## 7. What the module does not do yet

- The `host` message of `craft-web-host` (with `autosave: true`, craft-libs#713, tracked by
  Kazo-ca/Os-App-Word#17): the module will send it once upstream publishes it. Until then, the
  Craft apps' AutoSave stays off in Kazo OS.
- `handoff` and `handoff-done`, and opening by `file` (a `Blob` read in slices): `open` always
  sends `bytes`.
- No base window component: each app writes its own (ADR 0259, §2).
- No window menu (see §6): a shared menu would need an OS API, outside this module.

## 8. Testing

- The window's logic is tested with xUnit on the module's types (`DocumentSession`,
  `PickRequest`, `ReservedKeys`…), as in `Tests/Kazo.Os.AppSdk.Craft.Tests`.
- The proxy is tested with a fake Craft app (`Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests`).
- The journey in the OS: `wasm2/KazoOs/build/publish-trimmed.sh` packs the test package,
  installs it with `kazopkg` and runs `A_craft_app_on_the_os_module_opens_saves_and_follows_the_os`:
  `xdg-open` of a `.fake` file, typing (`*` in the title), Ctrl+S (rewrite, `saved`), F12 (Save As
  through a reserved chord), an error after `ready`, light then dark theme, the file picker
  through the OS dialog, the close guard, a setting read back by a second window
  ([`trimmed-publish.md`](../dev/trimmed-publish.md)).

## Task list (work item 4 of the `craft-host/base` series)

- [x] Test package: Craft window on the module, fake Craft app, installed by `kazopkg`.
- [x] Playwright journey in the trimmed published OS (`TrimmedPublishTests.CraftApps.cs`).
- [x] This guide (fr and en), indexes, port guide rewritten on the module.
