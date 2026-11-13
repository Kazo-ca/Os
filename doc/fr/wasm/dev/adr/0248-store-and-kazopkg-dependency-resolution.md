> [English version](../../../../en/wasm/dev/adr/0248-store-and-kazopkg-dependency-resolution.md)

# ADR 0248 : résolution des dépendances par le Kazo OS Store et `kazopkg`

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : décision 7
(« installer BoxedWine installe Os-x86 en même temps »), D12 (« le Store et
kazopkg résolvent les dépendances, un seul écran de consentement, un appel du
SDK “demander l'installation” pour une dépendance facultative ; désinstaller
Os-x86 avertit et désactive les apps dépendantes ») et 72.10 (consignes du
2026-11-10 : « installer BoxedWine doit proposer d'installer Os-x86 si
nécessaire, avec l'accord d'un administrateur ; en cas de refus,
l'installation de BoxedWine s'arrête avec un message clair »).

- **Complète :** l'[ADR 0005](0005-deterministic-app-dependency-order.md)
  (ordre des dépendances), l'[ADR 0048](0048-system-apps-and-uninstall.md)
  §4 (activer une app active ses dépendances présentes),
  l'[ADR 0182](0182-install-apps-from-github.md) (`kazopkg install`),
  l'[ADR 0212](0212-hack-store-app-build.md) (app Store) et
  l'[ADR 0214](0214-hack-store-hot-add-signing-transactions.md) (transactions).
- **Pour :** l'app BoxedWine (`Dependencies` obligatoire vers Os-x86) et
  toute app qui dépend d'une Feature ou d'une autre app.

## Contexte

- Un manifeste déclare des dépendances avec une plage de versions et un
  drapeau `Optional` (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:91,
  :203-212`). Le catalogue refuse une app dont une dépendance obligatoire
  manque (`Platform/Kazo.Os.Platform.Core/AppCatalog.cs:487-503`,
  `catalog.dependency.missing`) ; pour une app installée, l'hôte l'écarte
  avec une ligne de journal (`OS/Kazo.Os.Ecosystem.Host/HostAppCatalog.cs:104-129`).
- **Rien n'installe une dépendance manquante** : ni l'installeur de paquets
  (`Apps/Packages/AppPackageInstaller.cs`, `PrepareAsync` :266, contrôles
  :630-657), ni `kazopkg` (`Apps/Commands/Kazo.Os.Commands/Kazopkg/`), ni le
  Store (`Apps/System/Kazo.Os.Apps.Store/`). Une installation personnelle dont
  la dépendance obligatoire manque **réussit**, puis l'app disparaît du
  catalogue avec une seule ligne de journal.
- **Retirer un paquet ne regarde pas qui en dépend**
  (`AppPackageInstaller.RemoveAsync` :716-753) : les dépendants tombent au
  prochain rechargement du catalogue. Seule la couche activer/désactiver de
  l'ADR 0048 connaît les dépendants (`AppsConfigDocument.ExpandDependents`
  :219-239, utilisé par `AppInstallationService.cs:143, 366-377`).
- Le Store s'ouvre sur une app par `--app=<appId>`
  (`StoreWindow.razor.cs:52, :98-102`) ; une app peut déjà l'appeler par
  `IAppIntentGateway.LaunchAsync`. Le catalogue du Store (`catalog.json`)
  ne liste pas les dépendances d'une entrée.
- Une Feature s'installe au niveau système seulement (ADR 0260 §4) : une
  app installée « pour soi » qui dépend d'une Feature a donc une dépendance
  qui exige un administrateur.

## Décision

### 1. L'installation résout les dépendances obligatoires

- Avant d'installer un paquet, l'installeur (`AppPackageInstaller`, pour le
  Store et `kazopkg`) construit le **plan** : le paquet demandé, puis chaque
  dépendance obligatoire (`Optional = false`) **absente ou hors plage**, de
  façon transitive, dans l'ordre de l'ADR 0005 (dépendances d'abord).
- Une dépendance est cherchée **dans les catalogues configurés** (sources de
  confiance de l'ADR 0208, catalogue officiel d'abord) par son `appId` ; la
  version choisie est la plus récente qui satisfait **toutes** les plages des
  paquets du plan et des apps installées qui en dépendent déjà. Pour
  `kazopkg install <dépôt GitHub>` (ADR 0182), une dépendance qui n'est dans
  aucun catalogue arrête l'installation avec son identifiant et la plage
  attendue (`apps.package.dependency-unresolved`) : `kazopkg` ne devine pas
  un dépôt.
- Une dépendance **déjà installée mais désactivée** (ADR 0048) est réactivée
  dans le plan, pas réinstallée.
- Le plan est **affiché avant tout téléchargement** : liste des paquets avec
  version, source, taille, et l'ensemble des permissions dangereuses de
  chacun (une Feature : son ensemble, ADR 0260 §3). Le Store le montre dans
  sa fenêtre de validation (`AppInstallApprovalOverlay`, ADR 0208) ;
  `kazopkg` l'écrit puis demande une confirmation (`--yes` la saute).
- **Un seul consentement** pour le plan entier : si un paquet du plan est une
  Feature ou exige l'installation système, l'authentification d'un
  administrateur est demandée **une fois**, pour le plan ; une installation
  personnelle du paquet demandé reste personnelle, sa dépendance Feature va
  au niveau système.
- **Refus ou échec = rien n'est installé** : le plan est une transaction
  (journal `.transaction` de l'ADR 0214 §3 étendu au plan) ; un refus de
  l'administrateur, un paquet refusé (signature, API, ADR 0182 §5) ou une
  coupure annulent tout, avec le message « L'installation de <app> est
  arrêtée : <raison> » ; jamais une app installée dont la dépendance manque.

### 2. Dépendance facultative : sur demande

- Une dépendance `Optional = true` n'entre pas dans le plan. L'app s'installe
  sans elle ; `KazoFeatures.GetServiceAsync<T>()` rend null (ADR 0262).
- Le SDK gagne `KazoApps.RequestInstallAsync(appId, ct)` (permission
  `apps.launch`, déjà normale) : ouvre le Store sur le paquet
  (`--app=<appId>`), ou, sans Store, un message « Installe <app> avec
  `kazopkg install <appId>` ». L'app n'installe jamais elle-même ; la
  résolution du §1 s'applique au paquet demandé.
- Le Store affiche sur la fiche d'une app ses dépendances (obligatoires et
  facultatives) et, pour chacune, « installée », « à installer (avec cette
  app) » ou « facultative ».

### 3. Retirer un paquet dont d'autres dépendent

- `kazopkg remove` et le Store listent les apps installées qui déclarent le
  paquet en dépendance **obligatoire** et avertissent : « <app1>, <app2>
  ne fonctionneront plus et seront désactivées ». Confirmation (ou `--yes`).
- Après le retrait, ces apps sont **désactivées** (ADR 0048, `apps.config`),
  pas retirées : leurs données et leur paquet restent ; réinstaller la
  dépendance les réactive (`AppInstallationService.ReinstallAsync` :244-256,
  `ExpandDependencies`). Les apps à dépendance facultative ne sont pas
  touchées (elles reçoivent null).
- Retirer une Feature garde ses données (ADR 0260 §4).

### 4. Mise à jour

- Mettre à jour un paquet dont la nouvelle version exige une dépendance plus
  récente suit le §1 (plan, un consentement). Mettre à jour une dépendance
  vers une version **hors de la plage** d'un dépendant installé est refusé
  avec la liste des dépendants et leur plage
  (`apps.package.dependency-conflict`) ; `--force` n'existe pas pour ce cas
  (le dépendant serait désactivé en silence).

### 5. Catalogue du Store

- `catalog.json` gagne, par entrée, `dependencies` (identifiant, plage,
  facultatif), recopié du manifeste à la publication (workflow
  `publish-packages.yml` du modèle `kazoos-solution`, ADR 0219) : le Store
  construit le plan sans télécharger chaque paquet. La CI du catalogue le
  vérifie (issue AppMarketPlace #4, même mécanisme). Sans ce champ (ancien
  catalogue), le Store télécharge le manifeste pour le lire.

## Options écartées

- **A. L'app installe sa dépendance elle-même** (appel du SDK « installer ») :
  une app ordinaire qui installe un paquet système est contraire à
  `apps.manage` (dangereuse) ; et le consentement ne serait plus unique.
- **B. Refuser toute installation dont une dépendance manque, sans la
  résoudre** : l'usager devrait trouver et installer Os-x86 avant BoxedWine ;
  72.10 demande l'inverse.
- **C. Un consentement par paquet du plan** : deux fenêtres pour BoxedWine
  (BoxedWine, puis Os-x86) ; D12 veut un seul écran.
- **D. Retirer aussi les dépendants** : perte des données et des réglages des
  apps ; désactiver suffit et se défait.

## Conséquences

- `AppPackageInstaller` : `BuildPlanAsync`, transaction sur le plan, codes
  `apps.package.dependency-unresolved`, `apps.package.dependency-conflict` ;
  `kazopkg install`/`remove`/`update` (affichage du plan, `--yes`) ; Store
  (plan dans `AppInstallApprovalOverlay`, dépendances sur la fiche) ;
  `KazoApps.RequestInstallAsync` (SDK, `PublicAPI.Unshipped.txt`) ;
  `catalog.json` (`dependencies`) et le workflow de publication du modèle.
- Docs : [`app-installation.md`](../app-installation.md),
  [`kazoos-store.md`](../plans/kazoos-store.md) (format du catalogue),
  [`app-repository.md`](../../framework/app-repository.md), `features.md`,
  entrée `changes/unreleased`.
- Tests : app + Feature absente → plan de deux paquets, un consentement,
  Feature au niveau système ; refus → rien installé ; dépendance introuvable →
  arrêt avec message ; facultative → installée sans, `RequestInstallAsync`
  ouvre le Store ; retrait → avertissement, dépendants désactivés,
  réinstallation les réactive ; mise à jour hors plage → refusée.

## Questions posées à Benoit

1. **Une dépendance obligatoire introuvable dans les catalogues configurés,
   pour `kazopkg install <dépôt GitHub>` ?**
   - A) **(Recommandé)** Arrêt avec l'identifiant et la plage ; l'usager
     ajoute une source ou installe la dépendance d'abord.
   - B) `kazopkg` cherche un dépôt GitHub du même éditeur (`Kazo-ca/Os-App-<nom>`)
     : pratique, mais une convention de nom devient une règle de sécurité.
2. **Une installation personnelle (`--user`) d'une app qui dépend d'une
   Feature ?**
   - A) **(Recommandé)** Permise : l'app est personnelle, la Feature va au
     niveau système avec l'authentification d'un administrateur dans le même
     consentement.
   - B) Refusée : une app qui dépend d'une Feature s'installe au niveau
     système.

Réponses du mainteneur (2026-11-11) : 1 A, 2 A.
