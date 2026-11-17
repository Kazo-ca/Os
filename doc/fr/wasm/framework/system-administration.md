> [English version](../../../en/wasm/framework/system-administration.md)

# Administration du système dans le SDK

## Objet

Donne aux apps système les opérations d'administration qui passaient par des
services internes (ADR 0056 §8 à §11, ajusté par l'ADR 0060) :

- lire et écrire les documents de paramètres de l'OS ;
- administrer les capacités dangereuses (politique, décisions machine) ;
- se connecter au serveur optionnel et synchroniser ;
- redémarrer ou réinitialiser la machine ;
- lire l'instantané brut du stockage ;
- sauvegarder et restaurer toute la machine (ADR 0099 §1).

Paramètres et Diagnostic utilisent ces API et ne référencent plus Platform
Core ni Infrastructure.Browser.

## Architecture

| Couche | Fichiers |
|---|---|
| SDK (contrats, façades) | `Shared/Kazo.Os.AppSdk/Administration/` (`KazoPermissionAdministration`, `KazoComponentProviders`, `KazoServerConnection`, `KazoSystem`, `KazoStorageDiagnostics`, `KazoScreens` et leurs contrats), `Shared/Kazo.Os.AppSdk/Settings/IAppSystemSettingsGateway.cs`, `SettingsDocumentStores.cs` |
| Platform Core (passerelles) | `Platform/Kazo.Os.Platform.Core/Administration/` : `AppSystemSettingsGateway`, `AppPermissionAdministrationGateway`, `AppServerConnectionGateway`, `AppSystemPowerGateway`, `AppStorageDiagnosticsGateway`, `AppSystemBackupGateway`, `AppUserAccountsGateway`, `AppScreensGateway` (sur `IScreenControl`, ADR 0184), `AdministratorGate`, `SystemAdministrationGatewayServices` |
| Platform Core (services) | `Power/SystemPowerService.cs` (séquence d'arrêt, `RestartAfterAsync`), `Backup/SystemBackupService.cs`, `Sessions/LocalPasswordService.cs` (ADR 0120), `Settings/OsSettingsDocumentRegistry.cs`, `Policy/AppPermissionAdministration.cs` (`ApplyMachineDecisionAsync`, `ApplyPolicyAsync`) |
| Hôte Ecosystem | `Administration/BrowserSystemPowerDevice.cs` (`location.reload`, suppression de la base IndexedDB), `Administration/BrowserSystemBackupDevice.cs` (export, téléchargement, sélecteur de l'hôte), `Administration/IndexedDbStorageInspector.cs` (adaptateur de `IndexedDbInspectorService`) |
| SDK Blazor | `Interop/BrowserClipboard.cs` et `wwwroot/clipboard.js` (presse-papiers de l'hôte), `[Obsolete]` depuis l'ADR 0065 puis retirés au gel du SDK 1.0 (ADR 0114) : Diagnostic copie par `KazoClipboard`, que l'OS recopie vers l'hôte ([`clipboard.md`](../dev/clipboard.md)) |

`AppExecutionContextFactory` reçoit un `SystemAdministrationGatewayServices`
(résolutions paresseuses ; `Passwords` est une propriété `init` ajoutée en 1.1) et crée, pour chaque instance, les sept passerelles
avec les capacités et le principal de l'app. `AdministratorGate` appelle
l'agent d'authentification de l'OS
([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)) : l'app ne
voit jamais le mot de passe, root par `sudo` passe sans demande, et l'OS agit
ensuite lui-même.

## API

### Documents de l'OS

```csharp
await using KazoSettingsDocument appearance = await KazoSettings.OpenSystemAsync(AppContext, "appearance");
await appearance.SetAsync("accent", "cyan");                               // couche utilisateur
await appearance.SetManyAsync(values, SettingLayer.System);                // /etc : une seule approbation
```

Voir [`settings-files.md`](settings-files.md#documents-de-los-kazosettingsopensystemasync-adr-0056-8).

### Permissions (`permissions.manage`)

| Méthode | Effet | Administrateur |
|---|---|---|
| `GetPolicyAsync`, `GetOverviewAsync` | politique et état de chaque capacité dangereuse de chaque app | non |
| `SetPolicyAsync(Prompt / Block)` | écrit `/etc/kazoos/security.config`, journal `capability.policy` | oui (`permissions.policy`) |
| `SetMachineDecisionAsync(app, capacité, Allow / Deny / Ask)` | écrit `/etc/kazoos/app-permissions.config`, journal `capability.approve` / `capability.revoke` ; `Allow` retire aussi le refus de l'utilisateur de la session | oui (`permissions.machine-decision`) |
| `RemoveOwnDenialAsync(app, capacité)` | retire le refus de l'utilisateur, la question revient au lancement | non |

Une app système ou une capacité non déclarée par l'app est refusée **avant**
toute demande d'identifiants.

### Serveur (`server.connection.manage`)

`GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `DisconnectAsync`,
`SyncAsync(domaine, sens)`, `GetConflictsAsync`. Domaines : `FileSystem`
(push, pull), `Grants` (pull seulement : `ArgumentException` sinon),
`FileAssociations` (push, pull). Sans serveur configuré : `NotConnected`, et
la synchronisation ne fait rien. Les erreurs réseau deviennent
`KazoServerConnectionException`.

`GetRelayPolicyAsync` rend la politique du relais réseau du serveur connecté
(`KazoServerRelayPolicy` : ports, plages privées, quotas, flux ouverts, apps
accordées, avertissements), ou `null` sans serveur ; Paramètres l'affiche
dans sa section du serveur ([`server-network-relay.md`](../dev/server-network-relay.md)).

ADR 0101 (chantier `v3/9-sync`) : `GetConflictsAsync` lit les conflits en
attente enregistrés dans `~/.local/state/kazoos/sync-conflicts.json`
(`KazoSyncConflict` porte `DetectedAt`) ; `ShowConflictsAsync` ouvre le
dialogue de conflit de l'OS ; `GetSyncStatusAsync` rend un `KazoSyncStatus`
(synchro périodique active, suspendue, en ligne, dernier passage, envoyés,
reçus, dernière erreur, intervalle) ; `SetSyncPausedAsync` suspend ou reprend
la synchro de l'utilisateur (`~/.config/kazoos/sync.config`) ;
`SyncNowAsync` lance un passage complet. Les suppressions, les conflits et la
synchro périodique sont décrits dans
[`synchronization.md`](../dev/synchronization.md).

### Alimentation (`system.power`)

`KazoSystem.RebootAsync` et `KazoSystem.FactoryResetAsync` demandent toujours
un administrateur (`system.reboot`, `system.factory-reset`). Séquence de
`SystemPowerService`, comme un `shutdown` :

1. entrée d'audit ;
2. vidage des journaux ;
3. fermeture de la session (`ISessionService.ShutdownAsync`) ;
4. pour la réinitialisation, effacement du stockage ;
5. redémarrage de l'hôte.

### Stockage (`system.storage.inspect`)

`KazoStorageDiagnostics.GetSnapshotAsync` rend un `KazoStorageSnapshot`
(magasins, nombre d'enregistrements, JSON, chemins résolus, liens) et
`ToExportJson()`. L'instantané contourne les droits des fichiers : c'est
pourquoi Diagnostic, app non système, demande un administrateur à son premier
lancement.

`GetWebSiteDatabasesAsync` liste les bases IndexedDB des sites (`kazoweb.<id>` :
version, magasins, nombre d'enregistrements, taille estimée) et
`DeleteWebSiteDatabaseAsync(name)` en supprime une, avec la même capacité
(ADR 0088 §4, [`asp-kazo-site-data.md`](asp-kazo-site-data.md)). Diagnostic les
montre sous *Website databases*.

### Stockage du navigateur (`KazoStorage`, ADR 0250)

`KazoStorage` (`Shared/Kazo.Os.AppSdk/Storage/`) parle du stockage de
l'origine de la page, que partagent les Fichiers de Kazo (IndexedDB), les
caches de l'OS et l'OPFS des fonctionnalités :

- `GetEstimateAsync` : usage, quota, `IsPersisted`, `IsLowSpace` (seuils de
  l'ADR 0099), la part de chaque sorte de stockage quand le navigateur la donne
  (`IndexedDbBytes`, `CacheBytes`, `FileSystemBytes`, Chromium seulement),
  l'espace que la fonctionnalité appelante a déclaré et `PersistenceLost`.
  Aucune capacité.
- `RequestPersistenceAsync` : `navigator.storage.persist()`, **une fois par
  origine** (`OriginStorageService` ne redemande pas quand `persisted()` est
  vrai). Réservé aux fonctionnalités, à leurs points d'entrée et aux apps
  système ; une app ordinaire reçoit `KazoUnauthorizedAccessException`
  (`manifest.feature.only`). À appeler juste après un geste de l'usager.
- `RegisterMarkerAsync(path)` et `ReportUsageAsync(bytes)` : une
  fonctionnalité enregistre un fichier témoin de son OPFS et l'espace qu'elle
  occupe, gardés par l'OS dans `/var/lib/kazoos/storage/features.json`.
- `GetFeatureUsageAsync` : les fonctionnalités et l'espace déclaré (Paramètres,
  `df`).
- `Changed` : `Evicted` quand le témoin a disparu de l'OPFS, remis à la
  fonctionnalité dès qu'elle s'abonne à son lancement suivant.

Au démarrage, `StorageEvictionDetector` compare, **avant** l'ouverture des
bases, le marqueur `kazoos.storage.marker` de `localStorage` à l'existence de
la base `kazoos` : marqueur sans base = éviction. L'écran de première
installation l'annonce (date du dernier démarrage, restauration d'une
sauvegarde), `kern.log` et `syslog` reçoivent `storage.evicted`, puis le
démarrage réussi réécrit le marqueur. Les témoins des fonctionnalités sont
vérifiés ensuite ; un témoin disparu donne une notification à l'usager et
`Changed(Evicted)` à la fonctionnalité.

Paramètres ▸ Système ▸ Stockage (`StoragePanel`) montre usage, quota,
répartition, état « protégé » et le bouton « Protéger le stockage contre
l'effacement », et l'espace de chaque fonctionnalité. `df` ajoute une ligne
`/dev/feature/<id>` par fonctionnalité qui a déclaré son espace. La réserve de
5 % compte toujours l'origine entière : les disques des fonctionnalités
comptent dans l'espace des Fichiers.

### Sauvegarde (`system.backup`)

`KazoSystemBackup.CreateAsync` télécharge une archive `.kosbak` de toute la
machine sur l'ordinateur hôte ; `RestoreAsync(Merge | Replace)` ouvre le
sélecteur de l'hôte, valide l'archive, **puis** demande un administrateur,
ferme la session, restaure et redémarre. Création et restauration vont dans
`/var/log/auth.log`. Détails : [`system-backup.md`](../dev/system-backup.md).

### Écrans (`screens.manage`)

`KazoScreens` (ADR 0184) liste, ouvre, restaure, ferme et identifie les écrans
secondaires de la session, met l'écran de la fenêtre appelante en plein écran et
demande la permission `window-management`. Capacité normale, sans
administrateur : le navigateur limite déjà chaque appel au clic de
l'utilisateur. `IsSupportedFor(context)` est faux sur un hôte sans écrans
(mobile, tests). Mise en œuvre côté hôte : `ScreenControl` sur `ScreenService`
([`screens-settings.md`](../dev/screens-settings.md)).

### Comptes et mots de passe (`users.manage`)

`KazoUserAccounts.ListAsync` et `ChangePasswordAsync` (son propre mot de passe,
après le mot de passe courant) ne demandent aucune capacité ;
`ResetPasswordAsync` et `RemovePasswordAsync` exigent `users.manage` et un
administrateur (actions `users.password-reset` et `users.password-delete`).
Le trousseau du compte visé est mis de côté. Détails :
[`sensitive-settings.md`](../dev/sensitive-settings.md#4-mots-de-passe--passwd-et-réinitialisation)
et [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md).

`CreateAsync(new KazoNewUserAccount(login, nomAffiché, autorité, motDePasse))`
et `DeleteAsync(login, removeHome)` exigent aussi `users.manage` et un
administrateur (actions `users.add` et `users.delete`, ADR 0169). La création
donne au compte son propre groupe, et `administrators` pour un administrateur ;
le dossier personnel naît à la première connexion. La suppression refuse le
compte de la session et le dernier administrateur actif, emporte les clés
d'accès du compte et, avec `removeHome`, son dossier `/home/<login>`.
`ValidateNewAccount` vérifie un compte avant l'envoi (nom de connexion, nom
affiché de 128 caractères au plus, mot de passe). `KazoUserAccount.PasskeyCount`
donne le nombre de clés d'accès de chaque compte.

Clés d'accès de la session (ADR 0168), sans capacité : `ListPasskeysAsync`,
`AddPasskeyAsync(nom)` (le navigateur demande la présence de l'utilisateur ;
`OperationCanceledException` si la demande est fermée, `NotSupportedException`
sans WebAuthn) et `RemovePasskeyAsync(id)`. `KazoPasskey.UnlocksKeyring` dit si
la clé ouvre le trousseau. Détails : [`passkeys.md`](../dev/passkeys.md).

### Rôles des Features (ADR 0247, ajustée par l'ADR 0283)

Une Feature déclare des rôles (`FeatureDefinition.Roles`, `FeatureRole(id, titre)`
avec `Description` et `Grants`), chacun `<préfixe>:<nom>`, le préfixe étant le
`CapabilityPrefix` de la Feature, son nom court unique (`x86:vm-manager`,
[ADR 0283](../dev/adr/0283-feature-roles-isinrole-adjustments.md)) ; un point d'entrée marqué `RequiresRole` ne s'ouvre qu'à un
usager qui détient le rôle. Le manifeste les porte (`roles`, `requiresRole`) ;
le validateur refuse un rôle hors d'une Feature, sous un autre préfixe que le
sien, sans titre, ou
un `requiresRole` qu'aucun rôle de la Feature ne déclare.

- **Attribution** : `KazoUserAccounts.ListRolesAsync()` liste les rôles déclarés
  (`KazoFeatureRole`) ; `GrantRoleAsync(login, rôle)` et
  `RevokeRoleAsync(login, rôle)` exigent `users.manage` et un administrateur
  (actions `users.role-grant` et `users.role-revoke`). `KazoUserAccount.Roles`
  donne les rôles d'un compte. L'attribution est écrite dans
  `/etc/kazoos/roles.config` (`[roles]`, `x86:vm-admin=<id>,<id>`, identifiants
  stables, jamais des noms). Paramètres ▸ Utilisateurs montre une colonne par
  rôle (panneau `FeatureRolesPanel`) ; `sudo kazorole grant|revoke <usager> <rôle>`
  fait la même chose, `kazorole list` liste les rôles et leurs détenteurs.
- **Administrateur** (réponse 233 B) : il détient implicitement tous les rôles,
  sans ligne dans `roles.config` ; l'écran dit « implicites (administrateur) » et
  `kazorole grant` sur lui ne change rien (code 0). Un compte rétrogradé en
  `User` perd ces rôles implicites ; root sous `sudo` les détient tous.
- **Lecture** : `KazoUser.Current.IsInRole(rôle)` (ou `KazoUser.IsInRole(rôle)`)
  et `IAppExecutionContext.Roles`. Un rôle de l'OS a un nom simple : `admin`
  (`KazoRoles.Administrator`), vrai pour un administrateur et root ; un rôle de
  Feature est `<Feature>:<rôle>`, vrai pour son détenteur et pour tout
  administrateur. Une sous-commande d'une commande ouverte à tous (`x86 admin`)
  vérifie son rôle par `IsInRole`.
  Un point d'entrée sans son rôle est absent du lanceur, de l'accueil mobile et
  de la complétion du shell ; le lancer répond `AppLaunchStatus.RoleRequired`
  (`lifecycle.role.required`), le shell écrit « rôle requis » et sort avec 126,
  et `feature.role-denied` est écrit dans `auth.log`.
- **Opérations sensibles** : le rôle ouvre le point d'entrée, il ne vaut pas
  autorisation pour chaque opération. `KazoFeatureAuthentication.AuthenticateAsync(opération, portée)`
  ouvre la fenêtre de l'OS (jamais une fenêtre de la Feature) :
  `KazoAuthenticationScope.RoleHolder` redemande le mot de passe de l'usager de
  la session, qui doit détenir le rôle du point d'entrée marqué `RequiresRole`,
  ou, depuis un autre point d'entrée de la Feature, un rôle de la Feature (la
  Feature vérifie le rôle exact par `IsInRole` ; refusé à un service ou à un
  usager sans rôle de la Feature) ; `Administrator` demande un
  administrateur. Le billet rendu (`KazoAuthenticationTicket`) vaut 30 s, une
  fois, pour cette Feature, cette opération et cet usager :
  `ConsumeAsync(billet, opération)` le refuse pour une autre opération ou un
  autre usager, et rend le billet émis par l'OS, dont la Feature lit `Scope`
  (jamais la copie présentée). Sous `sudo`, aucune seconde fenêtre. Une
  app ordinaire est refusée. Détails :
  [`sensitive-settings.md`](../dev/sensitive-settings.md#5-rôles-des-features-et-authentification-des-opérations).

## Décisions

- Voir l'ADR 0060 : propriété `SystemPower` (pas `System`),
  `KazoPermissionDecision.Ask`, couche système écrite avec les droits de
  l'appelant, `SetManyAsync`, presse-papiers de l'hôte pour Diagnostic
  (ce dernier point est remplacé par l'ADR 0065 : `KazoClipboard` et
  `clipboard.write`).
- `IndexedDbInspectorService` reste dans Infrastructure.Browser ;
  l'adaptateur vers le SDK vit dans l'hôte Ecosystem.
- L'ancien chemin par mot de passe de `AppPermissionAdministration` est gardé
  pour ses tests ; les apps passent par l'agent.

## Tests

`Tests/Kazo.Os.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs` :
document de l'OS lu et écrit sans capacité, couche système refusée à un
utilisateur simple puis écrite sous root en une édition, document inconnu ;
capacités exigées ; refus de l'agent sans écriture ; décision machine et
politique journalisées ; app système refusée avant toute demande ; serveur
absent ; `Grants` en pull seulement ; redémarrage refusé ; réinitialisation
dans l'ordre fermeture, effacement, redémarrage ; inspection du stockage ;
sauvegarde (capacité, hôte sans sauvegarde, refus de l'administrateur,
sélecteur avant administrateur, ordre de la restauration, échec journalisé au
démarrage suivant).
`Tests/Kazo.Os.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs` :
ajout et suppression de comptes par la passerelle (`users.manage` et
administrateur), clés d'accès de la session sans capacité ni administrateur.
`Tests/Kazo.Os.Platform.Core.Tests/Sessions/LocalAccountServiceTests.cs` :
groupes, noms réservés ou pris, suppression avec ou sans dossier, refus pour
soi-même et pour le dernier administrateur.
`Tests/Kazo.Os.Platform.Core.Tests/Roles/` : rôles attribués, refusés,
implicites pour un administrateur, perdus par un compte rétrogradé, relus de
`roles.config` ; authentification des opérations (billet lié à l'opération,
usage unique, app ordinaire refusée, `sudo` sans fenêtre).
`Tests/Kazo.Os.Commands.Wave5.Tests/KazoroleCommandTests.cs` : `kazorole`.

## Liste des tâches

Voir [`app-sdk.md`](app-sdk.md#liste-des-tâches-du-chantier-rebuild4-sdk-system-admin).
