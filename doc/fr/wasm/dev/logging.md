> [English version](../../../en/wasm/dev/logging.md)

# Journaux en fichiers (`/var/log`)

Les fichiers de journal qu'une app produit et les API qui les écrivent sont dans [`app-log-files.md`](../framework/app-log-files.md).

Mise en œuvre de l'[ADR 0045](adr/0045-logs-as-files.md) et de la section 5.3 du
[plan de refonte](plans/ecosystem-rebuild-plan.md).

## Rôle

Les journaux de Kazo OS sont de vrais fichiers texte. `cat`, `tail`, `grep` et
Error Log Viewer les lisent avec les droits de fichier ordinaires.

| Fichier | Contenu | Mode |
|---|---|---|
| `/var/log/syslog` | `ILogger<T>` de la plateforme, démarrage (`kernel`), pannes de l'hôte (`ecosystem-host`), lignes perdues (`logging`) | `system:system 0644` |
| `/var/log/kern.log` | Journal du noyau (`KernelLog`) : OOM killer, garde de la mémoire du navigateur ; aussi copié dans `syslog` et lu par `dmesg` depuis `/dev/kmsg` (ADR 0126 §3, [`oom-killer.md`](oom-killer.md)) | `system:system 0644` |
| `/var/log/auth.log` | `IAuditLog` (`session.login`, `session.elevate`, `capability.*` de l'ADR 0049, `feature.authenticate`, `feature.role-denied`, `role.grant` et `role.revoke` de l'ADR 0247, `events.read-other` de l'ADR 0254…) | `system:system 0600` |
| `/var/log/audit.log` | Audit des créations, écritures, renommages, copies, suppressions et `chmod` sous `/etc`, `/bin` et `/var` (sauf `/var/log`), au format d'`auditd` (ADR 0100 §3, [`sensitive-settings.md`](sensitive-settings.md)) ; `ausearch` le filtre | `system:system 0600` |
| `/var/log/users/<id>/events.log` | Journal d'événements d'un usager (ADR 0254) : `KazoEvents` des apps et événements de l'OS pour cet usager (`kazoos.permission.*`, `kazoos.role.*`, `kazoos.app.installed`, `kazoos.sync.conflict-resolved`) ; `<id>` est l'identifiant stable du compte ([`app-log-files.md`](../framework/app-log-files.md#événements-dusager-adr-0254)) | `<usager>:<groupe> 0600`, dossiers `system:system 0711` |

## Architecture

```text
ILogger<T> ──► KazoOsDiagnosticLoggerProvider ─┐
IAppLoggingGateway ──► AppLoggingGateway ────────┼─► LogFileWriter ─► IFileSystemService ─► /var/log/…
IAuditLog ──► FileAuditLog ──────────────────────┘        │ (canal borné, vidage groupé, rotation)
HostExceptionReporter ───────────────────────────┘        ▼
                                     IDiagnosticSink (tampon mémoire des entrées récentes)
```

- `LogFileTarget` : `Syslog`, `AuthLog`, `AuditLog` (ADR 0100), `ForApp(appId)`, avec chemin et mode.
- `LogFileWriter` (singleton, `Platform.Core/Diagnostics/`) :
  - `Enqueue(target, line)` et `Write(target, severity, source, message, pid)`,
    synchrones, jamais bloquants. Canal de 10 000 lignes ; au-delà, les plus
    anciennes sont perdues et une ligne `WARN logging: <n> entries dropped` est
    écrite dans `syslog` au vidage suivant.
  - Plus de place (`NoSpace`, ADR 0099 §2) : le lot refusé est abandonné et
    ses lignes comptées (`PendingNoSpaceLoss`) ; au premier vidage réussi,
    une seule ligne `WARN logging: <n> lines lost (No space left on device)`
    est écrite dans `syslog` ([`system-backup.md`](system-backup.md)).
  - Vidage toutes les 1 s de temps simulé, dès 100 lignes en attente, à la
    déconnexion (`SessionLoggedOutEvent`) et à `DisposeAsync`. Un seul vidage à
    la fois par système de fichiers attaché ; une seule écriture par fichier et
    par vidage.
  - `AttachAsync(IFileSystemService)` est appelé par `EcosystemBootCoordinator`
    juste après `EnsureRootAsync`. Avant, les lignes attendent dans le canal.
    Le rédacteur crée lui-même `/var/log` et `/var/log/apps` s'ils manquent.
  - Attacher un **autre** système de fichiers abandonne le précédent : son
    jeton est annulé et les vidages suivants ne l'attendent plus. Sur
    `Kazo.Os.Server`, chaque circuit monte le sien ; un rechargement peut
    couper l'ancien circuit au milieu d'un vidage (appel JS sans réponse), et
    le démarrage du nouveau circuit restait bloqué derrière, environ une fois
    sur cinq (trouvé par `build/verify-hosts.sh`). Les lignes du lot abandonné
    ne restent que dans le tampon mémoire.
  - Un échec d'écriture n'est jamais journalisé dans un fichier : il est noté une
    fois par fichier (catégorie `logging`) dans le tampon mémoire.
- `FileAuditLog` remplace `BoundedAuditLog` dans la composition ; `Entries` reste
  servi par un tampon de 512 entrées.
- **Journaux d'usager (ADR 0254).** `LogFileTarget.ForUser(id, groupe)` vise
  `/var/log/users/<id>/events.log` avec un propriétaire (`LogFileOwner`) et un
  mode de dossier (`DirectoryMode`, `0711`). Le rédacteur crée le fichier au nom
  de l'usager, comme `install -o` (seule la création utilise ce contexte ;
  l'ajout et la rotation restent faits par le système), et crée les dossiers sous
  `/var/log` avec ce mode. `ReadLinesAsync(chemin)` et `DeleteDirectoryAsync(dossier)`
  (un dossier sous `/var/log/users` seulement) prennent le verrou de vidage,
  écrivent la file, puis lisent ou suppriment avant de le rendre : aucun vidage
  ni aucune rotation ne déplace un fichier pendant ce temps.
  - `IUserEventLog` / `FileUserEventLog` (singleton). `Write(owner, author, event)`,
    pour les apps, vérifie l'événement (`Validate` : genre d'un seul mot, genres
    `kazoos.*` refusés, tailles bornées, clés de l'OS refusées) et limite le débit
    par app et par usager (200 en 30 s, fenêtre fixe comme journald ; les
    événements en trop sont comptés et une ligne `user-events` de `syslog` le
    dit). `TryWriteSystem(...)`, pour l'OS, raccourcit les champs trop longs
    (`…`), ne lève jamais et écrit un échec dans `syslog`. Les deux passent les
    propriétés par le masquage, ajoutent `kind`, `subject.*` et `sender`, et
    formatent la ligne avec `LogLineFormat`. `ReadAsync(id, filtre)` (60 lectures
    par journal en 30 s) et `DeleteAsync(id)`, qui ne lève jamais et ignore tout
    événement écrit ensuite pour ce compte.
  - `AppUserEventsGateway` (une par instance d'app, `AppExecutionContextFactory.UserEvents`)
    vérifie `events.write`, prend l'app et le processus du contexte et
    l'expéditeur de `Intent.SenderAppId`, et ne relit que les lignes de l'app.
    Dans un worker : `RemoteUserEventsGateway`, opérations `uev.write` (envoyée
    sans réponse, vérifiée d'abord dans l'app) et `uev.read`.
  - Événements de l'OS (`OsUserEvents`, textes dans la langue de la session) :
    `DangerousCapabilityGate` (permission accordée ou refusée,
    `org.kazoos.permissions`), `FeatureRoleService` (rôle attribué ou retiré,
    `org.kazoos.accounts`), `AppManagementGateway` (app installée pour soi seulement,
    `org.kazoos.packages`), `FileSystemSyncService` (conflit résolu, à la main ou
    automatiquement, pour l'usager de la passe, `org.kazoos.sync`),
    `AppFeatureAuthenticationGateway` (opération d'une Feature authentifiée, à la
    consommation du billet, avec le sujet passé à `AuthenticateAsync`,
    `org.kazoos.authentication`). Chacun garde aussi son entrée d'`auth.log` ou
    de `syslog`, et passe par `TryWriteSystem` : le journal ne casse jamais
    l'opération déjà faite.
  - `AppUserAccountsGateway.ReadEventsAsync(login, filtre)` (ADR 0254 §3) : le
    journal de l'usager en session sans rien demander ; un autre compte après un
    administrateur dans la fenêtre de l'OS, une fois par passerelle (donc par
    processus), et une entrée `events.read-other` par compte lu dans `auth.log`.
  - `LocalAccountService.DeleteAsync` et `GuestAccountService.ReplaceAsync`
    suppriment le dossier du compte supprimé, même quand le dossier personnel reste.
- `HostExceptionReporter` (ADR 0240) écrit une ligne `CRIT ecosystem-host` par
  erreur non gérée, avec `correlationId`, `phase`, `exceptionType`, `module`,
  `source`, `message` et `stack`. Message et pile passent d'abord par
  `SensitiveTextRedactor` (mots de passe d'adresse, `token=`, `password=`,
  `Bearer`, IRC `PASS`... remplacés par `***redacted***`) ; la page d'erreur
  affiche les mêmes valeurs ([ADR 0240](adr/0240-detailed-error-page.md)).
- `SimulationClockDriver` (`Platform.Core/Time/`) : l'adaptateur de production
  prévu par l'ADR 0012, qui n'existait pas. Il fait avancer `ManualSimulationClock`
  depuis un minuteur de la plateforme (au plus 50 tics par battement, le reste
  est abandonné quand l'onglet est ralenti). Sans lui, le vidage périodique, l'horloge
  de la barre des tâches et les délais d'arrêt des processus ne s'exécutaient
  jamais. Il est démarré par `EcosystemBootCoordinator`. `ManualSimulationClock`
  protège désormais sa file de rappels par un verrou.

## Retraits

- `IPersistentDiagnosticRepository` et `IndexedDbDiagnosticRepository` ne sont
  plus enregistrés ; l'audit IndexedDB n'était déjà pas branché. Les classes et
  leurs magasins restent, et la version du schéma IndexedDB ne change pas.
- L'hôte `OS/Kazo.Os.Ecosystem` ne retire plus son `ILoggerProvider` : toutes ses
  dépendances sont des singletons. Le serveur garde sa composition (le pont
  `ILogger` y reste retiré, aucun système de fichiers n'y est attaché).

## Error Log Viewer

- Manifeste : `filesystem.system.read` et `filesystem.system.write` (au lieu de
  `diagnostics.*`).
- `LogFileReader` liste `syslog` puis `apps/*.log` (et les archives `.1` à `.5`
  si la case « Archives » est cochée), lit et analyse avec `LogLineFormat.TryParse`,
  et compte les lignes étrangères. Il ne lit jamais `auth.log` (`sudo cat` pour ça).
- `audit.log` et ses archives (ADR 0100) sont listés pour un administrateur ou
  une session élevée seulement (`LogSource.RequiresRoot`) et ne sont pas lus
  par la vue « tous les journaux ». Chaque ligne `type=PATH` est analysée par
  `AuditLineFormat` : un refus devient un avertissement, une écriture réussie
  une information, avec l'app comme source.
- Suivi en direct par `AppContext.Watch` sur `/var/log` et `/var/log/apps`.
- Onglet « Mes événements » (ADR 0254 §3, `UserEventsPanel`) : le journal de
  l'usager en session, `/var/log/users/<AppContext.UserId>/events.log` et ses
  archives, lu comme un fichier ordinaire dont l'usager est propriétaire
  (`LogFileReader.ReadUserEventsAsync`). Filtres par app, gravité, sujet (regroupé
  par app, type et identifiant) et texte ; du plus récent au plus ancien. Pas de
  suivi en direct (le dossier `0711` ne se liste pas) : « Rafraîchir » relit.
  Un administrateur (ou une session sous `sudo`) voit un sélecteur d'usager :
  un autre compte est lu par `KazoUserAccounts.ReadEventsAsync`, qui demande un
  administrateur une seule fois pour cette fenêtre (`ErrorLogViewerWindow.UserEvents.cs`).
- « Truncate » vide le journal sélectionné (jamais une archive). Pour un
  utilisateur ordinaire c'est refusé, la fenêtre propose l'approbation
  administrateur (ADR 0042), puis un second clic réussit.
- **Écart temporaire avec l'ADR 0045 §6 :** le lecteur passe par
  `IAppFileSystemGateway` et non par `KazoFile`/`KazoFileSystemWatcher`, qui
  arrivent avec la branche `rebuild/1-file-api` (ADR 0043). Le passage à `Kazo*`
  se fera une fois cette API mergée ; le comportement ne change pas.

## Tests de contrat

- `Tests/Kazo.Os.Platform.Core.Tests/Diagnostics/LogLineFormatTests.cs` : format,
  exemple exact de l'ADR, échappements, propriétés, audit, rejet des lignes étrangères.
- `.../LogFileWriterTests.cs` : création et modes, vidage périodique et par seuil,
  une écriture par fichier, rotation et archives, lot géant, perte de lignes,
  échecs non récursifs, déconnexion, arrêt, droits de lecture, nouvel attachement
  qui n'attend pas un vidage bloqué sur le précédent.
- `.../LogProducersTests.cs` : `ILogger`, `AppLoggingGateway`, `FileAuditLog`.
- `.../UserEventLogTests.cs` (ADR 0254) : écriture dans le fichier de l'usager avec
  l'app du contexte, propriétaire, modes `0600` et `0711`, un autre usager ne lit
  ni ne liste, lecture limitée aux lignes de l'app, refus sans `events.write`,
  événements mal formés, rotation, suppression, relecture d'une ligne, façade
  `KazoEvents`. Événements de l'OS dans `DangerousCapabilityApprovalTests`,
  `FeatureRoleServiceTests`, `AppPackageGatewayTests`,
  `FileSystemSyncDeletionAndConflictTests` ; suppression avec le compte dans
  `LocalAccountServiceTests` ; worker dans `WireCodecUserEventsTests`.
- `Tests/Kazo.Os.Platform.Core.Tests/Time/SimulationClockDriverTests.cs`.
- `Tests/Kazo.Os.Apps.ErrorLogViewer.Tests/` : manifeste et `LogFileReader` sur un
  vrai `AppFileSystemGateway`.

## Liste des tâches

- [x] `LogLineFormat` (`Format`, `FormatAudit`, `TryParse`).
- [x] `LogFileTarget`, `LogRotationPolicy` (1 Mio × 5).
- [x] `LogFileWriter` : canal borné, vidage groupé, rotation, pas de récursion.
- [x] `ILogger` → `syslog`, `IAppLoggingGateway` → `apps/<appId>.log`, `IAuditLog` → `auth.log`.
- [x] `HostExceptionReporter` sur `syslog`.
- [x] Attache au démarrage et ligne `kernel: boot` ; `/var/log/apps` créé par `FileSystemSeeder`.
- [x] `SimulationClockDriver` pour que le temps simulé avance.
- [x] Retrait de `IAppDiagnosticsGateway` et des dépôts IndexedDB de la composition.
- [x] Error Log Viewer lit les fichiers.
- [x] Tests de contrat.
- [x] Lignes perdues faute de place, comptées puis signalées (`v3/7`, ADR 0099 §2).
- [x] Journaux d'usager (ADR 0254) : `LogFileTarget.ForUser`, `FileUserEventLog`, `KazoEvents`, `events.write`, worker.
- [x] Événements écrits par l'OS : permissions, rôles, installation pour soi, conflits de synchronisation.
- [x] Journal supprimé avec le compte.
- [x] Error Log Viewer : onglet « Mes événements ».
- [ ] Passer Error Log Viewer sur `KazoFile` après le merge de l'ADR 0043.
