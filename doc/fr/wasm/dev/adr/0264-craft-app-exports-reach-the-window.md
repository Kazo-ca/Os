> [English version](../../../../en/wasm/dev/adr/0264-craft-app-exports-reach-the-window.md)

# ADR 0264 : les exports d'une app Craft arrivent dans sa fenêtre

## Statut

Proposée le 2026-11-13. Benoit a choisi cette voie pour Kazo Pdf (question Q1 du
[plan de Kazo Pdf](https://github.com/Kazo-ca/Os-App-PDF/blob/pdf/base/doc/fr/plan.md), réponse A).
Série `craft-exports/base`.

- **S'appuie sur :** l'[ADR 0259](0259-shared-craft-app-host-module.md) (module
  commun des apps Craft, `Kazo.Os.AppSdk.Craft`, et son sélecteur de fichiers).
- **Concerne :** Kazo Pdf d'abord, puis toute app Craft qui exporte de la même
  façon.

## Contexte

- Une app Craft (WordCraft, GridCraft, PdfCraft…) tourne dans un iframe. Elle remet
  ses enregistrements à la fenêtre par l'événement `save` de son mode hébergé,
  et le module intercepte déjà son sélecteur de fichiers (`rfd`) ainsi que le lien
  `rfd-output` que `rfd` montre pour un enregistrement hors du mode hébergé
  (`file-picker.js`).
- PdfCraft 0.21.1 fait autrement pour tous ses exports : Exporter (Word, images,
  texte…), Imprimer en PDF, Réduire la taille, copie signée, PDF créé, résultat de
  l'OCR, données de formulaire (FDF, XFDF, CSV), mesures, capture de zone. Sa
  fonction `download()` crée un `Blob`, son adresse `blob:` par
  `URL.createObjectURL`, un `<a download>` détaché du document, l'active par
  `click()`, puis révoque l'adresse aussitôt.
- Le navigateur télécharge alors le fichier dans le dossier Téléchargements de
  l'ordinateur réel. Il n'arrive jamais dans le système de fichiers de Kazo OS,
  et l'utilisateur ne voit pas le dialogue d'enregistrement de l'OS.
- L'adresse est révoquée dans le même tour de boucle que le clic : lire le fichier
  plus tard par `fetch(href)` échoue. Il faut garder le `Blob` lui-même.
- Certains exports s'appellent `.pdf` (Imprimer en PDF, Réduire la taille, copie
  signée). La fenêtre ne peut donc pas reconnaître un export à son extension :
  traité comme un enregistrement, il lierait la fenêtre au nouveau fichier, voire
  réécrirait le document ouvert.

## Décision

1. **Le module intercepte les téléchargements de la page.** Dans l'iframe,
   `file-picker.js` remplace, pour cette page seulement :
   - `URL.createObjectURL`, qui note chaque `Blob` sous son adresse tant qu'elle
     n'est pas révoquée (le `Blob` reste ensuite tenu jusqu'à sa remise) ;
   - `HTMLAnchorElement.prototype.click` : un lien avec un attribut `download`
     et une adresse `blob:` connue (ou `data:`) n'est pas suivi. Ses octets partent
     à la fenêtre dans un nouveau message `export { name, bytes }`.

   Un autre lien garde le comportement du navigateur. Le lien `rfd-output` reste
   un enregistrement (`save` avec `saveAs`), comme aujourd'hui.

2. **Seulement quand la fenêtre les reçoit.** `CraftFrame` gagne l'événement
   `Exporting` (`Func<FrameSave, Task<bool>>`, avec `FrameSave.SaveAs` à `false`).
   Le proxy n'intercepte que si la fenêtre s'y est abonnée avant `StartAsync`.
   Une fenêtre écrite avant cette version garde donc le téléchargement du
   navigateur, au lieu de recevoir un export qu'elle prendrait pour un
   enregistrement.

3. **La fenêtre l'enregistre comme une copie.**
   `DocumentSession.DecideCopy(name)` rend une décision `AskForCopy` dans le
   dossier du document, quel que soit le format. La fenêtre montre le dialogue
   d'enregistrement de l'OS (« Enregistrer une copie »), écrit le fichier, et sa
   liaison ne change pas. Si l'utilisateur annule ou si l'écriture échoue, seul
   un message le dit : le document n'est pas touché.

4. **Version.** C'est un ajout à l'API publique du module (`CraftFrame.Exporting`,
   `CraftFrame.OnFrameExport`, `DocumentSession.DecideCopy`) et au protocole du
   cadre (`export`). Une app qui s'en sert demande la version de l'OS qui
   l'apporte (`sdkCompatibility.minimumVersion`), la suivante après 3.3.3.

5. **En amont.** Benoit publie l'issue préparée pour PdfCraft : en mode hébergé,
   remettre les exports à l'hôte par `save`, comme les enregistrements.
   L'interception reste valable pour une app qui ne le fait pas.

## Conséquences

- Les exports de Kazo Pdf arrivent dans le système de fichiers de Kazo OS, par le
  dialogue de l'OS, et ne quittent plus le navigateur.
- Kazo Pdf demande la version de l'OS qui porte cette ADR : sa sortie au Store
  attend cette version.
- Les autres apps Craft n'ont rien à changer. Elles en profitent en s'abonnant à
  `Exporting`, si elles exportent par un lien de téléchargement.
- Le module remplace deux fonctions de la page de l'iframe (`createObjectURL` et
  `click` des liens), comme il remplace déjà `click` des champs de fichier. Ces
  remplacements sont défaits à l'arrêt du cadre.
