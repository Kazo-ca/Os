> [English version](../../../../en/wasm/dev/adr/0249-settings-features-section.md)

# ADR 0249 : section « Fonctionnalités » des Paramètres et pages fournies par une Feature

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : D38
(« section “Fonctionnalités” dans les Paramètres »), 122 (« page admin
d'Os-x86 dans Paramètres → Fonctionnalités plus une commande équivalente au
terminal »), 140 B (tes mots : « deux sections, une pour les VM, puis une
section pour les utilisateurs », « administration graphique dès le premier
jalon ») et 160 A (le rôle ouvre les pages).

- **Complète :** l'[ADR 0096](0096-added-intents-and-app-defined-intents.md) §4
  (« Paramètres de l'app »), l'[ADR 0148](0148-sdk-contracts-and-os-provided-controls.md)
  (composants intégrables), l'[ADR 0156](0156-replaceable-file-control-provider.md)
  §3 (page « Composants de l'OS »), l'[ADR 0243](0243-feature-app-type.md)
  §2 (point d'entrée `SettingsPage`) et l'[ADR 0247](0247-feature-declared-roles.md)
  (rôle, authentification par opération).
- **Pour :** les pages d'Os-x86 : « Machines virtuelles » (limites, VM
  communes, restrictions, images partagées) et « Utilisateurs » (VM par
  usager, attribution V2), toutes deux sous le rôle `x86.vm-admin`.

## Contexte

- Les sections et pages de Paramètres sont une **liste codée en dur** :
  `Apps/System/Kazo.Os.Apps.Settings/SettingsWindow.Navigation.cs:32-57`
  (`AllPages`, `SettingsPage(Id, GroupKey, LabelKey, Icon)` :8), filtrée par
  `PagesFor` (:76-82) ; le rendu est un `@switch (_activePage)` dans
  `SettingsWindow.razor:62` et suivantes (par exemple `case "os-components"`
  → `<OsComponentsPanel />`, :300-304). Aucune section « Fonctionnalités »
  n'existe ; aucune app ne peut ajouter une page.
- Le seul crochet par app est l'ADR 0096 §4 : `ShowAppSettingsAsync`
  (`AppGatewayContracts.cs:561-567`) ouvre l'app elle-même avec
  `--show-settings`, ou Paramètres avec `--app=<id>` sur la fiche générique
  `AppDetailsPanel` (`SettingsWindow.razor:539-544`,
  `AppIntentDispatcher.cs:480-509`). Une Feature n'a pas de fenêtre propre
  (ADR 0243 §1) ; sa page d'administration doit vivre **dans** Paramètres.
- Les composants intégrables (ADR 0148) rendent un fragment du fournisseur
  dans la fenêtre de l'hôte, **avec les droits de l'hôte** (§4). Paramètres
  déclare `apps.manage`, `permissions.manage`, `system.power`, `system.backup`,
  `users.manage` (toutes dangereuses, [`capabilities.md`](../../framework/capabilities.md)) :
  une page d'Os-x86 rendue à la manière de l'ADR 0148 **hériterait de ces
  droits**, et Paramètres hériterait des bogues de la page.
- Les opérations d'administration de Paramètres passent par les passerelles
  d'administration et l'agent d'authentification de l'OS à chaque opération
  (ADR 0247, contexte) ; une page de Feature doit faire de même par
  `IKazoFeatureAuthentication`.

## Décision

### 1. Une section « Fonctionnalités » dans Paramètres

- Nouveau groupe `Features` dans la navigation, entre « Applications » et
  « Réseau », avec une page intégrée **« Fonctionnalités installées »**
  (`features`) qui liste chaque Feature (ADR 0243) : nom, version, éditeur,
  état (activée, désactivée, redémarrage requis), ses points d'entrée
  (enfants) avec leur type, ses permissions déclarées (ADR 0246) et ses rôles
  (ADR 0247), les apps qui en dépendent (ADR 0248), et les actions :
  activer/désactiver (admin, `apps.manage`), ouvrir dans le Store, retirer
  (par le Store ou `kazopkg`, avec l'avertissement de l'ADR 0248 §3). Elle
  reprend `AppDetailsPanel` pour ce qui est commun.
- Sous le groupe, **une entrée par page fournie** par une Feature installée,
  activée et dont l'usager porte le rôle requis (§3) : libellé et icône de
  la page, préfixés du nom de la Feature dans la navigation
  (« Os-x86 ▸ Machines virtuelles »). Sans Feature installée, le groupe ne
  montre que « Fonctionnalités installées » (vide : « Aucune fonctionnalité.
  Le Kazo OS Store en propose. »).
- Le rendu est **découplé de la liste codée** : `AllPages` reste pour les
  pages intégrées ; un `IFeatureSettingsPages` (lu du catalogue, à chaud,
  ADR 0214 §1) fournit les entrées des Features.

### 2. Le point d'entrée `SettingsPage`

- `AppKind` gagne `SettingsPage`, **réservé aux enfants d'une Feature**
  (ADR 0243 §2 ; `manifest.feature.only` ailleurs). Il déclare : le
  composant Blazor de la page (dérivé de `KazoSettingsPageBase`, fourni par
  `Kazo.Os.AppSdk.Blazor`), `Title`, `Icon`, `Order`, `RequiresRole`
  (ADR 0247 §2) et son sous-ensemble de permissions (ADR 0243 §3).
- La page est rendue **dans la fenêtre de Paramètres** (même cadre, même
  navigation, même thème : styles `--kos-*`, pas de MudBlazor dans le SDK),
  mais s'exécute dans le **contexte d'exécution de l'enfant** : son
  identité (`ca.kazo.x86.admin`), ses permissions, son processus visible
  dans `ps` (ADR 0243 §2), ses passerelles. La page ne reçoit **aucune**
  passerelle de Paramètres ; Paramètres ne reçoit rien de la page. L'OS
  entre dans le contexte de l'enfant pour le rendu (`KazoExecutionContext.Enter`,
  comme un composant rendu par le lanceur de fenêtres), à la différence de
  l'ADR 0148 §4 (droits de l'hôte), parce qu'ici l'hôte est plus privilégié
  que le fournisseur.
- Une page qui lève suit l'ADR 0238 : l'erreur reste dans la zone de la page
  (« <Feature> a rencontré un problème », *Réessayer*), Paramètres continue.
- La page peut ouvrir ses propres dialogues (dialogues du SDK, dans son
  contexte) et appeler `IKazoFeatureAuthentication` (ADR 0247 §3) pour une
  opération sensible : la fenêtre sécurisée de l'OS s'affiche par-dessus
  Paramètres.
- Mobile : les pages de Feature suivent la présentation mobile de Paramètres
  (plein écran, ADR 0171) ; une page déclare `MobileSupported = false` pour
  n'apparaître que sur le bureau.

### 3. Rôle et élévation

- `RequiresRole` filtre la navigation : la page n'apparaît qu'aux usagers
  qui portent le rôle (ADR 0247 §2) ; un administrateur sans le rôle ne la
  voit pas non plus (ADR 0247, question 2). Sans `RequiresRole`, la page est
  visible de tout usager (par exemple une page « Mes machines virtuelles »
  d'Os-x86 pour les réglages personnels, hors de cette ADR).
- Ouvrir une page ne demande aucune authentification ; chaque **opération
  sensible** la demande (ADR 0247 §2, §3). Paramètres ne fait aucune
  élévation pour le compte de la page.

### 4. Ouvrir une page depuis ailleurs

- `KazoApps.ShowSettingsPageAsync("ca.kazo.x86.admin")` (permission
  `apps.launch`) ouvre Paramètres sur cette page : c'est ce que BoxedWine
  fait pour « Administrer les VM » (160 A : BoxedWine ouvre la page, il
  n'agit pas lui-même) et ce que `sudo x86 admin` propose au terminal
  (« voir aussi Paramètres ▸ Fonctionnalités ▸ Os-x86 »). Sans le rôle,
  Paramètres s'ouvre sur « Fonctionnalités installées » avec un message.
- `--page=<id d'enfant>` est l'argument de Paramètres correspondant
  (`AppIntentDispatcher`, comme `--app=`).

## Options écartées

- **A. Une fenêtre propre d'administration d'Os-x86** : une Feature n'a pas
  de fenêtre à elle (ADR 0243) et Benoit a demandé les sections dans
  Paramètres (140 B).
- **B. Un composant intégrable (ADR 0148) rendu par Paramètres** : droits de
  l'hôte (Paramètres, cinq permissions dangereuses) donnés au code de la
  Feature ; l'inverse du principe « une Feature n'a que ses permissions ».
- **C. Une page web (`Web`) de la Feature dans un cadre** : pas de SDK Blazor,
  pas de dialogues de l'OS, pas d'`IKazoFeatureAuthentication` ; et une page
  `Web` ne peut pas demander `filesystem.*` (ADR 0073).
- **D. Généraliser à toute app** (toute app ajoute une page à Paramètres) :
  Paramètres deviendrait un lanceur ; l'ADR 0096 §4 (`--show-settings`)
  reste la voie des apps ordinaires.

## Conséquences

- Paramètres : groupe `Features`, page `features`, `IFeatureSettingsPages`,
  rendu des pages de Feature dans leur contexte (`FeatureSettingsPageHost`),
  argument `--page=`, présentation mobile.
- SDK : `AppKind.SettingsPage`, `KazoSettingsPageBase` (`Kazo.Os.AppSdk.Blazor`),
  `RequiresRole`, `MobileSupported`, `KazoApps.ShowSettingsPageAsync` ;
  `PublicAPI.Unshipped.txt`, fixture `sdk-compat`.
- Plateforme : `AppCatalog` expose les enfants `SettingsPage` ; le lanceur
  entre dans le contexte de l'enfant pour le rendu ; `AppErrorBoundary`
  (ADR 0238) autour de chaque page.
- Docs : nouveau `framework/feature-settings-pages.md`,
  [`app-model.md`](../../framework/app-model.md), `features.md`, entrée
  `changes/unreleased`.
- Tests : Feature installée → entrée dans la navigation ; `RequiresRole` sans
  le rôle → absente, avec le rôle → présente ; la page n'obtient pas
  `apps.manage` de Paramètres (test d'une passerelle refusée) ; une page qui
  lève n'abat pas Paramètres ; `ShowSettingsPageAsync` ouvre la bonne page ;
  désactiver la Feature retire la page à chaud.

## Questions posées à Benoit

1. **Dans quel contexte s'exécute une page de Feature rendue dans
   Paramètres ?**
   - A) **(Recommandé)** Celui de l'enfant `SettingsPage` (ses permissions,
     son processus), l'OS entrant dans ce contexte pour le rendu ; Paramètres
     ne lui prête rien.
   - B) Celui de Paramètres, comme un composant de l'ADR 0148 : plus simple,
     mais la Feature hérite d'`apps.manage`, `users.manage`, `system.power`.
2. **Où se place le groupe « Fonctionnalités » ?**
   - A) **(Recommandé)** Un groupe à part, après « Applications », avec la
     page « Fonctionnalités installées » et une entrée par page fournie.
   - B) Sous « Applications », à côté de « Composants de l'OS ».
   - C) Un groupe par Feature (« Os-x86 », « Autre feature »), sans page
     « Fonctionnalités installées ».

Réponses du mainteneur (2026-11-11) : 1 A, 2 A.
