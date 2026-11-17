> [English version](../../../en/wasm/framework/feature-settings-pages.md)

# Pages de Paramètres ▸ Fonctionnalités

Une **fonctionnalité** (ADR 0260) n'a pas de fenêtre à elle. Ses pages
d'administration vivent dans Paramètres, section « Fonctionnalités »
([ADR 0249](../dev/adr/0249-settings-features-section.md), ajustée par
l'[ADR 0285](../dev/adr/0285-settings-features-section-adjustments.md)).

## Ce que voit l'usager

- Un groupe **Fonctionnalités**, après « Applications ». Il commence par
  **Fonctionnalités installées** : chaque Feature avec ses points d'entrée,
  les permissions et les rôles qu'elle déclare, les apps qui en dépendent, et
  ses actions (activer ou désactiver pour tous, détails, ouvrir dans le Store,
  comment la retirer). Sans Feature : « Aucune fonctionnalité. Le Kazo OS Store
  en propose. »
- Puis **une entrée par page** fournie par une Feature installée et activée,
  nommée « Os-x86 ▸ Machines virtuelles ». Une page qui exige un rôle n'apparaît
  qu'à ses détenteurs et aux administrateurs (ADR 0247, réponse 233 B).
- La page s'affiche dans la fenêtre de Paramètres, mais elle tourne dans **son
  propre processus** (enfant de Paramètres dans `ps`, `--type=settings-page`),
  avec son id et ses permissions. Une erreur de la page reste dans sa zone
  (« Os-x86 a rencontré un problème », *Réessayer*) ; Paramètres continue.

## Déclarer une page

Une page est un point d'entrée de genre `SettingsPage` d'une
`FeatureDefinition`. Son composant dérive de `KazoSettingsPageBase`
(`Kazo.Os.AppSdk.Blazor`).

```csharp
public sealed class X86Feature : FeatureDefinition
{
    public override string Id => "ca.kazo.x86";
    public override string Name => "Os-x86";
    public override string? CapabilityPrefix => "x86";
    public override IReadOnlyList<FeatureRole> Roles => [new("x86:vm-admin", "Administrateur des VM")];

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
    [
        new("admin", "Machines virtuelles", AppKind.SettingsPage, typeof(VmAdminPage))
        {
            RequiresRole = "x86:vm-admin",
            SettingsPage = new SettingsPageOptions { Order = 1, MobileSupported = false },
            Icons = ["icons/vm.svg"],
            Capabilities = ["x86.vm.use"],
        },
    ];
}
```

```razor
@inherits Kazo.Os.AppSdk.Blazor.KazoSettingsPageBase

<h3>@PageTitle</h3>
@* AppContext est le contexte de la page : les façades Kazo* agissent avec ses droits. *@
```

| Élément | Rôle |
| --- | --- |
| `AppKind.SettingsPage` | Réservé aux enfants d'une Feature (`manifest.feature.only`, `KOSAPP0033`). |
| `SettingsPageOptions.Order` | Place parmi les pages de la Feature (0 par défaut, puis par titre). |
| `SettingsPageOptions.MobileSupported` | `false` : la page n'apparaît que sur le bureau. |
| `Name`, `Icons` | Titre et icône de l'entrée du menu. |
| `RequiresRole` | Rôle de la Feature qui ouvre la page (ADR 0247 §2). |
| `Capabilities` | Sous-ensemble des permissions de la Feature que tient le processus de la page. |

Une page tourne sur le fil principal et peut ouvrir les dialogues de l'OS. Elle
ne reçoit **rien** de Paramètres (ni contexte, ni passerelle de fenêtre).
Ouvrir la page ne demande aucune authentification ; une opération sensible
appelle `KazoFeatureAuthentication.AuthenticateAsync` (ADR 0247 §3), dont la
fenêtre de l'OS s'affiche par-dessus Paramètres.

## Ouvrir une page depuis une app

```csharp
await KazoApps.ShowSettingsPageAsync("ca.kazo.x86.admin");
```

- Exige `apps.launch`. Paramètres s'ouvre (ou reçoit l'intention s'il est déjà
  ouvert) avec `--page=ca.kazo.x86.admin`.
- Sans le rôle, ou si la Feature est désactivée, Paramètres s'ouvre sur
  « Fonctionnalités installées » avec un message.
- Une page inconnue ou désactivée lève `KazoAppLaunchException`
  (`intent.show-settings-page.not-found` ou `.disabled`).
- Une page ne se lance jamais seule (`KazoApps.LaunchAsync` la refuse :
  `lifecycle.settings-page.not-launchable`).

## Architecture

| Pièce | Où | Rôle |
| --- | --- | --- |
| `IFeatureSettingsPages` | `Kazo.Os.AppSdk.Blazor.Settings` | Contrat injecté dans Paramètres : `GetPages`, `OpenAsync`, `Changed`. L'OS ne répond qu'à Paramètres. |
| `FeatureSettingsPages` | `Platform.Blazor/Settings` | Lit le catalogue à chaud, filtre (activation, rôle, mobile), démarre le processus de la page (`StartAuxiliaryAsync`) et rend son composant dans son contexte, sous `AppErrorBoundary` (*Réessayer* seulement). |
| `InstalledFeaturesPanel` | Paramètres | Page « Fonctionnalités installées » ; reprend la ligne d'installation d'`InstalledAppsPanel` et la page `AppDetailsPanel`. |
| `FeatureSettingsPageView` | Paramètres | Ouvre la page, l'arrête quand Paramètres la quitte, propose *Rouvrir* si l'OS l'a arrêtée. |
| `ShowSettingsPageIntent` | `AppIntentDispatcher` | Paramètres avec `--page=<id>`. |

## Liste des tâches

- [x] Genre `SettingsPage`, `SettingsPageOptions`, validateur, schéma, générateur.
- [x] `KazoSettingsPageBase`, `IFeatureSettingsPages`, `KazoApps.ShowSettingsPageAsync`.
- [x] `FeatureSettingsPages`, processus de page, rôle, mobile, barrière d'erreur.
- [x] Paramètres : groupe, « Fonctionnalités installées », pages, `--page=<id>`.
- [x] Tests : manifeste, plateforme, noyau, intention, Paramètres.
- [ ] Parcours complet avec une Feature réelle (Os-x86), question 286.
