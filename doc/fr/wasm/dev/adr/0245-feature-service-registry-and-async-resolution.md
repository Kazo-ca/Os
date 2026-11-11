> [English version](../../../../en/wasm/dev/adr/0245-feature-service-registry-and-async-resolution.md)

# ADR 0245 : registre d'interfaces des Features et résolution asynchrone (`KazoFeatures.GetServiceAsync<T>()`, null si absent)

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : décision 7
(« l'OS retourne null si la fonctionnalité n'est pas installée »), 15 (« comme
`GetService<T>()` avec le DI d'ASP.NET »), D10, D31, D36 et 158 A (`Ix86` dans
`Os-x86.Interface`, `x86-Lib` l'implémente, la Feature enregistre l'instance)
de la conception d'Os-x86.

- **Complète :** l'[ADR 0243](0243-feature-app-type.md) (seule une Feature
  fournit une interface) et l'[ADR 0244](0244-app-libraries-and-shared-versions.md)
  (une copie des contrats par page).
- **Ne change pas :** les contrats de composants (ADR 0063, 0148, 0156), qui
  restent la voie pour un **rendu** fourni ; cette ADR couvre un **service**
  sans interface graphique.
- **Pour :** `Ix86` ; BoxedWine et `BoxedWine.Lib` qui l'appellent ; l'app
  Terminal de Kazo, seule app système qui résout une interface de Feature
  (ADR 0252 §1), par la même façade.

## Contexte

- Il n'existe **aucun registre de services entre apps** : les apps installées
  n'ajoutent rien au conteneur d'injection (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:343-391`),
  et `[Inject]` d'un type hors SDK est refusé au build de l'app
  (`KOSAPP0031`, `Tools/Kazo.Os.Tools.ManifestGenerator/SdkBoundaryInspector.cs:34-47`).
- Les seuls mécanismes de communication sont les intents (ADR 0096 ;
  `IAppIntentGateway`, `Shared/Kazo.Os.Simulation.Abstractions/Gateways/AppGatewayContracts.cs:500-587`),
  sans réponse, charge JSON de 64 Kio ; le bus de sujets (ADR 0040) ; et les
  contrats de composants (ADR 0148), qui livrent un `RenderFragment` et un
  contrôleur à une app **fenêtrée**, exécutés avec les droits de l'hôte
  (ADR 0148 §4). Aucun ne donne à une app un objet typé `Ix86` sur lequel
  appeler `LaunchAsync(...)` avec un résultat.
- L'ADR 0148 a écarté un service DI global parce qu'il forcerait le chargement
  du fournisseur au démarrage ; le code d'une app n'est chargé qu'au premier
  usage (`AppLifecycleOrchestrator.EnsureDescriptorAsync`). Un
  `GetService<T>()` **synchrone** ne peut donc pas charger un fournisseur
  absent de la mémoire : la résolution doit être asynchrone.
- Une app qui déclare Os-x86 en dépendance facultative avec une version
  maximale est **retirée du catalogue** quand une version hors plage est
  installée (`Platform/Kazo.Os.Platform.Core/AppCatalog.cs:508-522`,
  `catalog.dependency.incompatible`, même pour `Optional`) : elle ne reçoit
  pas null, elle disparaît.

## Décision

### 1. Une Feature fournit un service

- `FeatureDefinition.ProvidedServices` liste des fournitures
  `(contrat, implémentation)` : le **type du contrat** (une interface d'une
  bibliothèque partagée, `OsX86.Interface.Ix86`) et le **type qui
  l'implémente** dans un assemblage de la Feature (`OsX86.X86Service`).
  Le manifeste les écrit en noms de types qualifiés d'assemblage.
- **Un fournisseur par contrat** (D35) : deux Features installées qui
  fournissent le même contrat → la seconde est refusée à l'installation
  (`apps.package.service-already-provided`, nom de l'autre Feature).
- L'implémentation est **instanciée au premier besoin**, une fois par page,
  dans le **contexte d'exécution de la Feature** (son identité, ses
  permissions : l'ensemble de l'ADR 0243 §3, restreint à l'enfant déclaré
  `ServiceEntryPoint` s'il y en a un), jamais dans celui de l'appelant. Elle
  reçoit un `IFeatureServiceContext` (contexte de la Feature, identité de
  l'appelant pour l'ADR 0253, jeton d'arrêt de la page).
- Une app ordinaire ne peut rien fournir (ADR 0243 §5) ; toute app, système
  comprise, peut consommer (§2 ; l'app Terminal, ADR 0252 §1).

### 2. Une app consomme par `KazoFeatures`

Façade statique du SDK, dans le style de `KazoApps` et `KazoUser`
(`Shared/Kazo.Os.AppSdk/Apps/KazoApps.cs:20`, `KazoUser.cs:24`), derrière
une passerelle `IAppFeatureGateway` du contexte d'exécution :

```csharp
public static class KazoFeatures
{
    public static ValueTask<T?> GetServiceAsync<T>(CancellationToken ct = default) where T : class;
    public static bool IsAvailable<T>() where T : class;
    public static ValueTask<KazoFeatureServiceStatus> GetStatusAsync<T>(CancellationToken ct = default) where T : class;
    public static event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed;
}
```

- `GetServiceAsync<T>()` rend **l'instance du fournisseur**, ou **null** s'il
  n'y a aucun fournisseur utilisable : Feature absente, désactivée (ADR 0048),
  hors de la plage de version que l'app déclare (§3), ou dont le code ne se
  charge pas. Le null **n'est jamais mis en cache** : l'appel suivant
  réessaie (une Feature installée entre-temps, ADR 0214 §1, est trouvée).
- Une instance rendue est la même pour tous les appelants de la page
  (singleton par page) ; l'app ne la garde pas au-delà de son usage immédiat
  (`Changed` prévient d'une désactivation ou d'un retrait, et les appels
  suivants rendent alors null).
- `IsAvailable<T>()` est **synchrone** : vrai si un fournisseur utilisable
  est installé, sans le charger ; sert à afficher ou cacher un bouton.
- `GetStatusAsync<T>()` rend la raison : `Available`, `NotInstalled`,
  `Disabled`, `VersionMismatch(installée, demandée)`, `LoadFailed(code)`,
  `RestartRequired` (copie plus ancienne chargée, ADR 0244 §3),
  `NotDeclared` (§3). Une app qui veut proposer l'installation ouvre le Store
  sur la Feature (`KazoApps.RequestInstallAsync`, ADR 0248).
- **Permission manquante = exception, pas null** : la résolution ne vérifie
  aucune permission de l'appelant ; c'est chaque opération du contrat qui
  exige les siennes (ADR 0246) et lève `UnauthorizedAccessException`, comme
  les passerelles du SDK. Une app apprend ainsi qu'il lui manque une
  permission, pas que la Feature est absente.

### 3. « Référencer la fonctionnalité » = une dépendance de manifeste

- Une app qui appelle `GetServiceAsync<T>()` **déclare** la Feature dans
  `Dependencies` (`AppDependencyManifest`, `Shared/Kazo.Os.App.Abstractions/AppManifest.cs:203-212`),
  obligatoire ou facultative (`Optional = true`), avec sa plage de versions.
  Le générateur l'exige (`KOSAPP0034` si un type de contrat partagé est
  utilisé sans dépendance vers une Feature qui le fournit… ce qu'il ne peut
  pas savoir à la compilation : il exige donc seulement qu'une dépendance
  vers **une** Feature existe) ; à l'exécution, un appel sans dépendance
  déclarée rend `NotDeclared` et null.
- **Dépendance facultative hors plage = absente, pas retirée du catalogue** :
  `AppCatalog.ValidateDependencies` ne refuse plus une app pour une
  dépendance `Optional` hors plage (:508-522) ; `GetServiceAsync<T>()` rend
  null avec `VersionMismatch`. Une dépendance obligatoire hors plage garde
  le comportement actuel (`catalog.dependency.incompatible`).
- La version de la Feature et la version de ses contrats sont liées par
  l'ADR 0244 (majeure commune) ; la plage de la dépendance porte sur la
  version de la Feature.

### 4. Chargement et redémarrage

- Premier `GetServiceAsync<T>()` : l'OS charge l'assemblage de la Feature
  (comme `EnsureDescriptorAsync`), choisit la copie des contrats
  (ADR 0244 §2), instancie l'implémentation, puis rend l'instance. Si la
  copie chargée est plus ancienne que celle de la Feature : `RestartRequired`
  et null, avec la proposition de redémarrer de l'ADR 0243 §6.
- Un fournisseur dont le constructeur lève : `LoadFailed`, journal dans
  `/var/log/apps/<feature>.log`, null ; réessai au prochain appel.

## Options écartées

- **A. Un service DI global enregistré au démarrage** : charge le fournisseur
  au boot, refusé par l'ADR 0148 ; et `[Inject]` hors SDK reste interdit.
- **B. Les intents (`SendIntentAsync`) comme seul canal** : sans réponse,
  JSON de 64 Kio, pas de type ; `Ix86` a des opérations à résultat (lancer et
  suivre un programme, détecter un format, lister les VM).
- **C. Un contrat de composant (`kazoos.x86`)** : réservé aux apps fenêtrées,
  livre un rendu, s'exécute avec les droits de l'hôte (ADR 0148 §4) : l'inverse
  de ce qu'une Feature qui applique des permissions exige.
- **D. Mettre le null en cache** : une Feature installée à chaud ne serait
  jamais trouvée avant un redémarrage, contre l'ADR 0214.
- **E. Rendre null quand une permission manque** : l'app ne distinguerait pas
  « pas installée » de « pas permise » ; D31 dit « permission manquante =
  exception ».

## Conséquences

- SDK : `KazoFeatures`, `IAppFeatureGateway`, `KazoFeatureServiceStatus`,
  `IFeatureServiceContext` ; `FeatureDefinition.ProvidedServices` ;
  `PublicAPI.Unshipped.txt`, fixture `sdk-compat`, version mineure du SDK.
- Plateforme : registre des fournisseurs dans `AppCatalog` (lu des
  manifestes au démarrage et à chaud), `FeatureServiceResolver` (chargement,
  contexte de la Feature, instance par page), `AppCatalog.ValidateDependencies`
  (dépendance facultative hors plage), code `apps.package.service-already-provided`.
- Docs : nouveau `framework/feature-services.md`,
  [`app-model.md`](../../framework/app-model.md), `features.md`, entrée
  `changes/unreleased`.
- Tests : Feature absente → null et `NotInstalled` ; installée à chaud → trouvée
  au deuxième appel sans redémarrage ; désactivée → null et `Changed` ;
  facultative hors plage → app encore au catalogue, `VersionMismatch` ; deux
  fournisseurs → refus ; opération sans permission → exception ; fournisseur
  qui lève → `LoadFailed` et réessai.

## Questions posées à Benoit

1. **Forme de l'API côté app ?**
   - A) **(Recommandé)** La façade statique `KazoFeatures` (comme `KazoApps`,
     `KazoUser`) sur une passerelle du contexte ; testable par le contexte.
   - B) Une passerelle injectée seulement (`IAppExecutionContext.Features`),
     sans façade.
2. **Dans quel contexte s'exécute l'implémentation du service ?**
   - A) **(Recommandé)** Celui de la Feature (ses permissions), l'identité de
     l'appelant passée pour le journal (ADR 0253) : une app ne gagne jamais
     les permissions de la Feature, et la Feature vérifie les permissions de
     l'appelant à chaque opération (ADR 0246).
   - B) Celui de l'appelant (comme un composant, ADR 0148 §4) : simple, mais
     une Feature de sécurité ne peut pas y faire confiance.

Réponses du mainteneur (2026-11-11) : 1 A, 2 A.
