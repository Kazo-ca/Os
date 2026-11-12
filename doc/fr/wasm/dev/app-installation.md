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
  app non système ; sauf pour une fonctionnalité (ADR 0243), dont le
  consentement à l'installation vaut approbation machine (section
  [Fonctionnalités](#fonctionnalités-adr-0243)).
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
  ([ADR 0244](adr/0244-app-libraries-and-shared-versions.md),
  [section plus bas](#bibliothèques-dapp-et-versions-partagées-adr-0244)).
- **Ressources `wwwroot/`** ([ADR 0206](adr/0206-serve-installed-app-assets.md)) :
  au premier lancement de l'app dans la session, chaque ressource déclarée est
  lue par `AppAssetStore` (empreinte vérifiée) et copiée dans le cache
  `kazoos-installed-apps` ; le service worker la sert à
  `_content/<assemblage>/<chemin>`, la même adresse qu'une app compilée dans
  l'OS (balises, `import()`, CSS, Web Workers et leurs `importScripts`). Sur
  `localhost`, l'OS enregistre `service-worker.js?mode=installed-apps`, qui ne
  sert que ces fichiers. Une page chargée avec Ctrl+F5 n'est pas contrôlée : l'OS
  demande au worker actif de la réclamer, ce qu'il fait si elle a sa version ;
  si elle tourne sur une version plus récente (un déploiement depuis), l'OS
  active le worker de cette version, et les onglets de l'ancienne redémarrent
  dessus ([ADR 0237](adr/0237-activate-the-worker-of-a-newer-page.md)) ; sinon
  (hors ligne, installation trop longue), les fichiers ne sont pas servis (ligne
  dans `/var/log/syslog`) et l'OS redemande au lancement suivant. Le ménage du cache, partagé par tous les
  onglets, garde aussi les fichiers des apps installées sur le disque par un
  autre onglet ([ADR 0225](adr/0225-serve-installed-app-files-to-every-page.md)).
- **CSS isolé** ([ADR 0221](adr/0221-pack-all-static-web-assets-of-an-app.md)) :
  une fois `<assemblage>.bundle.scp.css` copié dans le cache, l'OS ajoute un
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
  l'ADR 0244.

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

## Fonctionnalités (ADR 0243)

L'ADR [0243](adr/0243-feature-app-type.md) ajoute le genre d'app `Feature` :
une fonctionnalité facultative de l'OS (à la manière de WSL), une identité,
un paquet, un écran de permissions, installée pour toute la machine par un
administrateur. Son modèle côté SDK (`FeatureDefinition`, `FeatureEntryPoint`,
manifestes enfants) est dans
[`app-model.md`](../framework/app-model.md#fonctionnalités-featuredefinition-adr-0243).

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
- **Au niveau système seulement** : `kazopkg install --user` et « Installer
  pour moi » du Store refusent une fonctionnalité
  (`apps.package.feature-system-only`, « Une fonctionnalité s'installe pour
  toute la machine, par un administrateur ») ; sans option, elle va pour tous
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

Tests : `FeatureDefinitionTests` (App.Abstractions), `AppDefinitionInspectorTests`
et `ManifestWriterTests` (Tools.ManifestGenerator), `AppCatalogTests`,
`AppPackageTests`, `AppPackageWriterTests`, `InstalledAppsImageTests`,
`AppInstallationServiceTests` et `AppPackageGatewayTests` (Platform.Core).

## Bibliothèques d'app et versions partagées (ADR 0244)

L'ADR [0244](adr/0244-app-libraries-and-shared-versions.md) (issue #419) fait
entrer dans le paquet les assemblages dont l'app a besoin et que l'OS
n'apporte pas, et ne charge qu'une copie par page d'une bibliothèque
**partagée**. Côté auteur : [`app-repository.md`](../framework/app-repository.md#bibliothèques-de-lapp).

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
- **Refus à l'installation** : `apps.package.library-reserved` quand une
  bibliothèque porte le nom d'un assemblage de l'OS, du runtime .NET ou d'une
  app intégrée (une bibliothèque déjà chargée depuis un autre paquet n'est pas
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
- **Limites** : toutes les apps installées partagent le contexte de
  chargement par défaut. Deux bibliothèques **non partagées** de même nom
  simple dans deux apps : la première chargée sert les deux, la seconde est
  notée dans le journal. Deux versions **majeures** d'une bibliothèque
  partagée sont installées ensemble, mais une seule est dans la page à la
  fois : l'app de l'autre majeure demande un redémarrage. Tant que les
  fonctionnalités ne déclarent pas les services qu'elles fournissent
  (ADR 0245), une fonctionnalité qui embarque la bibliothèque partagée en est
  le fournisseur.

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
app système, non désinstallable, épinglée par défaut dans le menu Démarrer.

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
  ignorée avec un avertissement dans `/var/log/syslog`.
- **Source écrite** : `<appId>=catalog:<nom>/<appId>@<version>` dans
  `[installed]`. La confiance est celle de `catalog:<nom>` ; le catalogue
  officiel est *vérifié*. Le SHA-256 et la taille viennent du catalogue ; les
  autres vérifications sont celles d'une app GitHub.
- **AppId seul** : cherché dans tous les catalogues. Si un seul le liste, il
  est installé depuis celui-là ; si plusieurs le listent, l'installation est
  refusée (`apps.package.ambiguous`) et le message donne chaque
  `catalog:<nom>/<appId>` possible, à retaper
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §2).
- **L'app** : accueil par catégories, recherche (nom, description courte,
  étiquettes, identifiant, sans casse ni accents), fiche (icône, captures,
  description Markdown dessinée sans HTML, version, taille, licence,
  catégorie, date, dépôt, capacités, confiance), Installer pour moi ou pour
  tous, Ouvrir, Mettre à jour, Désinstaller, onglet **Installées** avec « Tout
  mettre à jour ». Quand une app attend un redémarrage, le bandeau propose
  **Redémarrer pour terminer** (sans administrateur). Arguments de lancement :
  `--page=installed`, `--app=<appId>`.
- **Mises à jour** : `AppUpdateMonitor` vérifie à l'ouverture de la session
  puis toutes les `[updates] interval-hours` heures et envoie une notification
  qui ouvre l'onglet Installées. Aucun badge sur l'icône du Store
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §1).
- **Épinglage par défaut** : `start-menu.json` note par profil
  `defaultPinsApplied` ; le Store est ajouté une fois, à un nouveau profil
  comme à un profil existant, et ne revient pas après un désépinglage.

Tests : `AppCatalogDocumentTests`, les tests de catalogue
d'`AppPackageGatewayTests`, `StartMenuPreferencesServiceTests`
(Platform.Core) et `Kazo.Os.Apps.Store.Tests` (`StoreCatalog`,
`StoreMarkdown`, manifeste).

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
