> [English version](../../../en/wasm/framework/file-drag-drop-api.md)

# Glisser-déposer des fichiers : `KazoFileDropZone`, `KazoFileDragSource`, `KazoFileDrag`

Une app peut recevoir des fichiers glissés depuis l'explorateur de fichiers, le bureau, une autre app
ou l'ordinateur réel, et donner ses propres fichiers à l'explorateur et au bureau
([ADR 0287](../dev/adr/0287-drop-files-into-an-app.md)). Un dépôt donne des **chemins**, jamais des
droits : l'app lit les fichiers avec ses propres capacités, comme sous Windows ou Linux. Un dépôt
dans une app est **toujours une copie** : la source garde ses fichiers.

## Recevoir des fichiers (`KazoFileDropZone`)

```razor
@using Kazo.Os.AppSdk.Blazor.Files

<KazoFileDropZone Accept="@(["audio/*", ".m3u"])" FilesDropped="OnFilesDroppedAsync">
    <PlaylistView />
</KazoFileDropZone>

@code {
    private async Task OnFilesDroppedAsync(KazoFilesDroppedEventArgs drop)
    {
        foreach (VirtualFileDragPayload file in drop.Files)
        {
            await using KazoFileStream stream = await (await KazoFileInfo.GetAsync(file.FilePath)).OpenReadAsync();
            // … lire la piste
        }

        // Fichiers de l'ordinateur : les lire, ou les écrire dans le système de fichiers de Kazo OS.
        await KazoFileOperations.ImportAsync(drop.HostFiles, "/home/benoit/Musique");
    }
}
```

| Paramètre | Rôle |
|---|---|
| `Accept` | Types MIME (`audio/mpeg`, `audio/*`) ou extensions (`.m3u`). Au dépôt, les fichiers qui ne correspondent à aucun sont retirés ; un dossier correspond toujours. Vide ou absent : tout fichier. |
| `FilesDropped` | Reçoit `KazoFilesDroppedEventArgs` quand il reste quelque chose après `Accept`. |
| `Class`, autres attributs | Passés à l'élément de la zone. |

`KazoFilesDroppedEventArgs` :

- `Files` : les fichiers de Kazo OS (`VirtualFileDragPayload` : chemin, nom, taille, type MIME, app source) ;
- `Folders` : les dossiers (`VirtualFolderDragPayload`) ; l'app décide d'y regarder ou non ;
- `HostFiles` : les fichiers de l'ordinateur (`KazoHostFile` : nom, taille, type MIME, `OpenReadStreamAsync(maxBytes, ct)`).
  Pour les écrire dans Kazo OS comme l'explorateur, `KazoFileOperations.ImportAsync(drop.HostFiles, dossier)`
  ([`file-operations-api.md`](file-operations-api.md)) : suffixe de copie pour un nom déjà pris, droits de l'app
  (`filesystem.user-home.write` pour `/home/<utilisateur>`).

Les fichiers de l'ordinateur sont libérés quand le gestionnaire `FilesDropped` se termine : l'app les lit ou
les enregistre avant de rendre la main.

Pendant le survol, l'élément porte la classe `kazo-file-drop-over` (un contour pointillé à la couleur
d'accent par défaut), que l'app peut redessiner dans son CSS.

## Donner des fichiers (`KazoFileDragSource`)

```razor
<KazoFileDragSource Files="@SelectedTrackPaths">
    <TrackRow />
</KazoFileDragSource>
```

- `Files` et `Folders` : les chemins de Kazo OS que l'élément glisse.
- Le navigateur demande les données pendant le `dragstart`, sans attente possible : le composant prépare le
  glisser à chaque changement de `Files` ou `Folders` (`KazoFileDrag.CreateAsync`). Tailles, noms et types
  viennent de la passerelle de fichiers de l'app, avec ses capacités : un chemin que l'app ne peut pas lire
  n'est pas mis dans le glisser. Sans rien de lisible, l'élément n'est pas glissable.
- `Drag` donne le glisser préparé.
- L'explorateur, le bureau et une autre `KazoFileDropZone` reçoivent ce glisser comme celui d'un autre
  explorateur, mais **copient toujours** (`effectAllowed = 'copy'`) : une app ne vide pas un dossier de
  l'utilisateur par un glisser.

## Le format (`KazoFileDrag`, app écrite en JavaScript)

Le type `DataTransfer` est `KazoFileDrag.MimeType` (`application/vnd.kazoos.file-drag+json`), le JSON est
celui que l'explorateur a toujours écrit :

```json
{"files":[{"sourceAppId":"…","filePath":"/home/benoit/a.mp3","fileName":"a.mp3","sizeBytes":12,"mimeType":"audio/mpeg"}],
 "folders":[{"sourceAppId":"…","folderPath":"/home/benoit/Musique","folderName":"Musique","itemCount":-1}]}
```

- `KazoFileDrag.TryParse(json, out KazoFileDragData data)` lit ce texte sans jamais lever : JSON invalide,
  chemin non absolu, nom vide ou plus de `KazoFileDrag.MaxItems` (1 000) éléments donnent `false`.
- `KazoFileDrag.Serialize(data)` écrit le texte, à mettre dans le `DataTransfer` pendant le `dragstart`.
- `KazoFileDrag.CreateAsync(fichiers, dossiers)` prépare un glisser depuis des chemins, comme
  `KazoFileDragSource`.

Une app qui gère elle-même `drop` lit `event.dataTransfer.getData('application/vnd.kazoos.file-drag+json')`
et passe le texte à `TryParse`.

## Hors de ce document

Le dépôt dans une app Craft (iframe) et dans une app rendue depuis un Web Worker sont suivis par
[#634](https://github.com/Kazo-ca/Os/issues/634) et [#635](https://github.com/Kazo-ca/Os/issues/635).

## Tâches

Voir la liste de tâches de l'[ADR 0287](../dev/adr/0287-drop-files-into-an-app.md#tâches).
