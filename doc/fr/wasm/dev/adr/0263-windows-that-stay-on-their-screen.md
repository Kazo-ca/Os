> [English version](../../../../en/wasm/dev/adr/0263-windows-that-stay-on-their-screen.md)

# ADR 0263 : fenêtres qui restent sur leur écran (apps Craft dans un iframe)

## Statut

Proposée le 2026-11-13, en attente des réponses de Benoit aux questions 1 et 2
(section « Questions »). Le texte suit les choix recommandés (A et A). Branche
`screens/single-screen-apps`. Issue [#482](https://github.com/Kazo-ca/Os/issues/482).

- **S'appuie sur :** l'[ADR 0178](0178-extended-screens-in-display-surfaces.md)
  (écrans étendus, portail de fenêtre), l'[ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)
  (menu « Autre écran », Démarrer de chaque écran), l'[ADR 0183](0183-drag-a-window-between-screens.md)
  (glisser une fenêtre d'un écran à l'autre) et l'[ADR 0234](0234-window-styles-inspired-by-winforms.md)
  (options de fenêtre de type WinForms).
- **Concerne :** le module Craft commun ([ADR 0259](0259-shared-craft-app-host-module.md))
  et Kazo Word.

## Contexte

- Benoit a remarqué que Kazo Word se comporte mal quand sa fenêtre passe sur un
  autre écran de Kazo OS, c'est-à-dire une autre fenêtre du navigateur.
- Pour changer d'écran, le portail (`WindowPortal.js`) déplace l'élément de la
  fenêtre dans le document de l'autre fenêtre du navigateur. Ses composants
  Blazor ne sont pas recréés, mais le navigateur recharge toujours un `<iframe>`
  retiré de son document (même cause que l'[ADR 0244](0244-stable-window-order-in-the-page.md),
  dans une seule page).
- Une app Craft (WordCraft, GridCraft…) tourne dans un iframe, derrière un proxy
  qui lui parle par un `MessagePort`. Après le rechargement, l'app redémarre à
  vide, le port est perdu, et le document ouvert ainsi que l'état non enregistré
  disparaissent.
- Une fenêtre change d'écran par quatre chemins : le menu « Autre écran ▸ Écran
  N / Nouvel écran », les raccourcis « écran précédent / suivant », le glisser
  entre écrans, et l'ouverture d'une app depuis le menu Démarrer d'un écran
  secondaire. La fermeture d'un écran ramène ses fenêtres sur l'écran 1.
- La vraie solution, selon Benoit, est de compiler nous-mêmes une version
  WebAssembly de chaque app Craft avec le support propre à Kazo OS, sans iframe
  ni proxy, dans une prochaine mise à jour. En attendant, il faut empêcher le
  changement d'écran et prévenir l'utilisateur.

## Décision

1. **Une nouvelle option de fenêtre.** `WindowOptions.CanChangeScreen`
   (`window.canChangeScreen` dans `manifest.json`, écrit seulement quand il vaut
   `false`), vrai par défaut. Une app la déclare dans son manifeste, ou la change
   pendant l'exécution avec `IAppWindowGateway.SetOptionsAsync`, comme les autres
   options de l'ADR 0234. Le gestionnaire de fenêtres la porte dans
   `WindowFrame.CanChangeScreen` (`UserCanChangeScreen`).

2. **Le gestionnaire de fenêtres la fait respecter.**
   - `WindowRuntime` refuse `MoveWindowToDisplayCommand` (aucun événement) si une
     fenêtre de la famille modale ne peut pas changer d'écran.
   - Une fenêtre créée avec cette option s'ouvre sur l'écran 1, même depuis le
     menu Démarrer d'un écran secondaire.
   - La fermeture d'un écran ramène toujours ses fenêtres sur l'écran 1 : c'est
     l'OS qui le fait, pas l'utilisateur.
   - Une app qui coupe l'option alors que sa fenêtre est déjà sur un écran
     secondaire la laisse où elle est : la ramener rechargerait aussi l'iframe.

3. **Avertissement.** Pour une telle fenêtre, le sous-menu « Autre écran » ne
   propose qu'une entrée grisée : « Pas encore disponible pour cette app :
   problème connu, une solution est en cours ». Un raccourci d'écran ou une
   fenêtre lâchée sur un autre écran ne la déplace pas : elle reste à sa place,
   aucun contour n'est dessiné sur l'autre écran pendant le glisser, et une
   notification affiche le même texte.

4. **Qui la pose.** Chaque app qui affiche un cadre Craft la déclare dans son
   manifeste : Kazo Word d'abord (version 0.1.1), puis les apps portées avec le
   module Craft ([ADR 0259](0259-shared-craft-app-host-module.md)), dont la
   documentation et le guide de portage le demandent. Le module ne la change pas
   lui-même : `CraftFrame` ne connaît pas la fenêtre, et une déclaration dans le
   manifeste vaut dès l'ouverture, avant que l'iframe existe. Une ancienne
   version de l'OS refuse un manifeste qui contient un champ inconnu : l'app
   demande donc la version de l'OS qui apporte cette option
   (`sdkCompatibility.minimumVersion`).

5. **Sortie.** L'option reste dans le SDK après la solution de Benoit : une app
   qui dépend de son document (un iframe, un lecteur en cours) peut en avoir
   besoin. Les apps Craft la retireront de leur manifeste quand elles
   n'utiliseront plus d'iframe.

## Questions posées à Benoit

1. **Comment l'OS bloque le changement d'écran ?** A (recommandé) : une option
   déclarée par l'app (cette ADR). B : la détection automatique de tout iframe
   dans la fenêtre, qui corrige Kazo Word 0.1.0 sans le mettre à jour mais touche
   aussi le Navigateur et dépend du DOM. C : les deux.
2. **Quel avertissement ?** A (recommandé) : l'entrée grisée avec le texte, et
   une notification quand on lâche la fenêtre sur un autre écran. B : le menu
   reste actif et un clic affiche la notification. C : l'entrée est cachée, sans
   message.

## Conséquences

- Les apps Craft ne perdent plus leur document quand l'utilisateur essaie de
  changer leur écran. En contrepartie, elles restent sur l'écran 1 tant que
  la solution de Benoit n'est pas en place.
- Kazo Word 0.1.0, déjà publié, reste touché jusqu'à la version de l'OS qui
  apporte cette option et Kazo Word 0.1.1.
- Ajout à l'API publique (`WindowOptions.CanChangeScreen`,
  `WindowFrame.CanChangeScreen`) : une version mineure du SDK.
- Issues `bug` : Kazo-ca/Os#482, Os-App-Word#19, Os-App-Spreadsheets#4, et #2
  dans Os-App-PDF, Os-App-Fig, Os-App-Photo, Os-App-Design, Os-App-Deck et
  Os-App-Vector.
