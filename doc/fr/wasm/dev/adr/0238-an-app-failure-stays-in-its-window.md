> [English version](../../../../en/wasm/dev/adr/0238-an-app-failure-stays-in-its-window.md)

# ADR 0238 : la panne d'une app reste dans sa fenêtre

## Statut

Acceptée le 2026-11-08 ; Benoit a choisi l'option A (« 2. A ») dans le fil
« Réparer le plantage d'IRC sans service worker ». Branche
`apps/failure-in-window-and-newer-worker`.

- **Va avec :** l'[ADR 0237](0237-activate-the-worker-of-a-newer-page.md)
  (la cause du plantage d'IRC) et l'[ADR 0239](0239-start-on-the-latest-version.md).
- **Ressemble à :** l'[ADR 0127](0127-priority-and-not-responding-dialog.md)
  §3 (la boîte « ne répond pas » couvre seulement la fenêtre concernée).
- **Ne change pas :** `HostErrorBoundary` reste la barrière de l'hôte pour ses
  propres écrans (connexion, bureau, barre des tâches) ; ce qu'affiche sa page
  d'erreur est l'ADR 0240.

## Contexte

Le 2026-11-08, l'app IRC n'a pas pu importer
`_content/Kazo.Irc/Components/MessagePane.razor.js` (404, voir l'ADR 0237). La
`JSException` levée dans `MessagePane.OnAfterRenderAsync` est remontée jusqu'au
seul `ErrorBoundary` de l'OS, celui de l'hôte (`App.razor`), qui a remplacé
**tout** l'écran par « An unexpected host failure interrupted the current
surface ». Une app tierce pouvait donc arrêter tout le système par une simple
erreur, ce qu'aucun vrai OS ne permet : un programme qui plante se ferme seul.

## Options posées à Benoit

- **A** Seule la fenêtre de l'app affiche « « App » a rencontré un problème »
  avec ses boutons ; le reste de l'OS continue. **Choisie.**
- **B** Fermer la fenêtre automatiquement et envoyer une notification.
- **C** Garder le comportement actuel (tout l'OS s'arrête).

## Décision

1. **Une barrière par fenêtre.** `WindowAppRenderer` (bureau, mobile, rendu
   déporté de l'ADR 0129) et `DesktopSurfaceHost` (app de surface du bureau)
   placent `AppErrorBoundary` (`Platform.Blazor/Windows`) autour du point
   d'entrée de l'app, à l'intérieur des valeurs en cascade (contexte, menus,
   passerelle de fenêtre). Une exception levée par un composant de l'app pendant
   son rendu, une méthode de cycle de vie (`OnAfterRenderAsync` compris) ou un
   gestionnaire d'événement s'arrête là.
2. **Ce que montre la fenêtre.** Le contenu de la fenêtre est remplacé par un
   panneau : « « IRC » a rencontré un problème. », « Une erreur a arrêté cette
   fenêtre. Le reste de Kazo OS continue de fonctionner. », puis le type et le
   message de l'exception (texte sélectionnable). La barre de titre, les autres
   fenêtres et la barre des tâches ne changent pas.
3. **Deux boutons.** *Réessayer* recrée les composants de l'app dans la même
   fenêtre et le même processus (`ErrorBoundaryBase.Recover`). *Fermer
   l'application* arrête son processus avec la raison `Fault`
   (`AppLifecycleOrchestrator.StopAsync`), ce qui ferme ses fenêtres.
4. **Journal.** La panne est écrite avec l'identifiant de l'app et l'exception
   complète (`ILogger<AppErrorBoundary>`, donc `/var/log/syslog` et la console
   du navigateur).

## Options rejetées

- **B (fermeture automatique) :** l'utilisateur perd la fenêtre sans voir
  l'erreur, et ne peut pas réessayer.
- **C :** une app tierce arrête tout le système.

## Conséquences

- Une app qui plante ne touche plus que sa fenêtre ; ses services et ses autres
  fenêtres continuent jusqu'à *Fermer l'application*.
- *Réessayer* refait la même chose : si la cause reste (fichier manquant), le
  panneau revient.
- Les exceptions lancées hors du rendu (tâche lancée sans `await`, minuterie)
  ne passent pas par une barrière Blazor ; elles restaient déjà hors de
  `HostErrorBoundary`.

## Tâches

- [x] `AppErrorBoundary.razor` (+ `.razor.cs`, `.razor.css`), textes
  `AppFailure.*` en anglais et en français.
- [x] `WindowAppRenderer` et `DesktopSurfaceHost` l'utilisent.
- [x] Tests : `AppErrorBoundaryTests` (app qui fonctionne, app qui plante,
  sans processus à fermer, en français).
- [x] Docs : [`process-priority-and-unresponsive-windows.md`](../process-priority-and-unresponsive-windows.md), index, `features.md`.
