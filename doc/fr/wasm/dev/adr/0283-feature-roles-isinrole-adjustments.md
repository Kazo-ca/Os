> [English version](../../../../en/wasm/dev/adr/0283-feature-roles-isinrole-adjustments.md)

# ADR 0283 : rôles des Features, ajustements de la mise en œuvre (`IsInRole` et `<Feature>:<rôle>`)

## Statut

Accepté le 2026-11-17.

- **Chantier :** `feature/20-feature-declared-roles-code` (série `feature/base`,
  code de l'ADR 0247, PR #604).
- **Ajuste :** l'[ADR 0247](0247-feature-declared-roles.md) §1 (forme des rôles,
  lecture par `HasRole`) et §3 (portée `RoleHolder`). Ses autres décisions restent
  vraies : une Feature déclare ses rôles, un administrateur les attribue, chaque
  administrateur les détient tous implicitement (réponse 233 B), un point
  d'entrée peut exiger un rôle (`RequiresRole`), et chaque opération sensible
  passe par l'authentification de l'OS.
- **Questions posées à Benoit** dans la PR #604 (série 22, questions 277 à 280).
  Réponses du 2026-11-17 : sa propre réponse à 277 (§1 à §3 ci-dessous), A à
  278, 279 et 280 (§6). Les questions de suivi 281 et 282 sont posées dans la
  même PR.

## Contexte

L'ADR 0247 nomme un rôle `<préfixe>.<nom>`, comme une permission
(`x86.vm-admin`), et le lit par `KazoUser.Current.HasRole(rôle)`. Sa portée
`RoleHolder` suppose un point d'entrée marqué `RequiresRole`. En écrivant le
code, une question restait ouverte (277) : une commande ouverte à tous, comme
`x86`, peut avoir une sous-commande sensible (`x86 admin`), alors que
`RequiresRole` est porté par un manifeste entier.

Réponse de Benoit (question 277, 2026-11-17) : « Je crois que pour simplifier le
développement, la feature doit pouvoir interroger l'OS pour savoir si le user a
les permissions... On va devoir avoir un objet `KazoUser` et pouvoir
l'interroger comme `_kazoUser.IsInRole("admin")` ou
`_kazoUser.IsInRole("x86:manager")` (le préfixe doit rester le nom de la feature
qui déclare les rôles). »

## Décision

### 1. L'usager s'interroge par `IsInRole`

- `KazoUser.Current` rend l'objet de l'usager du processus (`KazoUserInfo`,
  comme les autres services du SDK). Sa méthode `IsInRole(string rôle)` dit s'il
  est dans un rôle ; `KazoUser.IsInRole(rôle)` en est le raccourci.
- `IsInRole` remplace `HasRole`, qui n'a jamais été publiée (ajoutée par le même
  chantier, encore dans `PublicAPI.Unshipped.txt`).

### 2. Deux sortes de noms de rôles

- **Rôle de l'OS** : un nom simple. Le seul aujourd'hui est `admin`
  (`KazoRoles.Administrator`) : vrai pour un administrateur et pour root sous
  `sudo`. Un autre nom simple n'est pas un rôle de l'OS et rend faux.
- **Rôle d'une Feature** : `<Feature>:<rôle>`, par exemple `x86:vm-manager`.
  Le séparateur `:` (`KazoRoles.FeatureSeparator`) le distingue d'une
  permission (`x86.vm.use`, ADR 0246).
- Le préfixe est **le nom de la Feature qui déclare le rôle** : jamais choisi
  par l'appelant ni par une autre Feature. Le validateur refuse un rôle dont le
  préfixe n'est pas celui de sa propre Feature (`manifest.role.id`) ; le
  manifeste écrit l'identifiant complet (`"id": "x86:vm-manager"`), et
  `RequiresRole`, `roles.config` et `kazorole` utilisent ce même identifiant.

### 3. Le « nom de la Feature » est son `CapabilityPrefix`

Le préfixe d'un rôle est le `CapabilityPrefix` de la Feature (ADR 0246) :

- il est court et validé (une lettre minuscule, puis des minuscules, chiffres
  et tirets) ;
- il est réservé face au catalogue intégré, et l'installateur refuse une
  Feature dont le préfixe est déjà pris par une Feature installée
  (`apps.package.capability-prefix-taken`, ADR 0246 §2). Il est donc unique
  parmi les Features installées ;
- il est stable : le changer changerait le nom de toutes ses permissions et
  casserait les apps qui les demandent ;
- c'est déjà le nom court d'Os-x86 : `x86`, comme ses permissions `x86.vm.use`.

L'identifiant de la Feature (`ca.kazo.x86`) n'est pas retenu : il est long et
contient des points. Aucun champ nouveau n'est ajouté. Question de suivi 281
dans la PR #604.

### 4. Les administrateurs (233 B)

Un administrateur, et root sous `sudo`, répondent vrai à tout
`IsInRole("<feature>:<rôle>")`, sans ligne dans `roles.config`.
`IAppExecutionContext.Roles` liste, pour lui, tous les rôles des Features
installées.

### 5. `RequiresRole` reste, et une sous-commande vérifie `IsInRole`

- `RequiresRole` sur un point d'entrée reste possible, en plus : l'OS masque et
  refuse alors ce point d'entrée (126 « rôle requis »).
- Une sous-commande d'une commande ouverte à tous, comme `x86 admin`, vérifie
  elle-même `KazoUser.Current.IsInRole("x86:vm-manager")`.
- L'authentification par opération de l'OS (`KazoFeatureAuthentication`, `sudo`)
  reste à part et inchangée. La portée `RoleHolder` demande, depuis un point
  d'entrée marqué `RequiresRole`, ce rôle. Depuis un autre point d'entrée de la
  Feature, elle demande **un rôle de la Feature** : la Feature vérifie le rôle
  exact par `IsInRole` avant de demander. Un usager sans aucun rôle de la
  Feature n'obtient jamais cette portée. Question de suivi 282 dans la PR #604.

### 6. Autres réponses (278, 279, 280 : A)

- 278 : `roles.config` n'est pas synchronisé dans ce chantier ; une issue `TODO`
  suivra pour une liste des documents système synchronisés.
- 279 : Paramètres ▸ Utilisateurs montre un panneau « Rôles des Features », un
  tableau comptes × rôles avec cases à cocher.
- 280 : `kazorole list` est ouvert à tout usager, sans `sudo`.

## Options écartées

- **Un champ nouveau du manifeste** (`ShortName`) comme nom de la Feature : il
  doublerait `CapabilityPrefix`, qui a déjà les garanties voulues (§3).
- **L'identifiant de la Feature comme préfixe** (`ca.kazo.x86:vm-manager`) :
  long, avec des points, et différent du nom que le mainteneur écrit (`x86`).
- **Le point comme séparateur** (`x86.vm-manager`, texte de l'ADR 0247) : un
  rôle se confondrait avec une permission.
- **`RoleHolder` refusé hors d'un point d'entrée `RequiresRole`** : la
  sous-commande `x86 admin` ne pourrait pas redemander le mot de passe de son
  usager.
- **Un paramètre `rôle` à `AuthenticateAsync`** : deux surcharges avec
  paramètres facultatifs dans l'API publique, pour un contrôle que `IsInRole`
  fait déjà.

## Conséquences

Dans la PR #604 :

- SDK : `KazoUserInfo.IsInRole`, `KazoUser.IsInRole`, `KazoRoles`
  (`Administrator`, `FeatureSeparator`) ; `HasRole` retirée.
- Manifeste : rôles `<préfixe>:<nom>` (validateur, schéma JSON, fixture).
- Plateforme : la portée `RoleHolder` accepte, depuis un point d'entrée sans
  `RequiresRole`, un usager qui détient un rôle de la Feature.
- Tests : forme des rôles (`FeatureRoleManifestTests`), `IsInRole` (`admin`,
  noms simples, raccourci), `RoleHolder` depuis un point d'entrée ouvert à tous.
- Docs : [`system-administration.md`](../../framework/system-administration.md),
  [`sensitive-settings.md`](../sensitive-settings.md), `features.md` et l'entrée
  `changes/unreleased`.

## Références

- [ADR 0247](0247-feature-declared-roles.md) : rôles déclarés par une Feature.
- [ADR 0246](0246-provider-declared-permissions.md) : permissions déclarées par
  une Feature, `CapabilityPrefix`.
- [ADR 0256](0256-app-libraries-implementation-adjustments.md) : modèle d'un
  ADR d'ajustement de mise en œuvre.
