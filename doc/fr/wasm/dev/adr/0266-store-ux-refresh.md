> [English version](../../../../en/wasm/dev/adr/0266-store-ux-refresh.md)

# ADR 0266 : nouvel accueil et nouvelle fiche d'app du Kazo OS Store

## Statut

Acceptée le 2026-11-13. Benoit a répondu aux questions posées dans le fil (voir
la fin du document). Série `store-ux/base`.

- **S'appuie sur :** l'[ADR 0188](0188-hack-store-and-app-catalog.md) (Store et
  catalogue), l'[ADR 0212](0212-hack-store-app-build.md) (le Store v1),
  l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md) (installation
  pour soi ou pour tous), l'[ADR 0213](0213-theme-packs-build.md) (onglet
  Thèmes), l'[ADR 0257](0257-offer-a-store-app-for-an-unsupported-file.md)
  (`fileHandlers` dans le catalogue) et
  l'[ADR 0258](0258-direct-app-launch-link.md) (lien `?autolaunch=<appId>`).
- **Modifie :** la présentation décrite par l'ADR 0212 (accueil par rangées de
  catégories, onglet Thèmes toujours visible). Le format du catalogue, la confiance et l'installation ne changent
  pas.
- **Dépôt touché :** Kazo-ca/Os (`wasm2/KazoOs/Apps/System/Kazo.Os.Apps.Store/`,
  et le SDK pour la progression d'installation).

## Contexte

Le Store 1.0.0 de Kazo OS 3.3.3 a été testé avec Playwright sur os.kazo.ca, en
fenêtre de bureau et en mode téléphone (2026-11-13).

- **Bugs** : la fiche d'une app ne s'ouvre pas en haut (toutes les vues
  partagent la même zone défilante) ; un clic sur une capture ne fait rien ; les
  listes Markdown écrites sur deux lignes sont coupées ; le nom de l'app est
  écrit deux fois ; la ligne de l'onglet Installées est centrée ; la recherche
  est ignorée dans l'onglet Installées ; le focus clavier est invisible ; sur
  téléphone, l'en-tête prend trois lignes ; une installation n'affiche que
  « Working… ».
- **Design** : avec six apps et une rangée par catégorie, l'accueil est presque
  vide et rien n'est mis en avant ; la fenêtre par défaut est trop petite pour
  la fiche ; les capacités sont des identifiants techniques ; le bouton
  d'installation disparaît au défilement ; l'onglet Thèmes mène à une page vide
  tant qu'aucun catalogue n'offre de pack.

Les corrections de bugs (chantiers 1 et 2) n'ont pas besoin de décision
d'architecture. Cette ADR fixe les choix de présentation des chantiers 3 à 6.

## Décision

1. **Même identité visuelle.** Le Store garde les jetons du thème de l'OS
   (`--kos-*`). Aucune couleur propre au Store.

2. **Accueil.**
   - Un bandeau « À la une » montre une app : icône, nom, description courte,
     première capture et bouton d'installation. L'app est choisie
     automatiquement : la plus récemment publiée (`publishedAt` de sa dernière
     version compatible) parmi les apps non installées ; s'il n'y en a pas, la
     plus récemment publiée. Le catalogue ne change pas.
   - Une seule grille de toutes les apps remplace les rangées par catégorie,
     triée de la plus récente à la plus ancienne. Des pastilles filtrent par
     catégorie (« Toutes » en premier, avec le nombre d'apps). La page
     « Voir tout » d'une catégorie disparaît ; le Retour de l'OS (ADR 0220)
     enlève le filtre.
   - Chaque carte porte un bouton : « Installer » (pour moi), « Ouvrir » ou
     « Mettre à jour ». Le badge de confiance n'est montré sur une carte que
     si l'app n'est **pas** vérifiée ; la fiche le montre toujours.
   - La fenêtre du Store s'ouvre plus grande par défaut (environ 1100 × 720).
   - Le chargement montre des cartes vides à la place du mot « Loading ».

3. **Bouton d'installation.**
   - Sur une carte et dans le bandeau « À la une » : un bouton « Installer »,
     qui installe pour moi, et, collé à sa droite, un bouton à menu qui offre
     « Installer pour tout le monde ». Le bouton à menu n'est montré que si
     l'utilisateur courant est administrateur (`KazoUserInfo.IsAdministrator`).
   - Sur la fiche : les deux boutons actuels sont gardés (« Installer pour
     moi », « Installer pour tout le monde »).
   - L'OS garde sa propre demande d'administrateur (ADR 0189 §4) : le Store ne
     décide pas de la sécurité, il ne fait que cacher une option inutile.

4. **Fiche d'une app.**
   - Une barre reste visible pendant le défilement : Retour, puis l'icône, le
     nom et le bouton d'installation dès que l'en-tête de la fiche est sorti de
     l'écran.
   - Les capacités sont écrites en langage clair, groupées, les capacités
     sensibles en premier ; les identifiants techniques restent accessibles
     (« Voir les identifiants techniques »). Une capacité sans texte connu est
     montrée par son identifiant.
   - La fiche montre les types de fichiers que l'app ouvre (`fileHandlers`,
     ADR 0257), un lien « Code source » à la place de l'adresse brute, et un
     bouton qui copie le lien « Essayer » (ADR 0258).
   - Le panneau d'informations suit le défilement.
   - Les textes longs (descriptions) utilisent une police sans empattement du
     système (`system-ui`), sans téléchargement ; les titres, boutons et
     étiquettes gardent la police du thème.
   - La fiche devient un composant à part (`StoreAppPage.razor`) pour sortir de
     `StoreWindow.razor`.

5. **Onglet Thèmes.** Il est masqué tant qu'aucun catalogue n'offre de pack et
   qu'aucun pack n'est installé. `--page=themes` mène alors à l'accueil.

6. **Messages.** Le résultat d'une action s'affiche dans un message qui se
   ferme seul ; une erreur reste jusqu'à ce qu'on la ferme.

7. **Progression d'installation.** Pendant une installation, la fiche et la
   carte montrent l'étape en cours (téléchargement, vérification, installation)
   et, pour le téléchargement, l'avancement. Seuls les boutons de l'app
   concernée sont désactivés. `KazoAppPackages.InstallAsync` ne rapporte pas de
   progression aujourd'hui : le chantier 4 ajoute une surcharge optionnelle au
   SDK (`IProgress<KazoPackageInstallProgress>`), rétrocompatible, dans
   `PublicAPI.Unshipped.txt`.

8. **Téléphone.** L'en-tête tient sur deux lignes, les onglets passent en bas
   de l'écran, les cibles tactiles font au moins 44 px, et les captures en
   portrait passent avant les captures en paysage.

## Conséquences

- `StoreWindow.razor` (470 lignes) est découpé : fiche, carte, bandeau et
  visionneuse deviennent des composants avec leurs fichiers `.razor.css`.
- L'ADR 0212 reste vraie pour l'installation et la confiance ; sa description
  de l'accueil et des boutons est remplacée par celle-ci.
- Le SDK grandit d'une surcharge (chantier 4) : version mineure ou correctif au
  choix de Benoit au moment de la publication.
- Les textes français et anglais du Store changent (`localization/fr.json`,
  `Strings`) ; `app-installation.md` et `features.md` sont mis à jour par les
  chantiers.
- Les captures de la fiche du Store dans les docs devront être refaites à la fin
  de la série.

## Chantiers de la série `store-ux/base`

| N° | Branche | Contenu |
| --- | --- | --- |
| 0 | `store-ux/0-adr` | Cette ADR. |
| 1 | `store-ux/1-quick-fixes` | Défilement remis en haut, Markdown des listes, titre répété, ligne Installées, recherche dans Installées, focus visible, en-tête du téléphone. |
| 2 | `store-ux/2-screenshot-viewer` | Visionneuse de captures dans la fenêtre du Store. |
| 3 | `store-ux/3-app-page` | Décisions 4 et 6 : composant de la fiche, barre collante, capacités en clair, informations. |
| 4 | `store-ux/4-install-progress` | Décision 7 : progression dans le SDK et dans le Store. |
| 5 | `store-ux/5-home` | Décisions 2, 3 et 5 : bandeau, grille, filtres, cartes et leur bouton à menu, taille de la fenêtre, onglet Thèmes. Après le chantier 3. |
| 6 | `store-ux/6-mobile` | Décision 8, et vérification de toute la série sur téléphone. Après le chantier 5. |

Maquettes validées par Benoit : accueil et fiche, bureau et téléphone
(Artifact « Maquettes du Kazo OS Store » du fil de la série).

## Questions posées à Benoit (2026-11-13)

Première série :

1. Ampleur : A corrections et fiche seulement ; **B ✨ A plus un nouvel accueil,
   même identité visuelle (retenu)** ; C nouvelle identité visuelle.
2. Bouton d'installation de la fiche : A ✨ un seul bouton et un menu ;
   **B deux boutons (retenu)**.
3. Agrandir une capture : **A ✨ visionneuse dans la fenêtre du Store
   (retenu)** ; B calque sur tout l'écran de l'OS ; C fenêtre séparée.
4. Onglet Thèmes vide : **A ✨ le masquer (retenu)** ; B le garder.
5. Maquettes avant le code : **A ✨ oui (retenu)** ; B non.

Seconde série, sur les maquettes :

1. Police des descriptions : **A ✨ sans empattement pour les textes longs
   (retenu)** ; B tout dans la police du thème.
2. Bouton « Installer » d'une carte : A ✨ il installe pour moi ; B pas de
   bouton d'installation sur les cartes. Réponse de Benoit : « Installe pour
   moi, avec un dropdown à côté qui a l'option installer pour tout le monde si
   on est admin. »
3. Choix de l'app « à la une » : **A ✨ automatique, la plus récente
   (retenu)** ; B un champ dans le catalogue.
