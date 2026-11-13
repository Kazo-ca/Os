> [English version](../../../en/wasm/dev/craft-module-scripts.md)

# Module Craft : les scripts (côté OS et proxy de l'iframe)

Chantier 3 de la série `craft-host/base`
([ADR 0259](adr/0259-shared-craft-app-host-module.md)). Les scripts de Kazo Word
(`word-host.js`, `word-proxy.js` et ses modules) deviennent ceux du module
`Kazo.Os.AppSdk.Craft`, sans rien de propre à une app : l'identifiant de l'app
vient de la page de l'iframe. La bibliothèque C# (`CraftFrame`, chantier 2) les
appelle ; le parcours de bout en bout et le guide pour les auteurs d'apps
viennent au chantier 4.

## Rôle

Une app Craft (application Rust compilée en WebAssembly de l'équipe ArtCraft)
tourne dans une iframe de sa fenêtre Kazo OS. Deux scripts font le lien :

- **`craft-host.js`**, côté OS : crée l'iframe, lui donne un `MessagePort` à
  chaque chargement, transmet les messages du proxy au C# de la fenêtre,
  rejoue sur la page de l'OS les raccourcis réservés au gestionnaire de
  fenêtres, suit le thème de l'OS. Il ne décide rien.
- **`craft-proxy.js`**, dans l'iframe : démarre l'app en mode « page hôte »
  (`data-host="page"`), relaie ses événements `craft-web-host` vers la fenêtre
  et les messages de la fenêtre vers l'app, exécute les commandes de la page
  (`craft-web-api`). Ses modules : `file-picker.js` (le sélecteur `rfd`
  passe par la fenêtre), `reserved-keys.js` (raccourcis de l'OS),
  `settings-store.js` (le `localStorage` de l'app tient les réglages de
  l'utilisateur Kazo).

## Fichiers

Dans `wasm2/KazoOs/Shared/Kazo.Os.AppSdk.Craft/wwwroot/`, servis à
`_content/Kazo.Os.AppSdk.Craft/` :

| Fichier | Rôle | Repris de Kazo Word |
| --- | --- | --- |
| `craft-host.js` | côté OS : `create`, `frameUrl`, `readColorScheme`, `replayKey` | `js/word-host.js` |
| `craft-proxy.js` | proxy : `readAppDescription`, `startProxy`, `startInPage`, `runCommand`, `prepareApp`, `runApp` | `host/word-proxy.js` |
| `file-picker.js` | intercepte le sélecteur de fichiers `rfd` et ses téléchargements | `host/file-picker.js` |
| `reserved-keys.js` | renvoie les raccourcis réservés à la fenêtre | `host/reserved-keys.js` |
| `settings-store.js` | `MemoryStorage` à la place du `localStorage` | `host/settings-store.js` |
| `craft-page.css` | la toile remplit la page de l'iframe | `host/word-proxy.css` |

## La page de l'iframe (dans l'app)

Chaque app garde sa page `wwwroot/host/index.html`, sans script en ligne, qui
décrit l'app avec des attributs de sa toile :

```html
<link rel="stylesheet" href="../../Kazo.Os.AppSdk.Craft/craft-page.css">
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
...
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
```

- `data-craft-app` : préfixe des messages (`gridcraft:ready`) et objet des
  commandes (`window.gridcraft`). Forme vérifiée : `^[a-z][a-z0-9_]{0,63}$`.
- `data-craft-build` : fichier de provenance du build (`{ "glue": "...js",
  "wasm": "..._bg.wasm" }`), relatif à la page.
- L'id de la toile reste celui que l'app cherche ; le proxy prend la toile qui
  porte `data-craft-app`.
- Un élément où l'app écrit son erreur de démarrage peut porter
  `data-craft-loading` (style de `craft-page.css`).

Une page qui ne décrit pas d'app (pas de toile, attribut absent ou mal formé)
répond quand même `failed` à la fenêtre dès `init`, avec la raison.

## Frontière avec le C# (`CraftFrame`)

Fixée avec le chantier 2. `CraftFrame` importe
`./_content/Kazo.Os.AppSdk.Craft/craft-host.js` et appelle
`create(container, dotnetRef, options)` :

| Option | Sens |
| --- | --- |
| `page` | adresse de la page de l'iframe, relative à la base du document de l'OS (`_content/<assemblage>/host/index.html`) ; obligatoire |
| `parameters` | dictionnaire texte → texte ajouté à l'adresse (une valeur vide reste un drapeau, ex. `webgl=`) |
| `title`, `language`, `reservedKeys`, `settings` | comme Kazo Word |

L'objet rendu a `focus`, `open(name, bytes)`, `request(method, params,
timeoutMs)`, `answerPick(id, files)`, `colorScheme`, `dispose`. Le module
rappelle `OnFrameMessage(type, value)` et `OnFrameSave(name, saveAs, bytes)`.
Types de `OnFrameMessage` : `ready`, `failed`, `dirty`, `key`, `pick`,
`setting`, `scheme`, et `error` (nouveau, voir plus bas). L'iframe a la classe
`craft-frame-iframe`.

## Ce que chaque app fournit (pas les scripts)

Les scripts ne connaissent ni les commandes ni les formats d'une app ; tout ce
qui diffère d'une app Craft à l'autre passe par le C# de la fenêtre. Exemple de
GridCraft 0.19.1 face à WordCraft :

- thème et langue : autres commandes de la page (`ui.set { dark }`,
  `app.language.set { language }`) ; la fenêtre les envoie par `request` ;
- « Enregistrer sous » : `gridcraft:save` a toujours `saveAs: false` ; la
  fenêtre réserve F12 et Ctrl+Maj+S (`reservedKeys`, cible `app`) et décide
  elle-même ; Ctrl+S reste à l'app (test `craft-proxy-frame.test.mjs`) ;
  le nom reçu porte l'extension du format choisi ;
- `dirty` vaut pour tous les documents de la page ; la liste des formats
  ouvrables est celle de l'app.

## Messages internes (fenêtre ↔ proxy)

`init`, `ready`, `failed`, `open`, `save`, `saved`, `dirty`, `focus`,
`command`, `result`, `pick`, `picked`, `key`, `setting`. Ils restent internes au
module (ADR 0259, §3) : le côté OS et le proxy viennent toujours du même OS.

## Ce qui change par rapport à Kazo Word

- **Identifiant de l'app en paramètre** : lu sur la toile ; les événements d'une
  autre app sur la toile sont ignorés.
- **Champs optionnels de `craft-web-host`** :
  - un `save` qui porte un `id` le transmet ; si `OnFrameSave` renvoie `true`
    (fichier écrit), le module répond `<app>:saved { id }` à l'app, qui efface
    alors ses modifications. Sans `id`, ou si le C# ne renvoie rien, rien n'est
    envoyé (comportement de Kazo Word) ;
  - un `failed` reçu **après** `ready` concerne un document ou une sauvegarde :
    il arrive au C# comme `error` avec `{ error, id }` ; avant `ready`, c'est
    l'échec du démarrage (`failed`) ;
  - une `version` inconnue est lue comme `"1"`.
- **Adresse de la page** donnée par le C# (`page`, `parameters`) au lieu d'un
  chemin fixe et des options `author` et `webgl`.
- **Non pris en charge pour l'instant** : `host`, `handoff` et
  `handoff-done` (l'app se comporte comme avec les ponts antérieurs), et
  l'ouverture par `file` (`Blob` lu par tranches) ; `open` envoie toujours
  `bytes`.

## Tests

`wasm2/KazoOs/Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests/` : un projet xUnit qui
lance chaque fichier de `scripts/*.test.mjs` avec `node --test` (sauté si
`node` est absent ; les machines de GitHub l'ont), dans la suite
`NonBrowser`. Une fausse app Craft (`fakecraft`) remplace WordCraft.

| Fichier | Couvre |
| --- | --- |
| `craft-proxy.test.mjs` | `ready`, `failed`, file d'attente avant le port, origine et source d'`init`, `focus`, `dirty`, `open`, `save` |
| `craft-proxy-commands.test.mjs` | `command` → `window.<app>.request` → `result`, erreurs, délai |
| `craft-proxy-frame.test.mjs` | raccourcis réservés, réglages, démarrage après `init`, `picked` |
| `craft-proxy-contract.test.mjs` | identifiant d'app, `id` de `save` et de `failed`, `saved`, description de la page, page sans app |
| `craft-host.test.mjs` | adresse de l'iframe, `init`, `failed` / `error`, `saved` après `OnFrameSave`, commandes, fermeture |

Lancer à la main : `node --test wasm2/KazoOs/Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests/scripts/*.test.mjs`.
L'interception réelle du sélecteur dans un navigateur est couverte par le
parcours Playwright du chantier 4.

## Décisions

- Les scripts sont livrés sans le `.csproj` : celui de la bibliothèque vient
  du chantier 2, qui sert le `wwwroot/` tel quel.
- Le proxy ne démarre que dans une iframe (`window.parent !== window`), comme
  page hôte ; importé par les tests, il ne fait rien.
- Aucun nom d'app Craft dans le code du module (WordCraft n'apparaît que
  dans des exemples en commentaire). Aucun jeu sur ce repository.

## Liste des tâches

- [x] Reprendre `word-host.js`, `word-proxy.js` et ses trois modules dans le module.
- [x] Identifiant d'app et build lus sur la toile (`data-craft-app`, `data-craft-build`).
- [x] Frontière avec `CraftFrame` fixée avec le chantier 2 (`page`, `parameters`, `error`, `saved`).
- [x] Champs optionnels `id`, `saved` de `craft-web-host`.
- [x] Tests `node --test` avec une fausse app, lancés par xUnit.
- [x] Documentation fr et en, index.
