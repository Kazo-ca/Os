> [English version](../../../../en/wasm/dev/adr/0253-marked-sender-on-launch-and-open.md)

# ADR 0253 : expéditeur marqué par l'OS sur `LaunchAsync` et l'ouverture de fichier, pour le journal seulement

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : 52 (« l'OS
marque l'expéditeur (petit ADR) »), 108 (« pas de nouvelle demande et pas de
plafond de permission fondé sur le lanceur ») et 186 A (« l'expéditeur marqué
sert au journal seulement »), 2026-11-11.

- **Complète :** l'[ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (intents, `AppIntentDelivery.SenderAppId` pour les intents définis par une
  app), l'[ADR 0245](0245-feature-service-registry-and-async-resolution.md)
  (identité de l'appelant d'un service) et l'[ADR 0254](0254-per-user-events.md)
  (journal par usager).
- **Pour :** le journal d'Os-x86 (qui a demandé le lancement d'un programme :
  le terminal, BoxedWine, une autre app, un raccourci) et celui de BoxedWine.

## Contexte

- `AppIntentDispatcher` (`Platform/Kazo.Os.Platform.Core/Intents/AppIntentDispatcher.cs`)
  construit pour `LaunchAsync` (:218) et l'ouverture de fichier (:241-242) un
  `AppLaunchRequest(appId, principal, arguments)` **sans expéditeur** ; seul
  `apps.launch` est vérifié chez l'appelant (:206-207, :212-213, :225).
  Les autres intents (Reveal :380-382, Edit :419-421, OpenUri :473-475,
  ShowSettings :503-505) passent déjà `AppIntentDelivery(…, SenderAppId:
  request.CallerAppId)`.
- L'OS **connaît** l'appelant : `AppIntentRequest(RequestId, CallerAppId,
  UserId, Intent)` (`Shared/Kazo.Os.App.Abstractions/AppIntents.cs:221-225`),
  `CallerAppId` fixé par l'OS à la construction du contexte
  (`AppExecutionContextFactory.cs:255`, `AppIntentGateway.cs:15-29`). Le
  récepteur lit `IAppExecutionContext.Intent` (:144).
- Un raccourci `.desktop` lance une app avec n'importe quels arguments
  (`KazoDesktopEntry.cs:29, 55-75` ; lancement par l'explorateur,
  `ExplorerFileView.Desktop.cs:475`) : l'app lancée ne sait pas si c'est
  l'usager ou un fichier qui l'a lancée.
- Une action de notification lance une app avec des arguments sans
  vérifier l'app qui a posté la notification (`NotificationActionRunner.cs:37-44`).
- La conception d'Os-x86 avait un temps prévu un plafond de permission fondé
  sur l'app qui lance (D21, D52) ; Benoit l'a écarté (108) : l'origine du
  lancement ne change ni la bouteille ni les permissions du programme. Ce
  qui reste, c'est le besoin de **journaliser** qui a demandé quoi.

## Décision

### 1. L'OS marque l'expéditeur

- `AppLaunchRequest` et la livraison d'un lancement ou d'une ouverture de
  fichier gagnent un `Sender` : `KazoIntentSender(AppId, UserId, Kind)` avec
  `Kind` ∈ `App` (un appel du SDK), `Shell` (le shell d'un terminal,
  `AppId` = l'app Terminal), `Desktop` (double-clic, menu Démarrer,
  raccourci `.desktop`, « Ouvrir avec » : `AppId` = l'app qui tenait la vue,
  explorateur ou bureau), `Notification` (`AppId` = l'app qui a posté la
  notification), `System` (démarrage automatique, service).
- Le `Sender` est **fixé par l'OS**, jamais fourni par l'appelant : le
  répartiteur le prend de `AppIntentRequest.CallerAppId` et du contexte ;
  un appelant ne peut pas se faire passer pour un autre.
- Le récepteur le lit dans `IAppExecutionContext.Intent?.Sender` (fenêtre
  et service) et dans `TerminalExecutionContext.Sender` (commande) ; une
  app lancée sans intent (démarrage du bureau) a `Sender.Kind = Desktop`.
  Un service fourni par une Feature (ADR 0245) le reçoit par
  `IFeatureServiceContext.Caller` (`AppId`, `UserId`), même structure.

### 2. Pour le journal seulement

- Le `Sender` **n'entre dans aucune décision de permission** : ni dans
  `DangerousCapabilityEvaluator`, ni dans le choix d'une bouteille par
  Os-x86, ni dans la fenêtre de permission réseau (108, 186 A). Une app qui
  s'en servirait pour restreindre ferait une règle à elle, hors de l'OS.
- L'OS écrit le `Sender` dans `/var/log/syslog` à chaque lancement
  (`apps: launched <app> by <sender.kind>:<sender.appId> user=<guid>`) et
  dans l'événement de l'usager (ADR 0254) quand le récepteur en écrit un.
- Rien n'est caché : toute app lancée connaît son expéditeur ; ce n'est pas
  une donnée sensible (l'identifiant d'une app, pas un secret).

### 3. Ce qui ne change pas

- `apps.launch` reste la seule permission vérifiée ; l'ordre des
  candidats d'une association et la confirmation d'un changement d'app par
  défaut sont inchangés ; `AppIntentDelivery.SenderAppId` des intents
  définis par une app reste et devient un alias de `Sender.AppId`.

## Options écartées

- **A. Un plafond de permission fondé sur l'expéditeur** (D21, D52
  d'origine) : écarté par Benoit (108) ; et toutes les apps installées ont
  les mêmes droits que l'OS dans la page (`KazoAppPackages.cs:20-23`), un
  plafond serait de bonne foi seulement.
- **B. Laisser l'appelant mettre son identité dans les arguments**
  (`--from=…`) : falsifiable ; l'OS a déjà `CallerAppId`.
- **C. Ne rien marquer** : le journal d'Os-x86 ne saurait pas distinguer un
  lancement par l'usager au terminal d'un lancement par une app ; 52 le
  demande.

## Conséquences

- `AppLaunchRequest.Sender`, `KazoIntentSender`, `IAppExecutionContext.Intent.Sender`,
  `TerminalExecutionContext.Sender`, `IFeatureServiceContext.Caller` (SDK,
  `PublicAPI.Unshipped.txt`, fixture `sdk-compat`) ; `AppIntentDispatcher`
  (`DispatchLaunchAsync`, `DispatchOpenFileAsync`), le shell
  (`ShellInterpreter.SimpleCommands.cs:259-309`, ADR 0251), l'explorateur et
  le menu Démarrer, `NotificationActionRunner` (expéditeur = l'app qui a
  posté), `AppLifecycleOrchestrator` (journal).
- Docs : [`app-intents.md`](../../framework/app-intents.md), `features.md`,
  entrée `changes/unreleased`.
- Tests : `LaunchAsync` depuis une app → `Sender.Kind = App` et son id ;
  `./x.exe` → `Shell` et l'app Terminal ; double-clic → `Desktop` ; action de
  notification → `Notification` et l'app qui a posté ; un argument `--from=`
  n'a aucun effet ; journal écrit.

## Question posée à Benoit

1. **Le raccourci `.desktop` et l'action de notification : quel expéditeur ?**
   - A) **(Recommandé)** `Desktop` avec l'app qui tenait la vue (explorateur,
     bureau) pour le raccourci ; `Notification` avec l'app qui a posté pour
     l'action : c'est ce que l'OS sait de source sûre.
   - B) `Desktop` et `Notification` sans `AppId` (l'OS seulement) : plus
     simple, moins d'information dans le journal.

Réponse du mainteneur (2026-11-11) : 1 A.
