> [English version](../../../../en/wasm/dev/adr/0254-per-user-events.md)

# ADR 0254 : événements par usager, visibles par l'usager et les administrateurs, rattachés à une VM ou à un programme

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. La recommandation de chaque question posée à Benoit est
appliquée en attendant sa réponse. Décisions d'origine : 137 (« les
événements personnels de l'usager, comme une permission accordée ou une
mise à jour de sa VM, sont visibles par lui et par les administrateurs ;
les événements système sans données personnelles vont dans le journal
général ») et consignes §16 d'Os-x86 (« mise à jour, permission accordée,
conflit de fichiers résolu … rattachés à une VM ou à un programme ; ni un
accès universel aux détails de tous les utilisateurs, ni un journal réservé
aux administrateurs »), 2026-11-11. Le journal personnel est V1 ; seule une
visionneuse complète peut attendre.

- **Complète :** l'[ADR 0045](0045-logs-as-files.md) (journaux en fichiers,
  `/var/log`), l'[ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  (`audit.log`), l'[ADR 0247](0247-feature-declared-roles.md) §4 et
  l'[ADR 0253](0253-marked-sender-on-launch-and-open.md) (expéditeur).
- **Pour :** le journal d'Os-x86 (permission réseau accordée ou refusée à
  un programme, VM démarrée, arrêtée, mise à jour, image téléchargée, console
  ouverte, règle de l'admin appliquée) et celui de BoxedWine (programme
  ajouté, installé, désinstallé).

## Contexte

- Les journaux de Kazo OS sont des fichiers (ADR 0045) : `/var/log/syslog`
  et `/var/log/apps/<appId>.log` lisibles par tous (0644,
  `Diagnostics/LogFileTarget.cs:18-19, 52-60`), `auth.log` et `audit.log`
  réservés (0600, :21-22). Une app écrit dans **son** fichier par
  `IAppExecutionContext.Logging.Log(severity, message, properties)`
  (`AppGatewayContracts.cs:259-262`), avec l'identifiant de l'app et le pid,
  **sans identifiant d'usager** (`Execution/AppLoggingGateway.cs:66-74`).
- Il n'existe **aucun journal par usager** : ce que BoxedWine écrirait
  (« Alice a lancé setup.exe ») est lisible par Bob dans
  `/var/log/apps/org.kazoos.boxedwine.log`, et l'Error Log Viewer, app
  système avec `filesystem.system.read`, lit tout pour tout le monde
  (`Apps/System/Kazo.Os.Apps.ErrorLogViewer/LogFileReader.cs:42-64`).
- Les notifications (`AppNotificationGateway.cs:37-61`) sont par usager mais
  en mémoire, 100 par usager, perdues au rechargement
  (`InMemoryNotificationQueue.cs:6-9`) : ce n'est pas un journal.
- Les événements d'Os-x86 portent des données personnelles (nom d'un
  programme, destination réseau, nom d'une VM) et un **sujet** (une VM, un
  programme, une bouteille) que l'usager veut retrouver (« qu'est-ce qui
  s'est passé avec ma VM Windows hier ? »).

## Décision

### 1. Un journal d'événements par usager

- Fichier `/var/log/users/<guid>/events.log` (identifiant stable de
  l'usager, ADR 0169 ; jamais le nom de connexion), propriétaire l'usager,
  mode **0600** ; le dossier `/var/log/users/<guid>/` est créé à la première
  écriture, `system:system 0711` pour que les autres usagers ne listent pas
  les dossiers. Un administrateur lit tous les dossiers avec `sudo` ou
  l'élévation de la visionneuse (§3), comme pour `auth.log` (ADR 0045).
- Rotation comme les autres journaux (`LogRotationPolicy`, 1 Mio, 5
  archives) ; même canal borné et vidage groupé (`LogFileWriter`).
- Suppression du compte (ADR 0169) : le dossier est supprimé avec le compte
  (l'ADR « événement de suppression de compte pour les Features », V2
  d'Os-x86, prévient la Feature avant).

### 2. Écrire un événement : `KazoEvents`

```csharp
public static class KazoEvents
{
    public static void Write(KazoUserEvent e);   // dans le journal de l'usager en session
}
public sealed record KazoUserEvent(
    string Kind,                 // "x86.network.allowed", "x86.vm.started", "boxedwine.program.added"
    string Message,              // texte lisible, localisé par l'app
    KazoEventSubject? Subject = null,    // (Type, Id, DisplayName) : "vm", "program", "prefix", "file"
    KazoEventSeverity Severity = Info,   // Info, Warning, Error
    IReadOnlyDictionary<string, string>? Properties = null);
```

- Permission : **`events.write`**, nouvelle capacité **normale** du
  catalogue (une app n'écrit que dans le journal de l'usager pour qui elle
  tourne ; rien d'autre n'est atteint). L'OS ajoute l'heure, l'identifiant
  de l'app (du contexte, jamais de l'appelant), le pid et, s'il existe,
  l'expéditeur de l'ADR 0253. Format d'une ligne : celui de `LogLineFormat`
  (ADR 0045), avec `kind`, `subject.type`, `subject.id`, `subject.name` et
  `sender` dans le bloc de propriétés.
- L'OS écrit lui-même des événements d'usager pour ses propres actions
  personnelles : permission dangereuse accordée ou refusée pour cet usager
  (ADR 0049 §5, en plus d'`auth.log`), rôle attribué ou retiré
  (ADR 0247 §4), app installée pour soi (ADR 0208), conflit de
  synchronisation résolu (ADR 0101), opération sensible d'une Feature
  authentifiée (ADR 0247 §4, `Subject` = ce que la Feature a passé).
- Les événements **système sans donnée personnelle** restent dans `syslog`
  et `apps/<appId>.log` (ADR 0045) ; une app choisit l'un ou l'autre selon
  la nature de l'événement (le guide le dit : « si l'événement nomme un
  fichier, un programme ou une destination de l'usager, c'est un événement
  d'usager »).

### 3. Lire : l'usager et les administrateurs

- **Onglet « Mes événements »** dans l'Error Log Viewer (V1, minimal) :
  liste chronologique du journal de l'usager en session, filtre par app,
  par gravité et par **sujet** (une VM, un programme), recherche ; un
  administrateur voit un sélecteur d'usager (lecture de
  `/var/log/users/<autre>/events.log` après l'authentification de l'OS, un
  billet par session de la visionneuse, journalisé dans `auth.log`
  `events.read-other user=`). La visionneuse complète (regroupement par
  sujet, lien vers la page de la VM dans Paramètres ▸ Fonctionnalités,
  export) est un chantier ultérieur.
- Une app lit **son propre** journal d'événements de l'usager par
  `KazoEvents.ReadAsync(filter)` (permission `events.write` aussi : elle ne
  voit que ses lignes, filtrées par l'OS sur l'identifiant d'app) : BoxedWine
  montre « Historique » d'un programme ; Os-x86, la page des règles montre
  les décisions passées d'une bouteille.
- `cat ~/.events` n'existe pas : le fichier est sous `/var/log/users/`,
  lisible par `cat /var/log/users/<mon guid>/events.log` avec les droits de
  fichier ordinaires.

### 4. Rattachement à un sujet

- `KazoEventSubject(Type, Id, DisplayName)` : `Type` libre mais recommandé
  (`vm`, `program`, `prefix`, `file`, `app`), `Id` stable (GUID de VM,
  identifiant de programme d'Os-x86 : chemin + empreinte), `DisplayName`
  au moment de l'événement (le nom peut changer). La visionneuse regroupe
  par `(app, Type, Id)`.
- Une notification (ADR 0197) peut porter le même sujet
  (`NotificationAction.LaunchApp` avec `--subject=`) : la bulle « X attend
  ta réponse » mène à la page du sujet ; hors de cette ADR.

## Options écartées

- **A. Tout dans `/var/log/apps/<appId>.log`** : lisible par tous ; 137 et
  §16 l'interdisent pour les événements personnels.
- **B. Un fichier dans le dossier personnel (`~/.local/state/kazoos/events.log`)** :
  toute app avec `filesystem.user-home.write` (normale) pourrait l'effacer
  ou le falsifier (même fond que l'issue #433) ; `/var/log` est protégé par
  l'audit de l'ADR 0100.
- **C. Les notifications comme journal** : en mémoire, 100 par usager,
  perdues au rechargement.
- **D. Un journal par app et par usager** (`/var/log/users/<guid>/<appId>.log`)
  : plus de fichiers pour rien ; le filtre par app suffit, et l'OS a aussi
  ses propres événements d'usager.
- **E. Visible par l'usager seulement** : 137 veut les administrateurs
  aussi (après authentification).

## Conséquences

- SDK : `KazoEvents` (`Write`, `ReadAsync`), `KazoUserEvent`,
  `KazoEventSubject`, `KazoEventSeverity` ; capacité `events.write`
  (normale, catalogue, ADR 0049 §1 : phrase de classement) ;
  `PublicAPI.Unshipped.txt`, fixture `sdk-compat`.
- Plateforme : `LogFileTarget.ForUser(guid)` (0600, dossier 0711),
  `AppUserEventsGateway`, événements écrits par l'OS (permissions, rôles,
  installation, conflits, authentification de Feature), suppression avec le
  compte.
- Error Log Viewer : onglet « Mes événements », sélecteur d'usager pour un
  administrateur avec authentification.
- Docs : [`app-log-files.md`](../../framework/app-log-files.md) (section
  « Événements d'usager »), [`logging.md`](../logging.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  entrée `changes/unreleased`.
- Tests : `Write` écrit dans le fichier de l'usager en session avec l'app du
  contexte ; un autre usager ne lit pas le fichier (0600, dossier 0711) ;
  `ReadAsync` ne rend que les lignes de l'app ; l'administrateur lit après
  authentification et `auth.log` le dit ; rotation ; suppression du compte
  supprime le dossier ; l'OS écrit l'événement d'une permission accordée.

## Questions posées à Benoit

1. **Emplacement du journal d'un usager ?**
   - A) **(Recommandé)** `/var/log/users/<guid>/events.log`, 0600, dossier
     0711, protégé par l'audit de `/var` (ADR 0100).
   - B) `~/.local/state/kazoos/events.log` dans le dossier personnel :
     synchronisé avec les fichiers de l'usager, mais effaçable par toute app
     à permission normale.
2. **La capacité `events.write` : normale ?**
   - A) **(Recommandé)** Normale : une app n'écrit que dans le journal de
     l'usager pour qui elle tourne, et ses propres lignes.
   - B) Pas de capacité du tout (comme `Logging` aujourd'hui) : plus simple,
     mais Paramètres ▸ Capacités ne montrerait pas quelles apps journalisent.

Appliquées en attendant : 1 A, 2 A.
