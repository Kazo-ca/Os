> [English version](../../../../en/wasm/dev/adr/0258-direct-app-launch-link.md)

# ADR 0258 : lancer une app du Kazo OS Store par un lien direct

## Statut

Acceptée le 2026-11-13. Benoit a retenu la recommandation A des quatre
questions (voir [Questions](#questions-posées-à-benoit)) : paramètre
`autolaunch`, aucune confirmation, l'invité existant continue sans clic,
installation pour l'invité seulement. Série `app-link/base`.

D'abord numérotée 0246, renumérotée 0258 le 2026-11-13 (choix A de Benoit) : la
série `feature/base` réservait déjà 0243 à 0256.

- **S'appuie sur :** l'[ADR 0187](0187-guest-start-and-sign-in-credits.md)
  (démarrage en invité quand aucun compte n'existe), l'[ADR 0212](0212-hack-store-app-build.md)
  (l'app Store, source `catalog:official/<appId>`, argument `--app=<appId>`),
  l'[ADR 0208](0208-hack-store-sources-trust-build.md) (installation pour soi,
  liste noire), l'[ADR 0214](0214-hack-store-hot-add-signing-transactions.md)
  et la version 2.2.2 (signature du catalogue officiel obligatoire), et la
  porte des capacités dangereuses (`DangerousCapabilityGate`).
- **Ne remplace aucune ADR.**

## Contexte

Benoit veut un lien qu'on peut mettre dans le readme d'une app, sur un site ou
dans un message, et qui ouvre l'app dans Kazo OS sans autre manipulation :

> Démarrer Kazo OS en mode invité (si aucun compte) et télécharger
> automatiquement une application puis la lancer. Si l'utilisateur a un compte,
> on fait seulement ouvrir le Store à la page de l'application.
> Exemple : `https://os.kazo.ca/?autolunch=ca.kazo.word`

Ce qui existe déjà :

- le bouton **Démarrer en invité** sous le formulaire du premier compte, et
  **Continuer en invité** tant que l'invité est le seul compte (ADR 0187) ;
- l'app Store (`org.kazoos.store`) ouvre la page d'une app avec l'argument
  `--app=<appId>` (`StoreWindow.razor.cs`) ;
- `KazoAppPackages.InstallAsync` installe depuis un catalogue, avec la portée
  `KazoInstallScope` (pour moi ou pour tous) ; un paquet du catalogue officiel
  doit porter sa signature (`AppPackageDefaults.OfficialCatalogSigningKey`) ;
- rien ne lit encore l'adresse de la page au démarrage de l'OS.

Un lien est écrit par n'importe qui : il ne doit jamais permettre d'installer
autre chose qu'une app du catalogue officiel signé, ni de contourner une
autorisation que l'OS demande d'habitude.

## Décision

### 1. Le lien

`https://<adresse de l'OS>/?autolaunch=<appId>`, par exemple
`https://os.kazo.ca/?autolaunch=ca.kazo.word`. Le nom du paramètre est
`autolaunch` (anglais correct) ; voir la question Q1.

- Le paramètre ne porte **qu'un identifiant d'app**, jamais une adresse, un
  dépôt, un catalogue ou une version. Un identifiant mal formé (pas en notation
  inversée `a.b[.c…]`, plus de 128 caractères, caractères hors `a-z`, `0-9`,
  `.`, `-`) est ignoré : l'OS démarre comme d'habitude.
- Un seul paramètre est lu ; s'il est répété, seul le premier compte.
- Le même lien marche sur tout hôte de l'OS : la PWA autonome, un site statique
  hébergé ailleurs, l'OS servi par `Kazo.Os.Server`.

### 2. Lu une seule fois, puis retiré de l'adresse

Au démarrage, l'hôte (`App.razor`) lit le paramètre par un analyseur C#
(`AppLaunchLink.TryParse`, `Platform.Core`, testable sans navigateur), garde la
demande dans l'état de l'hôte (`EcosystemHostState.PendingAppLaunch`), puis
réécrit l'adresse sans le paramètre (`NavigationManager.NavigateTo(…,
replace: true)`, sans recharger). Recharger la page, ou la redémarrer pour une
mise à jour, ne relance donc pas l'installation. La demande ne survit pas à la
fermeture de l'onglet : rien n'est écrit dans le stockage.

### 3. Aucun compte : démarrer en invité, installer, lancer

Si aucun compte n'existe (`GuestAccountService.CanCreateAsync`), l'hôte ne
montre pas le formulaire du premier compte : il crée l'invité et ouvre sa
session comme le bouton **Démarrer en invité**, puis :

1. une fenêtre de progression de l'OS (« Installation de Kazo Word… », nom et
   éditeur lus dans le catalogue, barre de progression, bouton **Annuler**)
   s'ouvre sur le bureau ;
2. l'app est cherchée **seulement** dans le catalogue `official`
   (`catalog:official/<appId>`). La signature du catalogue et celle de
   l'auteur sont vérifiées comme pour toute installation, la liste noire
   (`app-policy.config`) et la version minimale de l'OS aussi ;
3. elle est installée **pour l'invité** (`KazoInstallScope` « pour moi ») : pas
   de fenêtre d'administrateur (voir Q4) ;
4. elle est lancée comme depuis le menu Démarrer. Les capacités dangereuses
   qu'elle déclare passent par la porte habituelle : la demande d'autorisation
   s'affiche au lancement, rien n'est accordé d'office par le lien.

Pas de confirmation avant l'installation (voir Q2) : le lien ne vise que le
catalogue officiel signé et une session invité neuve, qui ne contient encore
aucune donnée.

Si l'invité est **déjà le seul compte** (visite précédente), l'OS continue en
invité sans clic et fait la même chose ; si l'app est déjà installée, il la
lance sans réinstaller (voir Q3).

### 4. Un compte existe : ouvrir le Store à la page de l'app

Si un compte autre que l'invité existe, l'écran de connexion s'affiche comme
d'habitude ; un court bandeau sous le titre dit « Kazo Word s'ouvrira dans le
Kazo OS Store après la connexion ». Une fois la session ouverte, l'OS lance le
Store avec `--app=<appId>`. Rien n'est installé ni lancé sans un clic de
l'utilisateur dans le Store.

### 5. Erreurs

Toutes les erreurs laissent l'OS utilisable et se terminent par une
notification de l'OS avec le bouton **Ouvrir le Kazo OS Store** :

| Cas | Message |
| --- | --- |
| App absente du catalogue officiel (ou seulement d'un autre catalogue) | « Le Kazo OS Store ne propose pas l'app `<appId>`. » |
| Catalogue injoignable (hors ligne) | « Le Kazo OS Store est injoignable. Réessayez une fois en ligne. » |
| App sur la liste noire | Le message habituel de la liste noire. |
| Version de l'OS trop ancienne | Le message habituel du Store, avec la version demandée. |
| Signature refusée, paquet abîmé | Le message habituel de l'installation (journal `.transaction`). |
| Annulé par l'utilisateur | Pas de notification. |

### 6. Journal

Chaque lien traité écrit une ligne dans le journal des apps de l'OS
(`app-link.received`, `app-link.installed`, `app-link.launched`,
`app-link.store-opened`, `app-link.failed` avec la raison), comme `kazopkg`.

### 7. Readmes des apps

Le readme de chaque app du Store peut montrer un lien **Essayer dans Kazo OS**
vers `https://os.kazo.ca/?autolaunch=<appId>`. Ce travail se fait dans les
dépôts des apps, une fois cette série dans `main` et publiée.

## Chantiers de la série `app-link/base`

| N° | Branche | Contenu |
| --- | --- | --- |
| 0 | `app-link/0-adr-direct-launch-link` | Cette ADR, `features.md`, mémoire. |
| 1 | `app-link/1-read-link-and-guest-start` | `AppLaunchLink.TryParse` et ses tests, lecture et retrait du paramètre, `PendingAppLaunch`, démarrage invité automatique, bandeau de l'écran de connexion, Store `--app=` après la connexion d'un compte. |
| 2 | `app-link/2-install-and-launch` | Service `AppLaunchLinkRunner` (`Platform.Core`) : recherche dans le catalogue officiel seulement, installation pour l'invité, fenêtre de progression et Annuler, lancement, erreurs en notification, journal. Tests unitaires avec un catalogue de test. |
| 3 | `app-link/3-e2e-and-docs` | Tests Playwright (aucun compte, invité existant, compte existant, identifiant inconnu, rechargement), doc `dev/app-launch-link.md` (fr + en), entrée de changement. |

Aucun changement du SDK des apps n'est prévu : tout se passe dans l'hôte et
`Platform.Core`.

## Conséquences

- Un lien suffit pour faire essayer une app : pas de compte, pas de terminal,
  pas de clic dans le Store.
- Le lien ne peut installer qu'une app du catalogue officiel signé, et
  n'accorde aucune capacité dangereuse : les garde-fous restent ceux d'une
  installation normale.
- Un visiteur qui a déjà un compte ne voit jamais une app s'installer sans son
  clic.
- L'app installée par l'invité reste à lui : « Créer mon compte » ▸ Migrer la
  garde, « Supprimer l'invité » la retire (voir Q4).
- Quand la PWA est installée, le navigateur peut ouvrir le lien dans un onglet
  plutôt que dans la fenêtre de la PWA ; l'OS fonctionne pareil dans les deux.

## Questions posées à Benoit

Posées dans le fil du projet le 2026-11-13. Benoit a répondu A aux quatre le même jour.

- **Q1. Nom du paramètre.** A `autolaunch` (recommandé, anglais correct) ;
  B `autolunch` comme dans l'exemple ; C `autolaunch` documenté et `autolunch`
  accepté aussi.
- **Q2. Confirmation avant l'installation en invité.** A aucune, installation
  et lancement automatiques (recommandé) ; B un clic « Installer et ouvrir »
  dans une fenêtre qui montre l'app, l'éditeur et ses autorisations ; C
  automatique sauf si l'app déclare une capacité dangereuse.
- **Q3. L'invité existe déjà (seul compte).** A continuer en invité sans clic
  et lancer l'app (recommandé) ; B traiter l'invité comme un compte : écran de
  connexion, puis la page du Store.
- **Q4. Portée de l'installation en invité.** A pour l'invité seulement, sans
  fenêtre d'administrateur (recommandé) ; B pour la machine, avec un clic
  **Confirmer** de l'invité, et l'app reste si l'invité est supprimé.

## Mise en œuvre

### Chantier 1 (`app-link/1-read-link-and-guest-start`)

- `Kazo.Os.Platform.Core.AppLinks.AppLaunchLink` : `TryParse(adresse, out lien)` lit le premier
  `autolaunch` de la requête (le fragment `#…` n'est pas lu), `IsValidAppId` applique les règles
  du §1, `RemoveFromAddress` retire **tous** les `autolaunch` en gardant les autres paramètres et le
  fragment. Tests : `Tests/Kazo.Os.Platform.Core.Tests/AppLinks/AppLaunchLinkTests.cs`.
- `IAppLaunchLinkRunner.RunAsync(lien)` (même espace de noms) est le contrat que le chantier 2
  implémente. Tant qu'aucun runner n'est enregistré, la session invité ouvre elle aussi le Store
  à la page de l'app.
- `App.razor` lit le lien **après** la mise à jour au démarrage (ADR 0239, qui recharge la même
  adresse) et avant le premier écran, le garde dans `EcosystemHostState.PendingAppLaunch`, puis
  réécrit l'adresse (`NavigateTo(…, replace: true)`). Un identifiant mal formé est ignoré mais
  retiré lui aussi.
- Une seule fois, après le premier chargement : écran du premier compte → **Démarrer en invité** ;
  invité seul → **Continuer en invité**. Après une déconnexion, l'écran habituel revient.
- Le premier bureau affiché prend le lien (`TakePendingAppLaunch`) et le lance sans attendre :
  session de l'invité → `IAppLaunchLinkRunner` ; autre compte → `org.kazoos.store --app=<appId>`.
  Une erreur est journalisée et laisse la session utilisable.
- **Écart :** le bandeau de l'écran de connexion nomme l'app par son identifiant
  (« ca.kazo.word s'ouvrira dans le Kazo OS Store après la connexion. »), pas par son nom :
  l'écran de connexion ne lit pas le catalogue, qui demande le réseau et la session.
- La ligne `app-link.store-opened` du journal (§6) est laissée au chantier 3.

## Liste des tâches

- [x] Lire l'ADR 0187, l'app Store (`--app=`) et l'API d'installation.
- [x] Écrire cette ADR (fr + en) et mettre à jour les index.
- [x] Poser les quatre questions à Benoit dans le fil.
- [x] Chantier 1 : lire le lien, démarrage invité, Store après connexion.
- [ ] Chantier 2 : installer et lancer, erreurs, journal.
- [ ] Chantier 3 : tests Playwright, doc, entrée de changement.
