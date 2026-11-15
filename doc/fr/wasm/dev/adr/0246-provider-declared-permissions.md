> [English version](../../../../en/wasm/dev/adr/0246-provider-declared-permissions.md)

# ADR 0246 : permissions déclarées par une Feature, dangereuses comprises, jamais accordées en silence

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : D32 (série
3), 143 (« mêmes règles que Kazo : approbation admin, refus par usager, jamais
d'attribution silencieuse », 2026-11-11), 176 A (noms et niveaux des
permissions d'Os-x86, 2026-11-11) et 160 A (« gérer les sous-systèmes »
disparaît au profit du rôle de l'ADR 0247).

- **Complète :** l'[ADR 0049](0049-dangerous-capabilities-and-admin-approval.md)
  (catalogue, approbation machine, refus par usager) et
  l'[ADR 0260](0260-feature-app-type.md) §5 (seule une Feature déclare des
  permissions).
- **Corrige le fond de :** l'issue [#432](https://github.com/Kazo-ca/Os/issues/432)
  (le semeur accorde en silence tout identifiant hors catalogue), qui touche
  aussi les permissions « sujet » de l'[ADR 0040](0040-declared-topic-permissions.md).
- **Pour :** les permissions d'Os-x86 : `x86.programs.launch` (normale),
  `x86.vm` (dangereuse, couvre tout), `x86.vm.use` (normale),
  `x86.vm.destroy` (dangereuse) ; BoxedWine les déclare dans son manifeste.

## Contexte

- Le catalogue des capacités est **figé dans le code** :
  `Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs:196-287`
  (`Descriptions`, chaque entrée `Normal(...)` ou `Dangerous(...)`,
  17 dangereuses) ; `IsDangerous` (:323-331) rend faux pour tout identifiant
  hors catalogue, ce que son commentaire dit explicitement pour les
  permissions sujet du bus.
- Le validateur refuse une capacité inconnue (`AppManifestValidator.cs:846-864`,
  `manifest.capability.unknown` :858) sauf une permission sujet bien formée
  (:854). Une app ne peut donc **pas** déclarer `x86.programs.launch`
  aujourd'hui.
- À la connexion, `CleanProfileCapabilityGrantSeeder` (`Platform/Kazo.Os.Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs:48-72`)
  accorde **en silence** toute capacité déclarée que `IsDangerous` juge non
  dangereuse (:51-55, `Grant` :66-71), appelé par
  `LocalSessionService.cs:295-307`. Les permissions sujet de l'ADR 0040 sont
  ainsi accordées sans l'approbation explicite que l'ADR exige (:34-37,
  :145-148) : c'est l'issue #432. Une permission déclarée par une Feature
  subirait le même sort tant que le semeur ne la connaît pas comme
  dangereuse.
- Les capacités dangereuses suivent l'ADR 0049 : approbation machine dans
  `/etc/kazoos/app-permissions.config` (seule source d'un `allow`,
  `AppPermissionDecisions.cs:17-22`), refus par usager dans `~/.config`
  (§4, §9), fenêtre sécurisée au premier lancement (§5, §6), ou mode texte
  dans le terminal (§5 : bandeau, puis nom et mot de passe administrateur
  par les dialogues de l'app Terminal, `TerminalCredentialPrompt.cs:38-71`).
  La PR #428 fait qu'un `allow` machine retire le refus de l'usager qui le
  demande (`AppPermissionAdministration.cs:295-305`).
- La fenêtre d'approbation montre une **phrase de risque** par capacité
  dangereuse (ADR 0049 §1) ; une Feature doit pouvoir fournir la sienne.

## Décision

### 1. Une Feature déclare ses permissions

`FeatureDefinition.ProvidedCapabilities` liste des `ProvidedCapability` :

| Champ | Rôle |
|---|---|
| `Id` | identifiant, sous le **préfixe de la Feature** (§2) : `x86.programs.launch` |
| `Risk` | `Normal` ou `Dangerous` (`CapabilityRisk`) |
| `Title`, `RiskStatement` | titre affiché et, si dangereuse, la phrase de risque (ressources localisées de la Feature, ADR 0222) |
| `Covers` | pour une permission qui en couvre d'autres (`x86.vm` couvre `x86.vm.use` et `x86.vm.destroy`) : la liste couverte |

- Le manifeste les écrit ; le validateur vérifie la forme, le préfixe et
  l'absence de doublon avec le catalogue ou une autre Feature installée
  (`apps.package.capability-already-provided`).
- Le catalogue des capacités de l'OS devient **catalogue intégré + catalogues
  des Features installées**, lu par tout ce qui consulte `AppCapabilities`
  aujourd'hui : validateur (pour une app installée après la Feature), semeur,
  passerelle des capacités dangereuses, `DangerousCapabilityEvaluator`,
  Paramètres ▸ Capacités et ▸ Permissions, `kazopkg info`, l'écran
  d'approbation.

### 2. Préfixe réservé et unicité

- Chaque Feature déclare un `CapabilityPrefix` (un segment, en minuscules :
  `x86`). Ses permissions commencent par `<préfixe>.`. Un préfixe du
  catalogue intégré (`filesystem`, `network`, `apps`, `system`…) est refusé ;
  deux Features qui déclarent le même préfixe → la seconde est refusée à
  l'installation (`apps.package.capability-prefix-taken`).
- Une app qui déclare une permission `<préfixe>.*` dont aucune Feature
  installée ne porte le préfixe : refusée à l'installation
  (`manifest.capability.unknown`, inchangé), **sauf** si elle déclare une
  dépendance (ADR 0262 §3) vers une Feature dont le préfixe correspond ; la
  permission est alors acceptée et **restera non accordée** tant que la
  Feature n'est pas installée (une dépendance facultative absente).

### 3. Les mêmes règles que le catalogue, jamais en silence

- Une permission déclarée **normale** est accordée à la connexion comme une
  capacité normale du catalogue, **si et seulement si** la Feature qui la
  déclare est installée et activée. Sinon elle n'est pas accordée (pas
  d'erreur : l'app voit `GetServiceAsync` rendre null).
- Une permission déclarée **dangereuse** suit l'ADR 0049 mot pour mot :
  jamais accordée sans un `allow` dans le fichier machine, écrit après
  l'approbation d'un administrateur (fenêtre du premier lancement ou
  Paramètres ▸ Permissions) ; refus par usager dans `~/.config` ; politique
  `block` respectée ; phrase de risque de la Feature affichée ; octroi
  matérialisé par (app, usager, permission) (`IndexedDbCapabilityGrantRepository.cs:289-290`).
- **Le semeur ne connaît plus de « non dangereux par défaut »** : il n'accorde
  que les identifiants que le catalogue (intégré + Features) décrit comme
  normaux. Tout identifiant **inconnu** est ignoré avec un avertissement dans
  `/var/log/syslog` (`capability.unknown-skipped`). Les permissions sujet de
  l'ADR 0040 entrent dans le catalogue avec leur classement (question 2) :
  c'est la correction de #432.
- `Covers` : accorder `x86.vm` accorde aussi ce qu'elle couvre ; l'écran
  d'approbation le dit (« couvre : utiliser, supprimer et exporter ses VM »).
  Une permission couvrante est au moins aussi dangereuse que ce qu'elle
  couvre (validateur).

### 4. La Feature vérifie, par l'écran sécurisé de l'OS

- Chaque opération d'un service fourni (ADR 0262) ou d'une commande d'une
  Feature exige ses permissions par `IFeatureServiceContext.Caller.Require("x86.vm.use")`,
  qui lève `UnauthorizedAccessException` comme les passerelles du SDK. La
  Feature n'évalue jamais elle-même un fichier de décision.
- Quand une permission dangereuse déclarée est **en attente** pour l'app
  appelante (ADR 0049 §4) et que l'appel vient d'une **commande du
  terminal** (`./x.exe`, `x86 vm delete`), la demande passe par la **fenêtre
  sécurisée de l'OS** (ADR 0049 §6, `AdministratorAuthenticationOverlay`),
  pas par le mode texte du terminal : une sortie d'un programme invité
  relayée dans ce terminal (ADR 0252) pourrait imiter le bandeau et les
  dialogues de l'app Terminal. Le mode texte de l'ADR 0049 §5 reste pour les
  capacités du catalogue intégré (question 3).

### 5. Ce qui ne change pas

- Une permission déclarée par une Feature protège ce que **cette Feature**
  fait ; l'OS n'en tire aucun droit propre (elle n'ouvre ni fichier ni réseau
  de l'OS).
- `network.real.access` reste au catalogue intégré : Os-x86 la déclare dans
  `Capabilities` (181 A), pas dans `ProvidedCapabilities`.
- Retirer la Feature retire ses permissions du catalogue ; les lignes
  `allow`/`deny` restent dans les fichiers (ignorées, ADR 0049 §4 : « une
  ligne qui vise … une capacité normale est ignorée ») et redeviennent
  effectives à la réinstallation.

## Options écartées

- **A. Les permissions sujet de l'ADR 0040 comme modèle** (identifiants hors
  catalogue acceptés) : c'est précisément le mécanisme accordé en silence
  (#432) ; une permission dangereuse ne peut pas y vivre.
- **B. Ajouter les permissions d'Os-x86 au catalogue intégré de l'OS** : une
  Feature tierce ne pourrait pas en déclarer sans une version de l'OS ;
  contraire à « une Feature est un dépôt à part ».
- **C. Permissions d'une Feature toutes dangereuses** (une seule approbation
  admin couvre tout, 176 B) : `x86.programs.launch` (lancer un programme
  qu'on a soi-même choisi) demanderait un mot de passe d'administrateur ;
  Benoit a retenu 176 A.
- **D. Laisser la Feature lire elle-même `app-permissions.config`** : chaque
  Feature réécrirait l'évaluateur, et un bogue y accorderait des droits.

## Conséquences

- `AppCapabilities` devient un service (`ICapabilityCatalog`) composé du
  catalogue intégré et des Features installées ; `IsDangerous`, `Describe`,
  `GetRisk` lisent ce service ; `CleanProfileCapabilityGrantSeeder` n'accorde
  que le connu ; `DangerousCapabilityGate` et `DangerousCapabilityEvaluator`
  consultent le catalogue composé ; `AppManifestValidator` accepte un préfixe
  de Feature déclaré en dépendance ; codes `apps.package.capability-already-provided`,
  `apps.package.capability-prefix-taken`, `capability.unknown-skipped`.
- `IFeatureServiceContext.Caller.Require` ; demande d'approbation d'une
  permission de Feature par la fenêtre sécurisée même depuis le terminal.
- Paramètres ▸ Capacités et ▸ Permissions, `kazopkg info`, l'écran
  d'approbation montrent les permissions des Features avec leur phrase de
  risque et leur Feature d'origine.
- Issue #432 : fermée par cette ADR (le semeur) ; l'issue #433 (fichier de
  refus écrasable) n'en dépend pas et reste ouverte.
- Docs : [`capabilities.md`](../../framework/capabilities.md) (section
  « Permissions déclarées par une Feature »), [`dangerous-capability-approval.md`](../dangerous-capability-approval.md),
  `features.md`, entrée `changes/unreleased`.
- Tests : permission normale déclarée accordée seulement Feature installée ;
  dangereuse déclarée → fenêtre, `allow` machine, refus usager, `block` ;
  identifiant inconnu → ignoré et journalisé, jamais accordé (régression de
  #432 avec un identifiant au hasard et une permission sujet) ; préfixe pris
  → refus ; `Covers` ; opération sans permission → exception ; demande depuis
  une commande → fenêtre sécurisée.

## Questions posées à Benoit

1. **Qui réserve le préfixe d'une Feature ?**
   - A) **(Recommandé)** La première Feature installée qui le déclare ; une
     seconde avec le même préfixe est refusée ; aucun registre central.
   - B) Un registre dans le catalogue officiel du Store (préfixe par
     éditeur), vérifié à la publication ; plus sûr, mais une Feature hors
     Store n'a pas de préfixe garanti.
2. **Les permissions sujet de l'ADR 0040 (`topic-publish:…`,
   `topic-subscribe:…`), dans la correction du semeur ?**
   - A) **(Recommandé)** Classées **dangereuses** et approuvées comme l'ADR
     0040 l'exige (`UserApproval | AdministratorApproval`) : fin de l'octroi
     silencieux ; les apps qui s'en servent voient une fenêtre d'approbation
     une fois.
   - B) Classées normales (comportement actuel, rendu explicite) : #432 reste
     ouverte pour les sujets.
3. **La demande d'approbation d'une permission de Feature depuis une commande
   du terminal ?**
   - A) **(Recommandé)** Toujours par la fenêtre sécurisée de l'OS (§4) ; le
     mode texte de l'ADR 0049 §5 reste pour le catalogue intégré.
   - B) Le mode texte du terminal, comme pour le catalogue intégré (plus
     simple, mais imitable par un programme invité dont la sortie passe par
     le terminal).
   - C) La fenêtre sécurisée pour toutes les permissions dangereuses, Feature
     ou non (change l'ADR 0049 §5 pour toutes les commandes).

Réponses du mainteneur (2026-11-11) : 1 A, 2 A, 3 A.
