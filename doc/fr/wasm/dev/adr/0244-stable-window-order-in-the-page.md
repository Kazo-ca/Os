> [English version](../../../../en/wasm/dev/adr/0244-stable-window-order-in-the-page.md)

# ADR 0244 : garder l'ordre des fenêtres stable dans la page

## Statut

Acceptée le 2026-11-13. Benoit a choisi l'option A à la question 14 du fil
Kazo Word : corriger Kazo OS plutôt que contourner le problème dans l'app.
Branche `windows/stable-dom-order`.

- **Complète :** l'[ADR 0178](0178-extended-screens-in-display-surfaces.md) §3
  (fenêtres déplacées d'un écran à l'autre par portail, sans recréer leurs
  composants) et l'[ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)
  §3 (une seule surface sur mobile, le propriétaire reste rendu sous son
  dialogue).

## Contexte

`WindowRuntime.Windows` donne les fenêtres de l'arrière vers l'avant (tri par
`ZOrder`). `DesktopArea` les rendait dans cet ordre, dans une liste à `@key`.
Quand une fenêtre passe devant, son `ZOrder` augmente : la liste change d'ordre
et Blazor **déplace** l'élément de la fenêtre dans la page. Or un navigateur
recharge un `<iframe>` déplacé dans le document. Une app qui vit dans un
iframe perd donc son état à chaque clic sur une autre fenêtre : Kazo Word perd
son document, le navigateur de l'OS recharge sa page.

L'empilement visuel ne dépend pourtant pas de l'ordre dans la page : chaque
fenêtre reçoit son `z-index` par la variable `--window-z`
(`WindowHost.razor.js`, `projectWindowGeometry`).

Sur mobile, `SingleSurfaceArea` ne rendait que la fenêtre affichée (et ses
propriétaires sous un dialogue) : changer d'app retirait l'autre fenêtre de la
page, et son iframe repartait de zéro au retour.

## Décision

1. **Ordre d'ouverture dans la page.** Les zones de fenêtres (`DesktopArea`,
   `SingleSurfaceArea`) rendent leurs fenêtres dans l'ordre où elles sont
   apparues, retenu par un `WindowDocumentOrder` (Windowing Core) : une
   nouvelle fenêtre s'ajoute à la fin, une fenêtre fermée sort de la liste, une
   fenêtre qui passe devant **ne bouge pas**. Le paramètre `Windows` garde son
   contrat (de l'arrière vers l'avant) ; seul l'ordre du rendu change.
2. **Le `ZOrder` ne sert plus qu'au `z-index`.** L'empilement visible, le clic,
   l'aperçu et le coup d'œil (ADR 0199) ne changent pas.
3. **Mobile.** `SingleSurfaceArea` rend toutes les fenêtres, dans le même ordre
   stable ; celles qui ne sont pas à l'écran restent dans la page, cachées
   (`hidden`, inertes), comme une fenêtre minimisée du bureau. Changer d'app ne
   recharge plus un iframe. Le coût mémoire devient celui du bureau, où les
   fenêtres minimisées restent rendues.
4. **Clavier et accessibilité.** L'ordre de lecture et de Tab entre fenêtres
   devient l'ordre d'ouverture. Une fenêtre qui reçoit le focus clavier (Tab
   ou Maj+Tab depuis une autre fenêtre) **passe devant et devient active**,
   comme dans un vrai bureau : le focus n'est jamais dans une fenêtre cachée
   derrière une autre. Un clic ou Tab dans l'iframe d'une fenêtre d'arrière-plan
   la fait aussi passer devant : la page ne reçoit alors ni clic ni `focusin`,
   seulement sa propre perte de focus, que `trackFocus` (`WindowHost.razor.js`)
   transforme en `focusin` sur l'iframe. Une fenêtre bloquée par un dialogue modal reste inerte
   et ne reçoit pas le focus. Alt+Q, Alt+\` et la barre des tâches donnent
   toujours le focus à la fenêtre activée (ADR 0105 §4).

## Questions posées à Benoit (répondues le 2026-11-13 : A et A)

- **Q1 (clavier)** : **A, la fenêtre qui reçoit le focus passe devant**
  (recommandé, choisi) ; B, ne rien changer ; C, enfermer Tab dans la fenêtre
  active.
- **Q2 (mobile)** : **A, corriger aussi le mobile dans la même PR** (recommandé,
  choisi) ; B, seulement le bureau, une issue `TODO` pour le mobile.

## Conséquences

- Un iframe (Kazo Word, navigateur de l'OS, apps iframe) garde sa page et son
  état quand sa fenêtre passe devant ou derrière, sur le bureau comme sur
  mobile. Il se recharge encore quand sa fenêtre change d'écran (ADR 0178 §3) :
  c'est un vrai déplacement dans un autre document.
- Les tests ne peuvent plus supposer que la dernière fenêtre de la page est
  celle de devant : ils lisent `is-focused` ou `data-window-z`.
- Aucun changement du SDK des apps. Windowing Core gagne un type public
  (`WindowDocumentOrder`).

## Tâches

- [x] ADR (FR + EN) et index.
- [x] `WindowDocumentOrder` et ses tests unitaires.
- [x] `DesktopArea` et `SingleSurfaceArea` rendent dans l'ordre stable.
- [x] Une fenêtre qui reçoit le focus clavier passe devant.
- [x] Test E2E : un iframe ne se recharge pas quand sa fenêtre passe devant.
- [x] Docs (`window-portal.md`, `features.md`) et entrée de changement.
