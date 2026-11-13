> [English version](../../../../en/wasm/dev/adr/0265-direct-link-survives-update-restart.md)

# ADR 0265 : le lien direct survit au redémarrage pour une mise à jour

## Statut

Proposée le 2026-11-13. Série `app-link-reload/base`. Les questions sont posées
à Benoit dans le fil du projet (voir [Questions](#questions-posées-à-benoit)) ;
le code suit les recommandations en attendant ses réponses.

- **Remplace en partie :** l'[ADR 0258](0258-direct-app-launch-link.md) §2
  (« Recharger la page, ou la redémarrer pour une mise à jour, ne relance donc
  pas l'installation » et « rien n'est écrit dans le stockage »). Le reste de
  l'ADR 0258 ne change pas.
- **S'appuie sur :** l'[ADR 0239](0239-start-on-the-latest-version.md) (mise à
  jour au démarrage, porte de lancement), l'[ADR 0223](0223-restart-onto-the-new-version.md)
  (redémarrer sur la nouvelle version) et l'[ADR 0195](0195-start-menu-power-button.md)
  (« Redémarrer » du menu Démarrer).

## Contexte

Benoit, 2026-11-13 :

> Quand on utilise le lien pour essayer une application, il arrive que la page
> se rafraîchisse pour une mise à jour (exemple : on l'a visitée sur 3.3.3 sans
> créer de compte, puis on visite le lien quand le site est en 3.3.4) : la page
> se rafraîchit pour la mise à jour et on perd le query-string.

Le lien `?autolaunch=<appId>` (ADR 0258) est lu une fois au démarrage, gardé en
mémoire, puis retiré de l'adresse. Tout rechargement de l'onglet **après** cette
lecture le perd. Or le navigateur garde souvent l'ancienne version de Kazo OS
(cache du service worker), et plusieurs chemins rechargent l'onglet :

| Chemin | Où | Le lien était-il déjà lu ? |
| --- | --- | --- |
| Mise à jour au démarrage (ADR 0239 §1) | `App.razor`, `UpdateAtBootAsync` | Non : la lecture vient après, l'adresse est rechargée telle quelle. Mais si `version.json` répond après 5 s, ou si l'onglet a déjà essayé cette version, l'ancienne version démarre et lit le lien elle-même. |
| L'app ne s'ouvre pas, une version plus récente attend (ADR 0239 §2) | `HostUpdateLaunchGate`, dialogue « Redémarrer » | **Oui** : c'est justement le lien qui lance l'app. Le redémarrage arrive sur une adresse sans paramètre : l'app n'est pas lancée. |
| Nouvelle version activée par un autre onglet (ADR 0223) | `hostUpdate.js`, `OnActivatedElsewhere` | Oui, si l'autre onglet met à jour pendant l'installation. |
| « Redémarrer pour mettre à jour » de la notification, « Redémarrer » du menu Démarrer | `BrowserHostRestart` | Oui. |
| Échec de démarrage réessayé (garde de démarrage d'`index.html`) | `location.reload()` | Non : .NET n'a pas encore démarré. |

Le cas le plus probable de Benoit est la deuxième ligne : l'ancienne version
démarre, lit et retire le lien, ouvre la session invité, installe l'app ; la
porte de lancement refuse alors l'app parce qu'une version plus récente
attend, et « Redémarrer » recharge une adresse sans `?autolaunch=`.

## Décision

### 1. Le lien lu est aussi gardé dans l'onglet

Quand l'hôte lit un lien valide dans l'adresse, il le garde en mémoire
(`EcosystemHostState.PendingAppLaunch`) **et** dans le `sessionStorage` de
l'onglet, sous la clé `kazoos.appLaunchLink` (valeur : l'identifiant de l'app).
L'adresse perd toujours le paramètre (ADR 0258 §2).

Le `sessionStorage` survit aux rechargements de l'onglet, pas à sa fermeture,
et n'est pas partagé avec les autres onglets : seul l'onglet ouvert par le lien
peut le relancer (question Q1).

### 2. Au démarrage, l'adresse d'abord, puis l'onglet

Au démarrage, au même moment qu'avant (après la mise à jour au démarrage, avant
le premier écran) :

1. un lien dans l'adresse gagne, et remplace celui de l'onglet ;
2. sinon, le lien gardé par l'onglet est relu ; son identifiant repasse par
   `AppLaunchLink.IsValidAppId`, et une valeur mal formée est effacée ;
3. la suite ne change pas : démarrage en invité ou bandeau de l'écran de
   connexion, puis installation et lancement, ou Store à la page de l'app.

### 3. Effacé quand le lien a fini

Le lien gardé est effacé quand son traitement se termine : app lancée, Store
ouvert, erreur notifiée, ou **Annuler**. Un rechargement **après** cela ne
relance rien (comme l'ADR 0258).

Une exception : quand l'app n'a pas pu s'ouvrir parce qu'une version plus
récente de Kazo OS attend (ADR 0239 §2), le lien reste gardé. « Redémarrer »
recharge l'onglet sur la nouvelle version, qui relance le lien : l'app, déjà
installée, est lancée sans nouveau téléchargement. « Plus tard » efface le lien
(question Q2).

### 4. Ce qui ne change pas

- Le lien ne porte toujours qu'un identifiant d'app du catalogue officiel signé.
- Un rechargement manuel (F5) **pendant** le traitement le relance ; après, il
  ne fait rien.
- Fermer l'onglet oublie le lien.

### 5. Limite

C'est la version **déjà en cours d'exécution** qui lit le lien et recharge
l'onglet. Une copie en cache de Kazo OS 3.3.3 ou plus ancienne ne garde donc
pas le lien : la correction protège les mises à jour à partir de la version qui
la contient.

## Mise en œuvre

- `Kazo.Os.Platform.Core.AppLinks.AppLaunchLink.TryFromAppId(appId, out lien)` :
  relit un identifiant gardé (mêmes règles que `TryParse`).
- `IAppLaunchLinkStore` (hôte `Kazo.Os.Ecosystem.Host`) : `ReadAsync`,
  `SaveAsync`, `ClearAsync`. `BrowserAppLaunchLinkStore` appelle directement
  `sessionStorage` par l'interop JS de Blazor (aucun nouveau fichier JS) et ne
  lève jamais : un stockage refusé (navigation privée stricte) revient au
  comportement de l'ADR 0258.
- `App.razor` : lecture de l'adresse puis de l'onglet (§2), effacement à la fin
  du traitement du lien sauf si le dialogue de mise à jour s'est ouvert (§3),
  effacement sur « Plus tard ».
- Tests : `AppLaunchLinkTests` (`TryFromAppId`), tests de l'ordre adresse puis
  onglet, et un test de bout en bout de la publication réduite : l'onglet est
  rechargé pendant l'installation lancée par le lien, puis l'app s'ouvre quand
  même.

## Questions posées à Benoit

Posées dans le fil du projet le 2026-11-13.

- **Q1. Où garder le lien entre deux chargements ?** A le `sessionStorage` de
  l'onglet (recommandé : survit aux rechargements, oublié à la fermeture, pas
  partagé) ; B le `localStorage` avec une durée de vie (survit à la fermeture,
  mais une app pourrait s'ouvrir plus tard sans que l'utilisateur se souvienne
  du lien) ; C laisser le paramètre dans l'adresse tant que le lien n'a pas
  fini (simple, mais une adresse copiée pendant ce temps relance le lien
  ailleurs).
- **Q2. L'app est refusée parce qu'une version plus récente attend.** A garder
  le lien jusqu'au redémarrage et l'ouvrir sur la nouvelle version
  (recommandé) ; B l'oublier : l'utilisateur rouvre le lien lui-même.

## Chantiers de la série `app-link-reload/base`

| N° | Branche | Contenu |
| --- | --- | --- |
| 0 | `app-link-reload/0-adr-keep-link-across-restart` | Cette ADR, index, mémoire. |
| 1 | `app-link-reload/1-keep-link-across-restart` | Code, tests unitaires et de bout en bout, doc `dev/app-launch-link.md`, entrée de changement, `features.md`. |

## Liste des tâches

- [x] Trouver tous les chemins qui rechargent l'onglet.
- [x] Écrire cette ADR (fr + en) et mettre à jour les index.
- [x] Poser les questions à Benoit dans le fil.
- [ ] Chantier 1 : garder le lien dans l'onglet, tests, doc.
