> [English version](../../../../en/wasm/dev/adr/0257-offer-a-store-app-for-an-unsupported-file.md)

# ADR 0257 : proposer une app du Store pour ouvrir un fichier sans app

## Statut

Acceptée le 2026-11-13. Benoit a répondu « ok » aux questions 1 à 4 : les
recommandations (choix A) sont retenues. Série `open-with-store/base`.

- **S'appuie sur :** l'[ADR 0121](0121-user-file-associations.md) (résolution
  des associations et « Ouvrir avec »), l'[ADR 0188](0188-hack-store-and-app-catalog.md)
  (format de `catalog.json` et Store), l'[ADR 0212](0212-hack-store-app-build.md)
  (le Store v1 et l'installation depuis un catalogue) et
  l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md)
  (confiance et installation pour soi ou pour tous).
- **Dépôts touchés :** Kazo-ca/Os et Kazo-ca/Os-AppMarketPlace (outil du catalogue).

## Contexte

- Dans Kazo OS 3.3.2, ouvrir `NewFile.docx` sans app installée affiche
  « No application is registered to open this file. » (`Error.NoHandler` de
  l'Explorateur). Kazo Word 0.1.0 est pourtant dans le catalogue officiel et
  déclare `.docx` dans les `fileHandlers` de son manifeste.
- `FileAssociationResolver` ne regarde que les apps installées. Le résultat
  `NoHandler` remonte tel quel à l'Explorateur, au bureau et à `xdg-open`.
- `catalog.json` ne transporte pas les types de fichiers : l'outil
  d'Os-AppMarketPlace (`CatalogVersion`) recopie du manifeste les capacités, la
  plage du SDK, l'icône et les captures, mais pas `fileHandlers`. Côté OS,
  `AppCatalogVersion` n'a pas de champ pour eux.
- Le Store sait déjà s'ouvrir sur la page d'une app (`--app=<appId>`) et y
  installer, avec l'écran des capacités et la demande d'administrateur de
  l'ADR 0189.

## Décision

1. **Le catalogue annonce les types de fichiers (question 2, choix A).** Chaque
   version de `catalog.json` reçoit un champ optionnel `fileHandlers`, recopié du
   manifeste du paquet par l'outil d'Os-AppMarketPlace : pour chaque entrée,
   `extensions`, `mediaType` et `actions`. Le schéma reste en version 1 : un
   catalogue sans ce champ est toujours valide et ne propose simplement aucune
   app. `AppCatalogVersion` gagne `FileHandlers` (liste vide par défaut). Le
   champ fait partie de `catalog.json`, déjà signé avec le catalogue : aucune
   nouvelle signature.

2. **Recherche dans les catalogues.** Quand la résolution ne trouve aucune app
   installée, l'OS cherche dans les catalogues déjà téléchargés (sans requête
   réseau supplémentaire) les apps dont la **dernière version compatible avec le
   SDK de l'OS** déclare l'extension du fichier (ou, à défaut, son type MIME) pour
   l'action demandée. Les apps bloquées ou déjà installées sont exclues. Le
   catalogue officiel passe en premier, puis l'ordre de `[catalogs]`.

3. **Dans le chemin d'ouverture de l'OS (question 4, choix A).** Le résultat
   `NoHandler` de `OpenFileAsync` porte la liste des apps proposées
   (`SuggestedCatalogApps` : id, nom, catalogue). L'Explorateur, le bureau et
   les apps qui appellent `KazoApps.OpenFileAsync` la reçoivent ; `xdg-open`
   (`open`) l'écrit sous son message d'erreur avec la commande `kazopkg install`
   à lancer. L'ajout est rétrocompatible dans le SDK (nouvelle propriété
   optionnelle, `PublicAPI.Unshipped.txt`).

4. **La boîte de l'Explorateur (question 1, choix A).** Sous « Aucune
   application ne peut ouvrir ce fichier », l'Explorateur liste les apps
   proposées (icône, nom, description courte) avec un bouton « Voir dans le
   Store ». La boîte « Ouvrir avec » montre la même section sous les apps
   installées. Le bouton lance le Store avec `--app=<appId>` : l'installation
   passe par le Store et garde son consentement habituel (capacités, portée,
   administrateur). Sans proposition, la boîte reste celle d'aujourd'hui.

5. **Ouvrir le fichier après l'installation (question 3, choix A).** Le Store
   est lancé avec `--open-after-install=<chemin>`. Quand l'installation réussit,
   le Store rouvre le fichier par `KazoApps.OpenFileAsync` ; la résolution
   trouve alors la nouvelle app. Si l'utilisateur ferme le Store ou annule,
   rien ne se passe.

6. **Hors ligne.** Sans catalogue en cache, la liste est vide et la boîte reste
   celle d'aujourd'hui. Aucune proposition ne bloque l'ouverture ni n'attend le
   réseau.

## Conséquences

- Os-AppMarketPlace : `CatalogVersion.FileHandlers`, tests de l'outil, puis
  republication du catalogue pour que Kazo Word (et Webamp, IRC) annoncent leurs
  types.
- Os : lecture du champ (`AppCatalogDocument`), recherche dans les catalogues,
  `NoHandler` enrichi, boîte de l'Explorateur, argument du Store, `xdg-open`,
  docs (`user-file-associations.md`, `file-explorer.md`, `app-installation.md`,
  référence du SDK) et entrée de changement.
- Le texte de la boîte d'erreur devient plus utile sans changer le cas où rien
  n'est trouvé.

## Chantiers de la série `open-with-store/base`

| N° | Branche | Dépôt | Contenu |
| --- | --- | --- | --- |
| 0 | `open-with-store/0-adr` | Os | Cette ADR. |
| 1 | `open-with-store/1-catalog-file-types` | Os-AppMarketPlace | `fileHandlers` dans `catalog.json`, tests, republication. |
| 2 | `open-with-store/2-suggest-catalog-apps` | Os | Lecture du champ, recherche, `NoHandler` enrichi, SDK, `xdg-open`. |
| 3 | `open-with-store/3-explorer-and-store` | Os | Boîte de l'Explorateur et « Ouvrir avec », `--open-after-install` du Store, E2E, docs, entrée de changement. |

## Questions posées à Benoit (2026-11-13, réponse « ok » : choix A partout)

1. Où proposer l'app : A ✨ boîte d'erreur et « Ouvrir avec », bouton vers le
   Store ; B installation directe depuis l'Explorateur ; C simple bouton
   « Chercher dans le Store ».
2. Comment le catalogue annonce les types : A ✨ champ `fileHandlers` optionnel
   dans `catalog.json` ; B l'OS télécharge chaque manifeste ; C fichier
   `file-types.json` séparé.
3. Après l'installation : A ✨ le fichier s'ouvre tout seul ; B l'utilisateur
   refait le double-clic.
4. Portée : A ✨ chemin d'ouverture de l'OS (Explorateur, bureau, `open`,
   `KazoApps.OpenFileAsync`) ; B Explorateur seulement.
