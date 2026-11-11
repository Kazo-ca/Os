> [English version](../../../../en/wasm/dev/adr/0247-feature-declared-roles.md)

# ADR 0247 : rôles déclarés par une Feature, authentification de l'OS par opération sensible, journal avec app, opération et demandeur

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : 140 B
(administration graphique dès le premier jalon, « un rôle qui permet de gérer
les VM »), 156 B (tes mots : « un vrai rôle Kazo. La fonctionnalité devra
avoir la possibilité de déclarer des rôles ») et 160 A (le rôle ouvre les
pages et les commandes ; chaque opération sensible demande en plus
l'authentification d'admin de l'OS ; « gérer les sous-systèmes » disparaît),
2026-11-11 ; réponse du mainteneur du 2026-11-11 : un administrateur porte
tous les rôles déclarés, implicitement (§1).

- **Complète :** l'[ADR 0013](0013-local-user-session.md) (usagers et
  sessions), l'[ADR 0042](0042-sudo-root-elevation.md) (élévation `sudo`),
  l'[ADR 0049](0049-dangerous-capabilities-and-admin-approval.md) §6
  (fenêtre sécurisée) et l'[ADR 0243](0243-feature-app-type.md) §5.
- **Pour :** le rôle d'Os-x86 qui gère les VM (pages VM et utilisateurs de
  Paramètres ▸ Fonctionnalités, ADR 0249 ; `sudo x86 admin`).

## Contexte

- Kazo OS connaît trois niveaux d'autorité : `User`, `Administrator`,
  `System` (`Shared/Kazo.Os.App.Abstractions/AppAuthority.cs:9-19`,
  `Satisfies` :24-33). Un usager a une `Authority` et des groupes Unix
  (`LocalUser.cs:147, :150-153`, `LocalGroup` :64) qui servent aux droits de
  fichiers. Il n'existe **ni rôle déclaré, ni `UserRole`**, ni rôle dans un
  manifeste.
- Toute opération d'administration exige une permission dangereuse **et**
  une authentification d'administrateur **dessinée par l'OS**, à chaque
  opération, même pour un administrateur (comptes :
  `Platform/Kazo.Os.Platform.Core/Administration/AppUserAccountsGateway.cs:71-73, 83-84, 142-144` ;
  sauvegarde : `AppSystemBackupGateway.cs:42, 68` ; permissions :
  `AppPermissionAdministrationGateway.cs:85, 94`). L'agent
  (`Elevation/AdministratorAuthenticationAgent.cs:16`) journalise `app`,
  `operation`, `requestedBy`, `admin`, `outcome` (:175-198) dans
  `/var/log/auth.log` ; le billet vit 30 s, à usage unique
  (`InMemoryElevationTicketStore.cs:14, 37-50`).
- `AdministratorGate` est **interne** (`Administration/AdministratorGate.cs:13`)
  et n'est donné qu'aux passerelles intégrées
  (`Execution/AppExecutionContextFactory.cs:473-488`) ; rien dans `Shared/`
  ne l'expose. Le seul chemin ouvert à une app est un dialogue **de l'app**
  qui recueille le mot de passe et le vérifie (`Shared/Kazo.Os.AppSdk.Blazor/WindowAppBase.cs:452-489`,
  `IAdminElevationVerifier` injectable, `Policy/ElevationContracts.cs:46`) :
  l'app voit le mot de passe, ce qui contredit l'écran sécurisé que les
  décisions 122 et D61 exigent pour Os-x86.
- `sudo` (ADR 0042) donne à une commande `IsRootElevated` et l'autorité
  `System` (`AppOperationContext.cs:58-59`) ; l'OS vérifie
  (`Shell/ShellHostPrompts.cs:41-59`) mais la saisie passe par les dialogues
  de l'app Terminal (`TerminalCredentialPrompt.cs:38-51`).
- En mode invité, `guest` est un administrateur sans mot de passe qui
  n'existe que tant qu'aucun compte n'est enregistré, et s'élève par simple
  confirmation (ADR 0187 §2 ; `Sessions/GuestAccountService.cs:99-100,
  126-129` ; `Elevation/AdminElevationVerifier.cs:36-48`).

## Décision

### 1. Une Feature déclare des rôles

`FeatureDefinition.Roles` liste des `FeatureRole` :

| Champ | Rôle |
|---|---|
| `Id` | `<préfixe>.<nom>` avec le préfixe de l'ADR 0246 : `x86.vm-admin` |
| `Title`, `Description` | affichés dans Paramètres ▸ Utilisateurs (localisés) |
| `Grants` | ce que le rôle **ouvre** : points d'entrée de la Feature (pages `SettingsPage`, commandes ou sous-commandes marquées `RequiresRole`), et permissions déclarées de la Feature qu'il accorde à l'usager pour ces points d'entrée |

- Un rôle est **attribué par un administrateur**, usager par usager, dans
  Paramètres ▸ Utilisateurs (nouvelle colonne « Rôles » par usager, avec
  l'authentification de l'OS comme tout changement de compte, ADR 0120) ou
  par `sudo kazorole grant <usager> <rôle>`. L'attribution est écrite dans
  `/etc/kazoos/roles.config` (`[roles]`, `x86.vm-admin=<guid>,<guid>` ;
  identifiants stables d'usager, jamais des noms, ADR 0169) et synchronisée
  comme les autres documents système.
- Un administrateur **porte tous les rôles déclarés, implicitement**
  (réponse du mainteneur, 2026-11-11) : l'attribution explicite ne concerne
  que les usagers ordinaires. `roles.config` ne liste jamais un
  administrateur ; Paramètres ▸ Utilisateurs montre ses rôles comme
  « implicites (administrateur) », sans case à cocher ; `kazorole grant` sur
  un administrateur ne change rien et le dit (code 0). Un compte qui perd
  l'autorité `Administrator` perd les rôles implicites et doit se les faire
  attribuer comme un usager ordinaire. Le mode invité (ADR 0187) : `guest`
  est administrateur, donc porte tous les rôles.
- Le rôle est lu par `IAppExecutionContext.Roles` (liste d'identifiants) et
  `KazoUser.Current.HasRole("x86.vm-admin")` ; pour un administrateur,
  `Roles` liste tous les rôles des Features installées et `HasRole` rend
  vrai pour chacun. Retirer la Feature retire le
  rôle de l'affichage ; les lignes restent dans `roles.config` et reviennent
  avec la Feature.

### 2. Le rôle ouvre ; l'OS authentifie chaque opération sensible

- Un point d'entrée marqué `RequiresRole = "x86.vm-admin"` n'est **visible et
  lançable** que par un usager qui porte le rôle : page absente de
  Paramètres ▸ Fonctionnalités (ADR 0249), sous-commande refusée avec
  « rôle requis » (code 126) au terminal. Le rôle **n'accorde aucune
  autorité** `Administrator` : l'usager reste `User` partout ailleurs.
- Une **opération sensible** (régler, supprimer, partager une VM commune ;
  importer une image partagée ; changer une limite, une règle imposée, un
  refus machine : 124.1, 124.4, 124.5 d'Os-x86) demande **en plus**
  l'authentification de l'OS, **à chaque opération**, comme les passerelles
  d'administration de Kazo. L'ADR ne liste pas les opérations : la Feature
  les marque dans son code par l'API du §3.

### 3. L'agent d'authentification de l'OS, pour les Features

Le SDK gagne, **réservé aux points d'entrée et aux services d'une Feature**
(ADR 0243 §5 ; une app ordinaire reçoit `UnauthorizedAccessException`) :

```csharp
public interface IKazoFeatureAuthentication
{
    ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation,            // "x86.vm.delete-shared", affiché et journalisé
        KazoAuthenticationScope scope,  // RoleHolder ou Administrator
        CancellationToken ct = default);
}
```

- `RoleHolder` : l'usager en session **se ré-authentifie lui-même** (mot de
  passe ou clé d'accès, ADR 0168), comme `sudo` pour un administrateur ; il
  doit porter le rôle demandé par le point d'entrée.
- `Administrator` : un **administrateur activé** s'authentifie (lui-même ou
  un autre compte, comme ADR 0049 §5), pour les opérations qu'Os-x86 réserve
  aux admins (restrictions par VM, 124.1 ; refus machine, 124.5).
- La fenêtre est **celle de l'OS** (`AdministratorAuthenticationOverlay`,
  ADR 0049 §6, titre « <Feature> demande : <opération> »), jamais un dialogue
  de la Feature ; depuis une commande du terminal aussi (même raison qu'ADR
  0246 §4). La Feature ne voit jamais le mot de passe. Trois échecs annulent.
- Le billet rendu vit 30 s, à usage unique, **lié à l'opération** nommée
  (`InMemoryElevationTicketStore`, portée = Feature + opération) ; la Feature
  le présente à l'OS pour journaliser (§4) et exécute. Un billet d'une autre
  opération est refusé.
- En mode invité : simple confirmation (ADR 0187), journalisée.

### 4. Journal

- Chaque authentification écrit dans `/var/log/auth.log` (0600, ADR 0045) :
  `feature.authenticate app=<feature> operation=<op> requestedBy=<guid>
  scope=<RoleHolder|Administrator> admin=<guid|-> outcome=<ok|denied|cancelled>`,
  par `AdministratorAuthenticationAgent.Record` (:175-198) étendu de
  `scope`. Un refus de rôle (`RequiresRole` non porté) écrit
  `feature.role-denied app= entryPoint= user=`.
- La Feature écrit **en plus** l'événement de l'usager (ADR 0254) avec le
  sujet (VM) pour que l'usager concerné le voie.
- Attribuer ou retirer un rôle : `role.grant` / `role.revoke` dans
  `auth.log`, avec `admin`, `user`, `role`.

### 5. Ce qui ne change pas

- `AppAuthority` garde ses trois niveaux ; un rôle n'est ni un niveau ni un
  groupe Unix.
- `AdministratorGate` reste interne aux passerelles intégrées ; les apps
  ordinaires gardent le dialogue de `WindowAppBase` (à revoir hors de cette
  série).
- `sudo x86 …` reste possible : une commande élevée par `sudo` a
  `IsRootElevated` et passe les contrôles `Administrator` sans nouvelle
  fenêtre (comme `AdministratorGate.cs:25`).

## Options écartées

- **A. Le rôle suffit, sans authentification par opération** (160 B) : moins
  que ce que Kazo exige de ses propres administrateurs ; une session laissée
  ouverte permettrait de supprimer les VM de tous.
- **B. « Gérer les sous-systèmes » comme permission d'app** (160 C) : une app
  (BoxedWine) administrerait toutes les VM avec l'agent exposé aux apps ;
  Benoit a choisi A : BoxedWine ouvre la page des Paramètres, elle n'agit pas
  sur les VM des autres.
- **C. Un rôle = l'autorité `Administrator`** : donnerait à un gestionnaire
  de VM tous les droits d'un admin de Kazo (comptes, sauvegarde).
- **D. Les groupes Unix comme rôles** : ils protègent des fichiers, pas des
  points d'entrée ni des opérations, et aucune page ne les affiche.
- **E. Le dialogue de l'app (`WindowAppBase`) pour une Feature** : la Feature
  verrait le mot de passe ; D61 l'interdit.
- **F. Un administrateur sans rôle d'office**, qui se l'attribue comme un
  admin Linux se met dans `docker` (recommandation d'origine de cette ADR) :
  écarté par le mainteneur le 2026-11-11 ; un administrateur de Kazo
  administre déjà tout, et la fenêtre d'authentification du §2 protège
  chaque opération sensible, pas la visibilité des pages.

## Conséquences

- SDK : `FeatureDefinition.Roles`, `FeatureRole`, `RequiresRole` sur un point
  d'entrée, `IAppExecutionContext.Roles`, `KazoUser.Current.HasRole`,
  `IKazoFeatureAuthentication`, `KazoAuthenticationTicket`,
  `KazoAuthenticationScope` ; `PublicAPI.Unshipped.txt`, fixture `sdk-compat`.
- Plateforme : `roles.config` (document système, synchronisé), lecture des
  rôles à la construction du contexte (`AppExecutionContextFactory` ; tous
  les rôles déclarés pour un administrateur, sans ligne),
  `AdministratorGate` étendu (`RoleHolder`), billets à portée Feature +
  opération, entrées `feature.*` et `role.*` d'`auth.log`.
- Paramètres ▸ Utilisateurs : colonne « Rôles », attribution avec
  authentification pour les usagers ordinaires, « implicites
  (administrateur) » pour les administrateurs ; commande `kazorole`
  (`grant`, `revoke`, `list` ; `grant` sur un administrateur ne change rien).
- Docs : [`system-administration.md`](../../framework/system-administration.md)
  (section « Rôles des Features »), [`sensitive-settings.md`](../sensitive-settings.md),
  [`logging.md`](../logging.md), `features.md`, entrée `changes/unreleased`.
- Tests : rôle attribué → page visible, sous-commande acceptée ; non attribué
  → page absente, 126 ; administrateur sans ligne dans `roles.config` → page
  visible, `HasRole` vrai ; compte rétrogradé en `User` → page absente ;
  opération sensible → fenêtre de l'OS, billet lié à
  l'opération, refus d'un billet d'une autre opération ; `auth.log` complet ;
  app ordinaire → exception ; mode invité → confirmation ; `sudo x86` sans
  seconde fenêtre.

## Questions posées à Benoit

1. **À qui un rôle de Feature peut-il être attribué ?**
   - A) **(Recommandé)** À tout usager, par un administrateur ; l'opération
     sensible se ré-authentifie par l'usager lui-même (`RoleHolder`) ou par
     un administrateur (`Administrator`) selon ce que la Feature demande.
   - B) Aux administrateurs seulement (un rôle = un sous-ensemble des droits
     d'admin) ; plus simple, mais un usager standard ne peut jamais gérer les
     VM communes, ce que 140 B voulait permettre par le rôle.

Réponse du mainteneur (2026-11-11) : 1 A.

Question réglée le 2026-11-11 : un administrateur porte-t-il tous les rôles
d'office → oui, implicitement ; l'attribution explicite ne concerne que les
usagers ordinaires (§1).
