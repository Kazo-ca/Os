> [English version](../../../../en/wasm/dev/adr/0241-replace-an-older-worker-at-boot.md)

# ADR 0241 : remplacer un ancien service worker au démarrage

## Statut

Acceptée le 2026-11-09, sur le signalement de Benoit dans le fil « Réparer le
plantage d'IRC sans service worker » : « quand je fais F5, je suis sur 3.2.0,
mais si je fais Ctrl+F5 je suis sur 3.2.1. Puis, si je fais F5, je retourne sur
3.2.0 (la première version vue sur ce navigateur sur ce domaine) ». Branche
`service-worker/replace-older-worker-at-boot`.

- **Complète :** l'[ADR 0237](0237-activate-the-worker-of-a-newer-page.md)
  (activer le worker d'une page plus récente, au lancement d'une app installée)
  et l'[ADR 0239](0239-start-on-the-latest-version.md) (démarrer sur la dernière
  version).

## Contexte

Le service worker d'une version sert tous les fichiers de cette version, page
comprise. Quand une nouvelle version est déployée, le navigateur télécharge son
worker, qui reste **en attente** : il ne s'active que lorsqu'aucun onglet ni
fenêtre du site n'est plus ouvert (ou sur « Redémarrer pour mettre à jour »,
ADR 0223). Un F5 ne libère pas l'onglet : l'ancien worker garde la main et
resert l'ancienne version, aussi longtemps que l'utilisateur garde un onglet
ouvert.

L'ADR 0239 corrige ce cas à partir des versions qui la contiennent : la page
servie par l'ancien worker lit `version.json` au démarrage et redémarre sur la
nouvelle version. Mais une page d'une version plus ancienne (3.2.0) n'a pas ce
code. Ctrl+F5 contourne l'ancien worker et charge la dernière version, mais rien
ne remplace ce worker : l'ADR 0237 ne le fait qu'au lancement d'une app
installée. Le F5 suivant retombe donc sur l'ancienne version.

## Décision

1. **Au démarrage de l'OS**, une fois la vérification de l'ADR 0239 passée (la
   page tourne sur la dernière version), l'hôte appelle
   `BrowserInstalledAppAssets.AdoptAtBootAsync` en arrière-plan : le démarrage
   ne l'attend pas.
2. **Page non contrôlée avec un worker actif** (Ctrl+F5) : la page demande au
   worker actif de la prendre (ADR 0225). S'il met en cache une autre version,
   le worker de la version de la page est activé (ADR 0237) ; les onglets de
   l'ancienne version redémarrent dessus. Le F5 suivant reste sur la dernière
   version.
3. **Rien à faire** sur un hôte de développement (pas de cache de version), à la
   première visite (`index.html` inscrit le worker, qui prend la page en
   s'activant) ou quand un worker contrôle déjà la page.
4. **Échec** : il est journalisé (`boot: the service worker of this version
   could not take this page`) sans rien afficher ; « Redémarrer pour mettre à
   jour » reste possible.

## Conséquences

- Un seul Ctrl+F5 suffit pour sortir d'une ancienne version, même si cette
  version ne contient pas l'ADR 0239.
- Les autres onglets de l'ancienne version redémarrent sur la nouvelle (choix B
  de Benoit pour l'ADR 0237).
- `BrowserInstalledAppAssets.AdoptAtBootAsync` est public dans
  Infrastructure.Browser ; `installedAppAssets.js` exporte `adoptAtBoot`.

## Tâches

- [x] `installedAppAssets.js` : `adoptAtBoot`, qui réutilise `prepare` hors
  hôte de développement.
- [x] `BrowserInstalledAppAssets.AdoptAtBootAsync` ; appel en arrière-plan dans
  `App.razor` après `UpdateAtBootAsync`.
- [x] Vérifié dans Chromium : une version servie, puis une nouvelle, Ctrl+F5,
  puis F5 reste sur la nouvelle version.
