> [English version](../../../en/wasm/dev/app-launch-link.md)

# Lien direct vers une app

## Objet

Un lien `https://os.kazo.ca/?autolaunch=<appId>` ouvre Kazo OS directement sur une app du
Kazo OS Store. Sans compte, l'OS démarre en invité, installe l'app depuis le catalogue officiel
signé et la lance, sans aucun clic. Avec un compte, il ouvre le Store à la page de l'app après la
connexion. Décision : [ADR 0258](adr/0258-direct-app-launch-link.md), série `app-link/base`.

Exemple : `https://os.kazo.ca/?autolaunch=ca.kazo.word` ouvre Kazo Word.

Le même lien marche sur tous les hôtes de l'OS : la PWA autonome, un site statique hébergé
ailleurs, l'OS servi par `Kazo.Os.Server`.

## Pour l'utilisateur

| Situation | Ce qui se passe |
| --- | --- |
| Aucun compte sur cette machine | L'OS fait **Démarrer en invité** sans clic. Une fenêtre de progression montre l'app, son éditeur et l'étape (recherche, installation, ouverture), avec **Annuler**. L'app est installée pour l'invité seul, puis elle s'ouvre. |
| L'invité est le seul compte (visite précédente) | L'OS fait **Continuer en invité** sans clic. Si l'app est déjà installée depuis le catalogue officiel, elle s'ouvre sans être téléchargée de nouveau. |
| Un autre compte existe | L'écran de connexion reste le même, avec un bandeau : « `ca.kazo.word` s'ouvrira dans le Kazo OS Store après la connexion. » Après la connexion, le Store s'ouvre à la page de l'app. Rien n'est installé sans le clic de l'utilisateur. |
| Rechargement de la page | Le paramètre a été retiré de l'adresse au premier chargement : rien ne recommence. L'écran habituel s'affiche. |
| Identifiant mal formé | Le lien est ignoré (mais retiré de l'adresse) ; l'OS démarre comme d'habitude. |

### Erreurs

Chaque erreur laisse l'OS utilisable et se termine par une notification de l'OS « L'app n'a pas
pu être ouverte », avec un bouton **Ouvrir le Kazo OS Store** qui ouvre le Store à la page de
l'app.

| Cas | Message |
| --- | --- |
| App absente du catalogue officiel (même si un autre catalogue la propose) | Le Kazo OS Store ne propose pas l'app `<appId>`. |
| Catalogue injoignable (hors ligne) | Le Kazo OS Store est injoignable. |
| App bloquée (`app-policy.config`) | Message habituel de la liste de blocage. |
| Version de l'OS trop ancienne | Message habituel du Store, avec la version demandée. |
| Signature refusée, paquet abîmé | Message habituel de l'installation. |
| Annulé par l'utilisateur | Aucune notification. |

## Sécurité

- Le paramètre porte **seulement un identifiant d'app** (`a.b[.c…]`, `a-z`, `0-9`, `.`, `-`,
  128 caractères au plus), jamais une adresse, un dépôt, un catalogue ni une version.
- L'app est cherchée **seulement** dans le catalogue `official` (`catalog:official/<appId>`). Les
  signatures du catalogue et de l'auteur, la liste de blocage et la version minimale de l'OS sont
  vérifiées comme pour toute installation.
- Le lien n'accorde aucune capacité dangereuse : la demande d'autorisation habituelle s'affiche au
  lancement.
- Une personne qui a déjà un compte ne voit jamais une app s'installer sans son clic.
- La demande n'est jamais écrite dans le stockage : fermer l'onglet l'oublie.

## Architecture

| Élément | Où | Rôle |
| --- | --- | --- |
| `AppLaunchLink` | `Platform.Core/AppLinks` | `TryParse(adresse, out lien)` lit le premier `autolaunch` de la requête (pas le fragment `#…`) ; `IsValidAppId` applique les règles ci-dessus ; `RemoveFromAddress` retire **tous** les `autolaunch` et garde les autres paramètres et le fragment. |
| `EcosystemHostState.PendingAppLaunch` | `Ecosystem.Host` | Garde le lien en mémoire entre la lecture et le premier bureau ; `TakePendingAppLaunch` le rend une seule fois. |
| `App.razor` | `Ecosystem.Host` | Lit le lien **après** la mise à jour au démarrage (ADR 0239, qui recharge la même adresse) et avant le premier écran, puis réécrit l'adresse (`NavigateTo(…, replace: true)`, sans rechargement). Démarre ou continue l'invité une seule fois. Au premier bureau : session invité → `IAppLaunchLinkRunner` ; autre compte → `org.kazoos.store --app=<appId>`, puis `RecordStoreOpened`. |
| `IAppLaunchLinkRunner`, `AppLaunchLinkRunner` | `Platform.Core/AppLinks` | Cherche l'app dans le catalogue officiel, l'installe pour l'utilisateur de la session par la passerelle de gestion des apps (la même que le Store et `kazopkg`), la lance par l'orchestrateur, et transforme chaque échec en notification. Un deuxième lien pendant qu'un lien tourne est ignoré. |
| `AppLaunchLinkOperation` | `Platform.Core/AppLinks` | L'étape en cours (`Looking`, `Installing`, `Launching`), le nom, l'éditeur (propriétaire du dépôt) et la version, que montre la fenêtre de progression. **Annuler** est possible jusqu'au lancement. |
| `AppLaunchProgress` | `Platform.Blazor/Shell` | Fenêtre de progression de l'OS (`.app-link-progress`, `data-app-id`), non modale, sur le bureau et sur mobile. |

### Journal

Chaque lien écrit une ligne dans `/var/log/apps/org.kazoos.app-link.log` (source
`org.kazoos.app-link`, propriétés `appId`, `reason`, `version`) :

| Message | Quand |
| --- | --- |
| `app-link.received` | Un lien commence dans la session de l'invité. |
| `app-link.installed` | L'app a été installée (`version`). |
| `app-link.launched` | L'app a été lancée. |
| `app-link.store-opened` | Un compte s'est connecté et le Store s'est ouvert à la page de l'app. |
| `app-link.failed` | Échec, avec `reason` : `not-offered`, `unreachable`, `cancelled`, `busy`, `not-approved`, `no-session`, `unsupported`, `launch:<code>`, `error`, ou le code de l'installation (`apps.package.blocked`, `apps.package.bad-signature`…). |

## Tests

- **Unitaires** (`Tests/Kazo.Os.Platform.Core.Tests/AppLinks/`) : `AppLaunchLinkTests` (lecture,
  identifiants refusés, retrait de l'adresse) et `AppLaunchLinkRunnerTests` (catalogue officiel
  seulement, app déjà installée, thème du même nom, erreurs, annulation, deuxième lien, journal).
  `EcosystemHostStateTests` couvre `TakePendingAppLaunch`.
- **Bout en bout** (`Tests/Kazo.Os.TrimmedPublish.E2E.Tests/TrimmedPublishTests.AppLaunchLink.cs`,
  lancés par `build/publish-trimmed.sh`, voir [`trimmed-publish.md`](trimmed-publish.md)) :
  1. sans compte : l'invité démarre, l'app s'installe et s'ouvre, l'adresse perd le paramètre ; un
     rechargement ne relance rien ; une deuxième visite continue en invité et ouvre l'app sans la
     télécharger de nouveau ;
  2. avec un compte : bandeau de l'écran de connexion, puis le Store à la page de l'app, rien de
     téléchargé ;
  3. app absente du catalogue : notification, son bouton ouvre le Store ; puis **Annuler**
     pendant le téléchargement : ni app ni notification.

  Le catalogue officiel est servi par une route Playwright. Il liste le paquet de test de
  l'ADR 0206 (`Tests/fixtures/installed-app`), signé par une clé P-256 que le script crée à chaque
  exécution avec `openssl`. La publication testée accepte la moitié publique de cette clé pour
  `catalog:official`, à côté de la vraie clé (`-p:KazoOsAppSigningKeys`, ADR 0214 §2) : la
  vérification de signature est donc la vraie. Aucune clé privée n'est versionnée.

## Lien « Essayer » des README d'apps

Le README de chaque app du Store garde une place pour un lien **Essayer dans Kazo OS** vers
`https://os.kazo.ca/?autolaunch=<appId>`. Il sera rempli quand cette fonction sera dans une
version publiée de Kazo OS (la série `app-link/base` doit d'abord arriver dans `main`).

## Liste des tâches

- [x] Chantier 0 : ADR 0258 (fr + en), `features.md`, mémoire.
- [x] Chantier 1 : lecture du lien, démarrage en invité, Store après la connexion d'un compte.
- [x] Chantier 2 : installation et lancement, fenêtre de progression, erreurs, journal.
- [x] Chantier 3 : ligne `app-link.store-opened`, tests de bout en bout, ce document, entrée de
  changement, `features.md`.
- [ ] Après la sortie : lien **Essayer** dans les README des apps.
