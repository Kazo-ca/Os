> [English version](../../../en/wasm/framework/feature-services.md)

# Services des fonctionnalités : `KazoFeatures`

## Objet

Une fonctionnalité de l'OS (genre `Feature`, [ADR 0260](../dev/adr/0260-feature-app-type.md))
fournit des **services** aux autres apps : un objet typé, par exemple `Ix86`
d'Os-x86, sur lequel une app appelle des opérations qui rendent un résultat.
L'[ADR 0262](../dev/adr/0262-feature-service-registry-and-async-resolution.md)
fixe le modèle : la fonctionnalité **déclare** ses services, l'app les
**demande** par `KazoFeatures.GetServiceAsync<T>()`, comme `GetService<T>()`
de l'injection de dépendances d'ASP.NET, mais asynchrone (le code de la
fonctionnalité n'est chargé qu'au premier besoin), et l'OS rend `null` quand
la fonctionnalité est absente.

## Fournir un service (la fonctionnalité)

1. Le **contrat** est une interface d'une bibliothèque partagée
   (`<KazoOsSharedLibrary>true</KazoOsSharedLibrary>` dans son projet,
   [`app-repository.md`](app-repository.md#bibliothèque-partagée--kazoossharedlibrary)),
   par exemple `Os-x86.Interface`. Les apps clientes embarquent la même
   bibliothèque ; l'OS n'en charge qu'une copie par page.
2. L'**implémentation** est une classe de l'assemblage de la fonctionnalité,
   avec un constructeur public qui prend un `IFeatureServiceContext`, ou
   aucun paramètre.
3. La `FeatureDefinition` la déclare :

```csharp
public override IReadOnlyList<FeatureService> ProvidedServices =>
    [new(typeof(Ix86), typeof(X86Service))];

// Facultatif : les services tournent avec les permissions de cet enfant.
public override string? ServiceEntryPoint => "daemon";
```

Le générateur vérifie la déclaration (`KOSAPP0035` : contrat qui n'est pas
une interface d'un autre assemblage, implémentation qui ne l'implémente pas ou
qui n'a pas de constructeur utilisable, point d'entrée qui n'est pas un
enfant). Le paquet est refusé si le contrat n'est pas dans une bibliothèque
partagée du paquet (`apps.package.service-contract-not-shared`). **Un
fournisseur par contrat** : une seconde fonctionnalité qui fournit le même
contrat est refusée à l'installation (`apps.package.service-already-provided`).

```csharp
public sealed class X86Service(IFeatureServiceContext context) : Ix86
{
    public async Task<int> LaunchAsync(string image, CancellationToken cancellationToken)
    {
        // L'app qui appelle, pour vérifier SES permissions (ADR 0246).
        KazoFeatureCaller? caller = context.Caller;
        // Agir avec les permissions de la fonctionnalité, jamais celles de l'appelant.
        using (context.EnterFeature())
        {
            return await RunAsync(image, cancellationToken);
        }
    }
}
```

- L'instance est créée **au premier besoin, une fois par page**, dans le
  **contexte de la fonctionnalité** : un processus à elle, sous le compte
  `kazoos-svc`, avec les permissions de la fonctionnalité (ou de son enfant
  `ServiceEntryPoint`). Toutes les apps de la page reçoivent la même instance.
- `IFeatureServiceContext.Feature` est ce contexte ; `Caller` nomme l'app dont
  l'appel s'exécute (`null` dans le constructeur) ; `Stopping` est annulé à
  l'arrêt de la page.
- Un appel s'exécute sur le chemin de code de l'app appelante : une façade du
  SDK (`KazoFiles`, `KazoNet`…) appelée directement agit **pour l'appelant**.
  `EnterFeature()` rend le contexte de la fonctionnalité ambiant le temps
  d'un bloc ; `Caller` continue de nommer l'appelant.
- Une permission manquante de l'appelant est une **exception** de
  l'opération (`UnauthorizedAccessException`), jamais un `null` à la
  résolution.

## Consommer un service (l'app)

L'app déclare la fonctionnalité dans ses dépendances, facultative quand elle
fonctionne sans :

```csharp
public override IReadOnlyList<AppDependencyManifest> Dependencies =>
    [new("ca.kazo.x86", "1.0.0", MaximumVersion: "1.99.0", Optional: true)];
```

Le build exige une dépendance dès que l'app appelle `KazoFeatures`
(`KOSAPP0034`). Puis :

```csharp
Ix86? x86 = await KazoFeatures.GetServiceAsync<Ix86>();
if (x86 is null)
{
    KazoFeatureServiceStatus status = await KazoFeatures.GetStatusAsync<Ix86>();
    // NotInstalled, Disabled, VersionMismatch, LoadFailed, RestartRequired, NotDeclared
    return;
}

await x86.LaunchAsync("disk.img", cancellationToken);
```

| Membre | Rôle |
| --- | --- |
| `GetServiceAsync<T>()` | L'instance, ou `null`. Le `null` n'est **jamais mis en cache** : une fonctionnalité installée entre-temps est trouvée à l'appel suivant, sans redémarrage. |
| `IsAvailable<T>()` | Synchrone, sans rien charger : afficher ou cacher un bouton. |
| `GetStatusAsync<T>()` | La raison : `Available`, `NotInstalled`, `Disabled`, `VersionMismatch` (version installée, plage demandée), `LoadFailed` (code), `RestartRequired`, `NotDeclared`. |
| `Changed` | Une fonctionnalité qui fournit un contrat est apparue, désactivée ou retirée ; le gestionnaire tourne dans le contexte de l'app. |

- **Dépendance facultative hors plage** : l'app reste au catalogue et
  `GetServiceAsync<T>()` rend `null` avec `VersionMismatch`. Une dépendance
  obligatoire hors plage retire toujours l'app du catalogue
  (`catalog.dependency.incompatible`).
- **Fonctionnalité désactivée ou retirée** : l'instance est abandonnée,
  `Changed` est levé, les appels suivants rendent `null`. L'app ne garde pas
  l'instance au-delà de son usage immédiat.
- **Constructeur qui lève** : `LoadFailed` (`features.service.constructor-failed`),
  ligne dans `/var/log/apps/<fonctionnalité>.log`, nouvel essai à l'appel
  suivant.
- **Copie plus ancienne des contrats déjà chargée** : `RestartRequired`
  ([ADR 0261](../dev/adr/0261-app-libraries-and-shared-versions.md) §3).

## Architecture

- **SDK** (`Kazo.Os.AppSdk.Features`) : `KazoFeatures`, `IAppFeatureGateway`
  (propriété `IAppExecutionContext.Features`, qui répond « non installé » par
  défaut), `KazoFeatureServiceStatus`, `KazoFeatureServiceState`,
  `IFeatureServiceContext`, `KazoFeatureCaller`. `FeatureDefinition.ProvidedServices`
  et `ServiceEntryPoint`, `FeatureService` et `AppProvidedServiceManifest` sont
  dans `Kazo.Os.App.Abstractions`.
- **Plateforme** : `AppCatalog.GetServiceProviders` et `FindServiceProvider`
  (le registre, lu dans les manifestes du catalogue en cours, au démarrage et
  à chaud) ; `FeatureServiceResolver` (une instance par page : statut,
  chargement de l'assemblage par le chargeur paresseux, processus de la
  fonctionnalité par `AppLifecycleOrchestrator.StartFeatureService`) ;
  `AppFeatureGateway` (la passerelle de chaque app, qui répond pour son
  manifeste) ; `AppCatalog.ValidateDependencies` (dépendance facultative hors
  plage).
- **Bibliothèques partagées** : le fournisseur d'une bibliothèque partagée
  ([ADR 0261](../dev/adr/0261-app-libraries-and-shared-versions.md) §2, règle 1)
  est la fonctionnalité qui l'embarque et déclare fournir une de ses
  interfaces ; la règle provisoire de
  l'[ADR 0256](../dev/adr/0256-app-libraries-implementation-adjustments.md) §5
  est retirée.

## Tests

`FeatureServiceResolverTests`, `KazoFeaturesTests`, `FeatureServicePackageTests`,
`InstalledLibrariesTests` (Platform.Core) ; `FeatureServiceManifestTests` et les
fixtures de schéma `feature-provides-service.valid.json`,
`invalid-services-on-service.json` (App.Abstractions) ;
`FeatureServiceInspectorTests` (Tools.ManifestGenerator).

## Tâches

- [x] Déclaration (`ProvidedServices`, `ServiceEntryPoint`, `providedServices` du manifeste, schéma, validateur).
- [x] Vérifications du build (`KOSAPP0034`, `KOSAPP0035`) et du paquet (`service-contract-not-shared`, `service-already-provided`).
- [x] `KazoFeatures` et `IAppFeatureGateway` ; `FeatureServiceResolver` ; contexte de la fonctionnalité sous `kazoos-svc`.
- [x] Dépendance facultative hors plage gardée au catalogue.
- [x] Fournisseur d'une bibliothèque partagée restreint aux fonctionnalités qui en déclarent un contrat.
- [ ] Fixture `Tests/fixtures/sdk-compat` et version mineure du SDK, avec la publication.
