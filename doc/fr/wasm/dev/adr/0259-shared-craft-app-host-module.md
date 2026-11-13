> [English version](../../../../en/wasm/dev/adr/0259-shared-craft-app-host-module.md)

# ADR 0259 : un module commun du SDK pour les apps Craft (`Kazo.Os.AppSdk.Craft`)

## Statut

Acceptée le 2026-11-13. Benoit a retenu la recommandation A des deux questions
(voir [Questions](#questions-posées-à-benoit)) : le code commun des apps Craft
est un module livré et servi par Kazo OS, et il contient l'iframe, le proxy et
la logique des documents. Série `craft-host/base`. D'abord numérotée 0247 ; renumérotée 0259 le
2026-11-13 (choix de Benoit), car la série `feature/base` utilisait déjà 0247.

- **S'appuie sur :** l'[ADR 0019](0019-sdk-versioning-and-compatibility.md)
  (versions du SDK), l'[ADR 0055](0055-base-apps-on-public-sdk-only.md)
  (une bibliothèque du SDK ne dépend pas de Platform Core),
  l'[ADR 0176](0176-app-repository-template.md) (dépôt d'app, sous-module de
  l'OS), l'[ADR 0182](0182-install-apps-from-github.md) (paquets d'apps
  installées), l'[ADR 0221](0221-pack-all-static-web-assets-of-an-app.md)
  (ressources statiques d'une app dans son paquet ; celles des bibliothèques
  référencées n'y sont pas, car le SDK vient avec l'OS),
  l'[ADR 0242](0242-framework-api-for-installed-apps.md) (API qu'une app
  installée trouve dans l'OS publié) et
  l'[ADR 0243](0243-serve-installed-app-pages.md) (page d'une app installée
  dans une iframe).
- **Prend la décision 8 de Kazo Word** (« bibliothèque partagée ») et la
  section 6 du [guide de portage des apps Craft](../craft-app-port-guide.md).
- **Ne remplace aucune ADR.**

## Contexte

Kazo Word (dépôt `Kazo-ca/Os-App-Word`) affiche WordCraft, une app Rust
compilée en WebAssembly, dans une iframe de sa fenêtre. Trois parties le
permettent (plan d'intégration de Kazo Word, sections 2 et 5) :

| Partie | Fichiers dans Kazo Word | Rôle |
| --- | --- | --- |
| Côté OS (JS) | `wwwroot/js/word-host.js` | crée l'iframe, lui donne un `MessagePort`, envoie les commandes, rejoue les raccourcis réservés, suit le thème |
| Dans l'iframe (JS) | `wwwroot/host/word-proxy.js`, `file-picker.js`, `reserved-keys.js`, `settings-store.js` | démarre l'app Craft, relaie ses événements, intercepte son sélecteur de fichiers, renvoie les raccourcis, tient ses réglages en mémoire |
| C# | `WordFrame`, `FrameStartup`, `FrameMessages`, `FrameSave`, `FrameCommandResult`, `ReservedKeys`, `WordCraftLanguage`, `PickRequest`, `WebStorageFile`, `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest` | décide de tout : fichiers liés, modifications, titre, fermeture, réglages par utilisateur |

Sept autres apps Craft vont suivre (Kazo Spreadsheets, Pdf, Fig, Photo,
Design, Deck, Vector ; guide de portage). Elles parlent toutes le même
contrat amont :

- `craft-web-host` (dépôt `craft-libs`) : messages `<app>:ready`, `open`,
  `save`, `dirty`, `failed` (et `host`, `saved`, `handoff`), en `CustomEvent`
  sur le canevas quand la page le marque `data-host="page"`, version de
  contrat `"1"` ;
- `craft-web-api` : `window.<app>.request(method, params)` ;
- le sélecteur de fichiers `rfd` (le même `input type=file`).

Le code de Kazo Word est donc commun, sauf l'identifiant de l'app
(`wordcraft`), l'identifiant du canevas, le chemin de la compilation et la
liste des formats de documents. Le copier dans chaque app voudrait dire huit
copies à corriger une par une (Alt+F4, thème, `rfd`, réglages).

La taille n'est pas l'enjeu : environ 35 Ko de JS et 90 Ko de sources C# par
app, à côté d'un `.wasm` de 60 Mo pour WordCraft. L'enjeu est la maintenance.

Trois contraintes de l'OS décident de la forme :

1. Une app installée ne peut pas embarquer une deuxième assembly : le paquet
   ne contient que les assemblies que son manifeste déclare (issue
   [#419](https://github.com/Kazo-ca/Os/issues/419)).
2. `KazoOsPack` ne met pas dans le paquet les fichiers `_content/` des
   bibliothèques référencées (`PackagedWebAssets`, ADR 0221) : le SDK vient
   avec l'OS.
3. Les apps installées sont chargées dans `AssemblyLoadContext.Default` : deux
   apps qui apporteraient deux versions d'une même bibliothèque entreraient en
   conflit.

## Décision

### 1. Une bibliothèque du SDK livrée avec l'OS

Nouvelle bibliothèque `Shared/Kazo.Os.AppSdk.Craft` (`Microsoft.NET.Sdk.Razor`,
espace de noms `Kazo.Os.AppSdk.Craft`, paquet NuGet `Kazo.Os.AppSdk.Craft`
publié avec les autres paquets du SDK, `PublicAPI.Shipped.txt` et
`PublicAPI.Unshipped.txt`).

- Elle dépend seulement de `Kazo.Os.App.Abstractions`, `Kazo.Os.AppSdk` et
  `Kazo.Os.AppSdk.Blazor`, comme toute bibliothèque du SDK (ADR 0055) : pas de
  Platform Core, pas de MudBlazor.
- `Kazo.Os.Platform.Blazor` la référence : chaque hôte de l'OS (PWA
  `Kazo.Os.Ecosystem`, OS servi par `Kazo.Os.Server`, `test/test`) la publie
  donc dans `_framework` et sert ses fichiers à
  `_content/Kazo.Os.AppSdk.Craft/`. Son nom commence par `Kazo.Os.` : la
  publication rognée la garde entière (`_KazoOsRootKazoOsAssemblies`,
  ADR 0242).
- Une app Craft la référence pour compiler (paquet NuGet, ou projet par le
  sous-module de l'OS, ADR 0176) et ne l'embarque pas : son paquet reste à une
  seule assembly, et ses `_content/` ne contiennent que ses fichiers à elle
  (la compilation de l'app Craft, sa page d'iframe, ses icônes).
- Aucun code d'une app Craft particulière dans l'OS : ni identifiant d'app, ni
  format de document, ni compilation Rust. Aucun jeu sur ce repository.

### 2. Ce que le module contient (Q2)

Tout ce qui ne dépend pas de l'app, repris de Kazo Word et rendu générique :

| Partie | Dans le module | Reste dans l'app |
| --- | --- | --- |
| Côté OS (JS) | `craft-host.js` (ex-`word-host.js`) | — |
| Dans l'iframe (JS) | `craft-proxy.js` (ex-`word-proxy.js`), `file-picker.js`, `reserved-keys.js`, `settings-store.js` | `host/index.html` (quelques lignes, voir §3) |
| Cadre (C#) | `CraftFrame` (ex-`WordFrame`), `CraftFrameOptions`, `FrameStartup`, `FrameMessages`, `FrameSave`, `FrameCommandResult`, `ReservedKeys`, `CraftLanguage` (ex-`WordCraftLanguage`) | — |
| Documents (C#) | `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest`, un type `CraftDocumentFormat` (extension, type de média, par défaut, copie, ouvrable) | la liste de ses formats |
| Fichiers et réglages (C#) | `PickRequest`, `WebStorageFile` (réglages par utilisateur, dossier `~/.config/<appId>/`) | — |
| Fenêtre | — | sa fenêtre Razor (mince : elle assemble le cadre, la session et ses textes) |

Le module ne fournit pas de composant de fenêtre de base : un composant Razor
public est plus difficile à faire évoluer sans casser les apps installées. Les
noms exacts des types sont fixés par le chantier de la bibliothèque et gelés
par `PublicAPI.Unshipped.txt` ; ce tableau donne leur rôle.

### 3. La page de l'iframe reste dans l'app

Chaque app garde sa page `wwwroot/host/index.html`, servie par l'ADR 0243 à
`_content/<assembly>/host/index.html`. Elle ne contient pas de script en ligne
et décrit l'app par des attributs du canevas :

```html
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
```

- `data-craft-app` donne le préfixe des messages (`gridcraft:ready`) et
  l'objet de commandes (`window.gridcraft`).
- `data-craft-build` donne le fichier de provenance de la compilation (nom de
  la colle JS et du `.wasm`), relatif à la page.
- L'identifiant du canevas reste celui que l'app Craft cherche (WordCraft :
  `wordcraft_canvas`) ; le proxy prend le canevas qui porte `data-craft-app`.
- `CraftFrameOptions` donne au côté OS l'adresse de cette page
  (`_content/<assembly>/host/index.html`).

Le côté OS et le proxy viennent toujours du même OS : leurs messages
(`init`, `ready`, `failed`, `open`, `save`, `dirty`, `focus`, `command`,
`result`, `pick`, `picked`, `key`, `setting`) restent internes au module et
peuvent changer d'une version de l'OS à l'autre sans casser une app.

### 4. Compatibilité

- **Avec les apps Craft :** le module parle la version `"1"` du contrat de
  `craft-web-host` et traite une version qu'il ne connaît pas comme `"1"`,
  comme le demande le contrat. Les champs optionnels (`id`, `saveAs`, `file`,
  `appVersion`) sont lus quand ils sont là. Une nouvelle version du contrat
  amont se traite dans une nouvelle version de l'OS, sans retirer la
  précédente : une app installée garde la compilation Craft qu'elle a
  apportée.
- **Avec les apps installées :** l'API publique suit le SemVer du SDK
  (ADR 0019) : un ajout demande une version mineure, un retrait ou un
  changement de sens une version majeure. La fixture de compatibilité du SDK
  (`Tests/fixtures/sdk-compat`) couvre le module.
- **Version minimale de l'OS :** une app qui utilise le module déclare
  `sdkCompatibility.minimumVersion` sur la première version de l'OS qui le
  contient. Benoit choisit ce numéro au moment de la sortie (la 3.4.0 est déjà
  prévue pour le SDK en préversion, issue #456).

### 5. Les apps

- **Kazo Word sort tel quel**, avec sa copie (PR Os-App-Word#12 et sa
  publication au Store ne dépendent pas de cette série). Il passe au module
  dans une étape à lui, après la sortie de l'OS qui le contient (issue `TODO`
  dans `Kazo-ca/Os-App-Word`).
- **Kazo Spreadsheets** utilise le module dès son chantier 1. Tant que l'OS
  qui le contient n'est pas sorti, il compile contre le sous-module de l'OS
  sur `craft-host/base` ; sa publication au Store attend cette sortie.
- Les autres apps Craft suivent le guide de portage, mis à jour par cette
  série.

### 6. Chantiers de la série `craft-host/base`

| N° | Branche | Contenu |
| --- | --- | --- |
| 1 | `craft-host/1-adr` | cette ADR (fr et en), index, mémoire, `features.md` |
| 2 | `craft-host/2-library` | bibliothèque C# (cadre, documents, fichiers, réglages) reprise de Kazo Word, tests xUnit, `PublicAPI.Unshipped.txt`, référence depuis `Kazo.Os.Platform.Blazor`, entrée de changement |
| 3 | `craft-host/3-scripts` | `craft-host.js`, `craft-proxy.js` et ses modules, paramétrés par `data-craft-app` ; tests `node --test` avec une fausse app Craft |
| 4 | `craft-host/4-e2e-and-docs` | parcours Playwright avec une fausse app Craft (page d'iframe, ouverture, enregistrement, modifications, commande, sélecteur, réglages) dans l'OS rogné ; doc `framework/craft-apps.md` (fr et en) ; guide de portage mis à jour |

Les chantiers 2 et 3 peuvent avancer en parallèle : la frontière entre eux est
la fonction `create` de `craft-host.js` et les messages de `CraftFrame`, déjà
fixés par Kazo Word.

## Conséquences

- Un correctif du cadre (raccourcis, thème, `rfd`, réglages) arrive dans
  toutes les apps Craft installées avec la mise à jour de l'OS, sans
  reconstruire ni republier les apps.
- Une app Craft se réduit à sa fenêtre, sa liste de formats, sa page d'iframe,
  sa compilation et son manifeste.
- Le module devient une API publique : il doit rester compatible, et une
  erreur dans le module touche toutes les apps Craft à la fois. Les tests du
  chantier 4 tournent dans l'OS rogné pour la voir avant une sortie.
- Les apps Craft dépendent d'une version de l'OS plus récente que celle qu'il
  leur faudrait sans le module.
- L'OS grandit de quelques dizaines de Ko (une assembly et cinq modules JS).

## Questions posées à Benoit

Posées dans le fil « Partager le proxy des apps Craft dans Kazo OS » le
2026-11-13, réponse « je suis d'accord avec tes recommandations ».

**Q1. Où vit le code commun des apps Craft ?**

- **A (retenue) : un module de Kazo OS, livré et servi par l'OS**, comme le
  SDK. Évite #419, les apps installées reçoivent les correctifs sans être
  reconstruites ; en échange, une version minimale de l'OS et une API
  publique à garder compatible.
- B : un paquet NuGet embarqué par chaque app. Demande de régler #419, le
  transport des `_content/` des paquets et le conflit de versions entre deux
  apps.
- C : des sources partagées compilées dans chaque app (paquet de sources ou
  sous-module). Pas de version de l'OS, mais chaque correctif oblige à
  republier les huit apps.
- D : rester copié et aligner les copies à la main.

**Q2. Que met-on dans le module ?**

- **A (retenue) : l'iframe et le proxy, plus la logique des documents**
  (cadre, sélecteur, raccourcis, réglages, `DocumentSession`, règle
  d'enregistrement) ; chaque app garde sa fenêtre Razor.
- B : seulement l'iframe et le proxy.
- C : tout, y compris un composant de fenêtre de base.

## Liste des tâches

- [x] Évaluer ce qui est commun (Kazo Word, `craft-web-host`, `craft-web-api`)
      et poser les questions à Benoit.
- [x] Écrire l'ADR (fr et en), index, mémoire, `features.md`.
- [x] Chantier 2 : bibliothèque C#.
- [x] Chantier 3 : scripts ([`craft-module-scripts.md`](../craft-module-scripts.md)).
- [x] Chantier 4 : parcours de bout en bout, doc du framework, guide de
      portage.
- [x] Issue `TODO` dans `Kazo-ca/Os-App-Word` : passer Kazo Word au module ([#15](https://github.com/Kazo-ca/Os-App-Word/issues/15)).
