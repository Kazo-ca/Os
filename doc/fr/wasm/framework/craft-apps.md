> [English version](../../../en/wasm/framework/craft-apps.md)

# Écrire une app Craft avec le module `Kazo.Os.AppSdk.Craft`

Guide pour l'auteur d'une app Kazo OS qui affiche une app Craft (une app Rust de l'équipe
ArtCraft compilée en WebAssembly : WordCraft, GridCraft, PdfCraft…) dans une iframe de sa
fenêtre. Le module `Kazo.Os.AppSdk.Craft` ([ADR 0259](../dev/adr/0259-shared-craft-app-host-module.md))
fait le travail commun ; l'app n'écrit que ce qui lui est propre. Le guide de portage
([`craft-app-port-guide.md`](../dev/craft-app-port-guide.md)) dit dans quel ordre porter une app ;
ce guide-ci dit comment écrire le code.

**Exemple complet** : le paquet de test
[`Tests/fixtures/craft-app/Kazo.Os.Fixtures.CraftApp`](../../../../wasm2/KazoOs/Tests/fixtures/craft-app/Kazo.Os.Fixtures.CraftApp/)
est une fenêtre Craft entière (environ 650 lignes de C#, commentaires compris) autour d'une fausse app Craft. Le
parcours de bout en bout `TrimmedPublishTests.CraftApps.cs` l'installe dans l'OS publié réduit
et le conduit.

## 1. Ce que le module fournit, ce que l'app écrit

| Le module (livré et servi par l'OS) | L'app |
| --- | --- |
| `craft-host.js` (document de l'OS) et `craft-proxy.js` (dans l'iframe), avec le sélecteur de fichiers, les raccourcis réservés et les réglages ([`craft-module-scripts.md`](../dev/craft-module-scripts.md)) | sa page d'iframe `wwwroot/host/index.html` (quelques lignes) |
| `CraftFrame` : l'iframe et ses messages, en événements .NET | sa fenêtre Razor, qui branche ces événements |
| `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest` : fichiers liés, règle d'enregistrement, titre, garde de fermeture | sa liste de formats (`CraftDocumentFormats`) |
| `FrameStartup` : chargement, prête, échec, délai, « Réessayer » | ses textes et ses écrans |
| `ReservedKeys`, `CraftLanguage`, `PickRequest`, `WebStorageFile` | les commandes de son app Craft (thème, langue, renommer…) |
| — | le build de l'app Craft (`wwwroot/<app>/`) et son manifeste |

Le paquet de l'app ne contient **ni** l'assembly `Kazo.Os.AppSdk.Craft.dll` **ni** les fichiers
`_content/Kazo.Os.AppSdk.Craft/` : l'OS les a (ADR 0221, « le SDK vient avec l'OS »). Une
correction du module arrive donc à toutes les apps Craft installées avec la mise à jour de l'OS.

## 2. Le projet

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <ItemGroup>
    <!-- Paquets NuGet en Release, projets du sous-module de l'OS en Debug (ADR 0176). -->
    <PackageReference Include="Kazo.Os.AppSdk.Craft" Version="…" />
  </ItemGroup>
</Project>
```

- Référencer le module pour compiler, comme `Kazo.Os.AppSdk.Blazor`. Dans le dépôt de l'OS, un
  projet référence `Shared/Kazo.Os.AppSdk.Craft/Kazo.Os.AppSdk.Craft.csproj`.
- **Version minimale de l'OS** : `sdkCompatibility.minimumVersion` du manifeste = la première
  version de Kazo OS qui contient le module. Elle n'est pas encore publiée (série
  `craft-host/base`) : Benoit choisit son numéro à la sortie. En attendant, l'app compile sur le
  sous-module de l'OS placé sur `craft-host/base`, et sa publication au Store attend cette
  version.
- Capacités usuelles : dialogues d'ouverture et d'enregistrement, lecture et écriture du dossier
  personnel et du dossier privé, lancement d'apps (nouvelle fenêtre par Ctrl+N) ; voir
  `CraftFixtureApp.Capabilities`.

## 3. La page de l'iframe

`wwwroot/host/index.html`, servie à `_content/<assembly>/host/index.html` (ADR 0243), sans
script en ligne :

```html
<link rel="stylesheet" href="../../Kazo.Os.AppSdk.Craft/craft-page.css">
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
…
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
```

- `data-craft-app` : préfixe des événements (`gridcraft:ready`) et objet de commandes
  (`window.gridcraft`), forme `^[a-z][a-z0-9_]{0,63}$`.
- `data-craft-build` : le fichier de provenance du build (`{ "glue": "….js", "wasm": "…_bg.wasm" }`),
  relatif à la page.
- L'identifiant du canvas reste celui que l'app Craft cherche.

`CraftFrameOptions.PagePathOf("<assembly>")` donne cette adresse au C#.

## 4. La fenêtre

Une fenêtre `WindowAppBase` en trois états : chargement, app, erreur. Le squelette (le paquet de
test le montre en entier, dans `CraftFixtureWindow.razor.cs`) :

```csharp
CraftFrame frame = new(JavaScript);
frame.Ready += OnReadyAsync;          // FrameStartup.OnReady, puis thème, langue, document
frame.Failed += OnFailedAsync;        // écran d'erreur, bouton « Réessayer »
frame.DirtyChanged += OnDirtyChangedAsync;
frame.Saving += OnSavingAsync;        // renvoie true quand le fichier est écrit
frame.ErrorReported += OnErrorAsync;  // erreur après ready : un message, l'app continue
frame.KeyPressed += OnKeyPressedAsync;
frame.PickRequested += OnPickRequestedAsync;
frame.SettingChanged += OnSettingChangedAsync;
frame.SchemeChanged += OnSchemeChangedAsync;

await frame.StartAsync(_frameHost, new CraftFrameOptions(CraftFrameOptions.PagePathOf("Kazo.Spreadsheets"), "Kazo Spreadsheets")
{
    Language = CraftLanguage.Pick(AppContext.Culture, languagesOfTheApp),
    ReservedKeys = [.. ReservedKeys.All, ReservedKeys.Parse("F12", ReservedKeyTarget.App)],
    Settings = await settingsFile.LoadAsync(AppContext.CancellationToken),
}, AppContext.CancellationToken);
```

| Événement de `CraftFrame` | Ce que la fenêtre fait |
| --- | --- |
| `Ready(appVersion)` | `FrameStartup.OnReady`, annule le délai, envoie le thème (`GetColorSchemeAsync`) et la langue par `RequestAsync`, ouvre le fichier demandé (`OpenRequest.FromArguments(LaunchArguments)`) par `OpenAsync`, puis `DocumentSession.Opened`. |
| `Failed(error)` | `FrameStartup.OnFailed` : écran d'erreur. « Réessayer » : `DisposeAsync` du cadre, `FrameStartup.Retry`, nouveau `CraftFrame`. |
| `DirtyChanged(dirty)` | `DocumentSession.SetFrameDirty`, puis le titre (`DocumentSession.Title`). |
| `Saving(save)` | `DocumentSession.Decide(name, saveAs)` : réécrire, demander puis lier, ou copie ; dialogue d'enregistrement de l'OS au besoin ; `KazoFile.WriteAllBytesAsync` ; `Saved` ou `SaveFailed`. Renvoyer `true` seulement quand le fichier est écrit : si la sauvegarde de l'app portait un `id`, le module lui répond `saved` et elle efface ses modifications. |
| `ErrorReported(error)` | Un message dans la fenêtre ; l'app tourne toujours. |
| `KeyPressed(key)` | Les touches réservées à la fenêtre (`ReservedKeyTarget.App`) : Ctrl+N = nouvelle fenêtre (`KazoApps.LaunchAsync`), F12 ou Ctrl+Shift+S = Enregistrer sous quand l'app ne le dit jamais (GridCraft). |
| `PickRequested(request)` | Dialogue d'ouverture de l'OS, filtré par `request.Extensions`, puis **toujours** `AnswerPickAsync(request.Id, fichiers ou null)` : l'app attend la réponse. |
| `SettingChanged(key, value)` | `WebStorageFile.Set`, puis `SaveAsync` (Kazo Word regroupe les écritures sur 500 ms). |
| `SchemeChanged(scheme)` | La commande de thème de l'app (`ui.theme`, `ui.set { dark }`…). |

`IWindowCloseGuard.ConfirmCloseAsync` : `RequestAsync("ui.inspect")`,
`OpenDocument.ListFrom`, `DocumentSession.WithUnsavedWork` ; « Oui » enregistre chaque document
et ne ferme que si toutes les écritures ont réussi (voir le piège du §6).

**`ui.inspect` a deux formes.** WordCraft donne `documents[{ id, title, dirty, active, path }]` ;
GridCraft donne `documents[{ title, dirty }]` et `windows[{ doc, index, active }]` (`index` = la
place du document dans `documents`). `OpenDocument.ListFrom(inspect, nameOfTitle)` lit les deux :
un document par identifiant (`id` ou `doc`), son chemin quand l'app le donne, sinon
`nameOfTitle(title)`, le nom de fichier que l'app lui donne (GridCraft : `title + ".xlsx"`), à
retrouver dans `DocumentSession`.

**Langue** : `CraftLanguage.Pick(culture, langues)` rend le code régional que l'app liste
(WordCraft : `pt-br`, `zh-hans`, `sr-latn`), sinon le code à deux lettres s'il est listé
(GridCraft : `pt`, `sr`), sinon `en`. Passer la liste exacte de l'app suffit.

**Contexte d'exécution** : les événements arrivent de JavaScript (`[JSInvokable]`). Chaque
gestionnaire qui touche au SDK (`KazoFile`, `KazoApps`) commence par
`using IDisposable scope = KazoExecutionContext.Enter(AppContext);`.

**Titre** : `DocumentSession.Title(untitled)` donne `*nom — <App>`. Pour un document sans
fichier, passer le nom que l'app Craft lui donne (le `title` de `ui.inspect`) plutôt qu'un texte
à soi, pour que la barre de titre de l'OS et l'app disent la même chose.

## 5. La règle d'enregistrement

| Cas | `SaveDecision.Action` |
| --- | --- |
| Nom lié à un chemin, `saveAs` faux, dernier enregistrement réussi | `Rewrite` : réécrit ce chemin, sans dialogue. |
| `saveAs` vrai, nom lié à rien, ou dernier enregistrement de ce nom raté | `AskAndBind` : dialogue de l'OS, puis liaison ; renommer le document dans l'app si le nom change, puis `DocumentSession.Renamed`. |
| Format de copie (`CraftDocumentFormat.IsCopy`) | `AskForCopy` : dialogue, la liaison ne change pas. |

« Modifié » = l'app le dit, **ou** un enregistrement n'a pas abouti (dialogue annulé, écriture
refusée) : une app Craft dont la sauvegarde n'a pas d'`id` s'efface « modifié » dès qu'elle
la remet.

## 6. Pièges

- **L'app Craft se dit propre avant l'écriture.** `craft-web-host` efface « modifié » dès qu'il
  remet les octets d'une sauvegarde, avant que la fenêtre ne les ait écrits (dialogue, disque).
  Une garde de fermeture qui enverrait la commande d'enregistrement puis relirait `ui.inspect`
  croirait le document enregistré et fermerait la fenêtre pendant l'écriture. La garde attend
  donc la fin de **ses** écritures (le `Task` du gestionnaire `Saving`, ou `DocumentSession`
  qui ne passe à `Saved` qu'une fois le fichier écrit) et ne ferme que si toutes ont réussi ;
  l'état « modifié » de l'app ne sert pas de preuve.
- **Pas de menu de fenêtre dans le SDK.** Une fenêtre Kazo OS n'a pas de barre de menus que
  l'app pourrait remplir : les commandes de fenêtre (nouvelle fenêtre, Enregistrer sous pour
  GridCraft) passent par des touches réservées (`ReservedKeyTarget.App`), et le reste vit dans
  l'interface de l'app Craft.

## 7. Ce que le module ne fait pas encore

- Le message `host` de `craft-web-host` (avec `autosave: true`, craft-libs#713, suivi par
  Kazo-ca/Os-App-Word#17) : le module l'enverra quand l'amont le publiera. En attendant,
  l'enregistrement automatique des apps Craft reste coupé dans Kazo OS.
- `handoff` et `handoff-done`, et l'ouverture par `file` (un `Blob` lu par morceaux) : `open`
  envoie toujours `bytes`.
- Pas de composant de fenêtre de base : chaque app écrit la sienne (ADR 0259, §2).
- Pas de menu de fenêtre (voir §6) : un menu partagé demanderait une API de l'OS, hors de ce module.

## 8. Tester

- La logique de la fenêtre se teste en xUnit sur les types du module (`DocumentSession`,
  `PickRequest`, `ReservedKeys`…), comme `Tests/Kazo.Os.AppSdk.Craft.Tests`.
- Le proxy se teste avec une fausse app Craft (`Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests`).
- Le parcours dans l'OS : `wasm2/KazoOs/build/publish-trimmed.sh` emballe le paquet de test,
  l'installe par `kazopkg` et lance `A_craft_app_on_the_os_module_opens_saves_and_follows_the_os` :
  `xdg-open` d'un fichier `.fake`, frappe (`*` dans le titre), Ctrl+S (réécriture, `saved`), F12
  (Enregistrer sous par une touche réservée), erreur après `ready`, thème clair puis sombre,
  sélecteur de fichiers par le dialogue de l'OS, garde de fermeture, réglage relu par une
  deuxième fenêtre ([`trimmed-publish.md`](../dev/trimmed-publish.md)).

## Liste des tâches (chantier 4 de la série `craft-host/base`)

- [x] Paquet de test : fenêtre Craft sur le module, fausse app Craft, installée par `kazopkg`.
- [x] Parcours Playwright dans l'OS publié réduit (`TrimmedPublishTests.CraftApps.cs`).
- [x] Ce guide (fr et en), index, guide de portage réécrit sur le module.
