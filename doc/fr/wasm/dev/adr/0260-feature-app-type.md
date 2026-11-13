> [English version](../../../../en/wasm/dev/adr/0260-feature-app-type.md)

# ADR 0260 : le type d'app « Feature », une fonctionnalité facultative de l'OS installée au niveau système

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), première ADR du lot 1
de la série `feature/base`. Les questions posées à Benoit ont reçu leur
réponse le 2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont
consignées à la fin de cette ADR et la décision les applique. Décisions
d'origine, prises par Benoit dans la conception d'Os-x86 : D33, D37, D38, D39
(séries 3 et 4, 2026-11-09), 172 A puis 222 A (identifiants `ca.kazo.x86` et
`ca.kazo.boxedwine`, 2026-11-11).

D'abord numérotée 0243 ; renumérotée 0260 le 2026-11-13 (choix de Benoit), car `main` avait publié l'ADR 0243 dans Kazo OS 3.3.1 ou 3.3.2.

- **Complète :** l'[ADR 0047](0047-app-definition-and-generated-manifests.md)
  (`AppDefinition`, manifestes générés), l'[ADR 0048](0048-system-apps-and-uninstall.md)
  (apps système, désinstallation machine ou utilisateur) et
  l'[ADR 0182](0182-install-apps-from-github.md) (installation d'un paquet).
- **Ouvre :** les ADR 0261, 0262 et 0246 à 0255 de la même série, qui donnent à une Feature
  ses bibliothèques partagées, son registre d'interfaces, ses permissions,
  ses rôles, ses dépendances, ses pages des Paramètres, ses détecteurs de
  format et son relais.
- **Pour :** Os-x86 (`ca.kazo.x86`), qui fait tourner des machines
  virtuelles x86 dans le navigateur, et toute fonctionnalité du même genre.

## Contexte

- Os-x86 est, selon la décision 7 de sa conception, « une fonctionnalité
  facultative de l'OS, à la manière de WSL » : livrée par le Kazo OS Store,
  installée au niveau système par un administrateur, utilisée par d'autres
  apps (BoxedWine) par une interface que l'OS résout, et par le terminal
  (commande `x86`, programmes lancés par leur chemin). Elle n'a aucune app
  ordinaire : une commande, deux fenêtres de sécurité (permission réseau,
  page des règles) et des pages d'administration dans Paramètres.
- Le modèle d'app de Kazo OS a **un type par identifiant** : `AppManifest.Kind`
  (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:27`) vaut `Window`,
  `Terminal`, `Service` ou `Web` (`AppKind.cs:9-30`). Le validateur applique
  les règles du type au manifeste entier : un bloc `terminal` n'est permis
  que si `Kind == Terminal` (`AppManifestValidator.cs:295-316`,
  `manifest.terminal.forbidden`), les gestionnaires de fichiers que sur une app
  fenêtrée (`:318-321`, `manifest.fileHandlers.forbidden`).
- Un paquet `.kazoapp` porte **un seul manifeste** et donc un seul type
  (`Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackage.cs:47, :78-84,
  :190-194`). Os-x86 compterait donc au moins trois paquets, trois identités,
  trois écrans de permissions, sans lien entre eux.
- Le processus, l'état de service et la commande existent une seule fois par
  identifiant d'app ; le shell ne protège le nom d'une commande que pour les
  apps système (`AppCatalog.cs:343-374`, les apps système d'abord puis l'ordre
  d'activation, le perdant reçoit `catalog.command.duplicate`).
- L'installation « pour soi » (`~/.local/share/kazoos/apps`, ADR 0208) et
  l'installation système (`/usr/lib/kazoos/apps`, ADR 0182 §5) existent toutes
  deux ; rien n'oblige un paquet à n'exister qu'au niveau système.
- Rien dans le modèle ne distingue une app qui **fournit** quelque chose aux
  autres (une interface, des permissions, des rôles, un format exécutable, une
  page des Paramètres) d'une app ordinaire. Les contrats de composants
  (ADR 0148, 0156) sont le seul mécanisme de fourniture, réservé aux apps
  fenêtrées et aux contrôles de fichiers.

## Décision

### 1. Un nouveau genre d'app : `AppKind.Feature`

- `AppKind` gagne `Feature` (valeur JSON `feature`). Une Feature est une
  **unité d'installation, de mise à jour, de retrait, de présentation dans le
  Store et d'approbation des permissions**, avec une seule identité
  (`Id`, par exemple `ca.kazo.x86`), un seul manifeste, un seul paquet.
- Une Feature n'a pas de point d'entrée propre (`EntryPoint` est ignoré ; le
  générateur produit `KOSAPP0032` s'il est déclaré) : elle n'a ni fenêtre ni
  processus à elle. Ce qu'elle exécute, ce sont ses points d'entrée enfants.
- Dans la classe de définition, une Feature dérive de `FeatureDefinition`
  (`AppDefinition` spécialisée) : `Id`, `Name`, `Description`, `Version`,
  `PublisherId`, `Capabilities` (l'ensemble, §3), `Dependencies`, `EntryPoints`
  (§2), et les déclarations que les ADR suivantes ajoutent
  (`ProvidedServices`, `ProvidedCapabilities`, `Roles`, `BinaryFormats`).

### 2. Des points d'entrée enfants typés

- `FeatureDefinition.EntryPoints` liste des **enfants**, chacun avec un
  identifiant `<feature>.<suffixe>` (`ca.kazo.x86.cli`, `.permission`,
  `.rules`, `.admin`), un `Kind` ordinaire (`Terminal`, `Window`, `Service`,
  ou `SettingsPage` introduit par l'ADR 0249), son point d'entrée (type
  Blazor ou commande), ses options propres (`Terminal`, `Window`,
  `ServiceStartup`), et son **sous-ensemble de permissions** (§3).
- Le générateur de manifeste écrit **un manifeste par enfant** dans le paquet,
  à côté du manifeste de la Feature, avec `parent: "<feature>"`. Le validateur
  applique à chaque enfant les règles de son type (bloc `terminal` sur un
  enfant `Terminal`, options de fenêtre sur un enfant `Window`), et à la
  Feature les règles du genre `Feature` (pas de point d'entrée, pas de
  gestionnaire de fichiers, pas d'association d'extension : D64, Os-x86
  n'enregistre aucune association).
- Le catalogue expose chaque enfant comme une app à part entière pour tout ce
  qui est **exécution** : processus visible séparément dans `ps` et le
  Gestionnaire des tâches (nom de l'enfant, groupé sous la Feature), commande
  du terminal, fenêtre, service. Il expose la Feature pour tout ce qui est
  **identité** : Store, `kazopkg list`, Paramètres ▸ Applications, écran des
  permissions, activation et désactivation (ADR 0048 : désactiver la Feature
  désactive ses enfants), désinstallation (une seule opération).
- Un enfant ne peut pas être installé, activé ou retiré seul ; une app
  ordinaire ne peut pas déclarer `parent`.
- La commande d'un enfant `Terminal` d'une Feature passe **avant** celle d'une
  app ordinaire de même nom dans `AssignCommands` (après les apps système) :
  `x86` ne peut pas être masquée par une app installée ensuite. Deux Features
  qui déclarent le même nom : la plus ancienne gagne, l'autre reçoit
  `catalog.command.duplicate` et l'administrateur tranche en désactivant l'une.

### 3. Les permissions : en bloc, puis par point d'entrée

- `FeatureDefinition.Capabilities` déclare **l'ensemble** des permissions de
  la Feature. C'est cet ensemble que le Store, `kazopkg` et l'écran
  d'approbation montrent, **une fois**, à l'installation.
- Chaque enfant déclare son **sous-ensemble** (`Capabilities` de l'enfant,
  vide permis). Le validateur refuse une permission d'un enfant qui n'est pas
  dans l'ensemble (`manifest.feature.capability-outside-set`) et applique les
  règles de type à l'enfant (une page `Web` ne demande pas `filesystem.*`,
  ADR 0073).
- À l'exécution, le contexte d'un enfant n'a que son sous-ensemble. L'ensemble
  n'est jamais accordé à un processus.
- Le **consentement de l'administrateur à l'installation** (ADR 0182 §5 étape
  4, ADR 0208) vaut approbation machine des permissions dangereuses du
  catalogue de la Feature : l'OS écrit les lignes `allow` du fichier machine
  (ADR 0049 §4) à l'installation, pour la Feature et ses enfants, et l'écran
  d'approbation du premier lancement (ADR 0049 §5) ne revient pas. Le refus
  par usager (ADR 0049 §4, §9) reste possible, dans Paramètres ▸ Permissions.
  Les permissions **déclarées par la Feature elle-même** suivent l'ADR 0246.

### 4. Installation au niveau système seulement, par un administrateur

- Une Feature s'installe dans `/usr/lib/kazoos/apps/<id>/<version>/`, jamais
  dans `~/.local/share/kazoos/apps` : `kazopkg install --user` et
  l'installation personnelle du Store la refusent
  (`apps.package.feature-system-only`), avec le message « Une fonctionnalité
  s'installe pour toute la machine, par un administrateur ».
- Elle exige la même authentification d'administrateur que toute
  installation système (ADR 0182 §5 étape 4, `sudo kazopkg install`, ou
  l'agent d'authentification de l'OS).
- Mise à jour et retrait : mêmes règles. Retirer une Feature garde ses données
  (ADR 0048 §2 ; les données d'Os-x86 sont hors des Fichiers de Kazo) et
  avertit des apps qui en dépendent (ADR 0248).

### 5. Ce que seule une Feature peut faire

| Capacité du modèle | ADR | Refus pour une app ordinaire |
|---|---|---|
| Fournir une interface résolue par `KazoFeatures.GetServiceAsync<T>()` | 0262 | `manifest.feature.only` |
| Déclarer des permissions de fournisseur, dangereuses comprises | 0246 | `manifest.feature.only` |
| Déclarer des rôles | 0247 | `manifest.feature.only` |
| Fournir des pages à Paramètres ▸ Fonctionnalités | 0249 | `manifest.feature.only` |
| Déclarer des détecteurs de format (type d'un programme par son en-tête) | 0251 | `manifest.feature.only` |
| Donner une identité par flux au relais | 0255 | refusé à l'exécution |
| Dépendre d'une autre Feature (`Dependencies` vers un `Feature`) | 0248 | une app ordinaire le peut aussi (dépendance obligatoire ou facultative) |

Une app ordinaire qui déclare l'un de ces éléments est refusée par le
générateur (diagnostic `KOSAPP0033`) et par le validateur à l'installation.

### 6. Utilisable tout de suite après l'installation

- Une Feature ajoutée à chaud suit l'ADR 0214 §1 : ses enfants `Terminal` et
  `Window` sont lançables immédiatement, son service démarre au prochain
  démarrage, et son interface (ADR 0262) se résout dès l'installation.
- **Exception** : si une copie plus ancienne de ses contrats est déjà chargée
  dans la page (ADR 0261 §3), l'OS le dit et propose de redémarrer, comme
  « Redémarrer pour terminer » du Store (ADR 0212 §3).
- Mise à jour ou retrait : redémarrage proposé seulement si le code de la
  Feature est chargé (ADR 0214 §1.2).

### 7. Identifiants

- Identifiant d'une Feature : même forme que les apps (`a.b.c` en minuscules).
  Os-x86 prend `ca.kazo.x86` et ses enfants `ca.kazo.x86.cli` (commande
  `x86`), `ca.kazo.x86.permission` (fenêtre de permission réseau),
  `ca.kazo.x86.rules` (page des règles), `ca.kazo.x86.admin` (pages des
  Paramètres) ; l'app BoxedWine prend `ca.kazo.boxedwine` (172 A, préfixe
  `ca.kazo.` fixé par 222 A, comme `ca.kazo.irc`).
- Aucun code de Kazo ne réserve le préfixe `org.kazoos.` aux apps système
  (vérifié dans `AppManifestValidator.cs` et `AppCatalog.cs` : la qualité
  d'app système vient de la liste de confiance de l'ADR 0048 §1, pas du
  préfixe). Le mainteneur le garde ainsi (222 A) : `org.kazoos.` reste aux
  apps intégrées de ce dépôt, sans réservation dans le validateur (222 C non
  retenue).

## Options écartées

- **A. Trois paquets ordinaires** (une commande, une fenêtre, un service),
  liés par des dépendances : trois identités dans le Store et trois écrans de
  permissions pour une seule fonctionnalité ; rien n'empêche d'installer la
  moitié ; aucun des trois ne peut fournir une interface ni déclarer une
  permission.
- **B. Un paquet à plusieurs manifestes sans genre nouveau** : le paquet
  grouperait des apps, mais l'OS n'aurait toujours aucune notion d'unité
  d'installation ni de droits réservés ; le validateur, le Store et Paramètres
  continueraient à voir trois apps.
- **C. Une app fenêtrée avec des composants intégrables** (ADR 0148) pour tout
  fournir : un composant s'exécute avec les droits de l'app hôte (ADR 0148 §4),
  ce qui est l'inverse de ce qu'une fonctionnalité de sécurité (fenêtre de
  permission réseau) exige ; et une commande de terminal n'est pas un
  composant.
- **D. Installation personnelle permise** : une fonctionnalité qui déclare un
  détecteur de format, des permissions ou un rôle agit sur toute la machine ;
  deux usagers avec deux versions donneraient deux comportements du shell.

## Conséquences

- `Shared/Kazo.Os.App.Abstractions` : `AppKind.Feature`, `FeatureDefinition`,
  `AppManifest.Parent`, manifestes enfants dans le paquet
  (`kazoos-package.json` liste les manifestes), nouveaux diagnostics
  `KOSAPP0032`, `KOSAPP0033` et codes `manifest.feature.*`,
  `apps.package.feature-system-only` ; `PublicAPI.Unshipped.txt` et la fixture
  `Tests/fixtures/sdk-compat`.
- `AppCatalog` : enfants rattachés à leur Feature, priorité des commandes de
  Feature dans `AssignCommands`, activation et désactivation en bloc.
- `AppPackage`, `AppPackageInstaller`, `kazopkg`, Store : lecture de plusieurs
  manifestes, refus de l'installation personnelle, écran de permissions sur
  l'ensemble, `allow` machine écrit à l'installation.
- Paramètres ▸ Applications montre une Feature avec ses enfants ; la section
  « Fonctionnalités » est l'ADR 0249.
- Docs à écrire au code : [`app-model.md`](../../framework/app-model.md)
  (genre `Feature`, `FeatureDefinition`), [`app-installation.md`](../app-installation.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`, entrée
  `changes/unreleased`.
- Tests attendus : une Feature à trois enfants passe le générateur et le
  catalogue ; un enfant avec une permission hors ensemble est refusé ; une app
  ordinaire avec `parent` ou `ProvidedServices` est refusée ; `--user` refusé ;
  `x86` gagne sur une app installée ensuite ; désactiver la Feature retire ses
  enfants ; l'installation écrit les `allow` machine et aucune fenêtre
  d'approbation n'apparaît au premier lancement.
- Le code de cette ADR est le premier chantier de la série `feature/base`
  après la fusion des treize ADR ; aucun code avant.

## Questions posées à Benoit

1. **Le préfixe `org.kazoos.` pour Os-x86 et BoxedWine, qui ne sont pas des
   apps système de ce dépôt ?** Le code ne le réserve pas ; l'app du Store
   publiée par Kazo (IRC) utilise `ca.kazo.irc`.
   - A) **(Recommandé)** `ca.kazo.x86` et `ca.kazo.boxedwine` : le préfixe
     `org.kazoos.` reste aux apps intégrées de ce dépôt, comme pour IRC.
   - B) `org.kazoos.x86` et `org.kazoos.boxedwine` (172 A tel quel) : le
     préfixe dit « publié par Kazo » ; l'ADR 0048 §1 ne s'en sert pas.
   - C) Réserver `org.kazoos.` aux apps système dans le validateur et prendre A.
2. **Le consentement d'installation vaut-il approbation machine des
   permissions dangereuses du catalogue de la Feature (§3), ou la fenêtre du
   premier lancement reste-t-elle ?**
   - A) **(Recommandé)** Il vaut approbation : l'administrateur voit l'ensemble
     à l'installation (D39) et ne retape pas son mot de passe au premier
     lancement de `x86`.
   - B) Les deux : consentement à l'installation, puis fenêtre ADR 0049 §5 au
     premier lancement de chaque enfant qui a une permission dangereuse.
3. **Les manifestes enfants : un fichier par enfant dans le paquet
   (`manifest.json` + `entrypoints/<suffixe>.json`), ou un seul manifeste avec
   un tableau `entryPoints` ?**
   - A) **(Recommandé)** Un fichier par enfant, chacun validé comme un
     manifeste ordinaire de son type ; le code existant du validateur sert tel
     quel.
   - B) Un seul manifeste avec un tableau : moins de fichiers, mais un schéma
     nouveau et un validateur à réécrire.

Réponses du mainteneur (2026-11-11) : 1 A (222 A : `ca.kazo.x86` et
`ca.kazo.boxedwine`, appliqués dans toute la série ; `org.kazoos.` reste aux
apps intégrées de ce dépôt, sans réservation dans le validateur, C non
retenue), 2 A, 3 A.
