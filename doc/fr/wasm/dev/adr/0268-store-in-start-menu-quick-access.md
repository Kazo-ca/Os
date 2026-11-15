> [English version](../../../../en/wasm/dev/adr/0268-store-in-start-menu-quick-access.md)

# ADR 0268 : le Kazo OS Store dans l'Accès rapide du menu Démarrer

## Statut

Acceptée le 2026-11-13 : Benoit a choisi Q1 = A et, pour Q2, le Store entre
l'Explorateur de fichiers et les Paramètres (voir [Questions](#questions-posées-à-benoit)). Série
`store-quick-access/base`.

- **Remplace en partie :** l'[ADR 0188](0188-hack-store-and-app-catalog.md)
  (« épinglée par défaut dans la section Épinglées du menu Démarrer ») et
  l'[ADR 0212](0212-hack-store-app-build.md) §6 (la liste par défaut
  `org.kazoos.store`). Le mécanisme `defaultPinsApplied` de l'ADR 0212 §6 reste.
- **S'appuie sur :** l'[ADR 0044](0044-settings-as-files.md) §8 (le
  fichier `~/.config/kazoos/start-menu.json`).

## Contexte

Benoit, 2026-11-13 :

> faire que le `Kazo OS Store` soit dans la section `Quick access` plustôt que
> dans la section `Pinned`

Le menu Démarrer a deux endroits pour lancer une app hors de la liste des
programmes : la section **Épinglées** (les épingles de l'utilisateur, qu'il
range et retire) et le panneau **Accès rapide** à droite (des liens système
fixes : Explorateur de fichiers et Paramètres). Depuis l'ADR 0212 §6, le Store
est ajouté une fois aux épingles de chaque profil. Il occupe donc une place que
l'utilisateur considère comme la sienne, alors qu'il s'agit d'un outil système
comme les Paramètres.

## Décision

1. **Lien Accès rapide.** Le panneau Accès rapide du menu Démarrer gagne un
   troisième lien, **Store** (« Trouver et installer des apps »), entre
   l'Explorateur de fichiers (premier) et les Paramètres (derniers) (Q2). Il lance
   `org.kazoos.store` comme les deux autres liens, par le bureau
   (`OnAppSelected`) ; il est grisé si l'app n'est pas lançable.
2. **Plus d'épingle par défaut.** `StartMenuSettingsDocuments.DefaultPinnedAppIds`
   devient vide : un nouveau profil n'a aucune épingle. Le mécanisme
   `defaultPinsApplied` reste pour une future épingle par défaut.
3. **Migration une seule fois (Q1 A).** `RetiredDefaultPinnedAppIds` liste les
   anciennes épingles par défaut (`org.kazoos.store`). Au chargement d'un
   profil dont `defaultPinsApplied` contient une de ces apps, l'app est retirée
   des épingles **et** de `defaultPinsApplied`, puis le fichier est écrit.
   La migration ne se rejoue donc pas : si l'utilisateur épingle de nouveau le
   Store, il reste épinglé. Un Store épinglé par l'utilisateur avant toute
   épingle par défaut (absent de `defaultPinsApplied`) reste aussi.
4. **Mobile.** Rien ne change : l'écran d'accueil mobile liste toutes les apps
   et n'utilise pas les épingles du menu Démarrer.

## Conséquences

- Le Store n'apparaît plus deux fois chez les utilisateurs existants.
- Un utilisateur qui avait volontairement gardé l'épingle par défaut la perd
  une fois ; il peut l'épingler de nouveau (le bouton d'épingle reste dans la
  liste des programmes).
- Un échec d'écriture garde le résultat en mémoire pour la session ; le
  chargement suivant réessaie (comme l'ADR 0212 §6).

## Questions posées à Benoit

Posées dans le fil le 2026-11-13.

**Q1. Retirer le Store des épingles des profils existants ?**

- **A (recommandé, choisi par Benoit) :** oui, une seule fois, pour les profils où il a été
  épinglé par défaut. S'il est épinglé de nouveau, il reste.
- **B :** non, seulement les nouveaux profils. Les profils existants le voient
  dans Épinglées et dans Accès rapide jusqu'à ce que chacun le désépingle.

**Q2. Position du Store dans Accès rapide ?**

- **A (recommandé) :** en dernier, après l'Explorateur et les Paramètres.
- **B :** en premier, au-dessus de l'Explorateur.

Réponse de Benoit : ni A ni B, « settings sera le dernier, file explorer le
premier, donc entre les deux ».

## Tâches

- [x] Lien Store dans Accès rapide (`AppLauncher.razor`, textes EN/FR).
- [x] `DefaultPinnedAppIds` vide, `RetiredDefaultPinnedAppIds` et migration
  dans `StartMenuPreferencesService`.
- [x] Tests : `StartMenuPreferencesServiceTests` (migration une fois, épingle
  remise, épingle de l'utilisateur gardée), E2E `StartMenuQuickAccessTests`.
- [x] Docs FR/EN : `app-installation.md`, `features.md`, mémoire.
