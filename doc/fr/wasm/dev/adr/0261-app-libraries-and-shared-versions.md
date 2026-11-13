> [English version](../../../../en/wasm/dev/adr/0261-app-libraries-and-shared-versions.md)

# ADR 0261 : bibliothèques d'app dans le paquet (#419) et versions partagées entre apps

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les deux questions posées à Benoit ont reçu leur réponse le
2026-11-11 : « qui marque une bibliothèque comme partagée » (A, §2) et « deux
majeures sans Feature » (A, les deux coexistent) ; la décision les applique.
Décisions d'origine : D30 et D36 de la conception d'Os-x86 (séries 3 et 4,
2026-11-09) ; issue [#419](https://github.com/Kazo-ca/Os/issues/419) (`TODO`).

D'abord numérotée 0244 ; renumérotée 0261 le 2026-11-13 (choix de Benoit), car `main` avait publié l'ADR 0244 dans Kazo OS 3.3.1 ou 3.3.2.

- **Complète :** l'[ADR 0182](0182-install-apps-from-github.md) §5 (paquets,
  `lib/`, vérification des API), l'[ADR 0222](0222-installed-app-satellite-assemblies.md)
  (satellites des assemblages déclarés) et l'[ADR 0242](0242-framework-api-for-installed-apps.md)
  (vérification exacte des signatures).
- **Remplace :** la troisième « limite de la première version » de
  l'ADR 0182 (« les dépendances d'une app autres que le SDK ne sont pas
  emportées »).
- **Pour :** `Os-x86.Interface`, le paquet NuGet de contrats d'Os-x86
  (`Ix86` et ses types), embarqué dans chaque app cliente (BoxedWine,
  `BoxedWine.Lib`) ; et toute app qui a ses propres bibliothèques (IRC et
  `Kazo.Irc.Protocol`, #419).

## Contexte

- Un paquet `.kazoapp` ne contient que les assemblages que le manifeste
  **déclare** : points d'entrée et composants
  (`Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackageWriter.cs:53-67`,
  `AppPackage.GetDeclaredAssemblies` :380-391). Une bibliothèque propre à
  l'app (`ProjectReference`, paquet NuGet qui n'est pas fourni par l'OS) n'y
  est pas ; l'installation refuse alors l'app pour une « API que cet OS n'a
  pas » (`AppPackageApiCheck.cs:173-180`, code `apps.package.missing-api` dans
  `AppPackageInstaller.cs:647-654`). C'est l'issue #419 ; IRC contourne en
  compilant son protocole dans son assemblage principal.
- Le chargeur réutilise la **première assemblée chargée sous un nom simple**,
  sans regarder sa version (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:300-306`),
  dans le contexte de chargement par défaut (:316). Si deux apps embarquaient
  `Os-x86.Interface` en deux versions, la première lancée imposerait la
  sienne à l'autre pour toute la durée de la page, et une vieille copie
  pourrait masquer celle de la Feature.
- Le modèle d'Os-x86 (D30, D36) veut que chaque app cliente **embarque** les
  contrats (pour que `GetServiceAsync<Ix86>()` compile et rende null quand
  Os-x86 est absent, ADR 0262) et qu'une seule copie, **la plus récente
  compatible**, soit chargée par page.
- Le SDK (`Kazo.Os.AppSdk*`, `Kazo.Os.App.Abstractions`) reste fourni par
  l'OS (ADR 0019) ; il n'est pas une bibliothèque d'app, et la règle
  `KOSAPP0030` (`build/Kazo.Os.Apps.targets:401-430`) continue d'interdire
  qu'un paquet y touche.

## Décision

### 1. Le paquet embarque les bibliothèques de l'app

- `KazoOsPack` met dans `lib/` les assemblages **propres de l'app** : sorties
  des `ProjectReference` et paquets NuGet qui ne sont pas fournis par l'OS
  (ni le runtime .NET, ni le SDK, ni MudBlazor et ses dépendances livrées
  par l'hôte), avec leurs satellites (ADR 0222).
- Le manifeste du paquet (`kazoos-package.json`) gagne une liste
  `libraries` : nom simple, version d'assemblage, SHA-256, et pour chaque
  bibliothèque un drapeau `shared` (§2). `AppPackage.Read` vérifie chaque
  fichier comme les assemblages déclarés ; `AppPackageApiCheck` les vérifie
  contre le runtime comme l'assemblage principal (ADR 0242).
- Une bibliothèque qui porte le nom d'un assemblage de l'OS ou du runtime
  est refusée (`apps.package.library-reserved`).
- Le chargeur charge les bibliothèques **avant** l'assemblage de l'app, ou à
  la résolution (`AssemblyLoadContext.Default.Resolving`) ; une bibliothèque
  non partagée ne sert qu'à son app.

### 2. Bibliothèques partagées : une copie par nom simple et par page

- Une bibliothèque marquée `shared` est un **contrat partagé** : plusieurs
  apps l'embarquent, une seule copie est chargée. **C'est l'auteur de la
  bibliothèque qui la marque** (réponse du mainteneur, 2026-11-11), par une
  propriété MSBuild de son paquet NuGet (`buildTransitive` qui pose
  `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` sur la référence), ce
  qu'`Os-x86.Interface` fait ; une app ne peut pas rendre partagée une
  bibliothèque qui ne l'est pas.
- Le marquage **n'impose rien à l'app** qui utilise la bibliothèque, en ses
  mots : « aucune restriction n'oblige de la fournir si elle est utilisée dans
  une app par référence de projet, dll ou nuget (si elle est incluse dans le
  package de l'application) ». L'app la référence comme elle veut
  (`ProjectReference`, DLL, `PackageReference`), `KazoOsPack` l'embarque dans
  `lib/` comme toute bibliothèque (§1), sans déclaration ni restriction
  supplémentaire ; le drapeau `shared` du manifeste du paquet est recopié de
  la propriété et ne sert qu'à Kazo, pour ne charger qu'une copie par
  version majeure.
- Règle de choix, appliquée **au premier besoin** (premier lancement d'une
  app qui en a besoin, ou première résolution de l'ADR 0262) :
  1. s'il existe une **Feature installée qui fournit** une interface de cette
     bibliothèque (ADR 0262), sa copie est chargée ;
  2. sinon, parmi les copies des apps installées et activées, **la plus
     récente compatible** : même version majeure que celle que demande l'app
     qui déclenche le chargement, version mineure la plus haute.
- **Compatibilité = version majeure** (`AssemblyVersion` majeure, qui est
  aussi la majeure du paquet NuGet). Une version mineure **ne fait
  qu'ajouter** des membres : règle d'édition des contrats partagés, vérifiée
  par le suivi d'API public des paquets (`PublicAPI.Shipped.txt`) de
  `Os-x86.Interface` comme pour le SDK.
- À l'installation d'une app, l'installeur compare chaque bibliothèque
  `shared` du paquet avec les copies déjà installées : une **majeure
  différente** de la copie que fournit une Feature installée est refusée
  (`apps.package.library-major-mismatch`, message avec les deux versions et
  le nom de la Feature) ; entre apps sans Feature, les majeures différentes
  coexistent (chacune chargée pour ses apps, séparément) tant qu'aucune
  Feature ne tranche.

### 3. Copie déjà chargée : redémarrage, jamais de remplacement à chaud

- .NET ne décharge pas une assemblée du contexte par défaut. Si une copie
  d'un contrat partagé est déjà chargée quand une Feature qui fournit une
  copie plus récente s'installe (ADR 0260 §6), ou quand une app qui exige
  une mineure plus haute est lancée, l'OS **propose de redémarrer** (« Redémarrer
  pour terminer », ADR 0212 §3) et, en attendant, sert la copie chargée :
  une app qui demande un membre absent de cette copie reçoit
  `MissingMethodException` dans sa fenêtre (ADR 0238), jamais l'OS entier.
- L'OS écrit dans `/var/log/syslog` la copie choisie et la raison
  (`libraries: Os-x86.Interface 1.2.0 loaded for ca.kazo.boxedwine (feature
  ca.kazo.x86)`).

### 4. Ce qui ne change pas

- Le SDK et le runtime restent ceux de l'OS ; `KOSAPP0030` tient.
- Une app qui n'a pas de bibliothèque partagée ne voit aucune différence ;
  une app qui n'en a aucune de propre non plus.

## Options écartées

- **A. Charger chaque app dans son propre `AssemblyLoadContext`** : isolerait
  les versions, mais deux copies d'un contrat seraient deux types différents
  pour .NET ; `GetServiceAsync<Ix86>()` ne pourrait jamais rendre l'instance
  de la Feature à une app chargée ailleurs ; et le chargement unifié de
  l'ADR 0182 (contexte par défaut, `IsLoaded` :250-255) serait à refaire.
- **B. Les contrats d'une Feature dans le SDK de l'OS** : chaque Feature
  tierce devrait attendre une version de l'OS pour changer son interface ;
  contraire à « Os-x86 est un dépôt à part ».
- **C. La copie de la Feature toujours, sans règle de majeure** : une app
  compilée contre une majeure plus récente que la Feature installée
  planterait au premier appel ; refuser à l'installation vaut mieux
  (ADR 0242, même raisonnement).
- **D. Première copie chargée gagne (état actuel)** : dépend de l'ordre de
  lancement ; une vieille copie masque la Feature.

## Conséquences

- `Tools/Kazo.Os.Tools.ManifestGenerator` et `KazoOsPack` : collecte des
  bibliothèques, drapeau `shared`, `libraries` dans `kazoos-package.json` ;
  `AppPackage`, `AppPackageApiCheck`, `AppPackageInstaller` (nouveaux codes
  `apps.package.library-reserved`, `apps.package.library-major-mismatch`) ;
  `InstalledAppsBoot` (choix de la copie, journal, redémarrage proposé).
- Le modèle `kazoos-solution` et [`app-repository.md`](../../framework/app-repository.md)
  documentent la propriété `KazoOsSharedLibrary` ; `Os-x86.Interface` la
  pose dans son paquet. Une app n'a **rien à déclarer** pour utiliser une
  bibliothèque partagée : la référencer suffit ; `KazoOsPack` recopie le
  drapeau.
- L'app IRC retire son contournement (#419, « Débloque »).
- Docs : [`app-installation.md`](../app-installation.md) (format du paquet),
  [`sdk-packages.md`](../../framework/sdk-packages.md), `features.md`, entrée
  `changes/unreleased`.
- Tests : paquet à deux assemblages installé et lancé dans l'OS rogné
  (`Tests/fixtures/installed-app`) ; deux apps avec deux mineures d'un
  contrat partagé → la plus haute chargée ; majeure différente de la Feature
  refusée ; copie déjà chargée → redémarrage proposé et journal.

## Questions posées à Benoit

1. **Sans Feature installée, deux apps avec deux majeures différentes d'un
   contrat partagé ?**
   - A) **(Recommandé)** Les deux coexistent (chacune chargée pour ses apps)
     : rien ne les relie tant qu'aucun fournisseur n'existe.
   - B) Refuser la seconde installation : une seule majeure par machine.

Réponse du mainteneur (2026-11-11) : 1 A.

Question réglée le 2026-11-11 : qui marque une bibliothèque comme partagée →
l'auteur, par la propriété MSBuild de son paquet, sans rien imposer à l'app
qui l'utilise (§2).
