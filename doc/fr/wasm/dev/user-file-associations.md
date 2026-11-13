> [English version](../../../en/wasm/dev/user-file-associations.md)

# Applications par défaut propres à chaque utilisateur

Ce document couvre la mise en œuvre des associations de fichiers par utilisateur. Les formats des deux fichiers, l'ordre de résolution, l'API `KazoApps` et les capacités sont dans [`../framework/file-associations-api.md`](../framework/file-associations-api.md).

Chantier `v3/4b`, suite de `v3/4-intents`, selon
l'[ADR 0121](adr/0121-user-file-associations.md). Complète
[`app-intents.md`](../framework/app-intents.md) (résolution des intents) et
[`settings-files.md`](../framework/settings-files.md) (emplacements des fichiers).

## Objet

Chaque utilisateur choisit l'app qui ouvre un type de fichier (`.md`,
`text/plain`) ou un schéma d'URI (`https`) pour lui seul, sans droits
d'administrateur. Son choix passe avant celui de la machine, comme
`~/.config/mimeapps.list` passe avant `/etc/xdg/mimeapps.list` sous Linux.

## Architecture

```text
Paramètres / « Ouvrir avec » / xdg-mime
        │ KazoApps.Get/Set/ResetDefaultHandlerAsync
        ▼
IAppCatalogGateway (AppCatalogGateway ; RemoteCatalogGateway dans un worker : cat.defaults, cat.setdef)
        │ capacités file-associations.read (+ filesystem.user-home.write pour écrire)
        ▼
FileAssociationResolver ──► UserFileAssociationStore (~/.config, sous l'identité de l'utilisateur)
        │                └─► FileAssociationSettingsDocuments (/etc, ou contenu intégré)
        ▼
AppIntentDispatcher (passe le principal à chaque résolution)
```

- **`UserFileAssociationStore`** (`Platform.Core/Intents`) : lit le fichier de
  l'utilisateur (validé par `FileAssociationSettingsValidator`), l'écrit en
  gardant les autres entrées et les autres actions d'une même cible. Crée
  `~/.config/kazoos` en `0755` et le fichier en `0644`.
- **`PrincipalTextFile`** : lecture et écriture d'un petit fichier texte du
  dossier personnel sous l'identité de l'utilisateur, partagées avec
  `IntentChoiceStore` (`intents.config`).
- **`FileAssociationIndex`** : rangé par cible **et** action, dans les deux
  couches.
- **`FileAssociationResolver`** : surcharges qui prennent l'utilisateur
  (`ResolveAsync(intent, user)`, `ResolveSchemeAsync(scheme, user)`,
  `GetHandlerAppIdsAsync(path, user)`), `GetConfiguredDefaultsAsync(user)` et
  `SetUserDefaultAsync(user, target, action, appId?)`. Les anciennes
  signatures lisent la couche système seulement.
- **`AppCatalogGateway`** reçoit le principal et le vérificateur de capacités
  de l'app.

## Interfaces

- **Paramètres > Associations de fichiers** (« Applications par défaut ») :
  une ligne par cible et action déclarées par les apps installées (extensions,
  puis types de média, puis schémas affichés `https:`), un sélecteur
  (« Défaut du système (X) », « Seule app (X) » ou « Demander à chaque fois »,
  puis les apps), le badge d'origine (à vous, défaut système, intégré) et un
  filtre. Composant `FileAssociationsPanel`.
- **Explorateur > Ouvrir avec** : case « Toujours utiliser cette app pour les
  fichiers .ext » quand l'app choisie déclare l'extension. L'explorateur
  déclare `file-associations.read`.
- **`xdg-mime`** ([`linux-commands.md`](linux-commands.md)) :
  `query default <type>`, `default <app> <type>...`, `query filetype <fichier>`.

## Aucune app installée : les apps du Store (ADR 0257)

Chantier `open-with-store/2-suggest-catalog-apps` de
l'[ADR 0257](adr/0257-offer-a-store-app-for-an-unsupported-file.md). Quand
`FileAssociationResolver` ne trouve aucune app installée (`NoHandler`),
`AppIntentDispatcher` demande à **`CatalogAppSuggestions`**
(`Platform.Core/Apps/Packages`) quelles apps des catalogues du Store ouvrent ce
fichier :

- **Catalogues lus** : seulement les copies déjà téléchargées
  (`AppPackageInstaller.GetCachedCatalog`, quel que soit leur âge) ; ouvrir un
  fichier n'attend jamais le réseau. `AppUpdateMonitor` télécharge tous les
  catalogues de `[catalogs]` à l'ouverture de la session puis à chaque
  vérification (`RefreshCatalogsAsync`), en arrière-plan. Sans copie, la liste
  est vide et le résultat reste celui d'avant.
- **Choix d'une app** : sa dernière version stable compatible avec le SDK de
  l'OS doit déclarer l'extension du fichier (sinon son type MIME) pour
  l'action, dans le champ `fileHandlers` de `catalog.json`. Les apps déjà
  installées (catalogue en cours, `[installed]` du système et de
  l'utilisateur) et celles que bloque `app-policy.config` (identifiant,
  catégorie ou capacité) sont exclues. Catalogue officiel d'abord, puis
  l'ordre de `[catalogs]` ; une app listée par deux catalogues n'est proposée
  qu'une fois ; huit apps au plus.
- **Action** : `open` pour une ouverture ; pour une modification sans
  gestionnaire, les apps qui l'**ouvrent** (la modification retombe sur `open`).
- **Résultat** : `AppIntentOpenFileResult.SuggestedCatalogApps`
  (`AppIntentSuggestedCatalogApp` : id, nom, catalogue), transmis aux workers
  (`WireCodec`, champ `sg`) et, dans le SDK, `KazoOpenFileResult.SuggestedCatalogApps`
  (`KazoSuggestedCatalogApp`).
- **`xdg-open`** : sous « aucune application ne peut ouvrir », la liste des
  apps (nom, id, catalogue) et `kazopkg install catalog:<catalogue>/<appId>`
  pour la première ; le code de sortie reste 3.
- L'Explorateur et le Store (bouton « Voir dans le Store »,
  `--open-after-install`) viennent avec le chantier 3.

## Décisions

- Même format JSON que le fichier de la machine plutôt que `mimeapps.list` :
  un seul validateur, un seul index.
- Pas de verrou de l'administrateur pour l'instant (question posée, ADR 0121 §8).
- Le fichier de la machine reste écrit à la main par un administrateur.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/Intents/UserFileAssociationTests.cs` :
  ordre des couches, utilisateur par utilisateur, choix périmé, `open` et
  `edit` séparés, fichier `0644` et réinitialisation, fichier invalide gardé,
  refus (`InvalidTarget`, `InvalidAction`, `AppNotFound`, `AppCannotHandle`),
  schéma, « Ouvrir avec », liste des défauts.
- `AppIntentDispatcherTests.Open_uri_and_open_file_use_the_acting_users_own_default`.
- `AppCatalogGatewayTests` : capacités et ordre des lignes.
- `XdgMimeCommandTests`, parité `xdg-mime` dans `CommandParityTests`,
  `WireCodecIntentTests.Default_handlers_keep_target_action_app_and_layer`.
- `Kazo.Os.Apps.Settings.Tests/FileAssociationsPanelTests.cs` : lignes du
  panneau.
- ADR 0257 : `CatalogAppSuggestionsTests` (lecture de `fileHandlers`, choix de
  la version, exclusions, ordre, cache sans réseau),
  `AppPackageGatewayTests.Suggestions.cs` (vérification des mises à jour, app
  installée), `AppIntentGatewayTests.OpenFileAsync_NoHandler_*`,
  `WireCodecIntentTests.Open_file_result_keeps_the_proposed_store_apps`,
  `KazoAppsIntentTests`, `XdgOpenCommandTests`.

## Liste des tâches

- [x] ADR 0121.
- [x] Couche `~/.config/kazoos/file-associations.json` lue sous l'identité de l'utilisateur.
- [x] Index rangé par cible et action.
- [x] Ordre utilisateur, puis machine, dans toutes les résolutions.
- [x] API du SDK (`Unshipped`) et opérations des workers.
- [x] Panneau des Paramètres.
- [x] « Toujours utiliser » dans « Ouvrir avec ».
- [x] Commande `xdg-mime`.
- [x] Tests et documentation.
- [x] ADR 0257, chantier 2 : apps du Store proposées avec `NoHandler`, SDK, `xdg-open`.
- [ ] ADR 0257, chantier 3 : Explorateur, « Ouvrir avec » et `--open-after-install` du Store.
