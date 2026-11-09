> [English version](../../../../en/wasm/dev/adr/0239-start-on-the-latest-version.md)

# ADR 0239 : démarrer sur la dernière version

## Statut

Acceptée le 2026-11-08, sur la demande de Benoit dans le fil « Réparer le
plantage d'IRC sans service worker » : « Au démarrage, une application (et l'OS)
doit s'assurer d'être sur la dernière version, sinon on bloque en affichant un
message de redémarrer. Au démarrage de l'OS, on redémarre en affichant `Mise à
jour` ». Branche `apps/failure-in-window-and-newer-worker`.

- **Complète :** l'[ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (lecture de `version.json`), l'[ADR 0223](0223-restart-onto-the-new-version.md)
  (redémarrer sur la nouvelle version) et l'[ADR 0236](0236-build-number-and-version-status-at-sign-in.md)
  (numéro de build, vérification dès le démarrage).
- **Va avec :** l'[ADR 0237](0237-activate-the-worker-of-a-newer-page.md) et
  l'[ADR 0238](0238-an-app-failure-stays-in-its-window.md).

## Contexte

Le service worker sert une copie complète d'une version (page comprise). Après
un déploiement, un rechargement normal redémarre sur l'ancienne copie ; l'OS
ne fait qu'afficher « Redémarrer pour mettre à jour », toutes les 5 minutes ou
dès l'écran de connexion (ADR 0236). Une app ouverte depuis cette copie peut
charger des fichiers d'une autre version (le plantage d'IRC du 2026-11-08).
Benoit veut que l'OS et chaque app ne démarrent que sur la dernière version.

## Décision

1. **Au démarrage de l'OS.** Avant le premier écran, l'hôte lit `version.json`
   (au plus 5 s, `HostBootUpdateCheck.DefaultTimeout`). Si une autre version ou
   un autre build est servi (même règle que l'ADR 0236), l'écran de démarrage
   affiche « Mise à jour de Kazo OS », le nouveau service worker télécharge la
   version (ADR 0223) puis l'OS redémarre dessus (`BrowserHostRestart`). Sans
   réponse à temps (hors ligne, serveur lent) ou si le téléchargement échoue,
   l'OS démarre la version qu'il a, avec l'avis habituel.
2. **Pas de boucle.** Avant de redémarrer, l'hôte note la cible (`3.3.0+812`)
   dans le `sessionStorage` de l'onglet (`hostRestart.js`,
   `claimBootUpdate`). Si le redémarrage n'a pas atterri sur cette version (un
   déploiement en cours), l'onglet ne réessaie pas pour la même cible et démarre
   normalement.
3. **Au lancement d'une app.** `AppLifecycleOrchestrator.LaunchAsync` demande à
   la barrière `IAppLaunchGate` (Platform.Core) avant de charger l'app, pour les
   lancements interactifs d'apps `Window` et `Web` qui ne tournent pas déjà.
   L'hôte navigateur fournit `HostUpdateLaunchGate` : il relit `version.json`
   si sa dernière réponse a plus de 30 s (au plus 3 s d'attente) ; si l'OS est
   obsolète, le lancement est refusé (`AppLaunchStatus.UpdateRequired`, code
   `lifecycle.os.update-required`, « Une nouvelle version de Kazo OS est
   disponible. Redémarrez pour ouvrir « IRC ». ») et l'hôte affiche la boîte
   « Mise à jour requise » avec *Redémarrer pour mettre à jour* et *Plus tard*.
4. **Ce qui n'est pas bloqué.** Une app déjà ouverte (elle reçoit le focus), les
   services et les commandes du terminal : ils font partie du système en marche.
   Hors ligne, rien n'est bloqué.

## Conséquences

- Un déploiement ne laisse plus un onglet démarrer sur l'ancienne copie : le
  premier démarrage après le déploiement passe par « Mise à jour de Kazo OS ».
- Le démarrage attend `version.json` (une requête, quelques millisecondes en
  temps normal, 5 s au plus).
- Chaque ouverture d'app relit `version.json` au plus toutes les 30 s.
- `AppLaunchStatus` gagne `UpdateRequired` ; `IAppLaunchGate` et
  `AppLaunchRefusal` sont publics dans Platform.Core (pas dans le SDK des apps).

## Tâches

- [x] `IAppLaunchGate`, `AppLaunchRefusal`, `AppLaunchStatus.UpdateRequired`,
  appel dans `AppLifecycleOrchestrator.LaunchAsync`.
- [x] `HostBootUpdateCheck`, `HostUpdateLaunchGate` (Platform.Blazor),
  inscription dans `KazoOsBrowserHostExtensions`.
- [x] `App.razor` : « Mise à jour de Kazo OS » au démarrage, boîte « Mise à
  jour requise » ; `hostRestart.js` : `claimBootUpdate`.
- [x] Tests : `HostUpdateAtStartTests`, `AppLifecycleOrchestratorTests.LaunchGate`.
- [x] Vérifié dans Chromium : deux publications, la deuxième servie après la
  première, le démarrage affiche « Mise à jour de Kazo OS » et redémarre sur la
  deuxième.
