> [English version](../../../../en/wasm/dev/adr/0236-build-number-and-version-status-at-sign-in.md)

# ADR 0236 : numéro de build et état de la version sur l'écran de connexion

## Statut

Acceptée le 2026-11-08 ; Benoit a choisi toutes les recommandations
(« Je suis d'accord avec tes recommandations », voir « Questions posées à
Benoit »). Demande de départ : « sur la page de connexion, afficher à droite de
la version si la version actuelle est la dernière ou si elle est obsolète, avec
un numéro de build qui augmente à chaque version ; on peut le construire dans
l'action de publication avec un compteur ».

- **Complète :** l'[ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (`version.json` reçoit le numéro de build, comparé aussi) et
  l'[ADR 0173](0173-automatic-releases-from-the-version-file.md) (les workflows
  calculent le numéro).
- **Ne change pas :** `build/Versions.props` reste la seule version ; le
  bandeau et le redémarrage des ADR 0162, 0174 et 0223.

## Contexte

- L'écran de connexion (et les écrans de création du premier compte, de
  récupération et d'erreur) affiche « Kazo OS 3.2.1 » dans son pied de page
  (`ProjectInformation`, ADR 0187 §7). Rien ne dit si c'est la version que le
  site sert maintenant.
- Le site publie `/version.json` ; `HostReleaseMonitor` le relit toutes les
  5 minutes et la première lecture arrive 5 minutes après le démarrage :
  l'écran de connexion ne le sait jamais à temps.
- Une publication de GitHub Pages sans changement de `Versions.props` garde le
  même numéro de version. Seule l'empreinte de `dotnet.js` (ADR 0162) voit ce
  changement, et pas toujours.

## Décision

1. **Numéro de build = nombre de commits** de `HEAD`
   (`build/build-number.sh`, `git rev-list --count HEAD`). Sur `main`, il
   augmente à chaque merge, et tous les workflows qui construisent le même
   commit donnent le même numéro : GitHub Pages (`deploy-to-pages.yml`), le zip
   de la Release et le serveur (`release.yml`). Les workflows clonent tout
   l'historique (`fetch-depth: 0`) ; le script refuse un clone superficiel, qui
   compterait trop peu.
2. Le numéro passe à MSBuild par la variable d'environnement
   **`KazoOsBuildNumber`** (lue comme une propriété par tous les projets).
   `OS/Kazo.Os.Ecosystem` ne garde qu'un entier positif et l'écrit :
   - en `AssemblyMetadata("KazoOsBuildNumber", "<n>")` de son assemblage, que
     `HostBuildNumber.Read(Assembly.GetEntryAssembly())` relit dans l'OS ;
   - dans `version.json` : `{ "product": "Kazo OS", "version": "3.2.1", "build": 1234 }`.
   Une build locale n'a pas de numéro : `version.json` n'a pas de champ
   `build` et l'écran dit **« build dev »**.
3. **`HostReleaseMonitor`** reçoit le numéro en cours (`RunningBuild`) et tient
   un **`Status`** (`HostReleaseStatus`) : `NotChecked`, `UpToDate`,
   `UpdateAvailable`, `Unreachable`, avec l'événement `StatusChanged` et
   `AvailableBuild`.
   - Autre version servie : mise à jour, comme avant (`ReleaseAvailable`).
   - Même version, deux numéros connus et différents : mise à jour sans
     nouvelle version (le bandeau existant s'affiche sans numéro).
   - Un des deux numéros inconnu (build locale, site sans numéro) : seule la
     version compte.
   - Fichiers changés sous la page (ADR 0162) : mise à jour aussi.
   - Site injoignable ou fichier illisible : `Unreachable`, jamais une mise à
     jour.
   - Une page obsolète le reste jusqu'au redémarrage, même si le site devient
     injoignable : ses fichiers ne changent pas.
4. L'hôte navigateur **vérifie dès le démarrage** (`CheckAsync` en plus de la
   boucle de 5 minutes), donc avant ou pendant l'écran de connexion.
5. **Affichage** dans `ProjectInformation`, sur une ligne qui passe à la ligne
   sur un téléphone : « Kazo OS 3.2.1 » · « build 1234 » · une pastille
   (`role="status"`) :
   - « À jour » (vert, `--kos-success`) ;
   - « Obsolète : 3.2.2 (build 1240) disponible », « Obsolète : build 1240
     disponible » ou « Obsolète : mise à jour disponible » (ambre,
     `--kos-warning`) ;
   - « Vérification… » avant la première réponse (point qui pulse, immobile
     avec mouvement réduit) ;
   - « État inconnu (hors ligne) ».
   Sans moniteur (hôte sans navigateur), la pastille n'apparaît pas. Le
   bandeau « Redémarrer pour mettre à jour » reste le moyen de mettre à jour.

## Conséquences

- Le numéro est le même pour une même source, partout où elle est construite
  par la CI ; une reconstruction du même commit garde son numéro (ses fichiers
  sont identiques). Un commit qui ne touche que la documentation augmente le
  compteur sans publication : les numéros publiés peuvent sauter.
- Quand le cache de GitHub Pages réutilise une publication (aucune source de
  l'OS changée), `version.json` garde le numéro de cette publication : les
  sessions ouvertes restent « À jour », ce qui est juste.
- Un hôte d'un autre dépôt qui utilise les paquets sans passer
  `KazoOsBuildNumber` affiche « build dev » et compare les versions seulement.
- Le clone complet ajoute quelques secondes aux workflows.

## Options écartées

- **`github.run_number`** du workflow Pages (idée de départ de Benoit) : le zip
  de la Release et le serveur sont construits par un autre workflow, avec un
  autre compteur ; le même OS aurait plusieurs numéros.
- **Compteur dans une variable du dépôt** incrémenté par le workflow : demande
  un jeton qui écrit les variables, et deux exécutions en parallèle peuvent
  prendre le même numéro.
- **Dernière Release GitHub** comme référence : déjà écartée par l'ADR 0174
  (un serveur auto-hébergé serait toujours « obsolète », appel à github.com).

## Questions posées à Benoit

Posées dans le fil « Afficher l'état de la version au login » le 2026-11-08 :

1. Source du numéro : **A nombre de commits (recommandé)**, B
   `github.run_number`, C compteur dans une variable du dépôt.
2. Référence de « dernière version » : **A le `version.json` du site
   (recommandé)**, B la dernière Release GitHub.
3. Build locale : **A « build dev » (recommandé)**, B rien.
4. Où l'afficher : **A écran de connexion et Paramètres ▸ À propos
   (recommandé)**, B écran de connexion seulement. Paramètres est une app du
   SDK public, qui ne voit pas `HostReleaseMonitor` : elle lit le numéro par
   `KazoOsSdk.BuildNumber` (ajout au SDK) et affiche « build 1234 » à côté de
   la version, sans pastille.

## Mise en œuvre

- `Platform/Kazo.Os.Platform.Blazor/LazyLoading/` : `HostBuildNumber`,
  `HostReleaseStatus`, `HostReleaseMonitor` (`RunningBuild`, `AvailableBuild`,
  `Status`, `StatusChanged`, `ParseBuild`).
- `OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj` : métadonnée et
  `version.json`.
- `OS/Kazo.Os.Ecosystem.Host/` : `ProjectInformation.razor(.cs, .css)`,
  textes `Project.Build*` et `Project.Status.*`, vérification au démarrage
  dans `App.razor`, numéro passé au moniteur dans
  `KazoOsBrowserHostExtensions`.
- `Shared/Kazo.Os.App.Abstractions/KazoOsSdk.cs` : `BuildNumber`,
  `ReadBuildNumber` ; Paramètres ▸ À propos (`SettingsWindow.razor`).
- `build/build-number.sh`, `.github/workflows/deploy-to-pages.yml`,
  `.github/workflows/release.yml`.
- Tests : `HostReleaseMonitorTests`, `ProjectInformationTests`.
