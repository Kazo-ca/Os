> [English version](../../../../en/wasm/dev/adr/0285-settings-features-section-adjustments.md)

# ADR 0285 : section « Fonctionnalités » des Paramètres, ajustements de la mise en œuvre

## Statut

Accepté le 2026-11-17.

- **Chantier :** `feature/21-settings-features-section-code` (série `feature/base`,
  code de l'ADR 0249).
- **Ajuste :** l'[ADR 0249](0249-settings-features-section.md) §1 (actions et
  état d'une Feature), §2 (contrat de rendu), §3 (rôle d'un administrateur) et
  §4 (`ShowSettingsPageAsync`). Ses décisions restent vraies : un groupe
  « Fonctionnalités » après « Applications » (réponse 2 A), une page
  « Fonctionnalités installées », une entrée par page fournie, une page rendue
  dans la fenêtre de Paramètres mais exécutée dans le contexte de son point
  d'entrée (réponse 1 A), `RequiresRole`, aucune élévation par Paramètres.
- **Questions posées à Benoit** dans la PR du chantier (série 24, questions 283
  à 286), à la fin de cette ADR.

## Contexte

L'ADR 0249 a été écrite avant le code des ADR 0247 (rôles), 0283 (noms des
rôles) et avant de regarder l'ADR 0048 telle qu'elle est codée (installation
par `apps.config`). En codant la section, quatre points ne pouvaient pas suivre
son texte à la lettre :

- §3 dit qu'« un administrateur sans le rôle ne la voit pas non plus ». La
  réponse 233 B de l'ADR 0247, acceptée ensuite, dit l'inverse : chaque
  administrateur porte tous les rôles des Features, implicitement.
- §2 ne dit pas comment Paramètres, qui ne référence que le SDK, obtient le
  rendu d'une page : le générateur refuse qu'une app injecte autre chose qu'un
  type du SDK (`KOSAPP0031`, [ADR 0055](0055-base-apps-on-public-sdk-only.md) §3).
- §1 demande les actions « activer/désactiver » et l'état « redémarrage
  requis » ; l'OS ne connaît que l'installation et la désinstallation de
  l'ADR 0048 (`/etc/kazoos/apps.config`), et une Feature arrive à chaud
  ([ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §1), sans
  redémarrage.
- §4 ne dit pas comment l'intention arrive à Paramètres, ni ce qu'elle exige.

## Décision

### 1. Un administrateur voit les pages qui exigent un rôle

Une page marquée `RequiresRole` apparaît à l'usager qui détient le rôle et à
tout administrateur (ADR 0247, réponse 233 B ; ADR 0283 §4). Les rôles portent
le nom `<Feature>:<rôle>` (`x86:vm-admin`, ADR 0283 §2). Le contrôle est celui
du lanceur (`FeatureRoleService.OpensTo`) : sans service de rôles, seul un
administrateur passe.

### 2. Le contrat de rendu : `IFeatureSettingsPages`

- Le contrat est public, dans `Kazo.Os.AppSdk.Blazor` (espace
  `Kazo.Os.AppSdk.Blazor.Settings`), injecté dans Paramètres :
  `GetPages(hôte)`, `OpenAsync(hôte, idDePage)`, `Changed`. L'OS ne répond
  qu'à Paramètres (`org.kazoos.settings`) : un autre hôte n'obtient aucune page.
- `FeatureSettingsPages` (Platform.Blazor) lit le catalogue à chaud et filtre :
  Feature et page activées, rôle, et `MobileSupported` sur un téléphone. Il
  rafraîchit la liste quand l'activation change (`IAppEnablementRegistry`) ou
  quand `roles.config` est relu (`FeatureRoleService.Changed`, ajouté).
- Ouvrir une page démarre un **processus auxiliaire**, enfant de celui de
  Paramètres, avec l'id et les permissions de la page (`ps` le montre,
  `argv` : `--type=settings-page`), comme le rendu d'un site
  ([ADR 0084](0084-blazor-sites.md) §3).
  `AppLifecycleOrchestrator.StartAuxiliaryAsync` accepte désormais une page et
  y refait le contrôle du rôle. Quitter la page ou fermer Paramètres arrête ce
  processus ; si l'OS l'arrête (`kill`), Paramètres propose de la rouvrir.
- Le contenu rendu met en cascade le contexte de la page, masque la passerelle
  de fenêtre de Paramètres (`IAppWindowGateway` en cascade nulle) et place la
  page dans `AppErrorBoundary` (ADR 0238) avec *Réessayer* seulement
  (`AllowClose = false`) : la fenêtre appartient à Paramètres.
- Une page n'est jamais lancée seule : `LaunchAsync` la refuse
  (`lifecycle.settings-page.not-launchable`).

### 3. Le manifeste d'une page

- `AppKind.SettingsPage` (`settingsPage`), réservé aux enfants d'une Feature
  (`manifest.feature.only` au validateur, `KOSAPP0033` au générateur).
- Options `settingsPage` : `order` (0 par défaut) et `mobileSupported` (vrai
  par défaut), refusées sur un autre genre (`manifest.settingsPage.forbidden`).
  Le titre est le `Name` du point d'entrée, l'icône la première de ses `Icons`.
- Une page tourne sur le fil principal (`manifest.execution.window`) et peut
  demander les dialogues de fichiers de l'OS, comme une fenêtre.

### 4. « Fonctionnalités installées »

- Chaque Feature y montre ses points d'entrée et leur genre (avec le rôle
  exigé), les permissions qu'elle déclare (ADR 0246), ses rôles (ADR 0247) et
  les apps qui en dépendent (ADR 0248). `KazoAppInfo` gagne pour cela
  `RequiresRole`, `Roles` et `ProvidedCapabilities`.
- Activer et désactiver sont l'installation et la désinstallation de
  l'[ADR 0048](0048-system-apps-and-uninstall.md) pour tous les usagers
  (`/etc/kazoos/apps.config`, un administrateur s'authentifie) : la page
  reprend la ligne d'installation de `InstalledAppsPanel`. Le bouton *Détails*
  ouvre la page de l'app (`AppDetailsPanel`), *Ouvrir dans le Store* lance le
  Store sur la Feature, et la page rappelle comment la retirer
  (`sudo kazopkg remove --system <id>` ; les apps dépendantes sont désactivées,
  ADR 0248 §3).
- Il n'y a pas d'état « redémarrage requis » : une Feature installée ou mise à
  jour est utilisable à chaud (ADR 0214 §1). Question 284.

### 5. Ouvrir une page depuis ailleurs

- `KazoApps.ShowSettingsPageAsync(id)` envoie `ShowSettingsPageIntent`, qui
  exige `apps.launch` et arrive à Paramètres avec `--page=<id>` (intention
  `ShowAppSettings`, pour une instance déjà ouverte). L'id doit être une page
  connue (`intent.show-settings-page.not-found`) et activée
  (`intent.show-settings-page.disabled`).
- Le rôle est la réponse de Paramètres : sans lui, Paramètres s'ouvre sur
  « Fonctionnalités installées » avec un message, comme le dit l'ADR 0249 §4.

## Options écartées

- **Injecter `IServiceProvider` dans Paramètres** : refusé par `KOSAPP0031`.
- **Rendre la page dans le processus de Paramètres** (contexte prêté) :
  contraire à la réponse 1 A.
- **Un état « désactivée » distinct de la désinstallation** : un second
  fichier d'état pour la même chose que `apps.config` (question 283).

## Conséquences

- App Abstractions : `AppKind.SettingsPage`, `SettingsPageOptions`,
  `AppManifest.SettingsPage`, `FeatureEntryPoint.SettingsPage`,
  `ShowSettingsPageIntent`, validateur, schéma JSON et fixture
  `feature-settings-page.valid.json`.
- SDK : `KazoSettingsPageBase`, `IFeatureSettingsPages` et ses enregistrements,
  `KazoApps.ShowSettingsPageAsync`, `IAppIntentGateway.ShowSettingsPageAsync`,
  `KazoAppInfo.RequiresRole`, `Roles`, `ProvidedCapabilities`.
- Plateforme : `FeatureSettingsPages`, `StartAuxiliaryAsync` pour une page,
  `AppErrorBoundary.AllowClose`, `FeatureRoleService.Changed`, relais du
  worker (`int.settings-page`).
- Paramètres : groupe `Nav.Group.Features`, page `features`
  (`InstalledFeaturesPanel`), `FeatureSettingsPageView`, `--page=<id>`.
- Docs : [`feature-settings-pages.md`](../../framework/feature-settings-pages.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`, entrée
  `changes/unreleased`.

## Questions posées à Benoit (série 24)

283. **Activer et désactiver une Feature.**
     - A) **(Recommandé, codé)** Ce sont la désinstallation et la
       réinstallation de l'ADR 0048 pour tous les usagers, par la ligne
       d'installation reprise dans la page.
     - B) Un état « désactivée » à part, qui garde la Feature installée mais
       arrête ses enfants (un nouveau document système).
284. **L'état « redémarrage requis » de l'ADR 0249 §1.**
     - A) **(Recommandé, codé)** Pas affiché : une Feature arrive et se met à
       jour à chaud (ADR 0214 §1).
     - B) L'afficher quand une mise à jour attend un rechargement de la page
       ([ADR 0239](0239-start-on-the-latest-version.md)), une fois cet état
       exposé par le SDK.
285. **Où vit le contrat `IFeatureSettingsPages` ?**
     - A) **(Recommandé, codé)** Public dans `Kazo.Os.AppSdk.Blazor`, injecté
       dans Paramètres ; l'OS ne répond qu'à Paramètres.
     - B) Dans un assemblage interne à l'OS, avec une exception à `KOSAPP0031`
       pour Paramètres seulement.
286. **Tests de bout en bout.**
     - A) **(Recommandé, codé)** Tests unitaires et de rendu ici ; le parcours
       complet sera testé avec la première Feature réelle (Os-x86).
     - B) Ajouter dès maintenant une Feature d'essai et un test Playwright.

## Références

- [ADR 0249](0249-settings-features-section.md) : la section ajustée.
- [ADR 0247](0247-feature-declared-roles.md) et
  [ADR 0283](0283-feature-roles-isinrole-adjustments.md) : rôles.
- [ADR 0256](0256-app-libraries-implementation-adjustments.md) : modèle d'un ADR
  d'ajustement.
