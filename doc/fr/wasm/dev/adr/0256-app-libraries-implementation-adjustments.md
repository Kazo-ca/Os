> [English version](../../../../en/wasm/dev/adr/0256-app-libraries-implementation-adjustments.md)

# ADR 0256 : bibliothèques d'app, ajustements de la mise en œuvre

## Statut

Accepté le 2026-11-12.

- **Chantier :** `feature/15-app-libraries-code` (série `feature/base`, code de
  l'ADR 0261).
- **Ajuste :** l'[ADR 0261](0261-app-libraries-and-shared-versions.md) §1 et §2.
  Ses décisions restent vraies : le paquet embarque les bibliothèques de l'app,
  une bibliothèque partagée a une copie par page, la compatibilité est la
  version majeure, une copie chargée n'est jamais remplacée à chaud. Cet ADR
  corrige deux phrases du §2 que le code ne pouvait pas tenir et fixe ce
  qu'elles laissaient ouvert.
- **Questions posées à Benoit** (série 17, questions 259 à 262, posées dans la
  PR du chantier). Réponses du 2026-11-12 : A aux quatre, avec une précision
  sur la première, reprise au §1.

## Contexte

En écrivant le code de l'ADR 0261, quatre points ne tenaient pas tels
qu'écrits ou n'étaient pas décidés :

- L'ADR 0261 §2 dit que l'auteur marque sa bibliothèque « par une propriété
  MSBuild de son paquet NuGet (`buildTransitive` qui pose
  `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` sur la référence) ». Mais
  le même paragraphe veut qu'une app puisse l'utiliser « par référence de
  projet, dll ou nuget » : une DLL référencée n'a pas de `buildTransitive`, et
  un élément MSBuild posé dans le projet de l'app laisserait l'app marquer une
  bibliothèque qui ne l'est pas, ce que le §2 interdit.
- L'ADR 0261 §2 dit que, sans Feature, deux versions majeures « coexistent
  (chacune chargée pour ses apps, séparément) ». Toutes les apps installées
  sont chargées dans le contexte de chargement par défaut (ADR 0182, et
  l'option A écartée par l'ADR 0261) : il ne tient qu'une assemblée par nom
  simple.
- Rien ne disait quoi faire de deux apps qui embarquent chacune une
  bibliothèque **non partagée** du même nom simple (deux versions d'un même
  paquet NuGet).
- La règle 1 du §2 parle d'une « Feature installée qui fournit une interface
  de cette bibliothèque (ADR 0262) » ; l'ADR 0262 n'est pas encore codée.

## Décision

### 1. Une bibliothèque ordinaire accompagne l'app, rien de plus

Précision de Benoit (2026-11-12, réponse à la question 259) : « Donc une
librairie ou une dll référencée par l'application n'est gérée ni par Kazo OS
ni par le market place. Elle accompagne simplement l'application. »

- Une bibliothèque **non marquée partagée** est un fichier du paquet de l'app.
  L'OS vérifie son intégrité comme tout fichier du paquet (SHA-256, et la
  liste `libraries` dit ce que l'assemblage dit de lui-même), la vérifie
  contre le runtime comme l'assemblage de l'app (ADR 0242), et la charge
  avant l'app. C'est tout.
- Elle n'a **pas** d'entrée de catalogue ni de fiche dans le Store, pas de
  résolution de dépendance, pas de comparaison de versions entre apps, pas de
  proposition de redémarrage, pas de refus d'installation fondé sur sa
  version.
- **Seule** une bibliothèque que son auteur a marquée partagée (§2) entre dans
  les règles de l'ADR 0261 §2 et §3 : une copie par page, choix de la copie,
  refus `apps.package.library-major-mismatch`, « Redémarrer pour terminer ».

### 2. La marque partagée est dans l'assemblage (question 259, réponse A)

Remplace la parenthèse de l'ADR 0261 §2 sur `buildTransitive`.

- L'auteur pose `<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` dans **le
  projet de la bibliothèque**. `Kazo.Os.Apps.targets` en fait
  `[assembly: AssemblyMetadata("Kazo.Os.SharedLibrary", "true")]`. Un projet
  qui n'importe pas ce fichier (un paquet de contrats sans dépendance à
  Kazo OS) écrit lui-même l'élément
  `<AssemblyMetadata Include="Kazo.Os.SharedLibrary" Value="true" />`.
- La marque suit la bibliothèque, qu'une app la référence par projet, par DLL
  ou par paquet NuGet. `KazoOsPack` la lit dans l'assemblage et la recopie
  dans `shared` de `kazoos-package.json` ; `AppPackage.Read` refuse un paquet
  dont `shared` ne dit pas ce que dit l'assemblage
  (`apps.package.bad-description`).
- L'app ne déclare rien et ne peut pas marquer une bibliothèque.

### 3. Deux versions majeures sans Feature : installées toutes les deux, une par page (question 260, réponse A)

Remplace « chacune chargée pour ses apps, séparément » de l'ADR 0261 §2.

- Entre apps, sans Feature, deux versions majeures d'une bibliothèque partagée
  **s'installent toutes les deux** (réponse 1 A de l'ADR 0261, inchangée).
- Une seule est dans la page à la fois : la première chargée. L'app de l'autre
  version majeure est servie par la copie chargée, une ligne de
  `/var/log/syslog` le dit, et l'app est montrée « Redémarrer pour terminer »
  (ADR 0261 §3). Après le redémarrage, la première app lancée décide de
  nouveau.
- Un contexte de chargement à part pour les apps de l'autre version majeure
  reste possible plus tard ; il demande de revoir le chargement unifié de
  l'ADR 0182 et de le prouver dans l'OS rogné.

### 4. Deux bibliothèques non partagées du même nom simple (question 261, réponse A)

- La première chargée sert les deux apps. Quand la version de la seconde
  diffère, une ligne de `/var/log/syslog` le dit
  (`libraries: <nom> <version> of <app> not loaded, <version> is already in
  the page and serves it`).
- Rien d'autre : pas de refus à l'installation, pas de redémarrage proposé
  (§1). Une app qui appelle un membre absent de la copie chargée reçoit
  l'erreur dans sa fenêtre (ADR 0238), jamais l'OS entier.

### 5. Fournisseur d'une bibliothèque partagée avant l'ADR 0262 (question 262, réponse A)

- Tant que les Features ne déclarent pas les services qu'elles fournissent
  (ADR 0262), le fournisseur d'une bibliothèque partagée est **toute Feature
  installée qui l'embarque** ; quand plusieurs l'embarquent, celle qui a la
  version la plus haute.
- Le code de l'ADR 0262 restreindra la règle aux Features qui déclarent
  fournir une interface de la bibliothèque (`InstalledLibraries.FindProvider`).

### 6. `apps.package.library-reserved` protège l'OS

- Le refus d'une bibliothèque qui porte le nom d'un assemblage de l'OS, du
  runtime .NET ou d'une app intégrée (ADR 0261 §1) vaut pour toute
  bibliothèque, partagée ou non. Ce n'est pas une gestion de la bibliothèque
  (§1) : c'est l'OS qui refuse qu'un paquet remplace un de ses assemblages,
  comme il refuse un paquet qui prend l'identifiant d'une app intégrée
  (`apps.package.built-in`). Il ne regarde que le nom, jamais la version.
- Une bibliothèque déjà chargée depuis un autre paquet n'est pas un assemblage
  de l'OS : elle ne déclenche pas ce refus.

### 7. Autres précisions de mise en œuvre

- **Ce que l'OS apporte**, pour `KazoOsPack`, est un motif de noms
  d'assemblage (propriété `KazoOsProvidedAssemblyPattern` : `Kazo.Os.*`,
  `System*`, `Microsoft.*`, `MudBlazor*`, `netstandard`, `mscorlib`,
  `WindowsBase`), modifiable dans le projet de l'app. Le refus du §6 est le
  filet à l'installation.
- **Redémarrage proposé** (ADR 0261 §3) seulement quand il change quelque
  chose pour l'app : la copie chargée n'a pas ce que l'app attend (autre
  version majeure, ou plus ancienne que celle de l'app) **et** la copie
  choisie est autre. Une copie chargée plus récente de la même version
  majeure sert l'app telle quelle.
- **« Apps installées et activées »** (ADR 0261 §2, règle 2) : les apps
  installées présentes dans le catalogue en cours. Les copies comparées à
  l'installation sont celles de ce catalogue.
- **Chargement** : toutes les bibliothèques de l'app sont chargées avant son
  assemblage ; aucune résolution à la demande
  (`AssemblyLoadContext.Default.Resolving`) n'est ajoutée.
- **Démarrage** : `libraries` est lue dans le `kazoos-package.json` de chaque
  app installée, une lecture de plus par app ; une liste invalide écarte l'app
  avec un avertissement.

## Options écartées

- **Marque par `buildTransitive` seulement** (question 259, B) : les
  références par projet et par DLL ne sont pas couvertes.
- **Marque dans l'assemblage et élément `buildTransitive` accepté** (259, C) :
  l'app pourrait marquer une bibliothèque.
- **Contexte de chargement à part pour l'autre version majeure** (260, B) :
  chantier à part, voir §3. **Refuser la seconde installation** (260, C) :
  contredit la réponse 1 A de l'ADR 0261.
- **Refuser la seconde app** qui embarque une bibliothèque non partagée du
  même nom (261, B) : ce serait gérer une bibliothèque ordinaire (§1).
  **Un contexte par app** (261, C) : option A écartée par l'ADR 0261.
- **Aucun fournisseur avant l'ADR 0262** (262, B) : une vieille copie d'une
  app pourrait alors servir la Feature qui apporte le contrat.

## Conséquences

- `Os-x86.Interface` pose la marque dans son propre projet ; aucune app
  cliente n'a rien à déclarer.
- Les docs disent qu'une bibliothèque ordinaire accompagne l'app :
  [`app-installation.md`](../app-installation.md#bibliothèques-dapp-et-versions-partagées-adr-0261),
  [`app-repository.md`](../../framework/app-repository.md#bibliothèques-de-lapp).
- Tests : une bibliothèque non partagée n'est ni comparée à l'installation ni
  cause d'un redémarrage (`InstalledLibrariesTests`), en plus de ceux de
  l'ADR 0261.

## Références

- ADR 0182, 0222, 0238, 0242, 0260, 0261, 0262.
- Issue [#419](https://github.com/Kazo-ca/Os/issues/419).
