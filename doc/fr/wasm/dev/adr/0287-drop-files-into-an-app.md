> [English version](../../../../en/wasm/dev/adr/0287-drop-files-into-an-app.md)

# ADR 0287 : déposer des fichiers de l'explorateur dans une app

## Statut

Acceptée le 2026-11-20 : Benoit a répondu Q3 = A (l'app peut aussi écrire les
fichiers de l'ordinateur dans le système de fichiers, comme l'explorateur),
Q4 = B (glisser depuis une app entre dans cette ADR) et Q5 = A ; Q1 et Q2 sans
réponse, donc la recommandation (A). Série `todo-wins/base`, pour l'issue
[#380](https://github.com/Kazo-ca/Os/issues/380) (`TODO`).

- **Complète :** l'[ADR 0058](0058-base-app-features.md) §1 (glisser-déposer de
  l'explorateur entre ses fenêtres) et l'[ADR 0160](0160-desktop-implementation-adjustments.md)
  §3 (glisser depuis le bureau).
- **Ne change pas :** le modèle des droits sur les fichiers de
  l'[ADR 0231](0231-network-drives-count-as-personal-files.md) (capacité selon le
  chemin), si la question 1 garde la recommandation.

## Contexte

- L'explorateur (`FileView`) et le bureau (`FileViewDesktop`) écrivent ce qu'on
  glisse dans le `DataTransfer` du navigateur sous le type
  `application/vnd.kazoos.file-drag+json`. Le contenu est
  `FileViewDragEnvelope`, interne à `Kazo.Os.Apps.FileExplorer` :
  `{"files":[…],"folders":[…]}`, avec `VirtualFileDragPayload` et
  `VirtualFolderDragPayload` du SDK (`sourceAppId`, chemin, nom, taille, type
  MIME ou nombre d'éléments).
- Une app installée ne peut pas recevoir ce dépôt proprement :
  - le type MIME et l'enveloppe ne sont écrits nulle part dans le SDK ;
  - Blazor ne donne pas `getData` du `DataTransfer` en C# : `FileView` lit le
    dépôt dans son propre JavaScript (`FileView.razor.js`, écoute de `drop` en
    phase de capture), qu'une app ne peut pas réutiliser ;
  - rien ne dit quels droits l'app a sur les fichiers déposés.
- Benoit (2026-11-06, conception de Webamp, question 15) : pas de glisser-déposer
  dans la première version de Webamp ; cette issue garde le contrat public pour
  plus tard (déposer des morceaux dans la liste de lecture). Tout éditeur qui
  ouvre des fichiers en profite aussi.
- Dans un vrai OS, déposer un fichier sur une fenêtre donne son chemin à l'app
  (`WM_DROPFILES` / `IDropTarget` sous Windows, `text/uri-list` sous X11 et
  Wayland, le presse-papiers de glisser sous macOS). L'app lit ensuite le fichier
  avec ses propres droits ; seul le bac à sable de macOS donne en plus un droit
  sur le fichier déposé.

## Décision

### 1. Le format devient public

- `Kazo.Os.AppSdk.DragDrop.KazoFileDrag` porte le type MIME
  (`KazoFileDrag.MimeType = "application/vnd.kazoos.file-drag+json"`) et lit ou
  écrit l'enveloppe `KazoFileDragData(Files, Folders)`, sérialisée avec un
  contexte JSON généré (sûr au rognage), noms en camelCase : **le même JSON**
  qu'aujourd'hui.
- `KazoFileDrag.TryParse(string? json, out KazoFileDragData data)` refuse sans
  exception un JSON invalide, un chemin qui n'est pas absolu, ou plus de
  1 000 éléments.
- L'explorateur et le bureau utilisent ce type public à la place de
  `FileViewDragEnvelope`, qui disparaît. Un glisser entre deux explorateurs ne
  change pas.
- Une app écrite en JavaScript (Webamp) peut lire le même type MIME dans son
  propre `drop` et passer le texte à `KazoFileDrag.TryParse`.

### 2. Une zone de dépôt pour les apps Blazor

`Kazo.Os.AppSdk.Blazor` gagne le composant `KazoFileDropZone` :

```razor
<KazoFileDropZone Accept="@(["audio/*", ".m3u"])" FilesDropped="OnFilesDropped">
    <PlaylistView />
</KazoFileDropZone>
```

- Son JavaScript (`KazoFileDropZone.razor.js`, chargé par le composant) écoute
  `dragover`, `dragleave` et `drop` sur son élément, lit le type MIME du §1 et,
  selon la question 3, les fichiers de l'ordinateur.
- `Accept` (facultatif) : types MIME (`audio/*`) ou extensions (`.m3u`). Pendant
  le survol, la zone dit `copy` si au moins un élément convient, `none` sinon ;
  au dépôt, les fichiers qui ne conviennent pas sont retirés. Un dossier convient
  toujours (l'app décide d'y chercher ou non).
- `FilesDropped` reçoit `KazoFilesDroppedEventArgs` : `Files`
  (`KazoDroppedFile` : chemin, nom, taille, type MIME, app d'origine),
  `Folders` (`KazoDroppedFolder` : chemin, nom) et, selon la question 3,
  `HostFiles`.
- Pendant le survol, l'élément reçoit la classe `kazo-file-drop-over`, que l'app
  habille dans son CSS.
- Le dépôt dans une app est **toujours une copie** : la zone met
  `dropEffect = 'copy'` et l'explorateur ne retire rien de la source.

### 3. Fichiers de l'ordinateur (question 3 : A)

La zone reçoit aussi les fichiers glissés depuis le vrai ordinateur, sous
`HostFiles` (`KazoDroppedHostFile` : nom, taille, type MIME), comme le dépôt dans
l'explorateur aujourd'hui. La zone n'écrit rien d'elle-même ; l'app choisit :

- `OpenReadStream(maxBytes)` : elle lit le contenu (un morceau joué, un document
  ouvert sans l'enregistrer) ;
- `SaveToAsync(string path, bool overwrite = false)` : elle l'écrit dans le
  système de fichiers de Kazo OS, comme l'explorateur le fait d'un dépôt. Le
  chemin passe par la passerelle de fichiers de l'app, avec ses capacités
  (`filesystem.user-home.write` pour `/home/<user>`) ; un nom déjà pris donne
  `IOException` sauf avec `overwrite`.

Les fichiers sont libérés à la fin du gestionnaire `FilesDropped` : l'app les lit
ou les enregistre avant de rendre la main.

### 4. Droits sur les fichiers déposés (question 1)

Recommandation A : **aucun droit nouveau**. L'app reçoit des chemins et les lit
avec ses capacités, comme dans un vrai OS de bureau (ADR 0231) : une app qui a
`filesystem.user-home.read` lit ce qu'on dépose depuis `/home/<user>` ou depuis
un lecteur réseau de l'utilisateur ; sans elle, la lecture répond « Permission
refusée ». Une app qui pose une zone de dépôt n'a rien de plus à déclarer.

### 5. Glisser depuis une app (question 4 : B)

`Kazo.Os.AppSdk.Blazor` gagne aussi le composant `KazoFileDragSource` :

```razor
<KazoFileDragSource Files="@SelectedTracks">
    <TrackRow />
</KazoFileDragSource>
```

- `Files` et `Folders` : les chemins Kazo OS que l'élément fait glisser. Le
  composant prépare le JSON du §1 à chaque rendu : le navigateur demande les
  données **pendant** `dragstart`, sans attente possible.
- Son JavaScript met l'attribut `draggable`, écrit le type MIME du §1 au
  `dragstart` et met `effectAllowed = 'copy'`.
- Le composant remplit les tailles, les noms et les types MIME par la passerelle
  de fichiers de l'app, avec ses capacités : un chemin que l'app ne peut pas lire
  n'est pas mis dans le glisser.
- L'explorateur et le bureau reçoivent ce glisser comme celui d'un autre
  explorateur, mais **copient toujours** (le `effectAllowed` d'une app ne permet
  pas le déplacement) : une app ne vide pas un dossier de l'utilisateur par un
  glisser.
- Une app écrite en JavaScript appelle `KazoFileDrag.Serialize(data)` et écrit
  le texte elle-même dans son `dragstart`.

### 6. Hors de cette ADR (question 5 : A)

Chacun devient une issue `TODO` :

- le dépôt dans une app Craft (sa page est dans un iframe, le dépôt doit passer
  par `craft-proxy.js`, ADR 0259) et le glisser depuis elle ;
- le dépôt dans une app qui s'affiche depuis un Web Worker et le glisser depuis
  elle.

## Questions posées à Benoit

Réponses de Benoit (2026-11-20) : « 9. A mais l'application peut écrire dans le
système de fichier (Comme l'explorateur de fichier) », « 10. b », « 11. A »
(questions 3, 4 et 5 de cette ADR) ; questions 1 et 2 sans réponse, donc A.

1. Quels droits une app a-t-elle sur les fichiers qu'on y dépose ?
   - A) Aucun droit nouveau : elle lit les chemins avec ses capacités
     (`filesystem.user-home.read` pour le dossier personnel et les lecteurs
     réseau). Simple, comme Windows et Linux. ✨(Recommandé, retenu)
   - B) Le dépôt donne un droit de lecture sur ces fichiers seulement
     (`filesystem.user-selected.read`, jusqu'à la fin du processus) : une app
     sans `filesystem.user-home.read` peut les lire, comme le bac à sable de
     macOS. Plus de code dans la passerelle de fichiers, et un dossier déposé
     pose la question de son contenu.
   - C) A maintenant, B dans une ADR suivante avec le sélecteur de fichiers
     (même droit pour un fichier choisi dans la boîte de dialogue).
2. Quelle forme pour l'API ?
   - A) Les deux : le format public dans `Kazo.Os.AppSdk` (`KazoFileDrag`) et le
     composant `KazoFileDropZone` dans `Kazo.Os.AppSdk.Blazor`. ✨(Recommandé, retenu)
   - B) Le format seulement : chaque app écrit son JavaScript de dépôt.
   - C) Le composant seulement : le format reste interne.
3. Les fichiers glissés depuis le vrai ordinateur ?
   - A) La zone les donne aussi (`HostFiles`, lus en flux, rien n'est écrit dans
     le système de fichiers). ✨(Recommandé, retenu, avec l'écriture par l'app :
     §3)
   - B) Fichiers de Kazo OS seulement ; un fichier de l'ordinateur est refusé
     pendant le survol.
4. Glisser **depuis** une app vers l'explorateur ou le bureau ?
   - A) Pas dans cette ADR : une issue `TODO`. ✨(Recommandé)
   - B) Dans cette ADR (`KazoFileDragSource`, §5). **(Retenu)**
5. Les apps Craft (iframe) et les apps affichées depuis un Web Worker ?
   - A) Pas dans cette ADR : une issue `TODO` pour chacune. ✨(Recommandé, retenu)
   - B) Les apps Craft dans cette ADR (relais par `craft-proxy.js`).

## Conséquences

- Webamp et tout éditeur peuvent recevoir des fichiers de l'explorateur et du
  bureau avec un seul composant, et en donner avec un autre ; une issue suit dans
  [Os-App-Webamp](https://github.com/Kazo-ca/Os-App-Webamp) quand une version de
  l'OS contient l'API.
- Nouvelle API publique (`PublicAPI.Unshipped.txt` de `Kazo.Os.AppSdk` et de
  `Kazo.Os.AppSdk.Blazor`) ; une app qui l'utilise demande la version du SDK qui
  la porte.
- Le JSON du glisser ne change pas : un explorateur d'une version précédente et
  une app de cette version se comprennent.

## Tâches

- [x] `KazoFileDrag`, `KazoFileDragData`, contexte JSON, tests de lecture
  (valide, invalide, chemin relatif, trop d'éléments).
- [x] L'explorateur et le bureau passent à `KazoFileDragData` ; les tests de
  `FileViewDragDropTests` restent verts.
- [x] `KazoFileDropZone` (composant, JS, CSS), `Accept`, fichiers de
  l'ordinateur (`OpenReadStream`, `SaveToAsync`) ; tests bUnit.
- [x] `KazoFileDragSource`, `KazoFileDrag.Serialize` ; l'explorateur et le bureau
  copient le glisser d'une app ; tests.
- [ ] Un test Playwright : glisser de l'explorateur vers une app de test, et de
  l'app vers l'explorateur.
- [x] Docs `framework/` (fr et en), `features.md`, entrée de changement.
- [ ] Issues `TODO` du §6 ([#634](https://github.com/Kazo-ca/Os/issues/634),
  [#635](https://github.com/Kazo-ca/Os/issues/635)), issue dans Os-App-Webamp.

## Notes de mise en œuvre

Ces notes précisent la forme du code sans changer les décisions :

- Les arguments du dépôt réutilisent les types du SDK : `Files` est une liste de
  `VirtualFileDragPayload`, `Folders` de `VirtualFolderDragPayload` et
  `HostFiles` de `KazoHostFile` (nom, taille, type MIME,
  `OpenReadStreamAsync(maxBytes, ct)`), au lieu de nouveaux types
  `KazoDropped*`.
- L'écriture d'un fichier de l'ordinateur dans Kazo OS (§3) passe par
  `KazoFileOperations.ImportAsync(drop.HostFiles, dossier)`, celle qu'emploie
  l'explorateur (suffixe de copie, taille limite, droits de l'app), au lieu
  d'un `SaveToAsync` par fichier.
- `Accept` s'applique au dépôt : pendant le survol, le navigateur ne donne pas
  le contenu du glisser, donc la zone dit `copy` dès qu'un glisser porte des
  fichiers.
- `KazoFileDrag.CreateAsync(fichiers, dossiers)` prépare le glisser de
  `KazoFileDragSource` ; une app écrite en JavaScript peut l'appeler aussi.
- Guide : [`framework/file-drag-drop-api.md`](../../framework/file-drag-drop-api.md).
