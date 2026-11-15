> [English version](../../../../en/wasm/dev/adr/0243-serve-installed-app-pages.md)

# ADR 0243 : servir la page d'une app installée chargée dans une iframe

## Statut

Acceptée le 2026-11-13. Benoit a choisi l'option A de la question 10 de la
[vérification du plan de Kazo Word](https://github.com/Kazo-ca/Os-App-Word/pull/2)
(`doc/fr/plan-verification.md`) et demandé de la commencer tout de suite.
Branche `fix/installed-app-iframe-page`.

- **Complète :** l'[ADR 0206](0206-serve-installed-app-assets.md) (servir les
  fichiers `wwwroot/` des apps installées), qui ne servait que les sous-ressources
  (scripts, images, workers), pas les chargements de page.
- **S'appuie sur :** l'[ADR 0131](0131-cross-origin-isolation.md) §2 (en-têtes
  COOP/COEP ajoutés par le service worker) et l'[ADR 0225](0225-serve-installed-app-files-to-every-page.md)
  (le worker prend une page qu'il ne contrôle pas).

## Contexte

- Une app peut afficher une page HTML de son `wwwroot/` dans une iframe : Kazo
  Word enveloppe WordCraft, une application web complète, dans
  `_content/Kazo.Word/host/index.html?author=…`. La page lit ses paramètres
  (`?author=`, `?webgl`) dans sa propre adresse.
- Le chargement d'une iframe est une **navigation** (`mode: 'navigate'`). Le
  service worker publié répondait à toute navigation hors du manifeste de la
  version par `index.html`, **avant** de regarder le cache des apps installées :
  l'iframe démarrait un second Kazo OS.
- Sur un hôte de développement, le worker « apps installées seulement »
  (ADR 0206 §4) sert déjà ces navigations : la page marchait en développement et
  cassait une fois publiée.

## Décision

1. **Le cache des apps installées d'abord, navigation comprise.** Dans
   `fetchShellOrAsset` de `service-worker.published.js`, une requête `GET` de même
   origine sous `_content/` (`installedAppAssets.handles`) que le cache
   `kazoos-installed-apps` contient reçoit ce fichier, que ce soit un chargement
   de page ou une sous-ressource. La requête est ignorée pour trouver le fichier
   (clé sans requête, ADR 0206 §2) ; l'iframe garde son adresse complète, et la
   page lit `location.search`.
2. **En-têtes.** La réponse passe par `withIsolationHeaders` comme toute réponse
   de même origine : COOP, COEP et `Cross-Origin-Resource-Policy` quand l'OS est
   isolé (ADR 0131). Une page isolée peut donc charger l'iframe, et la page de
   l'iframe est isolée à son tour (`SharedArrayBuffer`, WebGL dans un worker).
3. **Repli inchangé.** Une adresse sous `_content/` qu'aucune app installée ne
   tient (pas encore copiée, app retirée) suit le chemin habituel : fichier de la
   version s'il existe, sinon `index.html` pour une navigation.
4. **Hors ligne.** Le fichier vient du cache : la page de l'iframe s'ouvre sans
   réseau, comme les autres fichiers de l'app.
5. **Développement.** `service-worker.js` et le mode « apps installées
   seulement » ne changent pas : `installedAppAssets.respond` sert déjà toute
   requête que `handles` accepte, navigation comprise.
6. **Toute navigation.** La règle ne regarde pas si la navigation vient d'une
   iframe ou de la barre d'adresse : ouvrir
   `_content/<assemblage>/page.html` dans un onglet montre aussi la page, comme
   en développement et comme pour un fichier d'une app compilée dans l'OS.

## Conséquences

- Une app installée peut afficher ses propres pages HTML dans une iframe, avec
  des paramètres dans l'adresse, en développement comme une fois publiée, en
  ligne comme hors ligne. Générique : aucune liste d'apps ni de pages.
- La page de l'iframe tourne dans l'origine de l'OS, comme le code de l'app
  (ADR 0182, Conséquences) : pas de nouvelle frontière de confiance.
- La correction n'agit qu'à partir de la version de l'OS qui la contient
  (prochaine version : 3.4.0 ou 3.3.1, selon le choix de Benoit).
- Test : `TrimmedPublishTests.InstalledAppPages.cs` copie une page dans le cache
  des apps installées et l'ouvre dans une iframe, sur l'adresse publiée (en
  ligne puis hors ligne) et sur l'adresse de développement.

## Tâches

- [x] Servir le fichier du cache des apps installées avant le repli vers
      `index.html` (`service-worker.published.js`).
- [x] Tests E2E de la publication trimmée (deux adresses, hors ligne compris) ;
      `EXPECTED_TESTS` passe à 10 dans `build/publish-trimmed.sh`.
- [x] ADR 0206 : lien vers cette ADR ; piège ajouté à `pitfalls.md`.
- [x] Entrée de changement, `features.md`, mémoire.
