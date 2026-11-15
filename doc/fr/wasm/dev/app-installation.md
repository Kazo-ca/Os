> [English version](../../../en/wasm/dev/app-installation.md)

# Désinstallation et réinstallation des apps

Phase 2 du [plan de refonte](plans/ecosystem-rebuild-plan.md) (section 6.3, décisions
D10 et D11), chantier `rebuild/2-app-uninstall` de l'[ADR 0048](adr/0048-system-apps-and-uninstall.md).
Les apps système (D9) sont décrites dans [`app-model.md`](../framework/app-model.md#apps-système-adr-0048-1).

## Objectif

Désinstaller une app la retire de l'OS sans rien effacer : elle disparaît du
lanceur, de la recherche, de « Ouvrir avec » et du terminal, ne se lance plus
par aucune voie, ses fenêtres ouvertes se ferment. Ses réglages
(`~/.config/<appId>/`, `/etc/<appId>/`), ses journaux et les fichiers de
l'utilisateur restent en place : une réinstallation retrouve tout.

L'état vit dans deux fichiers texte, comme sous Linux : `cat` montre ce qui
est retiré, `nano` a le même effet que l'app Paramètres. Il remplace le drapeau
IndexedDB `IsEnabled` de l'ADR 0032.

## Fichiers `apps.config`

| Portée | Fichier | Propriétaire et mode | Qui écrit |
| --- | --- | --- | --- |
| Machine | `/etc/kazoos/apps.config` | `system:system 0644` | un administrateur, par `sudo` ou l'agent d'authentification de l'OS (ADR 0055 §5) |
| Utilisateur | `~/.config/kazoos/apps.config` | l'utilisateur, `0644` | l'utilisateur lui-même |

```text
# /etc/kazoos/apps.config
[uninstalled]
org.kazoos.kazo-paint=true
org.kazoos.calculator=true
```

Règles de lecture (`AppsConfigDocument.Read`) :

- grammaire `.config` de l'ADR 0011, lue ligne à ligne sans schéma ; seules les
  clés de `[uninstalled]` comptent et la dernière occurrence d'une clé l'emporte ;
- `true` : désinstallée ; `false` ou absence : installée ; autre valeur : ligne
  ignorée avec un avertissement dans `/var/log/syslog` ;
- identifiant inconnu de ce build : ignoré **sans** avertissement, ligne
  conservée (un même fichier peut servir à plusieurs builds) ;
- identifiant d'app système, ou app dont une app système dépend : ignoré avec
  un avertissement. Éditer le fichier ne contourne donc pas la protection ;
- fichier absent ou illisible : rien de désinstallé.

Les écritures (`ConfigDocumentEditor`) ne touchent que la ligne concernée :
commentaires, ordre et identifiants inconnus sont préservés. Réinstaller retire
la ligne.

### État effectif

Une app est désinstallée pour la session si elle est listée dans le fichier
machine **ou** dans le fichier de l'utilisateur, ou si elle dépend (dépendance
non optionnelle, transitivement) d'une app ainsi désinstallée.

| `AppInstallationState` | Sens |
| --- | --- |
| `Installed` | ni listée ni dépendante d'une app listée |
| `UninstalledForUser` | désinstallée par le fichier de l'utilisateur |
| `UninstalledForMachine` | désinstallée par le fichier machine : seul un administrateur la réinstalle |
| `System` | app système, jamais désinstallable |

## Architecture

| Type | Projet | Rôle |
| --- | --- | --- |
| `IAppInstallationService`, `AppInstallationService` | `Platform/Kazo.Os.Platform.Core/Apps` | lit, surveille et écrit les deux fichiers ; tient `AppEnablementRegistry` égal à l'état effectif |
| `AppsConfigDocument` | idem | chemins, lecture tolérante, écriture ligne à ligne, fermetures de dépendances |
| `AppEnablementToFilesMigration` | idem | crée `/etc/kazoos/apps.config` commenté et y recopie une fois les anciens `IsEnabled = false` |
| `AppEnablementRegistry` | `Platform/Kazo.Os.Platform.Core/Lifecycle` | point de contrôle unique du lancement, désormais alimenté par le service ; événement `Changed` |
| `InstalledAppsPanel` | `Apps/System/Kazo.Os.Apps.Settings` | onglet « Installed Apps » de l'app Paramètres |

Flux unique, quelle que soit l'origine du changement (app Paramètres, `nano`,
synchronisation de `/home`, connexion, déconnexion) :

1. `ReloadAsync` relit les deux fichiers (contexte système pour `/etc`, contexte
   de l'utilisateur pour son fichier) ;
2. l'état effectif est calculé (union, puis dépendants) ;
3. les apps nouvellement désinstallées sont arrêtées et désactivées, dépendants
   d'abord, par `AppLifecycleOrchestrator.DisableAsync` (instances arrêtées,
   `AppDisabledEvent` publié) ; les apps réinstallées sont réactivées,
   dépendances d'abord, par `EnableAsync` ;
4. `Changed` est levé.

Au démarrage, `EcosystemBootCoordinator` exécute la migration puis
`AppInstallationService.StartAsync`, qui applique le fichier machine et pose
les surveillances (`SettingsFilesWatcher`) avant toute connexion. La connexion
et la déconnexion (`SessionActivatedEvent`, `SessionLoggedOutEvent`) relisent
la couche utilisateur et déplacent sa surveillance.

### Filtrage

Toutes les voies consultent `IAppEnablementRegistry` :

| Voie | Effet d'une app désinstallée |
| --- | --- |
| `AppLifecycleOrchestrator.LaunchAsync` | `AppLaunchStatus.Disabled` |
| `AppIntentDispatcher` (intentions, `launch`) | refus ; `launch` répond « is not installed » |
| `FileAssociationResolver` | ni défaut ni candidate ; les préférences de `/etc/kazoos/file-associations.json` et de `~/.config/kazoos/file-associations.json` ne sont **pas** réécrites (une entrée périmée est sautée, ADR 0121) |
| Lanceur et recherche (`LauncherAppProjection`) | absente ; le menu ouvert se met à jour sur `Changed` |
| Explorateur, « Open With... » | absente de la liste |
| Contrôle de fichiers et dialogues (`KazoComponentHost`) | sautée ; si c'était le fournisseur choisi, l'explorateur prend le relais (`component.fallback`) ; une désinstallation pour la machine retire sa ligne de `/etc/kazoos/components.config` (ADR 0156 §4, ADR 0158 §6) |
| Terminal (`ShellCommandResolver`, `ShellInterpreter`) | `command not found`, absente de `help` et de la complétion |

## Utilisation

```csharp
public interface IAppInstallationService
{
    event EventHandler? Changed;
    AppInstallationState GetState(string appId);
    IReadOnlyList<string> GetUninstallClosure(string appId);
    ValueTask<AppInstallationResult> UninstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
    ValueTask<AppInstallationResult> ReinstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
}
```

- `GetUninstallClosure(appId)` : les apps qui en dépendent (dépendants d'abord)
  puis l'app elle-même ; la boîte de confirmation les liste.
- `PlanUninstallAsync(appId)` (ADR 0097 §5) : la fermeture et les processus
  actifs qu'elle arrêtera (`AppProcessInfo` : PID, app, nom, utilisateur), sans
  rien changer. Paramètres les liste dans la confirmation (« These processes
  will be stopped »). Le résultat de `UninstallAsync` donne
  `StoppedProcesses`, avec `Forced` pour ceux tués après leur délai ; chacun est
  écrit dans `/var/log/syslog`. Dans le SDK : `KazoAppManagement.PlanUninstallAsync`
  et `UninstallWithReportAsync` (`KazoUninstallPlan`, `KazoUninstallReport`).
- `UninstallAsync` écrit toute la fermeture dans le fichier de la portée.
  Refus : `apps.app.system:<id>`, `apps.closure.system-app:<id système>`,
  `apps.app.not-found:<id>`, `apps.session.required`.
- `ReinstallAsync` retire l'app et ses dépendances obligatoires listées dans la
  même portée. Refus en portée utilisateur si l'app (ou une dépendance) est
  désinstallée pour la machine : `apps.uninstalled-by-administrator:<id>`.
- `writer` : passerelle dont les droits décident d'une écriture **machine**,
  normalement `AppContext.FileSystem` de l'app appelante. Un refus lève
  `KazoUnauthorizedAccessException`, ce qui déclenche l'invite d'approbation
  administrateur de l'ADR 0042 ; l'app réessaie une fois l'approbation donnée.
  Sans `writer`, l'utilisateur de la session écrit avec ses propres droits. La
  portée utilisateur est toujours écrite comme l'utilisateur de la session.
- Chaque changement est journalisé dans `/var/log/syslog`, par exemple
  `apps: org.kazoos.calculator uninstalled (scope=machine, by=alice)`.

Depuis la phase 4 (`rebuild/4-sdk-user-and-apps`), les apps n'utilisent plus ce
service directement : elles passent par `KazoAppManagement` du SDK, derrière la
capacité dangereuse `apps.manage` (ADR 0056 §3, [`app-sdk.md`](../framework/app-sdk.md)).
Pour la portée machine, la passerelle demande d'abord à l'agent
d'authentification de l'OS de confirmer un administrateur
([`admin-authentication-agent.md`](admin-authentication-agent.md)), puis l'OS
écrit `/etc/kazoos/apps.config` lui-même comme `system` : le paramètre
`writer` reçoit cette passerelle système, et l'invite de l'ADR 0042 n'est plus
utilisée pour ce fichier. La liste des apps installées, elle, est lue par
`KazoApps` sans capacité.

### App Paramètres, onglet « Installed Apps »

- une ligne par app fenêtrée (nom, éditeur, version), puis les commandes du
  terminal dans une section repliée ; les services restent gérés par l'onglet
  Services du Moniteur système ;
- badge « System » sans bouton pour une app système ;
- **Uninstall** pour une app installée, avec confirmation : liste des
  dépendants emportés, choix « Only for me » / « For all users » pour un
  administrateur (un utilisateur standard n'a que sa propre portée) ; « For all
  users » ouvre la fenêtre de l'agent d'authentification, où l'administrateur
  retape son mot de passe ;
- pour un site (app `Web`) emporté par la désinstallation, la confirmation
  annonce que sa base de navigateur `kazoweb.<id>` sera supprimée ; elle l'est
  après la désinstallation par `UninstallAsync`, seule donnée qu'une
  désinstallation efface (ADR 0088 §4, [`asp-kazo-site-data.md`](../framework/asp-kazo-site-data.md)) ;
- **Reinstall** pour une app désinstallée dans une portée que l'utilisateur
  peut modifier ; sinon la mention « Uninstalled by the administrator » ;
- l'ancien interrupteur activer/désactiver a disparu ;
- depuis la phase 4, le panneau n'utilise que le SDK : `AppContext.AppManagement`
  pour la liste et les changements, `AppContext.Catalog.Changed` pour se
  rafraîchir, `KazoUser.Describe(AppContext)` pour savoir si l'utilisateur est
  administrateur.

## Décisions de mise en œuvre

- **Un seul état.** « Désactivée » et « désinstallée » ne font plus qu'un. Le
  magasin IndexedDB garde l'instantané des manifestes, mais l'OS ne lit ni
  n'écrit plus `IsEnabled` ; sa version de schéma ne change pas.
- **`DisableAsync` et `EnableAsync` deviennent internes** à
  `Kazo.Os.Platform.Core` (visibles des tests par `InternalsVisibleTo`) : seul
  le service d'installation les appelle. L'orchestrateur ne reçoit plus
  `IOsSettingsService` ni `IPersistentAppCatalogRepository`.
- **Dépendants désinstallés implicitement.** Une ligne écrite à la main pour
  une bibliothèque désinstalle aussi ses dépendants, même s'ils ne sont pas
  listés : une app dont une dépendance obligatoire manque ne peut pas tourner.
  L'app Paramètres, elle, écrit toute la fermeture, pour que la réinstallation
  soit explicite.
- **Fichier machine créé au démarrage** par la migration (contexte système),
  pour qu'il appartienne à `system` et que les écritures suivantes ne modifient
  que son contenu.
- **Synchronisation.** Le domaine « AppCatalog » de l'ADR 0033
  (`AppCatalogSyncService`, `AppCatalogSyncPayload`) est retiré. Le fichier
  utilisateur est sous `/home` et voyage avec le domaine « FileSystem » ;
  `/etc` n'est pas synchronisé. L'énumération `SyncDomain.AppCatalog` reste
  côté serveur pour les données déjà stockées.
- **Écart avec la signature de l'ADR :** `UninstallAsync` et `ReinstallAsync`
  prennent un paramètre `writer` en plus. « Le contexte de l'appelant » de
  l'ADR est ainsi transmis explicitement, comme pour `IOsSettingsService`
  (ADR 0044), plutôt que par le contexte ambiant `Kazo*`, qui n'existe pas dans
  un gestionnaire d'événement Blazor.
- **`GetState` d'un identifiant inconnu** lève `ArgumentException` : l'ADR ne
  prévoit pas d'état « inconnu ».
- **Interface en anglais**, comme le reste de l'app Paramètres.

## Tests de contrat

`Tests/Kazo.Os.Platform.Core.Tests/Apps/AppInstallationServiceTests.cs`
(ADR 0048 section 7) :

- désinstallation utilisateur : fichier écrit, lancement refusé, instance
  arrêtée, données conservées, ligne de journal ;
- réinstallation : ligne retirée, lancement de nouveau possible ;
- app système et fermeture contenant une app système refusées ;
- lignes `apps.config` d'app système ou de valeur invalide ignorées avec
  avertissement, identifiant inconnu ignoré en silence ;
- portée machine sans droit : `KazoUnauthorizedAccessException` ; écriture
  approuvée : fichier écrit, effet pour tous les utilisateurs ;
- état effectif machine OU utilisateur, couche utilisateur retirée à la
  déconnexion, réinstallation utilisateur refusée pour une désinstallation
  machine ;
- fermeture des dépendances à la désinstallation et à la réinstallation ;
- modification de `apps.config` par l'API fichier prise en compte sans
  redémarrage ;
- édition qui préserve commentaires et identifiants inconnus ;
- migration : `IsEnabled = false` devient une ligne `[uninstalled]`, une seule
  fois, jamais pour une app système, fichier possédé par `system`.

Ailleurs : `ShellCommandResolverTests.Uninstalled_commands_are_neither_resolved_nor_listed`,
`EcosystemBootCoordinatorTests.BootAsync_moves_persisted_disabled_entries_into_the_machine_apps_config`,
et les tests existants du lanceur, du résolveur d'associations et du
répartiteur d'intentions, qui s'appuient déjà sur le registre.

## Apps installées depuis un dépôt GitHub

L'ADR [0182](adr/0182-install-apps-from-github.md) ajoute une troisième
origine d'app, à côté des apps système et des apps intégrées : un paquet
`.kazoapp` publié par le dépôt GitHub de l'app sur sa branche
`kazoos-packages`, lu par `raw.githubusercontent.com`.

```text
kazopkg install Kazo-ca/Os-App-Webamp            # pour soi si la source est de confiance
kazopkg install Kazo-ca/Os-App-Webamp@1.0.0 --system   # pour tous, validation admin
kazopkg update                                         # toutes les apps installées
kazopkg list | info <dépôt> | remove <appId> [--user|--system]
kazopkg rollback <appId> [--user|--system]             # revient à la version gardée (ADR 0214)
kazopkg repair [--system]                              # finit ou annule une installation interrompue
kazopkg sources [add <source> [--trusted] | remove <source>]
kazopkg policy [block|unblock app|category|capability <valeur>]
```

L'ADR [0189](adr/0189-app-sources-trust-and-per-user-install.md) (mise en
œuvre décrite par l'ADR [0208](adr/0208-hack-store-sources-trust-build.md))
ajoute les sources de confiance, l'installation pour soi, la liste noire et la
validation en deux étapes :

- **Sources** : `/etc/kazoos/app-sources.config` (créé au démarrage, mode
  0644) a quatre sections. `[hosts]` donne l'hôte de téléchargement ;
  `[catalogs]` les catalogues (`official=https://…/catalog.json`) ;
  `[trusted]` les sources (`catalog:<nom>`, `github.com/<owner>` ou
  `github.com/<owner>/<repo>`) mises à `true` (de confiance) ou `false`
  (ajoutée mais non fiable) ; `[updates]` l'intervalle `interval-hours`
  (1 à 720, 6 par défaut). Les valeurs par défaut sont fixées à la compilation
  de l'hôte (catalogue officiel, `catalog:official` et `github.com/Kazo-ca` de
  confiance) par les propriétés MSBuild `KazoOsAppCatalogs`,
  `KazoOsTrustedAppSources` et `KazoOsAppUpdateIntervalHours`. Un
  administrateur ajoute une source sans redéployer, par `kazopkg sources add`,
  Paramètres ▸ Apps ▸ Sources et liste noire, ou `sudo nano`. Le démarrage ne
  crée le fichier que s'il manque et ne le réécrit jamais : un fichier **sans
  aucune section `[catalogs]`** (écrit par un build de développement d'avant
  cette section) reçoit à la lecture les catalogues de l'hôte, avec leur
  confiance sauf si `[trusted]` la fixe. Une section `[catalogs]` vide est le
  choix de l'administrateur et reste respectée.
- **Niveaux de confiance** : *vérifiée* pour le catalogue officiel, *de
  confiance* pour une source intégrée ou marquée `true`, *non fiable* sinon.
- **Liste noire** : `/etc/kazoos/app-policy.config` (créé au démarrage) a
  `[blocked-apps]`, `[blocked-categories]` et `[blocked-capabilities]`, avec des
  défauts fixés à la compilation (`KazoOsBlockedApps`,
  `KazoOsBlockedAppCategories`, `KazoOsBlockedAppCapabilities`). Une app
  bloquée ne s'installe pas pour soi : seulement pour tous, après validation.
- **Installation pour soi** : sans `--system`, une app d'une source de
  confiance, non bloquée et pas déjà installée pour le système s'installe dans
  `~/.local/share/kazoos/apps/<appId>/<version>/` avec une ligne dans
  `[installed]` de `~/.config/kazoos/installed-apps.config`, sans
  administrateur. Une seule copie à la fois : une installation pour tous retire
  les copies personnelles, et une copie personnelle d'une app déjà installée
  pour le système est refusée.
- **Validation en deux étapes** : une installation pour tous, une source non
  fiable (même pour soi) et une désinstallation pour tous demandent, à chaque
  fois et même sous `sudo`, une connexion administrateur fraîche puis la
  fenêtre qui montre l'app, sa version, sa taille, sa source, son niveau de
  confiance et ses capacités (`AppInstallApprovalOverlay`). La même fenêtre sert
  au Store et à `kazopkg`. `--allow-untrusted` n'existe plus. Modifier les
  sources ou la liste noire demande une seule authentification administrateur.
- **Catalogue** : il est construit au démarrage de la page, avec les apps
  personnelles de tous les utilisateurs ; chaque session ne voit que les
  siennes (`InstalledPackageCatalog`). Si deux utilisateurs ont des versions
  différentes de la même app, la plus récente est chargée pour les deux.
- **Vérifications avant la demande d'administrateur** : SHA-256 du paquet
  (`index.json`) et de chaque fichier, identifiant, `sdkCompatibility`, API
  présentes dans l'OS, app sur le fil principal.
- **Écriture pour tous** : `/usr/lib/kazoos/apps/<appId>/<version>/` et une ligne
  `<appId>=github:owner/repo@version` dans `[installed]` de
  `/etc/kazoos/apps.config`. Le dossier de la version précédente est gardé pour
  `kazopkg rollback` (une seule, [ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md)) ;
  `kazopkg update` garde chaque copie là où elle est.
- **Sans redémarrage** ([ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md)) :
  l'app apparaît tout de suite. Seule la mise à jour, le retrait ou le retour
  arrière d'une app déjà chargée dans la page attend un redémarrage de Kazo OS ;
  `kazopkg list` affiche `(restart pending)` d'ici là.
- **Capacités dangereuses** : demandées au premier lancement, comme pour toute
  app non système ; sauf pour une fonctionnalité (ADR 0260), dont le
  consentement à l'installation vaut approbation machine (section
  [Fonctionnalités](#fonctionnalités-adr-0260)).
- **Publication** : `dotnet build -c Release -t:KazoOsPack` dans le projet de
  l'app écrit le paquet et `index.json` dans `bin/kazoos-packages` (propriété
  `KazoOsPackageOutput`), à pousser sur la branche `kazoos-packages` ; le
  workflow `publish-packages.yml` du modèle `kazoos-solution` le fait sur un
  tag `v*`, sans signature ([ADR 0219](adr/0219-app-authors-signing-tool.md)). Le
  paquet contient toutes les ressources statiques du projet, déclarées ou non :
  le `wwwroot/`, le JavaScript collocalisé (`.razor.js`) et le bundle du CSS
  isolé (`<assemblage>.bundle.scp.css`) ; celles que l'app ne déclare pas sont
  ajoutées, avec leur SHA-256, au manifeste **du paquet** seulement
  ([ADR 0221](adr/0221-pack-all-static-web-assets-of-an-app.md)). Il contient
  aussi les assemblages satellites des assemblages de l'app,
  `lib/<culture>/<assemblage>.resources.dll` (pas ceux du SDK)
  ([ADR 0222](adr/0222-installed-app-satellite-assemblies.md)), et les
  bibliothèques propres de l'app avec leurs satellites
  ([ADR 0261](adr/0261-app-libraries-and-shared-versions.md),
  [section plus bas](#bibliothèques-dapp-et-versions-partagées-adr-0261)).
- **Ressources `wwwroot/`** ([ADR 0206](adr/0206-serve-installed-app-assets.md)) :
  au premier lancement de l'app dans la session, l'OS écrit pour chaque
  ressource déclarée une ligne de `servedFiles` qui pointe vers le contenu du
  fichier dans le dossier de l'app, sans le relire ni le copier : le
  `contentHash` du fichier doit être le SHA-256 du manifeste, sinon l'adresse
  n'est pas servie et l'app est marquée abîmée
  ([ADR 0274](adr/0274-single-copy-of-app-files.md)) ; le service worker la sert à
  `_content/<assemblage>/<chemin>`, la même adresse qu'une app compilée dans
  l'OS (balises, `import()`, CSS, Web Workers et leurs `importScripts`, et une
  page HTML chargée dans une iframe, [ADR 0243](adr/0243-serve-installed-app-pages.md)). Sur
  `localhost`, l'OS enregistre `service-worker.js?mode=installed-apps`, qui ne
  sert que ces fichiers. Une page chargée avec Ctrl+F5 n'est pas contrôlée : l'OS
  demande au worker actif de la réclamer, ce qu'il fait si elle a sa version ;
  si elle tourne sur une version plus récente (un déploiement depuis), l'OS
  active le worker de cette version, et les onglets de l'ancienne redémarrent
  dessus ([ADR 0237](adr/0237-activate-the-worker-of-a-newer-page.md)) ; sinon
  (hors ligne, installation trop longue), les fichiers ne sont pas servis (ligne
  dans `/var/log/syslog`) et l'OS redemande au lancement suivant. Le ménage des lignes, partagées par tous les
  onglets, garde aussi les fichiers des apps installées sur le disque par un
  autre onglet ([ADR 0225](adr/0225-serve-installed-app-files-to-every-page.md)).
- **CSS isolé** ([ADR 0221](adr/0221-pack-all-static-web-assets-of-an-app.md)) :
  une fois `<assemblage>.bundle.scp.css` servi, l'OS ajoute un
  `<link rel="stylesheet">` vers lui à la page, une fois par session, comme la
  feuille `styles.css` de l'hôte le fait pour une app compilée.
- **Traductions** ([ADR 0222](adr/0222-installed-app-satellite-assemblies.md)) :
  avant de charger un assemblage de l'app, l'OS lit son satellite
  `lib/<culture>/<assemblage>.resources.dll` pour chaque langue de Kazo OS
  (`InstalledAppSatellites`) et le charge : `IStringLocalizer` et
  `ResourceManager` trouvent ses textes traduits, quel que soit le `LANG` du
  processus. `AppPackage` refuse un satellite qui ne traduit aucun assemblage
  déclaré.
- **Limites** : voir la section du même nom de l'ADR 0182 (fil principal
  seulement) ; la limite des ressources `wwwroot` est levée par l'ADR 0206,
  celle des dépendances de l'app (projets et paquets NuGet tiers) par
  l'ADR 0261.

Tests : `AppPackageGatewayTests`, `AppSourcesAndPolicyTests`,
`InstalledAppsImageTests`, `AppPackageWriterTests`, `AppPackageTests`
(Platform.Core), `AppInstallApprovalOverlayTests` (Platform.Blazor),
`KazopkgCommandTests` (Commands.Wave5), le scénario `kazopkg` de
`CommandParityTests`, `InstalledAppAssetPublisherTests` (Ecosystem),
`InstalledAppSatellitesTests` (Ecosystem), `PackagedWebAssetsTests` et
`PackagedSatellitesTests` (Tools.ManifestGenerator) et
`TrimmedPublishTests.InstalledApps.cs` (paquet de test
`Tests/fixtures/installed-app` installé par `kazopkg` dans la publication
réduite, lancé par `build/publish-trimmed.sh`).

## Fonctionnalités (ADR 0260)

L'ADR [0260](adr/0260-feature-app-type.md) ajoute le genre d'app `Feature` :
une fonctionnalité facultative de l'OS (à la manière de WSL), une identité,
un paquet, un écran de permissions, installée pour toute la machine par un
administrateur. Son modèle côté SDK (`FeatureDefinition`, `FeatureEntryPoint`,
manifestes enfants) est dans
[`app-model.md`](../framework/app-model.md#fonctionnalités-featuredefinition-adr-0260).

- **Paquet** : `manifest.json` est le manifeste de la fonctionnalité (genre
  `feature`, sans `entryPoint`) ; chaque enfant a son propre fichier
  `entrypoints/<suffixe>.json`, un manifeste ordinaire de son genre avec
  `parent: "<fonctionnalité>"`, et `kazoos-package.json` les liste dans
  `manifests` (un paquet sans cette liste est lu par le préfixe `entrypoints/`).
  `AppPackage.Read` valide chaque enfant comme un manifeste de son genre, puis
  l'ensemble (`AppManifestValidator.ValidateFeature` : version, éditeur,
  permissions dans l'ensemble, `apps.package.bad-manifest` sinon). Un paquet
  ordinaire avec un fichier `entrypoints/` est refusé
  (`apps.package.unexpected-file`), et un manifeste enfant à la racine aussi
  (un enfant ne s'installe jamais seul). `AppPackageWriter.Write` et
  `pack` écrivent les enfants trouvés dans `entrypoints/` à côté du
  `manifest.json` du projet.
- **Au niveau système seulement** : `kazopkg install --user` refuse une
  fonctionnalité (`apps.package.feature-system-only`, « Une fonctionnalité
  s'installe pour toute la machine, par un administrateur »). Le `catalog.json`
  d'un catalogue porte `kind: "feature"` sur son entrée : le Store la liste
  avec les apps, dit sur sa fiche qu'elle s'installe pour toute la machine et
  ne propose que « Installer pour tous » ; le refus au clic reste le filet
  (catalogue sans `kind`, paquet qui se révèle une fonctionnalité à la
  lecture). Sans option, elle va pour tous
  (`/usr/lib/kazoos/apps/<id>/<version>/`, les enfants dans
  `entrypoints/`), avec la validation en deux étapes. La fenêtre
  d'approbation dit que c'est une fonctionnalité, nomme ses points d'entrée et
  montre **l'ensemble** des permissions, une fois.
- **Consentement = approbation machine** : une fois le paquet écrit, l'OS
  inscrit dans `/etc/kazoos/app-permissions.config` une ligne `allow` pour
  chaque capacité dangereuse de la fonctionnalité et de chacun de ses enfants
  (et les ports réels déclarés, ADR 0233 §4). Aucune fenêtre d'approbation
  n'apparaît au premier lancement d'un point d'entrée ; le refus par usager
  (ADR 0049 §9) reste possible dans Paramètres ▸ Permissions. Le retrait de la
  fonctionnalité efface ces lignes. `kazopkg install` le dit (« approuvée pour
  toute la machine… ») à la place de la note du premier lancement.
- **Catalogue** : au démarrage et à chaque ajout, `InstalledAppsImage` lit les
  enfants listés par `kazoos-package.json` de la fonctionnalité ; une
  fonctionnalité dont un enfant manque ou est invalide est laissée de côté
  avec un avertissement. `HostAppCatalog` ajoute la fonctionnalité et ses
  enfants ensemble ; une erreur sur un enfant écarte toute la fonctionnalité.
  Dans `AppCatalog`, un enfant est rattaché à sa fonctionnalité
  (`FeatureChildren`, `GetFeatureOf`, `GetFeatureChildren`) : la
  fonctionnalité s'active avant ses enfants, les enfants d'une fonctionnalité
  refusée (SDK) sont refusés avec elle, et un enfant dont la fonctionnalité
  manque ou n'en est pas une, ou qui demande une permission hors de
  l'ensemble, fait échouer le catalogue (`catalog.feature.parent.missing`,
  `catalog.feature.parent.kind`, `catalog.feature.capability-outside-set`).
- **Commandes** : dans `AssignCommands`, les commandes des enfants d'une
  fonctionnalité passent avant celles des apps ordinaires (après les apps
  système) : `x86` ne peut pas être masquée par une app installée ensuite ;
  deux fonctionnalités de même nom, la première activée gagne, l'autre reçoit
  `catalog.command.duplicate`.
- **Activation et désactivation en bloc** : `AppsConfigDocument.ExpandDependents`
  et `ExpandDependencies` traitent `parent` comme une dépendance obligatoire ;
  désactiver la fonctionnalité (Paramètres, `apps.config`) désactive ses
  enfants, la réactiver les réactive. Une ligne `[uninstalled]` qui nomme un
  enfant est ignorée avec un avertissement, et `UninstallAsync` ou
  `ReinstallAsync` sur un enfant échoue (`apps.app.feature-child:<id>:<fonctionnalité>`) ;
  l'état d'un enfant est celui de sa fonctionnalité.
- **Exécution** : chaque enfant est une app à part entière (processus,
  commande, fenêtre, service), avec son seul sous-ensemble de permissions ;
  la fonctionnalité elle-même n'a pas de point d'entrée et `AppEntryPointDiscovery`
  l'ignore. Un paquet dont un enfant ne tourne pas sur le fil principal est
  refusé comme toute app installée (`apps.package.worker-unsupported`).
- **Paramètres ▸ Applications** liste les fonctionnalités dans un groupe à
  part, chacune avec ses points d'entrée (nom, genre, id) sans action propre ;
  désinstaller ou réinstaller se fait sur la fonctionnalité. La fiche d'une
  app (`KazoAppInfo.ParentId`) nomme sa fonctionnalité. La section
  « Fonctionnalités » des Paramètres est l'ADR 0249, pas encore codée.
- **Services fournis** ([ADR 0262](adr/0262-feature-service-registry-and-async-resolution.md),
  [`feature-services.md`](../framework/feature-services.md)) : le manifeste de
  la fonctionnalité liste `providedServices` ; leur code est un assemblage
  déclaré du paquet. `AppPackage.Read` refuse un contrat qui n'est pas dans
  une bibliothèque partagée du paquet (`apps.package.service-contract-not-shared`),
  et l'installeur une fonctionnalité qui fournit un contrat qu'une autre
  fonctionnalité du catalogue fournit déjà (`apps.package.service-already-provided`).
  `AppCatalog` ne retire plus une app pour une dépendance **facultative** hors
  plage : `KazoFeatures` lui répond `VersionMismatch`.

Tests : `FeatureDefinitionTests` (App.Abstractions), `AppDefinitionInspectorTests`
et `ManifestWriterTests` (Tools.ManifestGenerator), `AppCatalogTests`,
`AppPackageTests`, `AppPackageWriterTests`, `InstalledAppsImageTests`,
`AppInstallationServiceTests` et `AppPackageGatewayTests` (Platform.Core).

## Bibliothèques d'app et versions partagées (ADR 0261)

L'ADR [0261](adr/0261-app-libraries-and-shared-versions.md) (issue #419) fait
entrer dans le paquet les assemblages dont l'app a besoin et que l'OS
n'apporte pas, et ne charge qu'une copie par page d'une bibliothèque
**partagée**. L'ADR [0256](adr/0256-app-libraries-implementation-adjustments.md)
en ajuste la mise en œuvre. Côté auteur : [`app-repository.md`](../framework/app-repository.md#bibliothèques-de-lapp).

- **Une bibliothèque ordinaire accompagne l'app, rien de plus** (ADR 0256 §1) :
  une bibliothèque que son auteur n'a pas marquée partagée n'est gérée ni par
  Kazo OS ni par le Store. L'OS vérifie le fichier comme tout fichier du
  paquet et le charge avant l'app ; il n'y a pas d'entrée de catalogue, pas de
  résolution de dépendance, pas de comparaison de versions entre apps, pas de
  redémarrage proposé, pas de refus fondé sur sa version. Les règles
  ci-dessous sur la copie unique, le choix de la copie, la version majeure et
  « Redémarrer pour terminer » ne valent que pour une bibliothèque
  **partagée**.
- **Dans le paquet** : `KazoOsPack` met dans `lib/` les sorties des
  `ProjectReference`, les assemblages des paquets NuGet et les DLL référencées
  que le build copie à côté de l'app, avec leurs satellites
  (`lib/<culture>/<bibliothèque>.resources.dll`). Ce que l'OS apporte reste
  dehors, par nom d'assemblage (propriété `KazoOsProvidedAssemblyPattern` :
  `Kazo.Os.*`, `System*`, `Microsoft.*`, `MudBlazor*`, `netstandard`,
  `mscorlib`, `WindowsBase`). `kazoos-package.json` gagne la liste
  `libraries` :

  ```json
  "libraries": [
    { "name": "Kazo.Irc.Protocol", "version": "1.0.0.0", "sha256": "…", "shared": false }
  ]
  ```

  `name` est le nom simple de l'assemblage (fichier `lib/<name>.dll`),
  `version` sa version d'assemblage. Un paquet sans cette liste (fait avant
  l'ADR) n'a aucune bibliothèque.
- **Vérifié à la lecture** : `AppPackage.Read` relit chaque bibliothèque
  listée dans son assemblage (`AppPackageLibrary.Describe`) : fichier présent
  (`apps.package.missing-assembly`), même SHA-256
  (`apps.package.hash-mismatch`), même nom, même version et même marque
  `shared` que ce que dit l'assemblage, et pas un assemblage que le manifeste
  déclare (`apps.package.bad-description`). `AppPackage.Assemblies` les
  contient : `AppPackageApiCheck` les vérifie contre le runtime comme
  l'assemblage de l'app (ADR 0242).
- **Marque `shared`** : c'est l'auteur de la bibliothèque qui la pose, avec
  `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` dans le projet de la
  bibliothèque. La propriété écrit
  `[assembly: AssemblyMetadata("Kazo.Os.SharedLibrary", "true")]` dans
  l'assemblage : la marque suit la bibliothèque quelle que soit la façon de la
  référencer (projet, DLL, paquet NuGet), l'app ne déclare rien, et un paquet
  ne peut pas marquer partagée une bibliothèque qui ne l'est pas.
- **Refus à l'installation** : `apps.package.library-reserved` protège l'OS :
  un paquet ne remplace pas un assemblage de l'OS, du runtime .NET ou d'une
  app intégrée par une bibliothèque du même nom (seul le nom compte, jamais
  la version ; une bibliothèque déjà chargée depuis un autre paquet n'est pas
  un assemblage de l'OS) ; `apps.package.library-major-mismatch` quand une
  bibliothèque partagée a une autre version majeure que la copie d'une
  fonctionnalité installée (le message donne les deux versions et la
  fonctionnalité). Sans fonctionnalité, deux apps avec deux majeures
  différentes s'installent toutes les deux. Les deux refus arrivent avant
  toute demande à un administrateur ; le Store les montre comme une version
  incompatible.
- **Chargement** : `InstalledAppsImage` lit `libraries` du
  `kazoos-package.json` de chaque app installée (`InstalledApp.Libraries`).
  Avant de charger un assemblage de l'app, `InstalledAppLibraries` charge ses
  bibliothèques, satellites compris. Une bibliothèque non partagée vient du
  dossier de l'app. Pour une bibliothèque partagée, `InstalledLibraries.Choose`
  prend la copie d'une fonctionnalité installée de même version majeure,
  sinon la copie la plus récente de cette majeure parmi les apps installées ;
  le choix est écrit dans `/var/log/syslog`
  (`libraries: Os-x86.Interface 1.2.0 loaded for ca.kazo.boxedwine (feature ca.kazo.x86)`).
- **Copie déjà chargée** : .NET ne décharge pas une assemblée. Quand la copie
  dans la page n'a pas ce que l'app attend (version majeure différente, ou
  plus ancienne que celle de l'app) et qu'un redémarrage en chargerait une
  autre, l'app reste servie par la copie chargée, une ligne le dit dans
  `/var/log/syslog`, et l'app est montrée « Redémarrer pour terminer »
  (`KazoInstalledPackage.Loaded` à `false`, `RestartRequired` du rapport
  d'installation) dans le Store et `kazopkg list`. C'est aussi le cas d'une
  fonctionnalité installée avec une copie plus récente que celle déjà chargée.
- **Un seul contexte de chargement** (ADR 0256 §3 à §5) : toutes les apps
  installées partagent le contexte de chargement par défaut. Deux
  bibliothèques **non partagées** de même nom simple dans deux apps : la
  première chargée sert les deux, la seconde est notée dans le journal, rien
  d'autre. Deux versions **majeures** d'une bibliothèque partagée sont
  installées ensemble, mais une seule est dans la page à la fois : l'app de
  l'autre majeure demande un redémarrage. Le fournisseur d'une bibliothèque
  partagée est la fonctionnalité installée qui l'embarque **et déclare
  fournir une de ses interfaces** (`providedServices`, ADR 0262) ; la règle
  provisoire de l'ADR 0256 §5 (toute fonctionnalité qui l'embarque) est
  retirée : une fonctionnalité qui embarque la bibliothèque sans en fournir
  de contrat est une cliente comme une app.

Tests : `AppPackageLibraryTests`, `InstalledLibrariesTests`,
`InstalledAppsImageTests` et `AppPackageGatewayTests` (Platform.Core),
`InstalledAppLibrariesTests` (Ecosystem), `PackagedLibrariesTests` et
`MsBuildIntegrationTests` (Tools.ManifestGenerator, fixture
`ManifestGenerator.Fixtures.WithLibrary`), et le paquet à deux assemblages de
`Tests/fixtures/installed-app` installé dans l'OS rogné
(`TrimmedPublishTests.InstalledApps.cs`).

## Le Kazo OS Store et les catalogues

L'ADR [0212](adr/0212-hack-store-app-build.md) met en œuvre l'app **Kazo
Store** (`org.kazoos.store`, ADR [0188](adr/0188-hack-store-and-app-catalog.md)) :
app système, non désinstallable, dans l'Accès rapide du menu Démarrer
(ADR [0268](adr/0268-store-in-start-menu-quick-access.md) ; elle était épinglée par défaut avant).

```text
kazopkg install org.example.notes          # cherché dans les catalogues
kazopkg install org.example.notes@1.0.0    # une version publiée
kazopkg install catalog:official/org.example.notes
kazopkg info org.example.notes
```

- **Catalogues** : chaque entrée de `[catalogs]` désigne un `catalog.json`
  (`schemaVersion` 1, branche `packages` du dépôt catalogue). Les chemins du
  fichier (paquet, icône, captures, descriptions) sont relatifs à
  `catalog.json`. L'OS le garde 5 minutes ; une app ou une version invalide est
  ignorée avec un avertissement dans `/var/log/syslog`. Chaque entrée porte un
  `kind` : `app` quand il manque, `feature` (ADR 0260 §4 : listée avec les
  apps, installée pour toute la machine seulement) ou `theme-pack` (ADR 0213) ;
  une valeur que l'OS ne connaît pas est lue comme `app` avec un avertissement,
  pour qu'un catalogue plus récent ne cache rien (le paquet dit ce qu'il est à
  l'installation).
  porter `fileHandlers` (`{ mediaType?, extensions, actions }`, recopié du
  manifeste, ADR [0257](adr/0257-offer-a-store-app-for-an-unsupported-file.md)) :
  quand aucune app installée n'ouvre un fichier, l'OS propose les apps qui le
  déclarent, lues dans les catalogues déjà téléchargés
  ([`user-file-associations.md`](user-file-associations.md#aucune-app-installée--les-apps-du-store-adr-0257)).
  Au plus 64 entrées par version et 64 extensions ou actions par entrée.
- **Source écrite** : `<appId>=catalog:<nom>/<appId>@<version>` dans
  `[installed]`. La confiance est celle de `catalog:<nom>` ; le catalogue
  officiel est *vérifié*. Le SHA-256 et la taille viennent du catalogue ; les
  autres vérifications sont celles d'une app GitHub.
- **AppId seul** : cherché dans tous les catalogues. Si un seul le liste, il
  est installé depuis celui-là ; si plusieurs le listent, l'installation est
  refusée (`apps.package.ambiguous`) et le message donne chaque
  `catalog:<nom>/<appId>` possible, à retaper
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §2).
- **L'app** : accueil (ci-dessous), recherche (nom, description courte,
  étiquettes, identifiant, sans casse ni accents), fiche (icône, captures,
  description Markdown dessinée sans HTML, version, taille, licence,
  catégorie, date, dépôt, capacités, confiance), Installer pour moi ou pour
  tous, Ouvrir, Mettre à jour, Désinstaller, onglet **Installées** avec « Tout
  mettre à jour ». Quand une app attend un redémarrage, le bandeau propose
  **Redémarrer pour terminer** (sans administrateur). Arguments de lancement :
  `--page=installed`, `--page=themes`, `--app=<appId>` et, avec `--app`,
  `--open-after-install=<chemin>` : une fois l'app installée, le Store ouvre ce
  fichier (ADR [0257](adr/0257-offer-a-store-app-for-an-unsupported-file.md) §5,
  bouton « Voir dans le Store » de l'Explorateur). Le Store déjà ouvert reçoit
  aussi ces arguments.
- **Progression** (ADR [0266](adr/0266-store-ux-refresh.md) §7) : pendant une
  installation ou une mise à jour, la fiche et la carte de l'app montrent
  l'étape que l'OS rapporte à `KazoAppPackages.InstallAsync(source, scope,
  progress)` : préparation, téléchargement (pourcentage et tailles ; la taille
  du catalogue quand l'hôte n'annonce pas la sienne), vérification, attente
  d'un administrateur, installation. Seuls les boutons de cette app sont
  désactivés. Les actions qui changent les apps installées passent une à la
  fois, comme un gestionnaire de paquets qui tient son verrou : la suivante
  affiche « En attente de l'autre installation… ». Ouvrir une app n'attend pas.
- **Navigation et clavier** (série `store-ux`, chantier 1) : chaque vue
  (accueil, catégorie, résultats, Installées, Thèmes, fiche) s'ouvre en haut ;
  en revenant d'une fiche, la liste reprend la position où on l'avait laissée
  (`StoreScrollMemory` et `wwwroot/store-scroll.js` : la seule chose que C# ne
  peut pas faire est déplacer la barre de défilement). La recherche filtre
  aussi l'onglet Installées (y compris une app qu'aucun catalogue ne liste
  plus, par son nom et son identifiant) ; Entrée ouvre le premier résultat.
  Les onglets suivent le modèle WAI-ARIA (← → Début Fin, `tabpanel`), le focus
  du clavier est visible sur chaque contrôle, et sous 640 px l'en-tête tient
  sur deux lignes (cibles portées à 44 px par le chantier 6, ci-dessous). La description ne
  répète pas un premier titre égal au nom de l'app, et une ligne qui suit un
  élément de liste reste dans cet élément.
- **Captures de la fiche** (série `store-ux`, chantier 2) : chaque capture est
  un bouton (`StoreScreenshots.razor`). Un clic, Entrée ou Espace l'ouvre dans
  une visionneuse qui couvre la fenêtre du Store (pas l'écran) : précédent et
  suivant par les boutons, ← et →, Début et Fin, ou un balayage du doigt ;
  Échap, le bouton Fermer, un clic à côté de l'image ou le Retour de l'OS
  (bouton Précédent de la souris, Alt+←, Retour du téléphone) la ferment, et le
  focus revient à la vignette de la dernière capture vue. Le catalogue ne donne
  pas la taille des captures : la vignette a une hauteur fixe (240 px, 320 px
  dans une fenêtre large, 200 px sur téléphone) et une largeur en 16:10 tant
  que l'image n'est pas arrivée, pour que la page ne saute pas. Sur téléphone,
  les captures en portrait passent en premier (chantier 6, ci-dessous) ; la
  visionneuse suit l'ordre des vignettes.
- **Fiche d'une app** (série `store-ux`, chantier 3,
  [ADR 0266](adr/0266-store-ux-refresh.md) §4 et §6) : la fiche est un
  composant à part, `StoreAppPage.razor`, qui lit l'app et lance les actions
  de `StoreWindow`. Une barre reste en haut pendant le défilement : Retour,
  puis l'icône, le nom et le bouton principal (Installer pour moi, Mettre à
  jour ou Ouvrir) dès que l'en-tête est passé sous la barre ; l'en-tête garde
  les deux boutons d'installation. Les capacités sont écrites en langage
  clair (textes `Capability.<id>.Title` et `.Risk` du Store, en français et en
  anglais), groupées (accès sensibles d'abord, puis fichiers, apps et bureau,
  réseau, système et comptes, autres) ; « Voir les identifiants techniques »
  les montre, et une capacité sans texte connu garde son identifiant
  (`StoreAppFacts`). La fiche liste les types de fichiers que l'app ouvre
  (`fileHandlers` du catalogue, ADR 0257 §1, exposés par
  `KazoCatalogAppVersion.FileHandlers`), remplace l'adresse brute du dépôt par
  un lien « Code source sur GitHub » (http et https seulement), et, pour une
  app du catalogue officiel, un bouton copie le lien « Essayer »
  (`<adresse de l'OS>?autolaunch=<appId>`, ADR 0258) dans le presse-papiers de
  l'OS (capacité `clipboard.write` du Store), qui le passe à celui de la
  machine. Le panneau d'informations suit le défilement sans dépasser la
  hauteur de la fenêtre ; sous 640 px, il passe sous la description. Les
  textes longs (description courte et longue, capacités) sont écrits dans la
  police sans empattement du système (`system-ui`), sans téléchargement ; les
  titres, boutons et étiquettes gardent la police du thème.
  `StoreAppPage.razor.js` ne fait que ce que C# ne voit pas : savoir si
  l'en-tête est encore visible (`IntersectionObserver`), donner la hauteur de
  la zone qui défile, et lire l'adresse de la page.
- **Accueil** (série `store-ux`, chantier 5,
  [ADR 0266](adr/0266-store-ux-refresh.md) §2, §3 et §5) : un bandeau « À la
  une » montre une app (icône, nom, description courte, première capture,
  version, taille, licence, son bouton et « Voir la fiche »). Le Store la
  choisit seul : l'app publiée le plus récemment (`publishedAt` de la version
  qu'il installe) parmi celles qui ne sont pas installées, sinon la plus
  récente de toutes (`StoreCatalog.Featured`). Dessous, une seule grille
  liste toutes les apps, de la plus récente à la plus ancienne (une app sans
  date en dernier, par nom), avec des pastilles qui la filtrent par catégorie
  (« Toutes · 6 », puis chaque catégorie avec son nombre d'apps ; une app dont
  aucun catalogue ne nomme la catégorie est sous « Autre »). Le Retour de l'OS
  enlève le filtre ; la page « Voir tout » disparaît. Un clic n'importe où sur
  une carte, hors de ses boutons, ouvre la fiche ; la carte porte un bouton :
  « Installer » (pour moi), « Ouvrir » ou « Mettre à jour » avec son état.
  Pour un administrateur (`KazoUserInfo.IsAdministrator`), « Installer » a un
  bouton à menu à sa droite qui offre « Installer pour tout le monde » (bouton
  de menu WAI-ARIA : le menu ouvert prend le focus, Échap le ferme et rend le
  focus, un clic à côté le ferme) ; l'OS demande toujours lui-même un
  administrateur (ADR 0189 §4). La fiche garde ses deux boutons
  d'installation. Une carte ne montre le badge de confiance que si l'app
  n'est pas vérifiée ; la fiche le montre toujours. La fenêtre s'ouvre en
  1100 × 720 (ramenée à la taille de l'écran). Pendant le chargement des
  catalogues, la page dessine des cartes vides (un mot est lu aux lecteurs
  d'écran). L'onglet **Thèmes** est masqué tant qu'aucun catalogue n'offre de
  pack et qu'aucun pack n'est installé ; `--page=themes` ouvre alors
  l'accueil.
- **Messages** (série `store-ux`, chantier 3, ADR 0266 §6) : le résultat d'une
  action s'affiche en bas de la fenêtre et se ferme seul après 6 secondes ;
  une erreur reste jusqu'à ce qu'on la ferme (`StoreWindow.Messages.cs`).
- **Téléphone** (série `store-ux`, chantier 6,
  [ADR 0266](adr/0266-store-ux-refresh.md) §8) : quand le Store fait 640 px de
  large ou moins (un téléphone, ou une fenêtre étroite), une requête de
  conteneur change la mise en page sans changer le balisage.
  - L'en-tête tient sur deux lignes : la marque et Actualiser, puis la
    recherche.
  - Les onglets passent dans une barre en bas de la fenêtre (60 px), une icône
    au-dessus de chaque libellé, le nombre de mises à jour sur le coin de
    l'icône. Ce sont les mêmes boutons du même `tablist` : l'ordre du clavier
    et les touches ne changent pas. La zone défilante s'arrête au-dessus de la
    barre, et les messages flottent au-dessus d'elle.
  - Chaque cible tactile fait au moins 44 × 44 px (recherche, Actualiser,
    onglets, pastilles, boutons et menu « Installer », nom d'une carte, liens
    de la fiche, boutons de la visionneuse). Un lien dans le texte d'une
    description en est exempté, comme le permet le WCAG 2.5.8.
  - Les captures en portrait passent avant celles en paysage, chaque groupe
    dans l'ordre du catalogue (`StoreScreenshotOrder`). Le catalogue ne
    change pas : `StoreScreenshots.razor.js` lit la taille de chaque capture
    dans le navigateur et dit au composant si le Store est plus étroit que
    640 px ; une capture dont la taille n'est pas connue compte comme paysage.
    Une capture en portrait s'affiche haute (340 px). Sans JavaScript, l'ordre
    du catalogue reste.
- **Mises à jour** : `AppUpdateMonitor` vérifie à l'ouverture de la session
  puis toutes les `[updates] interval-hours` heures et envoie une notification
  qui ouvre l'onglet Installées. Chaque vérification télécharge aussi tous
  les catalogues de `[catalogs]`, pour les propositions de l'ADR 0257. Aucun badge sur l'icône du Store
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §1).
- **Épinglage par défaut** : `start-menu.json` note par profil
  `defaultPinsApplied` ; une épingle par défaut est ajoutée une fois, à un
  nouveau profil comme à un profil existant, et ne revient pas après un
  désépinglage. Depuis l'ADR [0268](adr/0268-store-in-start-menu-quick-access.md)
  la liste est vide : le Store est un lien de l'Accès rapide, et un profil qui
  l'avait reçu par défaut perd cette épingle une seule fois
  (`RetiredDefaultPinnedAppIds`) ; une épingle remise reste.

Tests : `AppCatalogDocumentTests`, les tests de catalogue et de progression
d'`AppPackageGatewayTests`, `HttpAppPackageDownloaderTests`,
`StartMenuPreferencesServiceTests` (Platform.Core), `Kazo.Os.Apps.Store.Tests`
(`StoreCatalog`, `StoreMarkdown`, `StoreNavigationTests`, `StoreOperations`,
`StoreScreenshotsTests`, `StoreScreenshotOrderTests`, `StoreAppFactsTests`, manifeste) et
`Kazo.Os.UI.E2E.Tests` (`StoreNavigationTests`, `StoreInstallProgressTests`,
`StoreScreenshotViewerTests`, `StoreAppPageTests`, `StoreHomeTests`,
`StoreMobileTests`, qui mesure chaque contrôle sur un téléphone de 390 × 844).

## Canal bêta et version proposée (ADR 0271)

Chantier 3 de la série `release-channels/base`
([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §5 et §6,
[plan](plans/release-channels-plan.md)).

- **Le canal suit l'OS.** Un OS dont la version a un suffixe
  (`3.5.0-preview.1`, `KazoOsSdk.SemanticVersion.PreRelease`) lit le canal
  bêta du catalogue officiel,
  `https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog-beta.json`
  (`AppPackageDefaults.OfficialBetaCatalogAddress`) ; un OS stable lit
  `catalog.json`. `AppPackageDefaults.OfficialCatalogAddressFor(version)` donne
  l'adresse, et `AppPackageDefaults.Standard` l'utilise : c'est ce qui est écrit
  dans un nouveau `/etc/kazoos/app-sources.config`. Aucun réglage.
- **Migration de `/etc/kazoos`.** L'OS ne réécrit jamais un fichier existant. À
  la lecture, une ligne de `[catalogs]` qui désigne le catalogue officiel de
  l'un ou l'autre canal est lue comme le catalogue officiel du canal de l'OS
  (`AppSourcesConfigDocument.Read`) : un fichier écrit par un OS stable donne
  `catalog-beta.json` à une préversion, et l'inverse. Une autre adresse (miroir,
  catalogue d'entreprise, ou `official=` changé par un administrateur) est
  gardée telle quelle, et un hôte compilé avec ses propres catalogues
  (`KazoOsAppCatalogs`) ne déplace rien.
- **Version proposée.** Le Store (`KazoCatalogApp.LatestVersion`, la version
  installée par défaut), `kazopkg info` et `kazopkg install <app>` sans
  version, la vérification des mises à jour (`AppUpdateMonitor`, `kazopkg update`), les liens « Essayer » (ADR 0258) et les propositions pour un
  fichier (ADR 0257) choisissent la **version la plus récente dont
  `sdkCompatibility` accepte le SDK de l'OS** (`AppVersionChoice`), et non plus
  la dernière publiée : une app reconstruite sur un SDK plus récent ne bloque
  pas un OS plus ancien, qui garde la version précédente. Une version sans
  plage connue (un `index.json` de dépôt GitHub n'en donne pas) compte comme
  compatible ; l'installation revérifie le manifeste. Quand aucune version ne
  convient, la plus récente reste affichée et l'installation la refuse avec
  `apps.package.sdk-incompatible`, qui dit quel SDK elle demande.
- **Préversions d'apps.** Proposées quand l'OS est lui-même une préversion, ou
  quand la version installée en est une (comme avant) ; sinon seules les
  versions stables le sont, sauf pour une app qui n'a publié que des
  préversions. `1.0.1` remplace `1.0.1-beta.2` (ordre SemVer).

### Format de `catalog-beta.json`

Fixé par ce chantier pour le chantier 4 (`Os-AppMarketPlace`), qui l'écrit :

- Même dossier que `catalog.json` (racine de la branche `packages`), **même
  format** (`schemaVersion` 1, mêmes champs, mêmes règles de lecture, 8 Mio au
  plus) : l'OS le lit avec `AppCatalogDocument.Parse`, sans cas particulier.
- **Contenu** : tout `catalog.json` (catégories, apps, versions stables), plus
  les versions bêta des apps, ajoutées dans `versions` de l'app. Une app qui
  n'a encore qu'une version bêta y figure aussi, avec ses champs de fiche.
- **Version bêta** : SemVer avec suffixe `<prochaine corrective>-beta.<n>`
  (`1.0.1-beta.1`), dossier de paquet
  `packages/<appId>/<version>/<appId>-<version>.kazoapp` et ressources
  `assets/<appId>/<version>/` comme une version stable, signée avec la même clé
  (`KAZO_STORE_SIGNING_KEY`), `sdkCompatibility.minimumVersion` = la préversion
  du SDK sur laquelle elle a été compilée (`3.5.0-preview.1`).
- **Ordre** : versions de la plus récente à la plus ancienne en SemVer
  (`1.0.1` > `1.0.1-beta.2` > `1.0.1-beta.1` > `1.0.0`).
- **`latestVersion`** : la plus récente version **stable**, comme dans
  `catalog.json` ; la plus récente bêta seulement pour une app sans version
  stable. L'OS ne s'en sert que quand aucune version n'est compatible.
- Les champs de fiche (`name`, `shortDescriptions`…) viennent de la version
  donnée par `latestVersion`, pour que la fiche d'une app ne change pas avec
  une bêta.
- Une version bêta ne compte pas dans les 3 versions gardées ; elle est retirée
  quand la version stable qu'elle précède est publiée (ADR 0271 §5).

Tests : `ReleaseChannelTests` et `AppPackageGatewayTests.ReleaseChannels`
(Platform.Core) ; les tests E2E du Store et du lien « Essayer » servent les deux
adresses du catalogue officiel.

## Ajout sans redémarrage, signature et transactions

L'ADR [0214](adr/0214-hack-store-hot-add-signing-transactions.md) (issue #251)
termine le Kazo OS Store.

- **Catalogue en direct** : après chaque installation, mise à jour, retrait ou
  retour arrière, la passerelle appelle `IInstalledAppsActivator`
  (`InstalledAppsActivator` dans `Kazo.Os.Ecosystem.Host`). Il relit les apps
  installées, remplace le contenu de l'unique `AppCatalog` de la page
  (`InstalledAppsBoot.Live`, `AppCatalog.ReplaceContents`, évènement
  `AppCatalog.Changed`) et ajoute les assemblies au registre de chargement
  paresseux. Une app dont une assembly est déjà chargée garde sa version jusqu'au
  redémarrage (`KazoPackageInstallReport.RestartRequired`).
- **Signature** : champ `signature` facultatif de chaque version (`index.json`,
  `catalog.json`), ECDSA P-256 / SHA-256 en IEEE P1363, base64. Les clés
  publiques sont dans `[signing-keys]` de `/etc/kazoos/app-sources.config`
  (`catalog:official=MFkw…`, plusieurs clés séparées par des virgules) et dans la
  propriété MSBuild `KazoOsAppSigningKeys` de l'hôte, toujours exigées. Une
  source qui a une clé refuse un paquet non signé (`apps.package.unsigned`) ou mal
  signé (`apps.package.bad-signature`). La vérification passe par Web Crypto
  dans le navigateur (`BrowserAppPackageSignatureVerifier`, `webAuthn.js`
  `verify`), par `ECDsa` ailleurs.
- **Clé du catalogue officiel** : depuis le 2026-11-01, la clé publique du
  catalogue officiel est compilée dans l'OS
  (`AppPackageDefaults.OfficialCatalogSigningKey`, `catalog:official`). Tout
  paquet du Kazo OS Store officiel doit porter la signature que `publish.yml` du
  catalogue pose avec le secret `KAZO_STORE_SIGNING_KEY` ; un paquet non signé ou
  falsifié est refusé. Pour changer de clé sans coupure : ajouter la nouvelle
  dans `[signing-keys]` (ou `KazoOsAppSigningKeys`), republier avec elle, puis
  remplacer la constante.
- **Signature seulement dans le marketplace**
  ([ADR 0219](adr/0219-app-authors-signing-tool.md),
  modifiée le 2026-11-07) : seul le dépôt
  `Kazo-ca/Os-AppMarketPlace` signe des paquets. `KazoOsPack` ne signe jamais
  (plus de `KazoOsKeyGen`, de `keygen`/`sign` dans le générateur, ni de
  `HACKEROS_APP_SIGNING_KEY`) ; un paquet d'une branche `kazoos-packages` porte
  son SHA-256 seulement. `index.json` garde les signatures des versions déjà
  publiées. Les clés de `[signing-keys]` sont celles des catalogues
  (`catalog:<nom>`) ; une ancienne clé `github.com/<owner>` ferait refuser les
  paquets non signés de ce dépôt et doit être retirée.
- **Transactions** : journal `<racine>/<appId>/.transaction`, fichiers écrits
  dans `<version>.partial/` puis renommés, ligne `[installed]` comme point de
  validation, ménage après. `kazopkg repair [--system]` finit ou annule ce
  qu'une coupure a laissé (`AppPackageInstaller.RecoverAsync`, `RepairAsync`).
  `update.minimumUpgradableVersion` refuse la mise à jour en place d'une version
  trop ancienne (`apps.package.reinstall-required`).
- **Retour arrière** : une mise à jour garde la version qu'elle remplace ;
  `kazopkg rollback <appId>` et le bouton « Revenir à <version> » du Store la
  remettent, avec les approbations d'une installation de la même portée.
- **Données** : au lancement d'une app installée dont la version a changé,
  `AppDataGuard` copie `~/.config/<appId>/` et `~/.local/share/<appId>/` dans
  `~/.local/share/kazoos/app-backups/<appId>/<ancienne version>/`, puis donne
  `KAZOOS_PREVIOUS_VERSION` à l'app (mise à jour) ou remet la sauvegarde de la
  version retrouvée (retour arrière). La dernière version lancée est notée dans
  `~/.config/kazoos/app-data-versions.config`. Deux sauvegardes au plus par app.

Tests : `AppPackageGatewayTests.Transactions.cs`, `AppDataGuardTests`,
`AppPackageSignatureTests`, `AppCatalogTests` (Platform.Core),
`InstalledAppsBootTests` (Ecosystem), `KazopkgCommandTests` (Commands.Wave5).

## Tâches

- [x] `IAppInstallationService`, `AppInstallationService`, `AppsConfigDocument`.
- [x] Lecture tolérante, avertissements dans `/var/log/syslog`, identifiants inconnus conservés.
- [x] Surveillance des deux fichiers, rechargement à la connexion et à la déconnexion.
- [x] Fermeture des dépendances (désinstallation, réinstallation), refus des apps système.
- [x] Arrêt des instances, `AppEnablementRegistry.Changed`.
- [x] Filtrage : lanceur, recherche, « Open With... », associations, intentions, `launch`, terminal.
- [x] Migration `AppEnablementToFilesMigration` et retrait de l'hydratation `IsEnabled` au démarrage.
- [x] Retrait d'`AppCatalogSyncService` et de son contrat.
- [x] Onglet « Installed Apps » refait (`InstalledAppsPanel`).
- [x] Tests de contrat et documentation.
- [x] Kazo OS Store : catalogues, `kazopkg install <appId>`, mises à jour par notification, épinglage par défaut (ADR 0212, ADR 0215).
- [x] Ajout sans redémarrage, signature, installation transactionnelle, `kazopkg rollback` et `repair`, sauvegarde des données (ADR 0214).
- [x] Progression d'installation dans le Store : `IProgress<KazoPackageInstallProgress>`, étapes, file d'attente, boutons de l'app seule (ADR 0266 §7).
- [x] Accueil du Store : app à la une, grille de la plus récente avec pastilles de catégorie, bouton de carte avec le menu de l'administrateur, cartes vides au chargement, fenêtre 1100 × 720, onglet Thèmes masqué sans pack (ADR 0266 §2, §3, §5).
