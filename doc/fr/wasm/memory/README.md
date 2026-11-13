> [English version](../../../en/wasm/memory/README.md)

# Mémoire du projet Kazo OS

État du projet et conventions de travail au **2026-10-31**. Ce dossier sert de
point de reprise : un nouveau contributeur (humain ou agent) y trouve ce qui
est fait, comment on travaille et ce qu'il ne faut pas refaire. Il est mis à
jour à la fin de chaque série ou quand une convention change.

## Où en est le projet

- **Code actif** : `wasm2/KazoOs/` (OS simulé en Blazor WebAssembly, .NET 10,
  solution `Kazo.Os.sln`). `src/` est l'ancienne version, gardée comme
  référence.
- **Version** : Kazo OS et le SDK passent en **3.3.3** (branche `release/3.3.3`,
  2026-11-13). Benoit a choisi un numéro correctif bien que l'API publique
  grandisse (comme la 2.2.1) ; la 3.4.0 reste réservée au SDK en préversion
  (TODO #456). Contenu : lien direct `?autolaunch=<appId>` (ADR 0258, série
  `app-link/base`, PR #490), module commun des apps Craft
  `Kazo.Os.AppSdk.Craft` (ADR 0259, série `craft-host/base`, PR #491),
  fenêtres qui restent sur leur écran (ADR 0263, PR #484) et apps du Store
  proposées pour un fichier (ADR 0257, PR #488). 450 lignes d'API repliées
  dans `PublicAPI.Shipped.txt`, 11 entrées dans `changes/3.3.3.json` ;
  publiée quand la PR arrive dans `main`. Ensuite : Kazo Word 0.1.1
  (`canChangeScreen`), chantier Store de Kazo Spreadsheets, liens « Essayer »
  des README.
- **Version 3.3.2** (2026-11-13, publiée), une version corrective : un fichier peut faire 128 Mio au lieu
  de 16 Mio, écrit et lu par lots de 16 Mio (ADR 0245, PR #455) ; Kazo Word
  s'installe dans l'OS publié. 1 ligne d'API d'Infrastructure.Browser repliée,
  1 entrée dans `changes/3.3.2.json`.
- **Version 3.3.1** (2026-11-13, publiée), une version corrective : le service worker publié sert la page
  d'une app installée chargée dans une iframe (ADR 0243, PR #451) et les
  fenêtres gardent leur place dans la page, un iframe ne se recharge plus quand
  sa fenêtre passe devant (ADR 0244, PR #452). 3 lignes d'API de Windowing.Core
  repliées (`WindowDocumentOrder`), 2 entrées dans `changes/3.3.1.json`.
- **Version 3.3.0** (2026-11-11, publiée) : numéro de build et état de la version à la connexion (ADR 0236),
  démarrage sur la dernière version et remplacement d'un worker plus ancien
  (ADR 0237, 0239, 0241), la panne d'une app reste dans sa fenêtre (ADR 0238),
  page d'erreur détaillée (ADR 0240), « Autoriser pour la machine » retire le
  refus de l'utilisateur (PR #428), CoreLib entière et contrôle exact des API
  pour les apps installées (ADR 0242). 115 lignes d'API repliées (ajouts au SDK
  dans App Abstractions et AppSdk.Blazor, d'où la version mineure), 7 entrées
  dans `changes/3.3.0.json`.
- **Version 3.2.1** (2026-11-08, publiée), une version corrective : la migration du renommage de 3.0.0 écrit
  le nouveau hash des fichiers de configuration réécrits, et une réparation unique
  au démarrage (`RenamedContentRepair`, [`rename-migration.md`](../dev/rename-migration.md))
  rend les fichiers que 3.0.0 à 3.2.0 laissaient sur un contenu supprimé
  (`ProviderFailure`, approbation d'IRC bloquée ; PR #422), et le Store relit le
  catalogue officiel dont la migration avait réécrit l'adresse en
  `KazoOs-AppMarketPlace` (`RenamedAppRepositories`, PR #424). 17 lignes d'API
  d'Infrastructure.Browser repliées, aucun changement du SDK, 2 entrées dans
  `changes/3.2.1.json`.
- **Version 3.2.0** (2026-11-07, publiée) : l'accord du vrai réseau émis par le
  serveur avec ses ports (ADR 0233), 68 lignes d'API de Platform Core et Server
  Contracts repliées, 1 entrée dans `changes/3.2.0.json`. Elle a débloqué le
  chantier IRC-3 de l'app IRC.
- **Version précédente** : Kazo OS et le SDK passent en **3.1.0** (`build/Versions.props`),
  préparée le 2026-11-07 par le chantier `os-3-1/7-release-3-1-0` de la série
  `os-3-1/base` ; elle est publiée (tag `v3.1.0`, GitHub Release, nuget.org)
  quand `os-3-1/base` arrive dans `main`. Elle replie 366 lignes d'API dans
  `Shipped` (styles de fenêtre WinForms, fenêtres enfants, vrai réseau des apps
  de discussion, ADR 0231 à 0235) et regroupe 15 entrées dans `changes/3.1.0.json`. La 3.0.0 (2026-11-05, publiée,
  renommage en Kazo OS) n'avait pas regroupé ses 15 entrées : elles sont rangées
  après coup dans `changes/3.0.0.json`. La 2.2.2 (2026-11-01) compile la clé publique du catalogue officiel dans l'OS
  (`AppPackageDefaults.OfficialCatalogSigningKey`, PR #349) : tout paquet du Kazo
  Store doit porter la signature du catalogue. La 2.2.1 (2026-11-01, publiée) a un numéro de correctif choisi par Benoit bien que l'API grandisse : elle
  verse dans `Shipped` 190 lignes d'API (boutons Précédent/Suivant de la
  souris, menus de fichier par écran…) et regroupe 11 entrées dans
  `changes/2.2.1.json`. Elle apporte ce que les apps tierces attendent pour le
  Kazo OS Store : ADR 0219 (signature des auteurs), 0221 (toutes les ressources
  statiques dans le paquet, CSS isolé) et 0222 (satellites des apps
  installées). Le catalogue doit demander le `manifest.json` du paquet
  (issue Kazo-ca/Os-AppMarketPlace#6). La 2.2.0 (2026-10-31) a versé
  dans `Shipped` les 3020 lignes d'API ajoutées depuis la 2.1.0 (Kazo OS Store,
  modèle `kazoos-solution`, thèmes…). Le SDK 2.0.0 a retiré `Kazo.Os.AppSdk.FileView` et
  refuse les apps 1.x
  ([ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md)).
  La 2.1.0 (2026-10-26) est la première version publiée automatiquement
  (tag, GitHub Release, nuget.org ; ADR 0173) ; elle a versé dans `Shipped`
  tous les ajouts depuis la 2.0.
- **Séries terminées et fusionnées dans `main`** :

| Série | Contenu | Bilan |
| --- | --- | --- |
| Refonte, phases 1 à 4 | API fichier `Kazo*`, manifeste en classe C#, réglages et journaux en fichiers, `/proc` et `/dev`, shell POSIX, corbeille, capacités dangereuses, apps de base sur le SDK public | [`ecosystem-rebuild-plan.md`](../dev/plans/ecosystem-rebuild-plan.md) |
| Phase 5 | Mesure CPU et mémoire, « Not responding », arrêt propre, processus en Web Workers | [`execution-isolation-plan.md`](../dev/plans/execution-isolation-plan.md) |
| Phase web | ASP.Kazo (MVC, vues `.razor`/`.cshtml`), sites Blazor, sites statiques, rendu HTML du navigateur, fenêtres détachées | [`asp-kazo-plan.md`](../dev/plans/asp-kazo-plan.md) |
| V3 (`v3/1` à `v3/16`) | Fin de la migration wasm v3, SDK 1.0 gelé, publication trimmée | [`wasm-v3-final-status.md`](../dev/plans/wasm-v3-final-status.md) |
| V3.1 | Géométrie et titre des fenêtres, SDK 1.1 | [`v3-1-series-plan.md`](../dev/plans/v3-1-series-plan.md) |
| EXEC (`exec/*`) | Limites de processus, OOM killer, passerelles distantes, boîte « ne répond pas », `KazoBackground`, OS servi par `Kazo.Os.Server`, isolation cross-origin, SDK 1.2 | [`execution-followups-plan.md`](../dev/plans/execution-followups-plan.md) |
| DESK (`desk/0` à `desk/6`) | Contrôle de fichiers fourni par l'OS, menu contextuel central, entrées de menu des apps, bureau, SDK 2.0 | [`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md) |
| Multi-moniteur (`multi-monitor/0` à `multi-monitor/9`) | Bureau étendu : un écran par surface d'affichage, `DisplayId` et zone de travail par écran, fenêtres déplacées par portail, service d'écrans (Chrome/Edge, repli ailleurs), une barre des tâches par écran, menus « Autre écran » et voile d'identification, Paramètres ▸ Écrans, glisser entre écrans ; aucun changement du SDK des apps | [`multi-monitor-plan.md`](../dev/plans/multi-monitor-plan.md) |
| File Explorer (`explorer/1` à `explorer/9`) | Barre latérale redimensionnable, état vide dans la liste, glisser vers l'ordinateur, raccourcis, lecteurs réseau suivis, erreurs en `MessageBox` ; le `.zip` ouvert comme dossier est fait dans `todo/base` (`todo/10-zip-as-folder`, issue #241) | [ADR 0185](../dev/adr/0185-file-explorer-improvements.md), [`file-explorer.md`](../dev/file-explorer.md) |
| Invité (`guest/*`) | Démarrage en invité quand aucun compte n'existe, élévation par simple confirmation, « Créer mon compte » (migrer ou supprimer l'invité), crédits à la connexion | [ADR 0187](../dev/adr/0187-guest-start-and-sign-in-credits.md), [`guest-account.md`](../dev/guest-account.md) |

- **Depuis DESK** (sur `main`, octobre 2026) : redémarrage proposé quand le
  build change sous la page (ADR 0162), aperçu local du déplacement des
  fenêtres (ADR 0163), icônes des apps et menu de l'icône de fenêtre
  (ADR 0164), accueil et tiroir mobiles (ADR 0165, 0166), publication GitHub
  Pages combinée PWA + legacy (ADR 0167), paquets NuGet mis à jour.
- **Clés d'accès et comptes** (octobre 2026, PR vers `main`) : connexion par
  clé d'accès WebAuthn et trousseau format 2 à emplacements (ADR 0168,
  [`passkeys.md`](../dev/passkeys.md)), ajout et suppression de comptes par un
  administrateur (ADR 0169). Ajouts au SDK dans `PublicAPI.Unshipped.txt`
  (prochaine mineure).
- **Explorateur mobile** (octobre 2026, PR #212 vers `main`) : point d'entrée
  mobile des apps honoré, explorateur de fichiers mobile, dialogues en plein
  écran sur mobile, un toucher ouvre (`KazoFileView.OpenOnTap`) (ADR 0171).
- **Depuis le multi-moniteur** (sur `main`, 28 au 30 octobre 2026) : dossier de
  l'ordinateur monté comme lecteur réseau (ADR 0181), `kazopkg install` depuis
  GitHub (ADR 0182), présentation des fonctionnalités `features.md` (ADR 0192),
  heure locale et fuseau horaire (ADR 0193, chantier 2 de l'issue #242),
  menus contextuels de la barre des tâches (ADR 0194, chantier 4 de #242).
  Les ADR du Kazo OS Store (0188 à 0190) sont acceptées, rien n'est codé (issues
  `TODO` #246 à #257). Le serveur local installable (ADR 0191, issue #267) est
  codé dans la série `todo/base` (chantier `todo/14-installable-server`,
  [`installable-server.md`](../dev/installable-server.md)) ; restent à essayer
  en vrai les scripts Windows et macOS et l'image Docker.
- **Bouton Marche/arrêt dans le menu Démarrer** (ADR 0195, PR #291, fusionnée
  le 2026-10-30) : « Changer d'utilisateur » reste grisé, choix de
  Benoit le 2026-10-30 (ADR 0201, issue #290 ; prototype non fusionné sur la
  branche `switch-user/simultaneous-sessions-prototype`).
- **Thèmes de curseurs** (ADR 0205, série `todo/base`, chantier
  `todo/9-cursor-themes`, issue #257) : dossiers `cursor.theme` de PNG dans
  `/etc/kazoos/cursors/` et `~/.config/kazoos/cursors/`, `cursors.config`,
  thèmes intégrés Système (défaut), Kazo OS Néon et Gothique Pourpre,
  appliqués à toute la surface par `cursorTheme.js`, apps comprises. Fusionné
  dans `todo/base` (PR #301) ; Benoit a confirmé le 2026-10-31 le thème par
  défaut Système et la portée partout.
- **Série `todo/base`** (2026-10-30, toutes les issues `TODO` en parallèle,
  une PR finale vers `main`, branches `todo/<n>-<sujet>`, ADR 0197 à 0211
  réservées) : chantier 1 du Kazo OS Store fait, modèle
  `kazoos-solution` et workflow de publication vers le catalogue (ADR 0207,
  [`app-repository.md`](../framework/app-repository.md), issue #246). Le
  dépôt `Os-AppTemplate` ne sera régénéré qu'avec un sous-module sur la
  release qui contient ce modèle, la 2.2.0 (PR en brouillon dans ce dépôt).
  Fonds d'écran en image faits aussi ([ADR 0202](../dev/adr/0202-desktop-wallpaper-images.md),
  issue #254, [`wallpapers.md`](../dev/wallpapers.md)).
  L'ADR 0206 est prise par l'issue #247 (`wwwroot/` des apps installées servi
  par le service worker, [ADR 0206](../dev/adr/0206-serve-installed-app-assets.md)).
- **Packs d'icônes** (ADR 0203, issue `TODO` #255, série `todo/base`) : packs
  PNG pour les apps et les fichiers dans `/usr/share/icons` et
  `~/.local/share/icons`, choisis dans Paramètres ▸ Apparence (document de
  réglages `icons`, clé `iconPackId`) ; le pack Néon intégré est installé
  d'office. Voir [`icon-packs.md`](../dev/icon-packs.md).
- **Kazo OS Store 3, sources de confiance** (ADR 0208, issue `TODO` #248, série
  `todo/base`) : installation pour soi dans `~/.local/share/kazoos/apps`,
  liste noire `/etc/kazoos/app-policy.config`, validation en deux étapes
  (`AppInstallApprovalOverlay`), `kazopkg --user/--system`, `sources add/remove`,
  `policy`, Paramètres ▸ Sources et liste noire. Le catalogue charge au
  démarrage les apps personnelles de tous et les masque par session (question
  posée à Benoit). Voir [`app-installation.md`](../dev/app-installation.md).
- **Kazo OS Store 5, l'app** (ADR 0212, issue `TODO` #249, série `todo/base`,
  branche `todo/5-hackstore-app`) : app système `org.kazoos.store`
  (`Kazo.Os.Apps.Store`), source `catalog:<nom>/<appId>@<version>`,
  `kazopkg install <appId>`, `AppUpdateMonitor` et notification, épinglage par
  défaut (`defaultPinsApplied` de `start-menu.json`). Réponses de Benoit
  (2026-10-31) : Q1 A redémarrage sans administrateur, Q2 A source
  `catalog:official/<appId>`, Q3 C notification seulement (pas de badge),
  Q4 B appId listé par plusieurs catalogues refusé (`apps.package.ambiguous`) ;
  Q3 et Q4 dans l'ADR 0215 (branche `todo/5b-hackstore-no-badge`). Voir
  [`app-installation.md`](../dev/app-installation.md#le-kazo-os-store-et-les-catalogues).
- **Kazo OS Store v2, packs de thèmes** (ADR 0213, issue `TODO` #253, série
  `todo/base`, branche `todo/18-theme-packs`) : format `.kazotheme`
  (`ThemePack.Read`), `kind: "theme-pack"` dans `catalog.json`, pack déballé
  tel quel dans `theme-packs/<id>/` et lu en place par les thèmes, fonds,
  icônes, sons et curseurs, onglet Thèmes du Store. Quatre questions posées à
  Benoit (fichiers lus en place, CI du catalogue, rien appliqué après
  l'installation, pas de badge), réponses A pour les quatre (2026-10-31). CI du catalogue :
  issue `TODO` AppMarketPlace #4.
- **Kazo OS Store, étapes suivantes** (ADR 0214, issue `TODO` #251, série
  `todo/base`, branche `todo/19-hackstore-later`) : ajout sans redémarrage
  (catalogue vivant `AppCatalog.Changed`), signatures ECDSA P-256
  (`[signing-keys]`, `KazoOsAppSigningKeys`), journal `.transaction`,
  `kazopkg rollback`/`repair`, `AppDataGuard`. Cinq questions posées à Benoit,
  réponse A à chacune. La clé du catalogue officiel a été créée par Benoit le
  2026-11-01 (`keygen`, secret `KAZO_STORE_SIGNING_KEY` du catalogue) ; sa clé
  publique est dans `AppPackageDefaults.OfficialCatalogSigningKey` ; outil pour les auteurs :
  issue `TODO` #321.
- **Bulles de notification** (ADR 0197, chantier 1 de #242, série
  `todo/base`) : bulles des notifications et « Update ready » en
  notification persistante avec « Redémarrer ». Voir
  [`notification-toasts.md`](../dev/notification-toasts.md).
- **Animations des fenêtres** (ADR 0198, chantier 3 de #242, branche
  `todo/12-window-animations` vers `todo/base`) : ouvrir, fermer, minimiser vers
  la barre de son écran, restaurer, agrandir, snap ; variables
  `--kos-window-anim-*` ([`window-animations.md`](../dev/window-animations.md)).
- **Sons du système** (ADR 0204, issue #256, série `todo/base`, branche
  `todo/8-os-sounds`) : jeux de sons freedesktop.org dans `/usr/share/sounds`
  (jeux intégrés `kazoos` et `soft` écrits par `FileSystemSeeder`) et
  `~/.local/share/sounds`, `sound.config`, Paramètres ▸ Sons. Pas d'API SDK pour
  jouer un son (question Q2 à Benoit). Voir [`system-sounds.md`](../dev/system-sounds.md).
- **Aperçus de la barre des tâches** (ADR 0199, chantier 5 de l'issue #242,
  série `todo/`, branche `todo/13-taskbar-previews` vers `todo/base`) : copie
  réduite en direct d'une fenêtre au survol de son bouton, et peek. Numéro 0199
  pris dans la réserve 0197 à 0205 de la série `todo/`.
- **Vrais sites dans le navigateur** (ADR 0210, série `todo/base`, issue #266) :
  le Navigateur est une app système avec `network.real.access` ; avec un
  serveur connecté, il ouvre les vrais sites par le proxy, cookies compris.
  Restent le proxy public et la Wayback Machine.
- **Point des notifications non lues à côté de l'horloge** (ADR 0196, PR #294,
  fusionnée sur `main` le 2026-10-31) : remplace la pastille du menu Démarrer des
  nouveautés ; ramenée dans `todo/base` par la fusion de `main`.
- **ADR** : 0001 à 0171 et 0173 à 0196 existent sur `main` (quelques numéros sautés ;
  0141 et 0172 n'ont pas servi). Repères récents : 0173 publication automatique,
  0174 annonce de nouvelle version, 0175 « Nouveau dossier » des dialogues, 0176
  dépôt template d'apps, 0177 barre de titre des fenêtres agrandies, 0178 à 0180,
  0183 et 0184 multi-moniteur, 0181 lecteur réseau, 0182 `kazopkg`, 0185 File
  Explorer, 0186 nouveautés ([`change-list.md`](../dev/change-list.md)), 0187
  invité et crédits, 0188 à 0190 Kazo OS Store ([`kazoos-store.md`](../dev/plans/kazoos-store.md)),
  0191 serveur local installable, 0192 `features.md` et exception à l'ADR 0135,
  0193 heure locale, 0194 menus de la barre des tâches, 0195 bouton
  Marche/arrêt du menu Démarrer, 0196 point des notifications non lues ; série `todo/base` : 0197 bulles de
  notification, 0201 « Changer d'utilisateur » reste grisé, 0203 packs d'icônes, 0207 modèle `kazoos-solution`, 0208 sources de confiance du Kazo OS Store, 0212 app Kazo OS Store, 0213 packs de thèmes, 0214 Kazo OS Store sans redémarrage, signature et transactions, 0215 réponses du Kazo OS Store (pas de badge, appId ambigu refusé) (les
  autres numéros jusqu'à 0211 sont réservés par ses chantiers ; 0216 à 0219 par le lot 3 : 0216 mode « Ne pas déranger », 0217 fond d'écran de l'accueil mobile, 0218 « Définir comme fond d'écran » dans le menu des images, 0219 outil de signature des auteurs d'apps) ; 0220 boutons Précédent et Suivant de la souris, série `mouse-back/base` ; 0221 toutes les ressources statiques d'une app dans son paquet et CSS isolé d'une app installée (branche `app-assets/static-web-assets`) ; 0222 traductions des apps installées, assemblages satellites (#337, branche `app-assets/satellite-assemblies`) ; 0223 redémarrer sur la nouvelle version (série `version-cache/base`) ; 0224 réessayer le démarrage après une erreur du serveur (branche `boot/retry-server-errors`) ; 0225 servir les fichiers des apps installées à chaque page (branche `installed-apps/serve-uncontrolled-page`) ; 0226 mot de passe oublié, réinitialiser depuis la connexion (branche `login/forgot-password-factory-reset`) ; 0227 la réinitialisation efface tout le navigateur (branche `login/factory-reset-erase-everything`) ; 0228 HackerOS devient Kazo OS (série `rename/base`) ; 0229 « Kazo OS » avec une espace, dépôts `Os-App-*`, redirection vers os.kazo.ca ; 0230 migration du renommage selon la version précédente ; 0231 les lecteurs réseau de l'utilisateur comptent comme ses fichiers personnels (#381, ADR dans `docs/network-drive-app-access`, code dans `network-drives/app-access`, pour l'app Webamp) ; 0232 icônes d'apps illustrées (série `icons/base`) ; 0233 le serveur émet l'accord du vrai réseau avec ses ports (série `real-network/base`, chantier 4 ; l'ADR prévue au départ pour ce numéro a été versée dans l'ADR 0219) ; 0234 styles de fenêtre inspirés de WinForms (série `os-3-1/base`, version 3.1.0) ; 0235 le vrai réseau pour les apps de discussion (WebSocket dans le SDK, TLS par le relais, ports demandés ; #395, ADR dans `docs/real-network-for-chat-apps`, code dans la série `real-network/base`, pour l'app IRC) ; 0236 numéro de build et état de la version sur l'écran de connexion (branche `login-version/build-number-and-status`) ; 0237 activer le worker d'une page plus récente, 0238 la panne d'une app reste dans sa fenêtre, 0239 démarrer sur la dernière version (branche `apps/failure-in-window-and-newer-worker`) ; 0240 page d'erreur détaillée avec les secrets masqués (branche `error-page/more-details`) ; 0241 remplacer un worker plus ancien au démarrage (PR #429, branche `service-worker/replace-older-worker-at-boot`) ; 0242 l'API .NET qu'une app installée trouve dans l'OS publié, CoreLib entière et vérification exacte des signatures (branche `apps/framework-api-for-installed-apps`) ; 0243 la page d'une app installée chargée dans une iframe est servie par le service worker publié (branche `fix/installed-app-iframe-page`, pour Kazo Word) ; 0244 garder l'ordre des fenêtres stable dans la page, un iframe ne se recharge plus quand sa fenêtre passe devant (branche `windows/stable-dom-order`) ; 0245 un fichier peut faire 128 Mio, écriture et lecture par lots de 16 Mio (branche `files/raise-max-file-size`, pour Kazo Word, version 3.3.2) ; 0246 à 0256 et 0260 à 0262 sont pris par les séries en cours (`feature/base` et d'autres) ; 0257 app du Store proposée pour un fichier, 0258 lien direct de lancement, 0259 module commun des apps Craft et 0263 fenêtres qui restent sur leur écran sont dans `main` (version 3.3.3) ; 0264 exports d'une app Craft remis à sa fenêtre (série `craft-exports/base`, pour Kazo Pdf). **Prochain numéro libre :
  0265.** Avant d'en
  prendre un, vérifier `dev/adr/` sur `main` et les PR ouvertes.

## Conventions de travail

Les règles de travail avec Benoit (langue, questions, issues `TODO`, noms de
branche, fusion vers `main`, numéros d'ADR, entrées de changement) sont dans la
section **IV** de [`AGENTS.md`](../../../../AGENTS.md), que Claude Code lit par
[`CLAUDE.md`](../../../../CLAUDE.md). En résumé :

- **Langue** : on répond à Benoit en français.
- **À faire** : « ajouter à la liste des choses à faire » = une issue GitHub
  avec le label `TODO` et une section « Dépendances » ; « qu'est-ce qu'il reste
  à faire » = les issues `TODO` ouvertes d'abord, puis la doc.
- **Aucun jeu sur ce repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)) :
  ni code, ni mention dans la doc, les plans, les ADR ou la mémoire.
- **Branches** : noms descriptifs (`taskbar/4-context-menus`), jamais
  `claude/project-thread-…`. Une branche d'intégration `<série>/base` par série, une branche
  `<série>/<n>-<sujet>` par chantier, PR vers la branche d'intégration. Les ADR
  d'une phase sont écrits avant son code.
- **`main`** : seul Benoit fusionne dans `main` et décide quand une branche
  d'intégration y va.
- **Questions** : on peut les noter dans un ADR, mais on les pose toujours à
  Benoit directement, avec des choix, le contexte et les implications.
- **Documentation** : écrite en français dans `doc/fr/`, traduite en anglais dans `doc/en/` (mêmes dossiers, mêmes noms de fichiers, en anglais) ; chaque changement met à jour les deux langues. Les readme à la racine : `readme.md` en anglais, `readme-fr.md` en français, chacun avec un lien vers l'autre en première ligne. `dev/` pour le
  développement de l'OS, `framework/` pour créer des apps et des sites,
  `memory/` pour ce dossier. Chaque changement met à jour la doc touchée et
  l'index [`../README.md`](../README.md) ; les docs remplacées vont dans
  `dev/old/`. Un ADR n'est jamais modifié : un nouvel ADR le remplace.
- **Barre de fusion** : build et tests locaux verts (suites E2E une à la fois),
  `build/publish-trimmed.sh` et la compatibilité du SDK
  (`Tests/fixtures/sdk-compat`) quand ils sont concernés. Les GitHub Actions
  tournent de nouveau depuis le 2026-10-24 et vérifient chaque PR vers `main`
  et `*/base` (tests en échec relancés une fois ; Playwright chaque nuit sur `main`
  si `main` a changé, un échec ouvre une issue) : [`../dev/ci-workflows.md`](../dev/ci-workflows.md).
- **SDK** : l'API publique est suivie par `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` ; un ajout passe par `Unshipped` jusqu'à la
  publication suivante (`build/tools/ship-public-api.sh`).
- **Publier** : changer `KazoOsVersion` dans `build/Versions.props` et
  fusionner dans `main` ; le workflow `Release Kazo OS` crée le tag, la
  Release GitHub et pousse sur nuget.org ([`releases.md`](../dev/releases.md),
  ADR 0173).

## Décisions et contexte hors du code

| Note | Contenu |
| --- | --- |
| [`decisions-not-built.md`](decisions-not-built.md) | Décisions prises par Benoit en discussion et pas encore codées ni écrites ailleurs : fenêtres et barre des tâches (chantiers restants de #242), vrais sites dans le navigateur (#266 : proxy public et Wayback restants), apps tierces portées, apps hébergées par le serveur, idées tirées de daedalOS, éditeurs. |
| Renommage (2026-11-04) | HackerOS devient Kazo OS ([ADR 0228](../dev/adr/0228-rename-to-kazoos.md), [ADR 0229](../dev/adr/0229-kazo-os-display-name-and-new-address.md), [ADR 0230](../dev/adr/0230-rename-migration-by-previous-version.md)) : série `rename/base` en cours, chantiers 2 à 8 listés dans l'ADR. Benoit renomme lui-même le dépôt en `Kazo-ca/Os`. Dépôts d'apps renommés `Os-App-*`, `Os-AppTemplate`, `Os-AppMarketPlace`. Le site passe à `os.kazo.ca` ; `hackos.kazo.ca` est redirigé par un Worker Cloudflare (PR #362), sans récupération des données. Chantiers 3 et 4 réunis (SDK 3.0.0, paquets `Kazo.Os.*`, types `Kazo*`) : l'enregistrement `HackIcon` devient `KazoSurfaceIcon`, car le composant `HackerIcon` prend le nom `KazoIcon`. Benoit (2026-11-04) : le magasin s'appelle « Kazo OS Store » (chantier 5). Les badges NuGet du readme visent `Kazo.Os.AppSdk` et `Kazo.Os.Templates`. Chantier 5 fait (branche `rename/5-system-names`) : commandes `kazopkg` et `kazobackup` (les anciens noms restent des alias qui avertissent), extensions `.kazoapp` et `.kazotheme` (les anciennes restent lues), chemins `/etc/kazoos`, `~/.config/kazoos`, `/etc/kazoweb`..., ASP.Kazo, « Kazo OS Store », Kazo Paint, section de configuration `KazoOs` et préfixe `KAZOOS_` (les anciens restent lus, `KazoOsConfigurationNames`), serveur `kazoos-server`, modèles `kazoos-*`. Portée élargie le 2026-11-04 (Benoit : « On modifie aussi ce qui est inscrit sur le disque. Plus aucune mention de HackOs. ») : les noms enregistrés sont renommés aussi, `org.kazoos.*`, `pub.kazoos`, capacités `kazoos.*`, bases `kazoos*`/`kazoweb.*` et clés de stockage, identifiants de thèmes et de sons, `--kazoos-*`, `data-kazo-menu*`, `blazor.kazo.js`, branche `kazoos-packages`, `external/KazoOs`, `IKazo*` ; les données enregistrées sous les anciens noms sont migrées par le chantier 6. Chantier 6 fait (branche `rename/6-storage-migration`, [`rename-migration.md`](../dev/rename-migration.md)) : au démarrage, avant que l'OS ouvre sa base, les bases `hackeros*`/`hackweb.*` sont copiées sous leurs nouveaux noms puis supprimées, les anciennes clés de stockage renommées, les anciens caches supprimés, les chemins système, fichiers de configuration et ids enregistrés réécrits (jamais les documents) ; la version qui a écrit les données est maintenant gardée dans `localStorage` (`kazoos.osVersion`), les sauvegardes portent `osVersion`, une sauvegarde d'avant 3.0.0 est migrée à la restauration, le serveur renomme `hackeros.db` en `kazoos.db`. Les fixtures IndexedDB restent `hackeros`. Chantier 7 fait (branche `rename/7-new-address`) : le `CNAME` de Pages (`assemble-pages.ps1`) vaut `os.kazo.ca`, l'avis « version d'origine » de `ProjectInformation` s'affiche sur `os.kazo.ca`, readmes et docs pointent vers `https://os.kazo.ca/` ; `hackos.kazo.ca` ne reste que là où il est question de l'ancienne adresse et de sa redirection. Gardés : sites simulés `.hack`, `hacker-grid`, `hackos.kazo.ca`, sel `hackeros-server-account`, secrets `HACKEROS_APP_SIGNING_KEY` et `HACK_STORE_*`. Chantier 9 (branche `rename/9-kos-short-names`, Benoit 2026-11-04) : les préfixes courts « HOS » deviennent « KOS » par un script : `.kosbak` (`.hosbak` encore proposé à la restauration), codes de diagnostic `KOSAPP*`, `KOSSDK*`, `KOSWEB*`, `KOSBG*`, `KOSCHG*`, variables CSS `--kos-*` (un fichier `.theme` en `--hos-*` est encore lu ; thèmes et réglages enregistrés réécrits par `RenamedNames`), `data-kos-*`, classes `kos-*` ; les ADR acceptés, les listes de changements publiées et les fixtures de compatibilité SDK gardent `HOS`. Liste complète : [`sdk-packages.md`](../framework/sdk-packages.md#noms-du-système-chantier-5), [noms courts](../framework/sdk-packages.md#noms-courts-chantier-9). |
| [`known-test-failures.md`](known-test-failures.md) | Échecs de tests préexistants connus (à nommer dans une PR, pas à cacher) et astuces d'environnement récentes. |

## Environnement et pièges

Les contournements de build (NuGet, conteneur Linux, Playwright) et les tests
instables connus sont regroupés dans [`../dev/pitfalls.md`](../dev/pitfalls.md)
et [`../dev/building-and-testing.md`](../dev/building-and-testing.md).

## Notes historiques

Notes écrites pendant les premières phases ; les chemins et nombres qu'elles
citent datent de leur écriture.

| Note | Contenu |
| --- | --- |
| [`blazor-window-manager-bugs.md`](blazor-window-manager-bugs.md) | Leçons de l'ancien gestionnaire de fenêtres Blazor (appel de base oublié dans `OnAfterRenderAsync`, état de la solution v3 en août 2026). |
| [`manifest-serializer-notes.md`](manifest-serializer-notes.md) | Sérialiseur strict de `AppManifest` et fixture canonique. |
| [`p2-idb-008-filesystem-repository.md`](p2-idb-008-filesystem-repository.md) | Analyse des transactions du dépôt de fichiers IndexedDB (phase 2). |
