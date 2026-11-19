> [English version](../../../en/wasm/framework/app-log-files.md)

# Journaux des apps (`/var/log`) : API et fichiers produits par une app

Ce document décrit les fichiers de journal qu'une app, une commande ou un site produit, les API qui les écrivent et ce qui a été retiré du SDK. L'implémentation (`LogFileWriter`, rotation, canal, Error Log Viewer) et les journaux du système (`syslog`, `kern.log`, `auth.log`, `audit.log`) sont dans [`dev/logging.md`](../dev/logging.md).

## Fichiers produits par une app

| Fichier | Contenu | Mode |
|---|---|---|
| `/var/log/apps/<appId>.log` | `IAppExecutionContext.Logging` de l'app | `system:system 0644` |
| `/var/log/<démon>/<fichier>.log` | `IAppLoggingGateway.AppendToLogFile` d'une **app système** seulement, ligne écrite telle quelle (ADR 0080 §3) ; par exemple `/var/log/kazoweb/error.log` de `kazowebd` ([`static-sites.md`](../dev/static-sites.md)) | `system:system 0644` |
| `/var/log/kazoweb/<appId>.log` | Journal de débogage d'un **site** (app `Web`) qui l'active par `builder.Logging.EnableDebugLog()` : `ILogger<T>`, requêtes, exceptions, par `IAppLoggingGateway.AppendToSiteDebugLog` (ADR 0088 §5, [`asp-kazo-site-data.md`](asp-kazo-site-data.md)) | `system:system 0644` |

## Écrire dans un journal

Dans une app, `IAppExecutionContext.Logging` (type `IAppLoggingGateway`) écrit dans `/var/log/apps/<appId>.log`. Une app système écrit une ligne telle quelle dans un fichier de son démon avec `AppendToLogFile(relativePath, line)`. Un site qui active le journal de débogage (`builder.Logging.EnableDebugLog()`) écrit avec `AppendToSiteDebugLog(line)`.

## Événements d'usager (ADR 0254)

Un événement d'usager dit ce qui est arrivé à quelque chose **de l'usager** :
une permission accordée à un programme, une machine virtuelle démarrée, un
conflit de fichiers résolu. Il va dans le journal de cet usager,
`/var/log/users/<id>/events.log`, lisible par lui et, après l'authentification
de l'OS, par un administrateur. Règle du guide : **si l'événement nomme un
fichier, un programme ou une destination de l'usager, c'est un événement
d'usager** ; sinon il va dans le journal de l'app (`Logging`), lisible par tous.

| Fichier | Contenu | Mode |
|---|---|---|
| `/var/log/users/<id>/events.log` | `KazoEvents.Write` des apps et événements de l'OS pour cet usager ; `<id>` est l'identifiant stable du compte (ADR 0169), jamais son nom de connexion | `<usager>:<groupe> 0600`, dossiers `/var/log/users` et `/var/log/users/<id>` en `system:system 0711` |

```csharp
using Kazo.Os.AppSdk.UserEvents;

// Manifeste : Capabilities = [AppCapabilities.EventsWrite] (« events.write », normale).
KazoEvents.Write(new KazoUserEvent(
    "x86.vm.started",                                  // genre, un seul mot pointé
    L["VmStarted", vm.Name],                           // texte déjà traduit par l'app
    new KazoEventSubject("vm", vm.Id, vm.Name),        // sujet : type, id stable, nom du moment
    KazoEventSeverity.Info,
    new Dictionary<string, string> { ["memory"] = "64 MiB" }));

// L'historique d'une machine : seulement les lignes de cette app.
IReadOnlyList<KazoUserEventEntry> history = await KazoEvents.ReadAsync(
    new KazoUserEventFilter { SubjectType = "vm", SubjectId = vm.Id, Limit = 200 });
```

- **L'OS remplit** l'heure, l'app (prise dans son propre contexte, jamais dans
  l'appel), le processus et, quand il le connaît, l'expéditeur (l'app qui a lancé
  celle-ci ou lui a fait ouvrir un fichier, ADR 0253). Une ligne a le format de
  tous les journaux (ADR 0045), avec `kind`, `subject.type`, `subject.id`,
  `subject.name` et `sender` dans le bloc de propriétés :
  `2026-11-18T09:30:00.000Z INFO  org.example.vm[42]: Windows 98 démarrée {kind=x86.vm.started, memory=64 MiB, subject.id=4d2c, subject.name=Windows 98, subject.type=vm}`.
- **Refusé** (`ArgumentException`) : un genre vide ou avec des espaces (128
  caractères au plus), un genre `kazoos.*` (réservé aux événements de l'OS), un
  message vide (2 048 au plus), un sujet sans type ou sans id (256 par champ),
  plus de 16 propriétés (clé de 64, valeur de 512), et une propriété qui porte
  une clé de l'OS (`KazoUserEventProperties`).
- **Débit**, comme journald : au plus 200 événements par app et par usager en
  30 s ; au-delà, les événements sont abandonnés sans erreur et
  `/var/log/syslog` dit combien (`user-events: suppressed N events of <app>`,
  sans leur contenu). Un journal se relit au plus 60 fois en 30 s ; au-delà,
  `ReadAsync` lève `InvalidOperationException`.
- **Sans `events.write`**, `Write` et `ReadAsync` lèvent
  `KazoUnauthorizedAccessException`. Dans un worker, `Write` est vérifié dans
  l'app avant d'être envoyé sans réponse : un refus de l'OS après cette
  vérification ne va que dans le journal du processus.
- `KazoEvents.ReadAsync` rend les événements **de cette app seulement**, du plus
  ancien au plus récent, archives comprises ; `KazoUserEventFilter` filtre par
  genre (ou préfixe finissant par `.`), sujet, gravité minimale, date, texte et
  nombre (1 000 par défaut, 5 000 au plus).
- `KazoUserEventEntry.TryParse(ligne)` relit une ligne du fichier ;
  `cat /var/log/users/<mon id>/events.log` marche avec les droits ordinaires.
- **Même rotation** que les autres journaux (1 Mio, 5 archives) ; le dossier est
  supprimé avec le compte.
- L'onglet « Mes événements » de l'Error Log Viewer montre tout le journal de
  l'usager en session, toutes apps confondues ([`logging.md`](../dev/logging.md#error-log-viewer)).
- **Lire le journal d'un autre compte** :
  `KazoUserAccounts.ReadEventsAsync(login, filtre)` rend tous ses événements.
  Le journal de l'usager en session ne demande rien ; celui d'un autre compte
  demande un administrateur dans la fenêtre de l'OS, une fois par processus
  (le billet reste valable jusqu'à la fin du processus), et chaque compte lu est
  écrit dans `auth.log` (`events.read-other user=<login>`).
- **Opération sensible d'une Feature** :
  `KazoFeatureAuthentication.AuthenticateAsync(opération, portée, sujet)` ; quand
  la Feature consomme le billet, l'OS écrit
  `kazoos.feature.operation-authenticated` dans le journal de l'usager avec ce
  sujet ([`system-administration.md`](system-administration.md#rôles-des-features-adr-0247-ajustée-par-ladr-0283)).
- **Limites.** Le fichier appartient à l'usager : une app de cet usager qui a
  `filesystem.system.read` ou `filesystem.system.write` (dangereuses, approuvées
  par un administrateur) peut le lire en entier ou le modifier par l'API des
  fichiers ; « une app ne voit que ses lignes » vaut pour `KazoEvents`.
  Empêcher l'usager de modifier ce fichier est à faire
  ([#632](https://github.com/Kazo-ca/Os/issues/632)). Le
  message, le sujet et le genre ne sont pas masqués ; seules les valeurs des
  propriétés passent par le masquage des clés sensibles.

## Format des lignes

- `LogLineFormat` (`Shared/Kazo.Os.Simulation.Abstractions/Diagnostics/`) :
  `Format`, `FormatAudit` et `TryParse`. Une entrée tient sur une ligne :
  `2026-10-10T03:11:42.123Z WARN  org.kazoos.text-editor[42]: message {clé=valeur}`.
  Le message échappe `\`, `\n`, `\r`, `\t` et aussi `{` (en `\{`), pour qu'un
  message ne soit jamais relu comme un bloc de propriétés. Dans ce bloc, les
  clés et valeurs échappent en plus `,`, `=` et `}`. Les espaces d'une source
  deviennent `_`.

## Rotation

- `LogRotationPolicy` : `MaxFileBytes` = 1 Mio, `MaxArchives` = 5. Rotation à
  la `logrotate` (`.5` supprimé, `.4`→`.5` … `<f>`→`.1`, nouveau `<f>` vide).
  Un fichier vide ne tourne jamais : un lot plus gros que 1 Mio est écrit entier.

## Délais et pertes

Les lignes d'un journal sont vidées par lot : toutes les 1 s de temps simulé, dès 100 lignes en attente, à la déconnexion et à l'arrêt. Au-delà de 10 000 lignes en attente, les plus anciennes sont perdues ; une ligne `WARN logging` le signale au vidage suivant.

## Ce qui a été retiré

- `IAppDiagnosticsGateway`, `AppDiagnosticsGateway` et `IAppExecutionContext.Diagnostics`
  sont supprimés. Lire les journaux, c'est lire des fichiers.
- `diagnostics.read` et `diagnostics.clear` sont retirées du catalogue par
  l'ADR 0049 ([`capabilities.md`](capabilities.md)).
