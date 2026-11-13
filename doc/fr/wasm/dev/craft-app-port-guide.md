> [English version](../../../en/wasm/dev/craft-app-port-guide.md)

# Porter une app Craft dans Kazo OS

Guide interne, écrit le 2026-11-13 d'après le portage de **Kazo Word** (WordCraft, dépôt
[`Kazo-ca/Os-App-Word`](https://github.com/Kazo-ca/Os-App-Word)). Il dit comment refaire la
même chose pour les autres apps de la suite Craft de l'équipe ArtCraft (organisation GitHub
`storytold`) : la méthode, l'ordre des chantiers, les pièges déjà payés et ce qu'il faut
vérifier pour chaque app avant de commencer.

Le détail de Kazo Word reste dans son dépôt, à lire avec ce guide :
[`plan.md`](https://github.com/Kazo-ca/Os-App-Word/blob/main/doc/fr/plan.md) (but et décisions),
[`plan-verification.md`](https://github.com/Kazo-ca/Os-App-Word/blob/main/doc/fr/plan-verification.md)
(contre-vérification sur le code de WordCraft) et
[`integration-plan.md`](https://github.com/Kazo-ca/Os-App-Word/blob/word/base/doc/fr/integration-plan.md)
(fichiers, contrats, résultats de chaque chantier, sur la branche `word/base`).

## 1. Les apps à porter

| Dépôt Kazo | Nom dans Kazo OS | App amont | Build web amont (2026-11-13) |
| --- | --- | --- | --- |
| `Kazo-ca/Os-App-Word` | Kazo Word | [WordCraft](https://github.com/storytold/wordcraft) | `apps/wordcraft-web`, `craft-web-host` et `craft-web-api`. **Fait** (chantiers 1 à 4), Store en cours. |
| [`Kazo-ca/Os-App-Spreadsheets`](https://github.com/Kazo-ca/Os-App-Spreadsheets) ([TODO #1](https://github.com/Kazo-ca/Os-App-Spreadsheets/issues/1)) | Kazo Spreadsheets | [GridCraft](https://github.com/storytold/gridcraft) | `apps/gridcraft-web`, `craft-web-host` et `craft-web-api`. **Suivante**, dès que Kazo Word est publié au Store. |
| [`Kazo-ca/Os-App-PDF`](https://github.com/Kazo-ca/Os-App-PDF) ([TODO #1](https://github.com/Kazo-ca/Os-App-PDF/issues/1)) | Kazo Pdf | [PdfCraft](https://github.com/storytold/pdfcraft) | `apps/pdfcraft-web`, `craft-web-host` et `craft-web-api`. |
| [`Kazo-ca/Os-App-Fig`](https://github.com/Kazo-ca/Os-App-Fig) ([TODO #1](https://github.com/Kazo-ca/Os-App-Fig/issues/1)) | Kazo Fig | [FigCraft](https://github.com/storytold/figcraft) | `apps/figcraft-web`, `craft-web-host` et `craft-web-api`. |
| [`Kazo-ca/Os-App-Deck`](https://github.com/Kazo-ca/Os-App-Deck) ([TODO #1](https://github.com/Kazo-ca/Os-App-Deck/issues/1)) | Kazo Deck | [DeckCraft](https://github.com/storytold/deckcraft) | `apps/deckcraft-web`, `craft-web-host` et `craft-web-api`. |
| [`Kazo-ca/Os-App-Vector`](https://github.com/Kazo-ca/Os-App-Vector) ([TODO #1](https://github.com/Kazo-ca/Os-App-Vector/issues/1)) | Kazo Vector | [VectorCraft](https://github.com/storytold/vectorcraft) | `apps/vectorcraft-web`, `craft-web-api` seulement : **pas de mode hébergé**. |
| [`Kazo-ca/Os-App-Photo`](https://github.com/Kazo-ca/Os-App-Photo) ([TODO #1](https://github.com/Kazo-ca/Os-App-Photo/issues/1)) | Kazo Photo | [PhotoCraft](https://github.com/storytold/photocraft) | `apps/photocraft-web`, **ni `craft-web-host` ni `craft-web-api`**. |
| [`Kazo-ca/Os-App-Design`](https://github.com/Kazo-ca/Os-App-Design) ([TODO #1](https://github.com/Kazo-ca/Os-App-Design/issues/1)) | Kazo Design | [DesignCraft](https://github.com/storytold/designcraft) | `apps/designcraft-web`, **ni `craft-web-host` ni `craft-web-api`**. |

La colonne « build web » vient du `Cargo.toml` de `apps/<app>-web` sur la branche `main` de
chaque dépôt amont, lu le 2026-11-13. Elle change vite (une version par semaine environ) : la
revérifier à l'étape 0. Les sept dépôts Kazo et les forks `Kazo-ca/<app>craft` existent depuis le 2026-11-13 ; chaque dépôt a son issue `TODO` #1 qui suit ce guide. Toutes ces apps sont sous licence `MIT OR Apache-2.0`, écrites en Rust
avec egui et wgpu (WebGPU, repli WebGL 2), en alpha.

Une app sans `craft-web-host` ne peut pas être hébergée comme Kazo Word : elle ne sait ni
recevoir un fichier ni en rendre un à la page qui l'entoure. Il faut d'abord ce mode en amont
(issue et PR dans `storytold/craft-libs` et dans l'app, ouvertes par Benoit) ou dans notre fork,
avant le chantier 2 (section 4, étape 0).

## 2. L'architecture, la même pour chaque app

```
┌─ Fenêtre Kazo <App> (Blazor, C#) ────────────────────────────────┐
│ <App>Window.razor  ⇄  craft-host.js  (document de l'OS, module)   │
│   CraftFrame       │        ⇅  MessagePort (un par fenêtre)       │
│        │        ┌─ iframe  _content/Kazo.<App>/host/index.html ─┐ │
│        │        │ craft-proxy.js (module)                       │ │
│        │        │      ⇅  CustomEvent sur le canvas             │ │
│        │        │ <App>Craft (.wasm), data-host="page"          │ │
│        │        └───────────────────────────────────────────────┘ │
│  KazoFile, dialogues de l'OS, titre, fermeture                    │
└───────────────────────────────────────────────────────────────────┘
```

- **Une iframe par fenêtre.** Le canvas des apps Craft a un identifiant écrit en dur
  (`wordcraft_canvas`, `gridcraft_canvas`…) : une seule instance par document HTML. L'iframe
  donne une page par fenêtre, donc plusieurs fenêtres.
- **La fenêtre (C#) décide tout** : quel fichier est lié à quel document, où on écrit, le
  titre, l'état « modifié », la fermeture. Toute la logique testable est en C#.
- **`craft-host.js`** (dans le document de l'OS, lancé par le `CraftFrame` du module) crée
  l'iframe, tient le `MessagePort` et appelle le C#. Il ne décide rien.
- **`craft-proxy.js`** (dans l'iframe) parle à l'app Craft : événements `<app>:*` du canvas en
  mode `data-host="page"`, `window.<app>.request` (`craft-web-api`), sélecteur de fichiers,
  touches, `localStorage`. Il ne décide rien non plus.
- Un `MessagePort` par fenêtre, remis à l'iframe à son chargement : deux fenêtres ne se
  mélangent pas.
- Les deux scripts et les types C# sont au **module `Kazo.Os.AppSdk.Craft`**, livré et servi
  par l'OS ([ADR 0259](adr/0259-shared-craft-app-host-module.md)). Une app écrit sa fenêtre, sa
  page d'iframe, sa liste de formats et sa compilation : comment, dans
  [`craft-apps.md`](../framework/craft-apps.md), avec un exemple complet (le paquet de test
  `Tests/fixtures/craft-app`).

Les messages du port (`init`, `ready`, `failed`, `open`, `save`, `saved`, `dirty`,
`pick`/`picked`, `command`/`result`, `key`, `setting`, `focus`) sont internes au module
([`craft-module-scripts.md`](craft-module-scripts.md)) : une app ne les voit jamais, seulement
les événements de `CraftFrame`.

## 3. Ce que Kazo OS fournit déjà

Ces corrections ont été faites pour Kazo Word ; une nouvelle app Craft n'a rien à demander à
l'OS pour elles. **Version minimale : Kazo OS 3.3.2**, et pour le module commun, la première version de l'OS
qui le contient (pas encore publiée, voir la dernière ligne).

| Besoin | Réponse de l'OS | Version |
| --- | --- | --- |
| Servir la page de l'iframe d'une app **installée** (le service worker publié répondait `index.html` de l'OS) | [ADR 0243](adr/0243-serve-installed-app-pages.md) | 3.3.1 |
| Une fenêtre qui passe devant ne recharge plus son iframe (ordre stable des fenêtres dans la page, empilement en `z-index`) | [ADR 0244](adr/0244-stable-window-order-in-the-page.md) | 3.3.1 |
| Un fichier de plus de 16 Mio (le `.wasm` de WordCraft fait 60 Mo) : 128 Mio par fichier | [ADR 0245](adr/0245-raise-the-maximum-file-size.md), remplace la limite de l'[ADR 0094](adr/0094-deferred-decisions-status-and-maximum-file-size.md) | 3.3.2 |
| Paquet `.kazoapp` : 64 Mio compressé, 128 Mio décompressé, 2000 fichiers | `AppPackage` (`MaximumArchiveBytes`, `MaximumExtractedBytes`, `MaximumFiles`) ; installation : [`app-installation.md`](app-installation.md) | — |
| Dépôt d'app depuis le modèle, sous-module de l'OS, Store par un tag | [ADR 0176](adr/0176-app-repository-template.md), [`app-repository.md`](../framework/app-repository.md) | — |
| Signature seulement dans `Os-AppMarketPlace` | [ADR 0219](adr/0219-app-authors-signing-tool.md) | — |
| Le code Kazo commun des apps Craft : iframe, proxy, sélecteur de fichiers, raccourcis réservés, réglages par utilisateur, documents et règle d'enregistrement | module `Kazo.Os.AppSdk.Craft`, [ADR 0259](adr/0259-shared-craft-app-host-module.md), guide [`craft-apps.md`](../framework/craft-apps.md) | série `craft-host/base`, version de l'OS à venir |

Si une app dépasse une de ces limites (un `.wasm` de plus de 128 Mio, un paquet de plus de
64 Mio), c'est une nouvelle décision pour l'OS : une ADR dans `Kazo-ca/Os`, dans un fil à
part, puis une version de l'OS que l'app demande.

## 4. Les étapes

Une série `<app>/base` dans le dépôt de l'app (par exemple `spreadsheets/base`), une branche
`<app>/<n>-<sujet>` et une PR vers `<app>/base` par chantier. Le plan est écrit et fusionné
avant le code. Benoit fusionne `<app>/base` dans `main`.

### Étape 0 : vérifier l'app amont et écrire le plan

Avant tout code, lire l'app amont et noter dans `doc/fr/plan.md` (et `doc/en/plan.md`) :

- **Build web** : `apps/<app>-web` existe ? Dépend-il de `craft-web-host` et `craft-web-api` ?
  Quelle version de `craft-libs` (révision épinglée dans son `Cargo.toml`) ?
- **Messages du mode hébergé** : lire `apps/<app>-web/src/host.rs` (ou son équivalent) et la
  page `index.html` : identifiant du canvas, préfixe des événements (`gridcraft:open`…),
  ce que `save` envoie (`name`, `saveAs`, `bytes`, `id`), quand `dirty` change.
- **Commandes** : `window.<app>.request` existe ? `ui.inspect`, `ui.theme`, `ui.language`,
  `file.new`, `file.save`, `file.rename`, `ui.backstage` existent-ils sous ces noms ?
- **Sélecteur de fichiers** : où l'app ouvre-t-elle le sélecteur du navigateur (rfd) et pour
  quoi (document, image, polices, données) ? Chaque usage doit passer par le dialogue de l'OS.
- **Formats** : extensions ouvertes, enregistrées, exportées (les copies ne changent pas la
  liaison du document), types MIME, et lesquels associer à l'app (`open`, `edit`).
- **Compilation** : version de Rust, Trunk, révisions de `craft-i18n` et `craft-fonts`,
  workflow de release amont (dans `storytold/craft-common-actions`), zip officiel
  `<app>-web-<version>.zip` publié ou non.
- **Taille** : `.wasm` et paquet, contre les limites de la section 3.
- **Issues amont** ouvertes qui touchent le mode hébergé ou le web (une issue par problème en
  amont ; on se joint aux issues existantes).
- **Marque** : le nom et le logo ArtCraft sont des marques ; un build **modifié** doit les
  retirer, et la fiche du Store dit « basé sur <App>Craft de l'équipe ArtCraft », sans logo.

Puis poser à Benoit, dans le fil, les questions que cette lecture ouvre (avec choix,
contexte, implications et recommandation). Pour une app sans `craft-web-host` (aujourd'hui
PhotoCraft, DesignCraft, VectorCraft), la première question est : ajouter le mode hébergé en
amont (Benoit ouvre l'issue et la PR, préparées par Claude), ou le porter dans notre fork
seulement, en attendant ?

### Chantier 1 : solution et iframe (`<app>/1-solution`)

Le dépôt est créé depuis `Os-AppTemplate` (fil « Créer des dépôts GitHub depuis mon PC » :
une session cloud ne peut pas créer de dépôt).

- Renommer la solution du modèle (`Kazo.<App>`, `Kazo.<App>.Os`, `Kazo.<App>.Tests`),
  `[AppManifest]` : id `ca.kazo.<app>`, éditeur `pub.kazo`, catégorie, **plusieurs
  instances**, taille de fenêtre par défaut et minimum (celui de l'app Craft).
- `tools/vendor-<app>.sh` : copie un build (zip officiel épinglé par SHA-256 au début) dans
  `wwwroot/<app>/` avec un `<app>.json` de provenance (version, source, noms et SHA-256).
  Le build vit **dans le dépôt** : le Store compile avec `dotnet build -t:KazoOsPack`
  seulement et ne récupère que le sous-module `external/KazoOs` ; il n'a ni Rust ni Trunk.
- Référencer `Kazo.Os.AppSdk.Craft`. Seulement `wwwroot/host/index.html` : le canvas avec
  `data-host="page"`, `data-craft-app` et `data-craft-build`, le `craft-proxy.js` et le
  `craft-page.css` du module, aucun script en ligne ([`craft-apps.md`](../framework/craft-apps.md),
  §3). Aucun script propre à l'app.
- La fenêtre sur `CraftFrame` et `FrameStartup` : chargement, app, erreur (délai de 60 s,
  « Réessayer ») (`craft-apps.md`, §4 ; le paquet de test `Tests/fixtures/craft-app` en est un
  exemple complet).
- **Sous-module de l'OS sur `craft-host/base`** jusqu'à la sortie de la version de l'OS qui
  contient le module, puis sur cette version.
- Tests : manifeste, `<app>.json` égal aux fichiers, parcours Playwright qui attend `ready` ; le
  document survit quand sa fenêtre passe devant puis derrière. Le proxy lui-même est testé par
  l'OS (fausse app Craft, `node --test` et parcours de la publication réduite).
- **Terminé quand** : l'app s'affiche et répond au clavier en WebGPU et en WebGL 2
  (`--webgl` ajoute `?webgl` à l'iframe), deux fenêtres tournent ensemble, et **l'app
  s'installe dans un Kazo OS publié** (`sudo kazopkg install Kazo-ca/Os-App-<App>`). Noter la
  taille du paquet et la mémoire par fenêtre.

### Chantier 2 : ouvrir et enregistrer (`<app>/2-open-save`)

- Associations de fichiers et « Ouvrir avec », fichier demandé au lancement, intentions.
- Le `DocumentSession` du module (avec les `CraftDocumentFormats` de l'app) : chaque nom de
  document de l'app lié à un chemin. Sa règle d'enregistrement (`SaveDecision`), que la fenêtre
  applique sur `Saving` :

| Cas | Décision |
| --- | --- |
| `name` lié à un chemin et `saveAs` faux | Réécrit ce chemin. |
| `saveAs` vrai, ou `name` lié à rien | Dialogue d'enregistrement de l'OS, puis liaison au chemin choisi (et `file.rename` dans l'app si le nom change). |
| Format de copie (PDF, PNG, texte…) | Dialogue d'enregistrement, la liaison ne change pas. |

- Les apps Craft envoient `save` **sans confirmation** et s'effacent « modifié » tout de
  suite (craft-libs #611) : la fenêtre garde son propre état. « Modifié » = l'app le dit,
  **ou** un enregistrement n'a pas abouti. Un dialogue annulé ou une écriture refusée laisse
  la fenêtre « modifiée ».
- Une page Craft peut tenir **plusieurs documents** (Fichier › Nouveau) ; `dirty` ne parle
  que du document affiché. La garde de fermeture demande `ui.inspect` et enregistre chaque
  document avant de fermer.
- Démarrage : fichier demandé envoyé par `open`, sinon document vide ; fermer l'écran
  d'accueil de l'app s'il y en a un.
- **Terminé quand** : aucun cas ne perd de modifications sans que l'utilisateur l'ait choisi,
  y compris quand l'écriture échoue.

### Chantier 3 : le reste du cadre (`<app>/3-proxy`)

Le proxy du module fait déjà le côté navigateur ; la fenêtre branche les événements de
`CraftFrame` ([`craft-apps.md`](../framework/craft-apps.md), §4) et fournit ce qui est propre à
l'app :

- **Sélecteur de fichiers** (`PickRequested`) : le dialogue de l'OS, puis toujours
  `AnswerPickAsync`. Un document choisi par Fichier › Ouvrir s'ouvre dans une **nouvelle
  fenêtre** (sur place seulement pour un document vide jamais touché).
- **Raccourcis** (`ReservedKeys`, `KeyPressed`) : `ReservedKeys.All` (ceux de l'OS et `Ctrl+N`),
  plus ceux de l'app (pour GridCraft, F12 et Ctrl+Shift+S : Enregistrer sous décidé par la
  fenêtre).
- **Thème et langue** (`SchemeChanged`, `CraftLanguage`) : les commandes de l'app (`ui.theme`
  pour WordCraft, `ui.set { dark }` pour GridCraft) et sa liste de langues.
- **Réglages par utilisateur Kazo** (`WebStorageFile`, `SettingChanged`) :
  `~/.config/ca.kazo.<app>/web-storage.json`, réécrit au plus 500 ms après la première
  écriture non enregistrée.
- **Terminé quand** : aucun dialogue du navigateur ne s'ouvre depuis l'app, et les raccourcis
  de l'OS marchent le curseur dans le document.

### Chantier 4 : build depuis les sources (`<app>/4-build-from-source`)

- Fork `Kazo-ca/<app>craft` (créé par Benoit) en sous-module `external/<app>craft`. Le commit
  d'une release amont est souvent sur la branche `release`, que le fork ne copie pas : le
  garder par une branche `kazo/v<version>` du fork.
- `tools/build-<app>.sh` compile comme la release amont (révisions de `craft-i18n` et
  `craft-fonts` lues dans le sous-module, Trunk à la version exacte, cible
  `wasm32-unknown-unknown`, **Rust stable du jour**), puis `vendor-<app>.sh --dir`.
- Workflow `build-<app>.yml` : lancé à la main, il compile, pousse sur une branche, fait
  tourner la CI et ouvre la PR ; sur une PR qui touche le sous-module, il compile seulement.
- Un test xUnit vérifie que le commit de `<app>.json` est celui du sous-module dans l'index git.
- **Terminé quand** : monter de version = changer le commit du sous-module et lancer le workflow.

### Chantier 5 : Store (`<app>/5-store`)

- Icône sans logo ArtCraft, fiche fr et en (« basé sur <App>Craft de l'équipe ArtCraft »,
  limites connues), captures d'écran, `THIRD-PARTY-NOTICES.md`, licences dans le paquet.
- **README du dépôt** : il reprend la fiche du Store (texte de `store/description.fr.md` et
  `store/description.en.md`, captures), puis une section « Essayer » (place réservée pour le
  futur lien `https://os.kazo.ca/?autolaunch=<id>`) et une courte présentation de Kazo OS avec
  le lien vers `Kazo-ca/Os`. Modèle : [`app-repository.md`](../framework/app-repository.md)
  (modèle `kazoos-solution`, Kazo-ca/Os#469 et Kazo-ca/Os-AppTemplate#9) ; exemple complet :
  Kazo-ca/Os-App-Word#13. Les sept dépôts Craft ont été créés depuis l'ancien modèle : leur
  README est refait à ce chantier.
- Version minimale de l'OS dans le manifeste ; parcours Playwright dans un OS publié.
- Tag par le workflow « Tag a release » (une session cloud ne peut pas pousser de tag), PR au
  catalogue d'`Os-AppMarketPlace` (signature là-bas seulement), ligne dans `features.md`.
- **Terminé quand** : l'app s'installe depuis le Store et ouvre un fichier par double-clic.

**Ordre** : 1, puis 2, 3 et 4 en parallèle (C#, proxy, compilation : trois fils), puis 5.

## 5. Pièges déjà rencontrés

| Piège | Réponse |
| --- | --- |
| Un rappel `[JSInvokable]` (message du proxy) ne tourne pas dans le contexte de l'app : `KazoFile` refuse. | Chaque gestionnaire qui touche au SDK fait `using var s = KazoExecutionContext.Enter(AppContext);`. |
| L'événement `cancel` du champ de fichier de rfd fait paniquer WordCraft 0.18.1 (`removeChild`). | Annuler par un `change` sans fichier. |
| rfd appelle `input.click()` juste après avoir ajouté sa carte. | Remplacer `HTMLInputElement.prototype.click` dans le domaine de l'iframe. |
| `localStorage` est une propriété propre de la fenêtre de l'iframe ; les réglages sont partagés par tous les utilisateurs Kazo (même origine). | Copie en mémoire, alimentée et réécrite par la fenêtre (chantier 3). Les polices ajoutées (IndexedDB) restent partagées. |
| Deux écritures du fichier de réglages qui se chevauchent remettaient d'anciennes valeurs. | Une écriture à la fois (file d'attente). |
| Un `.wasm` compilé avec un vieux Rust importe des fonctions de plus que le zip officiel. | Rust stable du jour, comme la release amont. Le `.wasm` n'est jamais identique octet pour octet : comparer le comportement. |
| `Alt+F4` n'est lié par l'OS qu'en plein écran (verrou du clavier). | Hors plein écran, fermer = `Alt+Q` ; le parcours de test l'utilise. |
| « Polices du système » demande la permission du navigateur (polices locales). | Limite connue, notée dans la fiche. |
| L'impression passe par le dialogue d'impression du navigateur. | Exception acceptée pour Kazo Word. |
| L'explorateur ignore un fichier déposé avant que sa vue existe (parcours « insérer une image » instable). | Le test attend la première liste du dossier. |
| Le sélecteur du navigateur s'ouvre en silence dans un test. | Les parcours Playwright échouent si un sélecteur du navigateur s'ouvre. |
| Un fichier poussé de plus de 50 Mo déclenche un avertissement GitHub (refus au-delà de 100 Mo). | Le `.wasm` de WordCraft (60 Mo) passe ; un `.wasm` de plus de 100 Mo demande une autre décision (LFS ou téléchargement), à poser à Benoit. |

## 6. Ce qui reste partagé entre les apps

- **Le code Kazo** (cadre de l'iframe, proxy, sélecteur, touches, réglages,
  `DocumentSession`) est le même à quelques noms près. Décision 8 de Kazo Word, prise le
  2026-11-13 : il devient un module du SDK livré et servi par l'OS,
  `Kazo.Os.AppSdk.Craft` ([ADR 0259](adr/0259-shared-craft-app-host-module.md), série
  `craft-host/base` : bibliothèque C#, scripts, parcours de bout en bout et guide
  [`craft-apps.md`](../framework/craft-apps.md)). Chaque app garde sa fenêtre, sa liste de
  formats, sa page d'iframe (`data-craft-app`) et sa compilation. Kazo Word sort avec sa copie
  et passe au module ensuite (Kazo-ca/Os-App-Word#15) ; Kazo Spreadsheets l'utilise dès son
  chantier 1. Les chantiers 1 à 3 ci-dessus sont écrits sur le module.
- **Le `.wasm`** ne se partage pas : deux modules wasm ne partagent pas de code Rust. Seules
  les données chargées à part (traductions, orthographe, polices : environ 9 Mo sur 60 dans
  WordCraft) pourraient l'être, si l'amont les sort du `.wasm`.
- **La piste en amont** (une issue par problème, textes préparés par Claude en anglais,
  publiés par Benoit sans mention d'agent) sert toutes les apps : confirmation
  d'enregistrement (craft-libs #611), `open-request` (craft-libs #610), touches réservées,
  réglages confiés à l'hôte.

## 7. Liste de contrôle pour une nouvelle app

- [ ] Dépôt `Kazo-ca/Os-App-<App>` créé depuis `Os-AppTemplate` et ajouté au projet.
- [ ] Fork `Kazo-ca/<app>craft` créé (avant le chantier 4).
- [ ] Étape 0 : lecture de l'app amont, plan fr et en fusionné, questions posées à Benoit.
- [ ] Mode hébergé présent en amont ou ajouté (apps sans `craft-web-host`).
- [ ] Chantier 1 : page d'iframe sur le module, fenêtre sur `CraftFrame`, deux fenêtres, installation dans un Kazo OS publié.
- [ ] Chantier 2 : ouvrir, enregistrer, garde de fermeture.
- [ ] Chantier 3 : sélecteur, raccourcis, thème, langue, réglages par utilisateur.
- [ ] Chantier 4 : build depuis le fork, workflow.
- [ ] Chantier 5 : fiche, README refait sur la fiche (« Essayer », présentation de Kazo OS), tag, catalogue, `features.md`.
