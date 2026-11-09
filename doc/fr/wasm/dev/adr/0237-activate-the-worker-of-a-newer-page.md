> [English version](../../../../en/wasm/dev/adr/0237-activate-the-worker-of-a-newer-page.md)

# ADR 0237 : activer le service worker d'une page plus récente

## Statut

Acceptée le 2026-11-08 ; Benoit a choisi l'option B (« 1. B ») parmi les
options posées dans le fil « Réparer le plantage d'IRC sans service worker ».
Branche `apps/failure-in-window-and-newer-worker`.

- **Complète :** l'[ADR 0225](0225-serve-installed-app-files-to-every-page.md)
  (prise de contrôle d'une page sans contrôleur). Remplace sa conséquence
  « quand la page tourne sur une autre version que le worker, les fichiers ne
  sont pas servis avant le redémarrage ».
- **S'appuie sur :** l'[ADR 0223](0223-restart-onto-the-new-version.md)
  (les autres onglets redémarrent proprement sur la nouvelle version).
- **Va avec :** l'[ADR 0238](0238-an-app-failure-stays-in-its-window.md)
  (la panne d'une app reste dans sa fenêtre).

## Contexte

Benoit (2026-11-08, `https://os.kazo.ca`, version 3.2.1) : l'app IRC, installée
depuis le Kazo OS Store, a fait tomber tout l'OS au démarrage. La console :

- « the service worker does not serve this page, installed apps keep their
  wwwroot/ files until the next restart » ;
- `_content/Kazo.Irc/Components/MessagePane.razor.js` : 404 ;
- `JSException: Failed to fetch dynamically imported module` dans
  `MessagePane.OnAfterRenderAsync`, puis l'erreur fatale de l'hôte.

La page n'avait pas de contrôleur (Ctrl+F5 ou premier chargement d'un nouvel
onglet servi par le réseau). Le worker actif venait d'un déploiement plus
ancien : la « version » comparée par l'ADR 0225 est celle de
`service-worker-assets.js`, une empreinte des fichiers, qui change à **chaque**
déploiement de `main` (deux le 2026-11-08 : la 3.2.1 et #425), pas seulement à
chaque version. L'ADR 0225 refuse alors la prise de contrôle et les fichiers
`wwwroot/` des apps installées ne sont servis par personne.

Le nouveau worker existe pourtant : `index.html` appelle `register()` à chaque
chargement, le navigateur télécharge le nouveau script et l'installe. Il reste
en attente tant qu'un autre onglet, écran ou fenêtre détachée du site garde
l'ancien.

## Options posées à Benoit

- **A** L'ancien worker prend la page mais ne lui sert que les fichiers des apps
  installées (indépendants de la version), tout le reste passe au réseau.
- **B** Activer tout de suite le nouveau worker ; les autres onglets et écrans
  redémarrent sur la nouvelle version. **Choisie.**
- **C** Ne rien changer, l'app affiche « redémarrez Kazo OS ».

## Décision

1. **Quand le worker actif refuse la page** parce qu'il cache une autre version
   (réponse de `KAZOOS_CLAIM_FOR_INSTALLED_APPS`), `installedAppAssets.js`
   cherche le worker de la version de la page : celui en attente, sinon celui
   en cours d'installation, sinon il demande `registration.update()`.
2. **Une fois ce worker installé** (cache de la version rempli et vérifié), la
   page lui envoie `KAZOOS_ACTIVATE_FOR_INSTALLED_APPS` avec sa version.
   `installedAppAssets.sw.js` n'appelle `skipWaiting()` que si c'est la version
   qu'il cache (même contrôle `mayClaim` que l'ADR 0225) : un worker d'une autre
   version ne s'active jamais pour cette raison.
3. **Son `activate` prend toutes les pages du site** (`clients.claim()`, déjà
   en place) et efface le cache de l'ancienne version. La page qui a demandé met
   `window.kazoOsClaimForInstalledApps` à `pending` avant de demander :
   `index.html` ne la recharge pas (elle tourne déjà sur cette version). Les
   pages de l'ancienne version redémarrent proprement sur la nouvelle
   (`hostUpdate.js`, `OnActivatedElsewhere`, ADR 0223), une page que l'OS ne
   gère pas encore est rechargée.
4. **Installation plus longue que le délai** (10 s,
   `BrowserInstalledAppAssetCache.ServiceWorkerTimeout`) : le lancement
   continue sans les fichiers, comme avant ; la page attend la fin de
   l'installation en arrière-plan (au plus 3 min) puis demande l'activation. Le
   lancement suivant trouve la page contrôlée (ADR 0225 §3).

## Options rejetées

- **A (ancien worker en mode « apps installées seulement » pour cette page) :**
  pas de redémarrage des autres onglets, mais le worker doit retenir, d'un
  redémarrage du worker à l'autre, quelles pages sont d'une autre version.
- **C (message « redémarrez ») :** l'app reste inutilisable jusqu'au
  redémarrage, à chaque déploiement.

## Conséquences

- Après un déploiement, une page chargée par le réseau sert les fichiers des
  apps installées dès que le nouveau worker est installé.
- Les autres onglets, écrans et fenêtres détachées encore sur l'ancienne
  version redémarrent sur la nouvelle quand une app installée est lancée dans
  la page plus récente (l'utilisateur ne l'a pas demandé à ce moment-là ;
  c'est le prix de l'option B).
- Si le nouveau worker ne s'installe pas (hors ligne, fichier refusé par
  l'intégrité), rien ne change par rapport à l'ADR 0225 : un avertissement dans
  la console et `/var/log/syslog`, et l'ADR 0238 garde la panne de l'app dans sa
  fenêtre.

## Tâches

- [x] `installedAppAssets.js` : `activateNewerWorker`, `waitUntilInstalled`,
  `askToActivate`.
- [x] `installedAppAssets.sw.js` : message `KAZOOS_ACTIVATE_FOR_INSTALLED_APPS`.
- [x] Vérifié dans Chromium (Playwright, `service-worker.published.js` servi
  sur `127.0.0.2`, deuxième version simulée par un autre
  `service-worker-assets.js`).
- [x] Docs : [`app-installation.md`](../app-installation.md), index, `features.md`, mémoire.
