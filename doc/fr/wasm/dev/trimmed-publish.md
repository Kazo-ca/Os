> [English version](../../../en/wasm/dev/trimmed-publish.md)

# Publication Release trimmée

## Objet

La publication Release de l'hôte Ecosystem (`OS/Kazo.Os.Ecosystem`) est
élaguée par ILLink (chantier `v3/14-trimming`, ADR 0106 et 0113) : le code du
framework .NET que Kazo OS n'utilise pas est retiré des fichiers
WebAssembly. Le premier chargement télécharge moins, et chaque Web Worker
(ADR 0070) démarre un runtime plus petit.

| Publication de l'Ecosystem | Fichiers `.wasm` | Taille | Brotli |
|---|---|---|---|
| avant (`PublishTrimmed=false`) | 273 | 50,9 Mio | 13,5 Mio |
| après (trimmée) | 134 | 26,5 Mio | 8,0 Mio |

Mesuré le 2026-10-17 avec `build/publish-trimmed.sh --compare` (fichiers des
satellites `fr/` compris). L'AOT est hors périmètre.

## Architecture

```
Ecosystem.csproj            PublishTrimmed=true, TrimMode=partial
        │
        ├─ CollectKazoOsBuiltInApplications (build/Kazo.Os.Apps.targets)
        │     ManifestGenerator aggregate --trimmer-roots
        │       → obj/.../BuiltInApplications.json
        │       → obj/.../Kazo.Os.TrimmerRoots.xml  ──► TrimmerRootDescriptor
        │
        └─ _KazoOsRootKazoOsAssemblies (avant PrepareForILLink)
              chaque Kazo.Os.*.dll publiée ──► TrimmerRootAssembly
                                    │
                              ILLink (publish)
            System.Private.CoreLib : entière (ADR 0242)
            reste du framework et bibliothèques élaguables : élagués
            assemblies Kazo OS et apps : entières
            paquets non élaguables (MudBlazor…) : entiers (partial)
```

- **Pourquoi des racines.** L'OS n'appelle jamais le code d'une app
  directement : il charge son assembly à la demande
  (`BlazorWebAssemblyLazyLoad`) et trouve son point d'entrée, ses composants
  intégrables et la racine Blazor d'un site par les noms du manifeste
  (`Assembly.GetType`, `Activator.CreateInstance`, chargement par nom dans les
  workers). Sans racine, ILLink supprime ces assemblies.
- **Pourquoi des assemblies entières.** Le code de Kazo OS passe ses records à
  l'interop JavaScript, qui les sérialise par réflexion sans que l'analyse de
  trimming le voie. Un élagage membre par membre casse le démarrage (stockage
  « unavailable ») pour seulement 0,4 Mio de Brotli gagné (ADR 0113 §1).
- **`Kazo.Os.TrimmerRoots.xml`** (`TrimmerRootDescriptorWriter` du générateur
  de manifestes) : une ligne `<assembly fullname="…" preserve="all" />` par
  assembly que nomme un manifeste (point d'entrée, entrées par plateforme,
  composants, `web.blazor`). Il couvre les apps installées en paquets, même
  quand leur nom ne commence pas par `Kazo.Os.`.
- **Apps installées après la publication (ADR 0242).** Une app du Store ou de
  GitHub n'est pas une racine : elle trouve le framework que l'OS a gardé.
  `System.Private.CoreLib` reste donc entière (`TrimmerRootAssembly` de
  `_KazoOsRootKazoOsAssemblies`, +0,6 Mio en Brotli ; `KazoOsKeepCoreLib=false`
  l'élague comme avant). Pour le reste du framework, `kazopkg` compare la
  signature complète de chaque méthode référencée par le paquet au runtime
  chargé, et refuse le paquet avec la surcharge manquante
  (`apps.package.missing-api`). Avant l'ADR 0242, seul le nombre de paramètres
  était comparé : IRC 1.0.0 passait la vérification et s'arrêtait sur
  `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)`.
- **Suppressions d'avertissements.** Les attributs `UnconditionalSuppressMessage`
  restants (découverte des apps, workers, composants intégrables, routeur des
  sites Blazor, liaison de modèle MVC) citent la racine qui les rend sûrs.

## Utilisation

### Publier et tester

```bash
wasm2/KazoOs/build/publish-trimmed.sh            # publie, mesure, sert, teste
wasm2/KazoOs/build/publish-trimmed.sh --compare  # publie aussi sans trimming et compare
wasm2/KazoOs/build/publish-trimmed.sh --skip-build
```

Le script publie dans `artifacts/publish-trimmed/publish`, échoue sur toute
alerte `IL…`, sert la publication sur `127.0.0.1` et `127.0.0.2`, puis lance
`Tests/Kazo.Os.TrimmedPublish.E2E.Tests` :

| Test | Ce qu'il vérifie |
|---|---|
| `Trimmed_publish_runs_every_app_a_worker_command_and_both_sample_sites` | création du profil ; chaque app fenêtrée du menu Démarrer (lue dans `BuiltInApplications.json`, approbation administrateur comprise) ouvre sa fenêtre sans erreur ; `seq 30 \| grep 1 \| wc -l` et `/proc/kazoos/workers` dans le terminal ; l'Intranet (vue `.cshtml`, script, API REST, tableau Blazor) et le site Blazor d'exemple (compteur, lien, API) dans le navigateur |
| `Trimmed_publish_boots_offline_after_the_first_load` | sur `127.0.0.2` (le service worker n'est pas enregistré sur `127.0.0.1`) : mise en cache de la version, rechargement sans réseau, connexion, lancement d'une app jamais chargée |
| `The_calculator_rendered_in_a_worker_works_like_on_the_page` | essai de l'ADR 0133, réglage activé par une réécriture d'`appsettings.json` : la Calculatrice dans un worker calcule à la souris et au clavier, garde son CSS, `Tab`, axe, `Alt+F7`/`Alt+Q`, worker dédié libéré à la fermeture ([`remote-window-rendering.md`](remote-window-rendering.md)) |
| `Measures_the_calculator_on_the_page_and_in_a_worker` | mesures de l'ADR 0133 (ouverture, latence, mémoire, lots), écrites dans `remote-window-measurements.json` du dossier des artefacts |
| `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts` | ADR 0206 : le paquet de test `Tests/fixtures/installed-app` (emballé par le script avec `KazoOsPack`, servi par Playwright à la place de `raw.githubusercontent.com`) est installé par `sudo kazopkg install`, l'OS redémarre, l'app importe son module, lance son Web Worker (`importScripts` relatif), affiche son image et appelle des surcharges de CoreLib que l'OS n'utilise pas lui-même (ADR 0242) ; sur `127.0.0.1`, par le service worker du mode `installed-apps` |
| `The_published_service_worker_serves_installed_app_files_to_a_worker` | ADR 0206 sur `127.0.0.2` : le service worker publié sert les fichiers du cache `kazoos-installed-apps` à un Web Worker sous isolation cross-origin |
| `The_published_service_worker_serves_an_installed_app_page_to_an_iframe` | ADR 0243 sur `127.0.0.2` : une page HTML copiée dans le cache `kazoos-installed-apps` s'ouvre dans une iframe avec sa requête (`?author=kazo&webgl`), isolée comme l'OS, en ligne puis hors ligne, au lieu de l'`index.html` de l'OS |
| `The_installed_apps_worker_serves_an_installed_app_page_to_an_iframe` | ADR 0243 sur `127.0.0.1` : la même page, servie par le worker du mode `installed-apps` |
| `Trimmed_publish_shows_a_new_notification_as_a_bubble_above_the_taskbar` | ADR 0197 : la notification des nouveautés postée à la connexion d'un nouveau compte apparaît en bulle au-dessus de la barre (source « Kazo OS », titre, bouton « Voir »), part seule après environ 6 s et reste dans le panneau de l'horloge ([`notification-toasts.md`](notification-toasts.md)) |
| `Trimmed_publish_shows_unread_notifications_next_to_the_clock` | ADR 0196 : un nouveau compte reçoit la notification des nouveautés (`whats-new.json` servi) ; point et infobulle de l'horloge, aucun point sur Applications, panneau ouvert = lu, **Voir** ouvre Paramètres ▸ Nouveautés et ferme la notification ; captures `trimmed-clock-dot.png` et `trimmed-whats-new-page.png` |
| `A_direct_link_with_no_account_starts_the_guest_installs_and_launches_the_app` | ADR 0258 : `?autolaunch=<id>` sans compte démarre l'invité sans clic, installe le paquet de test depuis le catalogue officiel servi par Playwright (signé par la clé de l'exécution) et l'ouvre ; l'adresse perd le paramètre ; un rechargement ne relance rien ; une deuxième visite continue en invité et ouvre l'app sans nouveau téléchargement ([`app-launch-link.md`](app-launch-link.md)) |
| `A_direct_link_with_an_account_opens_the_store_at_the_app_page` | ADR 0258 : avec un compte, bandeau de l'écran de connexion, puis le Store à la page de l'app ; rien de téléchargé |
| `A_direct_link_reports_a_missing_app_and_can_be_cancelled` | ADR 0258 : app absente du catalogue officiel → notification dont le bouton ouvre le Store ; **Annuler** pendant le téléchargement → ni app ni notification |
| `A_craft_app_on_the_os_module_opens_saves_and_follows_the_os` | ADR 0259 : le paquet de test `Tests/fixtures/craft-app` (une fenêtre Craft sur le module `Kazo.Os.AppSdk.Craft`, autour d'une fausse app Craft ; emballé par le script dans `kazoos-packages-craft`, sans le module) est installé par `sudo kazopkg install` ; `xdg-open seed.fake` ouvre la fenêtre sur le fichier, la frappe met `*` dans le titre, Ctrl+S réécrit le fichier et l'app reçoit `saved`, F12 (touche réservée par la fenêtre) fait Enregistrer sous par le dialogue de l'OS, une erreur après `ready` devient un message, le thème clair puis sombre suit, Ctrl+O passe par le dialogue d'ouverture de l'OS, la fermeture avec des modifications demande, une deuxième fenêtre relit le réglage écrit par la première ([`craft-apps.md`](../framework/craft-apps.md)) |

Le script exige que les quatorze tests passent (`EXPECTED_TESTS`). Pour les trois tests du lien direct, il
crée à chaque exécution une clé P-256 (`openssl`, dans `artifacts/publish-trimmed/test-catalog-signing/`)
et publie avec `-p:KazoOsAppSigningKeys=catalog:official=<clé publique>` : la publication testée
accepte cette clé pour le catalogue officiel, à côté de la vraie.

Aucune erreur de console n'est admise. Sans `KAZOOS_TRIMMED_URL`, la suite est
ignorée (un `dotnet test` de la solution ne la lance pas).

Ce test fait partie de la porte de fusion locale de `v3/14` et des chantiers
suivants (ADR 0106 §5). `build/verify-hosts.sh` publie aussi l'Ecosystem en
Release : son test de démarrage porte donc lui aussi sur la publication trimmée.

### Un hôte d'un autre dépôt

Le gabarit `kazoos-host` publie trimmé de la même façon. Les cibles de
`Kazo.Os.App.Build` génèrent les racines et gardent les paquets `Kazo.Os.*`
entiers ; les gabarits d'apps partent avec `IsTrimmable=true`, et le paquet
active l'analyse de trimming dans un projet d'app qui ne la règle pas.

### Piège : publications successives

La conversion WebCIL du SDK est incrémentale par dates de fichiers. Une
publication non trimmée (`-p:PublishTrimmed=false`) faite après une publication
trimmée dans le même dossier `obj/` peut reprendre les assemblies élaguées.
Avant de comparer, supprimer `OS/Kazo.Os.Ecosystem/obj/Release/net10.0/linked`,
`webcil` et `tmp-webcil` (le script le fait).

## Décisions clés

- `TrimMode=partial` (ADR 0106 §1) : les paquets qui ne se déclarent pas
  élaguables restent entiers.
- Assemblies Kazo OS et apps gardées entières ; seul le framework est élagué
  (ADR 0113 §1-2), sauf `System.Private.CoreLib`, gardée entière pour les apps
  installées (ADR 0242).
- Pas de table générée pour les pages `[Route]` d'un site Blazor : l'assembly du
  site est entière (ADR 0113 §3).
- Test hors ligne sur `127.0.0.2` plutôt que `[::1]` (ADR 0113 §4).

## Tâches

- [x] `PublishTrimmed=true` et `TrimMode=partial` dans l'Ecosystem et le gabarit d'hôte.
- [x] `Kazo.Os.TrimmerRoots.xml` généré par l'agrégation et passé à ILLink.
- [x] Assemblies `Kazo.Os.*` gardées entières à la publication d'un hôte.
- [x] Justifications des suppressions d'avertissements mises à jour.
- [x] `Kazo.Os.App.Build` : analyse de trimming dans les projets d'apps ; gabarits `IsTrimmable=true`.
- [x] `build/publish-trimmed.sh` et `Tests/Kazo.Os.TrimmedPublish.E2E.Tests`.
- [x] Tailles avant et après mesurées (ici et dans `building-and-testing.md`).
- [x] ADR 0113.
- [x] ADR 0242 : CoreLib entière, vérification exacte des signatures, paquet de test qui appelle des surcharges de CoreLib.
