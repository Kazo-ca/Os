> [English version](../../../../en/wasm/dev/plans/drive-providers-plan.md)

# Plan de la série « fournisseurs de fichiers tiers » (`drive-providers/base`)

Validé par Benoit le 2026-11-17 (réponses 1 A à 5 A). Décisions :
[ADR 0282](../adr/0282-third-party-drive-providers.md). Un développeur tiers
publie dans le Store une Feature qui ajoute un type de lecteur (comme Dropbox
et OneDrive), et les connecteurs livrés avec Kazo OS peuvent être désactivés
à la publication.

**Aucun code avant le feu vert de Benoit** : ce plan et l'ADR sont le
chantier 0.

## But

- Un tiers écrit un fournisseur avec le SDK public seulement, et il arrive sur
  os.kazo.ca par le Store, sans republier l'OS.
- L'OS garde la connexion au service, les jetons, le point de montage et toute
  l'interface ; le fournisseur ne fait que parler à son service.
- Dropbox, OneDrive et S3 restent livrés avec l'OS, avec une propriété de
  build par connecteur et un réglage d'exécution pour les désactiver.
- Tout marche sans serveur Kazo.

## Chantiers

| # | Branche | Contenu | Dépend de |
| --- | --- | --- | --- |
| 0 | `drive-providers/0-adr` | ADR 0282, ce plan, `features.md`, mémoire. **Fait.** | — |
| 1 | `drive-providers/1-direct-http` | Mode direct de `KazoHttpClient` (le `fetch` du navigateur, corps en flux, sans serveur), capacité dangereuse `network.direct.access`, hôtes déclarés au manifeste et montrés à l'approbation, marche aussi dans un worker (ADR 0282 §4). Doc `sdk-network.md`, `capabilities.md`. | 0 |
| 2 | `drive-providers/2-provider-contract` | SDK : `Kazo.Os.AppSdk.Drives` (`KazoDriveProvider`, `KazoDriveFolder`, modèles, `KazoDriveException`, contexte), `FeatureDefinition.DriveProviders`, `driveProviders` du manifeste, schéma, validateur, diagnostics du générateur, capacité `drives.provide`, types réservés (§1, §2). `PublicAPI.Unshipped.txt`. Aucun branchement dans l'OS. | 0 |
| 3 | `drive-providers/3-shipped-connectors-switch` | Connecteurs sortis dans `Kazo.Os.Platform.CloudDrives`, `build/Kazo.Os.CloudDrives.targets` (`KazoOsDropboxDrives`, `KazoOsOneDriveDrives`, `KazoOsS3Drives`), interrupteurs d'exécution, `KazoOs:CloudDrives:Disabled` et clé `disabled` (§5). Test de publication avec les trois à `false`. | 0 |
| 4 | `drive-providers/4-platform-adapter` | Registre des fournisseurs dans `AppCatalog`, `FeatureDriveSource` (pont vers `INetworkDriveSource`), chargement au premier besoin dans le contexte de la Feature, sources dynamiques dans `NetworkDriveService` (ajout à chaud), lignes « fournisseur absent » (§3). Fournisseur factice dans `Tests.Support`. | 2, 3 |
| 5 | `drive-providers/5-declared-connection` | Connexion déclarée : fournisseur OAuth lu du manifeste dans `CloudProviderCatalog`, formulaire à champs typés et secrets scellés, jeton d'accès et secrets rendus au seul fournisseur du compte, document de réglages générique (`client-id` par type) (§3). | 2 |
| 6 | `drive-providers/6-ui` | Explorateur, Paramètres ▸ Comptes cloud et `mount -t` sans nom de service en dur : nom, icône, forme de connexion et champs lus par `KazoDriveInfo.GetNetworkDriveTypesAsync` ; nom de la Feature à côté du service ; lecteur grisé et « Installer le fournisseur ». Textes en français et en anglais, mobile compris. | 4, 5 |
| 7 | `drive-providers/7-developer-kit` | Suite de conformité publique (`KazoDriveProviderTests`), modèle `dotnet new` d'un fournisseur, fournisseur d'exemple complet, guide `framework/drive-providers.md`, fixture `sdk-compat`, E2E sur l'OS rogné avec un fournisseur installé. | 1, 4, 5 |
| 8 | `drive-providers/8-store-check` | Dépôt Os-AppMarketPlace : vérification de la déclaration dans `validate.yml` (type non réservé, hôtes en `https`, pas de joker nu). Après une version de l'OS qui porte le contrat. | 2, version publiée |
| 9 | `drive-providers/9-final` | Vérification de la série : build, toutes les suites, publication rognée, `features.md`, mémoire, version mineure du SDK (3.5.0). | 1 à 7 |

Les chantiers 1, 2 et 3 vont en parallèle. Les chantiers 4 et 5 vont en
parallèle ensuite, puis 6 et 7.

## Règles de la série

- Une branche par chantier, créée depuis `drive-providers/base`, une PR vers
  `drive-providers/base`. Jamais de fusion dans `main` : Benoit décide.
- Chaque PR ajoute son entrée de changement (ADR 0186) et met à jour les docs
  en français et en anglais, avec les index.
- Barre de fusion : build et tests locaux verts, suites E2E une à la fois,
  `build/publish-trimmed.sh` et la fixture `Tests/fixtures/sdk-compat` quand
  le chantier les touche.
- Une question ouverte en cours de chantier est posée à Benoit dans le fil,
  avec des choix ; le chantier n'avance pas sur ce point avant sa réponse.

## Points à surveiller

- **Rupture de `Kazo.Os.Platform.Core`** (chantier 3) : les types
  `CloudDrives.*` changent d'assemblage. À écrire dans l'entrée de changement
  et dans les notes de version ; le fichier `PublicAPI.Shipped.txt` de
  Platform.Core perd ces lignes, celui du nouvel assemblage les gagne.
- **CORS** (chantier 1) : le mode direct ne joint que les services qui
  l'acceptent depuis une page isolée (ADR 0131). Le guide du chantier 7 le dit
  et donne la façon de le vérifier.
- **Connexion de l'utilisateur** (chantier 4) : un fournisseur qui ne se
  charge pas, qui lève ou qui tarde ne doit jamais la retarder ; c'est un test
  obligatoire.
- **Jetons** (chantier 5) : le jeton de renouvellement ne sort jamais de
  l'OS ; un fournisseur ne reçoit rien d'un compte d'un autre type ; c'est un
  test obligatoire.
- **Le fournisseur d'exemple** (chantier 7) doit viser un service qui accepte
  CORS et dont l'inscription d'une app est gratuite ; le choix du service est
  une question à poser à Benoit au début de ce chantier.

## Ce que Benoit fait lui-même

- Donner le feu vert pour commencer le code.
- Décider quand `drive-providers/base` va dans `main`.
- Fusionner la PR du chantier 8 dans Os-AppMarketPlace.
