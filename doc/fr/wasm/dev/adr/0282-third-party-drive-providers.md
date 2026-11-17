> [English version](../../../../en/wasm/dev/adr/0282-third-party-drive-providers.md)

# ADR 0282 : fournisseurs de fichiers tiers (une Feature qui fournit un type de lecteur) et fournisseurs livrés désactivables à la publication

## Statut

**Proposée** le 2026-11-17, **non décidée**. Évaluation, **aucun code**. Les
questions de la fin sont posées à Benoit dans le fil ; rien n'est construit
avant ses réponses. La section « Décision » décrit l'option **recommandée**
pour chaque question, pour qu'il puisse la juger ; elle sera réécrite selon ses
choix, et le plan de la série (`drive-providers/base`) ne sera écrit qu'après.

- **Complète :** l'[ADR 0267](0267-cloud-drives-as-network-drives.md) (lecteurs
  cloud montés comme lecteurs réseau, sources par type),
  l'[ADR 0269](0269-cloud-tokens-stay-on-the-device.md) (jetons sur
  l'appareil), l'[ADR 0270](0270-cloud-drives-in-the-app-sdk.md) (comptes et
  lecteurs cloud dans le SDK) et l'[ADR 0260](0260-feature-app-type.md) §5 (ce
  que seule une Feature peut fournir : cette ADR ajoute une ligne).
- **S'appuie sur :** l'[ADR 0262](0262-feature-service-registry-and-async-resolution.md)
  (code d'une Feature chargé au premier besoin, exécuté dans son contexte),
  l'[ADR 0246](0246-provider-declared-permissions.md), l'[ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  et l'[ADR 0231](0231-network-drives-count-as-personal-files.md) (règles
  des lecteurs réseau), l'[ADR 0066](0066-optional-resource-accounting.md)
  (option choisie au build), l'[ADR 0277](0277-trimmed-api-check-before-publishing.md)
  (API de l'OS rogné).
- **Ne change pas :** la signature des paquets, faite seulement dans
  Os-AppMarketPlace (ADR 0219, 0188).

## Contexte

Demande de Benoit (2026-11-17) : qu'une personne puisse créer un fournisseur de
fichiers « comme pour Dropbox et OneDrive » ; ceux qui existent restent livrés
avec Kazo OS et peuvent être désactivés quand on publie.

Ce que le code fait aujourd'hui (vérifié sur `main`, version 3.4.2) :

- Un type de lecteur est une **source** : `INetworkDriveSource`
  (`Shared/Kazo.Os.Simulation.Abstractions/FileSystem/NetworkDriveSourceContracts.cs`),
  qui crée et rouvre un `IHostFolder` (lister, lire, écrire, déplacer, quota,
  écriture conditionnelle). `NetworkDriveService` monte ce que la source
  rend, sous `/mnt`, avec les règles de l'ADR 0181.
- Les trois connecteurs livrés (`dropbox`, `onedrive`, `s3`, environ 3 500
  lignes) sont dans `Platform/Kazo.Os.Platform.Core/CloudDrives/`. Ils sont
  enregistrés **en dur** dans l'injection de dépendances de l'hôte
  (`EcosystemServiceCollectionExtensions.cs:576-594`), et la liste des sources
  est figée à la construction de `NetworkDriveService` (`:282-289`).
- Les fournisseurs OAuth sont deux fabriques statiques
  (`CloudOAuthProvider.Dropbox`, `.OneDrive`) et un record de réglages à champs
  fixes (`CloudProviderSettings`). Dropbox et OneDrive ne sont proposés que si
  l'hôte a un identifiant d'app (`KazoOs:CloudDrives`, ou
  `/etc/kazoos/cloud-drives.config`) ; `s3` est toujours proposé.
- L'interface a des noms en dur : formulaire S3 de l'Explorateur
  (`ConnectNetworkDriveDialog.razor.cs`), noms des services dans les
  Paramètres (`CloudAccountsPanel.razor.cs:75-77`), `mount -t dropbox|onedrive|s3`.
- Une app installée **ne peut rien ajouter** à l'injection de dépendances
  (ADR 0262, contexte) : aucun chemin ne mène d'un paquet du Store à
  `INetworkDriveSource`.
- Le genre d'app **Feature** existe (ADR 0260, livré en 3.4.0) : installée
  pour toute la machine par un administrateur, elle peut fournir des services
  (ADR 0262), des permissions, des rôles, des pages de Paramètres. Son code
  est chargé au premier besoin et tourne dans son propre contexte.
- **Réseau.** Les connecteurs de l'OS appellent le service par le `fetch` du
  navigateur (`HttpClient`, CORS). Une app n'a pas ce chemin : `KazoHttpClient`
  passe par le réseau simulé puis par le relais du serveur facultatif, et la
  publication rognée ne garantit pas `System.Net.Http` à une app installée.
  Kazo AI contourne avec son propre module JavaScript (`ai-fetch.js`).
- **Rognage.** Les assemblages `Kazo.Os.*` et ceux des apps restent entiers à
  la publication ; seul le framework est rogné. Un interrupteur d'exécution ne
  retire donc pas le code d'un connecteur du téléchargement : il faut que
  l'hôte ne référence pas son assemblage. Le précédent est la mesure des
  ressources (ADR 0066, `build/Kazo.Os.ResourceAccounting.targets`).
- **API publique.** Les types des connecteurs et des comptes cloud sont
  publics dans `Kazo.Os.Platform.Core` (337 lignes de `PublicAPI.Shipped.txt`).
  `IHostFolder` et `INetworkDriveSource` sont publics dans
  `Kazo.Os.Simulation.Abstractions`.

## Approches comparées

| | A. Feature du Store qui fournit un type de lecteur | B. App ordinaire qui fournit un type | C. Paquet NuGet composé dans l'hôte au build | D. Option de build seule |
| --- | --- | --- | --- | --- |
| Qui peut l'écrire | tout développeur, avec le SDK | idem | qui publie son propre Kazo OS | l'équipe de l'OS |
| Arrive sur os.kazo.ca sans republier l'OS | oui (Store) | oui (Store) | non | non |
| Portée | la machine (comme `mount -t`, `/proc/mounts`) | un utilisateur : deux utilisateurs, deux versions du même type | la machine | la machine |
| Qui approuve | administrateur, une fois, à l'installation | chaque utilisateur | qui publie | qui publie |
| Modèle existant | ADR 0260 §5, ADR 0262 | contraire à l'ADR 0260 §5 (« fournir » est réservé aux Features) | déjà possible : `services.AddScoped<INetworkDriveSource, …>()` | ADR 0066 |
| API publique nouvelle | SDK (contrat du fournisseur, déclaration) | idem | aucune, mais les contrats internes deviennent un engagement | aucune |
| Répond à « une personne crée un fournisseur » | oui | oui | à moitié | non |
| Répond à « désactiver ceux livrés à la publication » | non | non | non | oui |

A et D ne s'opposent pas : A pour les tiers, D pour les connecteurs livrés. C
reste possible sans rien faire (c'est la couture actuelle), sans promesse de
stabilité.

## Décision

### 1. Un fournisseur tiers est une Feature qui déclare un type de lecteur

- `FeatureDefinition` gagne `DriveProviders` : une liste de déclarations
  `KazoDriveProviderDeclaration` (type, nom affiché, icône, classe du
  fournisseur, connexion, champs du formulaire, hôtes joints). Le manifeste
  l'écrit dans `driveProviders`. Une app ordinaire qui le déclare est refusée
  (`KOSAPP0033`, `manifest.feature.only`), comme les autres fournitures.
- **Type** : minuscules, chiffres et tirets (`pcloud`, `webdav`), c'est le nom
  de `mount -t`, `/proc/mounts` et `df -T`. Les types de l'OS sont réservés
  (`hostfs`, `dropbox`, `onedrive`, `s3`, et les types du noyau : `proc`,
  `dev`, `tmpfs`…), même quand un connecteur livré est désactivé (§5).
  **Un fournisseur par type** : une seconde Feature qui déclare le même type
  est refusée à l'installation (`apps.package.drive-type-already-provided`).
- La Feature s'installe pour la machine, par un administrateur (ADR 0260 §4).
  Chaque utilisateur connecte ensuite ses propres comptes et lecteurs, comme
  aujourd'hui.

### 2. Le contrat du fournisseur, dans le SDK public

Nouvel espace `Kazo.Os.AppSdk.Drives`. Le fournisseur dérive d'une **classe
abstraite** (on peut lui ajouter un membre virtuel sans casser les
fournisseurs compilés, ce qu'une interface ne permet pas) :

```csharp
public abstract class KazoDriveProvider
{
    // Ouvre un lecteur : jamais d'appel au service ici (la connexion de l'utilisateur n'attend pas le réseau).
    public abstract ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken ct);
}

public abstract class KazoDriveFolder : IAsyncDisposable
{
    public abstract ValueTask<KazoDriveState> GetStateAsync(CancellationToken ct);      // Ready, SignInRequired, Unavailable
    public abstract ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken ct);
    public abstract ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken ct);
    public abstract ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken ct);
    public virtual ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken ct) => new(false);
    public virtual ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken ct) => new((KazoDriveQuota?)null);
}
```

- `KazoDriveOpenRequest` porte ce que l'OS garde du lecteur : la racine, les
  options non secrètes, et un `IKazoDriveProviderContext` qui donne
  `GetAccessTokenAsync()`, `GetSecretAsync(nom)`, l'identité du compte et un
  événement « le service peut être joint de nouveau ».
- Les erreurs sont des `KazoDriveException` avec un genre (`NotFound`,
  `Exists`, `Unavailable`, `SignInRequired`, `RevisionConflict`,
  `FileTooLarge`, `QuotaExceeded`), que l'OS traduit en erreurs POSIX comme
  pour les connecteurs livrés.
- Le SDK **n'expose pas** `IHostFolder` ni `INetworkDriveSource` aux
  fournisseurs : ce sont les contrats internes de la plateforme, que l'OS
  doit pouvoir faire évoluer (lecture partielle de l'ADR 0275, par exemple).
  Un adaptateur de la plateforme (`FeatureDriveSource`) fait le pont.
- Le SDK livre un **banc d'essai** (`KazoDriveProviderTests` dans un paquet de
  test) : la même suite de conformité que les connecteurs livrés (créer, lire,
  conflit de révision, hors ligne, fichier trop gros).

### 3. Ce que l'OS garde, ce que le fournisseur fait

| L'OS (inchangé pour tous les types) | Le fournisseur |
| --- | --- |
| Point de montage sous `/mnt`, modes `0700`/`0600`, liste des lecteurs, démontage à la déconnexion, accès des apps (ADR 0231) | Parler au service : lister, lire, écrire |
| L'interface : « Connecter un lecteur réseau… », Paramètres ▸ Comptes cloud, `mount -t <type>` ; aucun écran du fournisseur | Déclarer son nom, son icône, ses champs |
| La connexion OAuth (fenêtre, PKCE, page de retour, ADR 0267 §5), le renouvellement, le scellement des jetons et des secrets (ADR 0269) | Déclarer ses adresses OAuth et son identifiant d'app public ; demander un jeton d'accès |
| Limite de 128 Mio par fichier, délais, déconnexion du lecteur quand le fournisseur lève `Unavailable` | Répondre vite ; ne jamais joindre le service dans `OpenAsync` |

- **Connexion déclarée**, deux formes, dans la déclaration :
  - `OAuth` : adresses d'autorisation, de jeton et de révocation, identifiant
    d'app public par étendue (tout le stockage, dossier réservé), paramètres
    supplémentaires, adresse et champs de l'identité. C'est le contenu de
    `CloudOAuthProvider`, devenu une donnée de manifeste. L'OS mène toute la
    connexion ; le fournisseur ne voit **jamais** le jeton de renouvellement.
  - `Form` : des champs typés, chacun marqué secret ou non (comme l'adresse,
    le compartiment et les clés de S3). L'OS dessine le formulaire, scelle les
    secrets, et les rend au fournisseur de ce compte seulement.
- **Identifiant d'app OAuth.** Il est public (PKCE, sans secret). L'auteur du
  fournisseur enregistre chez le service la page de retour de chaque site où
  son fournisseur doit marcher (`https://os.kazo.ca/…`, le site bêta). Un
  administrateur d'un autre site le remplace dans
  `/etc/kazoos/cloud-drives.config`, groupe du type, clé `client-id` (le
  document de réglages devient générique, un groupe par type déclaré).
- **Chargement.** Comme un service de Feature (ADR 0262 §4) : l'assemblage est
  chargé au premier lecteur ouvert de ce type, une instance par page, dans le
  contexte de la Feature (compte `kazoos-svc`, ses permissions). Un lecteur
  dont le fournisseur ne se charge pas est monté déconnecté, avec une ligne
  dans `/var/log/apps/<feature>.log` ; la connexion de l'utilisateur continue.
- **À chaud.** `NetworkDriveService` lit les sources dans un registre qui
  change (les connecteurs livrés, plus `AppCatalog.GetDriveProviders`) : un
  fournisseur installé est proposé tout de suite (ADR 0214).
- **Fournisseur retiré ou désactivé.** Ses lecteurs sont démontés mais leurs
  lignes **restent** dans `network-drives.json`, marquées « fournisseur
  absent » ; l'Explorateur les montre grisés avec « Installer le fournisseur »
  (Store). Aujourd'hui une ligne dont le type n'a pas de source est retirée :
  cette règle ne reste que pour un type qui n'a jamais été déclaré. Les
  comptes et leurs jetons restent scellés jusqu'à ce que l'utilisateur les
  retire.

### 4. HTTP direct dans le SDK (prérequis)

- `KazoHttpClient` gagne un mode **direct** : la requête part par le `fetch`
  du navigateur, sans serveur Kazo, avec le corps en flux. C'est ce que les
  connecteurs de l'OS font déjà en interne.
- Nouvelle capacité dangereuse **`network.direct.access`**. L'app déclare ses
  **hôtes** (`api.pcloud.com`, `*.example.org`) dans le manifeste ; l'OS refuse
  tout autre hôte, et l'écran d'approbation les nomme. Pour un fournisseur de
  lecteur, la liste est celle de sa déclaration.
- Limites honnêtes : le service doit accepter CORS depuis une page isolée
  ([ADR 0131](0131-cross-origin-isolation.md) ; Nextcloud et WebDAV ne le font
  pas par défaut), et pas de cookies ni d'en-têtes interdits par le navigateur.
- Ce mode sert aussi les autres apps (Kazo AI pourra retirer son module
  JavaScript), mais c'est hors de cette série.

### 5. Les connecteurs livrés : absents du build sur demande

- Les connecteurs sortent de `Kazo.Os.Platform.Core` vers un assemblage à
  eux, `Kazo.Os.Platform.CloudDrives`, que l'hôte référence selon des
  propriétés MSBuild, sur le modèle de l'ADR 0066 :

  ```xml
  <KazoOsDropboxDrives>false</KazoOsDropboxDrives>
  <KazoOsOneDriveDrives>false</KazoOsOneDriveDrives>
  <KazoOsS3Drives>false</KazoOsS3Drives>
  ```

  Toutes valent `true` par défaut. `dotnet publish -p:KazoOsDropboxDrives=false`
  publie un OS sans Dropbox : il n'est plus proposé, et l'assemblage quitte le
  téléchargement quand les trois sont à `false`. Chaque propriété devient
  aussi un interrupteur d'exécution (`RuntimeHostConfigurationOption`), pour
  qu'un connecteur éteint soit ignoré même si l'assemblage reste pour un
  autre.
- **À l'exécution**, sans republier : `KazoOs:CloudDrives:Disabled` dans
  `appsettings.json` (liste de types) et la clé `disabled` de
  `/etc/kazoos/cloud-drives.config`. Un type désactivé n'est plus proposé ; ses
  lecteurs déjà montés suivent la règle « fournisseur absent » du §3.
- Les connecteurs livrés **restent du code de l'OS** : ils ne passent pas par
  le contrat des tiers et gardent l'accès direct aux contrats internes. Ils
  servent de référence à la suite de conformité.
- L'interface cesse de nommer les services en dur : l'Explorateur, les
  Paramètres et `mount` lisent le nom, l'icône, la forme de connexion et les
  champs de chaque type (livré ou tiers) par `KazoDriveInfo.GetNetworkDriveTypesAsync`,
  qui rend maintenant une description par type.

## Sécurité

- **Ce qu'un fournisseur voit.** Le contenu de tous les fichiers du lecteur et
  un jeton d'accès du compte : c'est inévitable, c'est son rôle. Les garde-fous
  sont donc à l'entrée : installation par un administrateur, paquet signé par
  le catalogue (ADR 0219), capacité dangereuse nommée à l'écran
  (`drives.provide`, déclarable par une Feature seulement), hôtes déclarés.
- **Jetons.** Le jeton de renouvellement et les secrets restent scellés par
  l'OS sur l'appareil (ADR 0269). Un fournisseur ne reçoit que le jeton d'accès
  ou les secrets **du compte du lecteur qu'il ouvre**, jamais ceux d'un autre
  type, et aucune app ordinaire n'en reçoit (ADR 0270 inchangée).
- **Pas de bac à sable.** Le code d'une app tourne dans la page ; son propre
  JavaScript peut appeler `fetch`. La liste d'hôtes borne ce qui passe par le
  SDK, pas un fournisseur malveillant. La barrière réelle reste la revue du
  Store et la signature : le guide du développeur le dira tel quel.
- **Signature.** Inchangée : seul Os-AppMarketPlace signe ; aucun dépôt de
  fournisseur n'a de clé. Le catalogue vérifie en plus la déclaration (type non
  réservé, hôtes en `https`, pas de joker nu `*`).
- **Hameçonnage.** L'OS montre toujours le nom de la Feature à côté du nom du
  service (« pCloud, fourni par ca.exemple.pcloud ») dans le dialogue de
  connexion et les Paramètres ; les icônes de marque suivent la règle du
  2026-11-15 (polices d'icônes ou favicon du service).

## Rognage et API publique

- Les nouveaux types sont dans `Kazo.Os.AppSdk` et `Kazo.Os.App.Abstractions`,
  gardés entiers à la publication : un fournisseur installé les trouve. Son
  paquet passe `check-api` (ADR 0277) comme toute app.
- Un fournisseur ne compte pas sur `System.Net.Http` : il passe par le mode
  direct de `KazoHttpClient` (§4).
- **SDK** : ajouts seulement (`PublicAPI.Unshipped.txt`, fixture `sdk-compat`),
  donc une version **mineure** (3.5.0).
- **`Kazo.Os.Platform.Core`** : les types `CloudDrives.*` changent
  d'assemblage. C'est une rupture pour un hôte tiers qui les nomme, pas pour
  une app. Aucun renvoi de type n'est possible (il obligerait Platform.Core à
  référencer le nouvel assemblage). Le changement est écrit dans les notes de
  version ; l'hôte `Kazo.Os.Ecosystem.Host` suit dans la même version.

## Options écartées

- **B. Une app ordinaire fournit un type** : portée par utilisateur pour une
  chose que le shell montre à toute la machine, et contraire à l'ADR 0260 §5.
- **C comme seule voie** (paquet NuGet composé au build) : rien n'arrive sur
  os.kazo.ca sans republier l'OS, et `IHostFolder` deviendrait un contrat gelé.
- **Le fournisseur mène sa propre connexion** (sa fenêtre, `KazoKeyring`) :
  chaque auteur referait PKCE et la page de retour ; un jeton de
  renouvellement vivrait dans le code d'un tiers ; l'utilisateur verrait un
  écran différent par service.
- **Un fournisseur hors de la page** (worker, protocole sur fil) : meilleure
  isolation, mais les fournisseurs de fichiers de l'OS tournent sur la page et
  la connexion OAuth exige le geste de l'utilisateur ; à revoir si les
  Features gagnent un worker.
- **Les connecteurs livrés deviennent des Features préinstallées** : élégant
  (un seul mécanisme), mais il faut un mécanisme de paquets préinstallés dans
  l'image publiée, qui n'existe pas. Possible plus tard, sans changer le
  contrat des tiers.
- **Interrupteur d'exécution seul pour les connecteurs livrés** : le code
  reste dans le téléchargement, ce qui ne répond pas à « désactivé quand on
  publie » pour qui veut un OS sans ces services.

## Conséquences

- SDK : `Kazo.Os.AppSdk.Drives` (`KazoDriveProvider`, `KazoDriveFolder`,
  modèles, exception, contexte), `FeatureDefinition.DriveProviders`,
  `driveProviders` du manifeste et son schéma, capacités `drives.provide` et
  `network.direct.access`, mode direct de `KazoHttpClient`,
  `KazoDriveInfo.GetNetworkDriveTypesAsync` enrichi.
- Plateforme : registre des fournisseurs dans `AppCatalog`,
  `FeatureDriveSource`, sources dynamiques dans `NetworkDriveService`, lignes
  « fournisseur absent », `CloudProviderCatalog` et réglages génériques,
  assemblage `Kazo.Os.Platform.CloudDrives`, `build/Kazo.Os.CloudDrives.targets`.
- Apps système : Explorateur, Paramètres et `mount` sans nom de service en dur.
- Store : vérification de la déclaration dans `validate.yml`.
- Docs à écrire au code : `framework/drive-providers.md` (guide du
  développeur), `dev/cloud-drives.md`, `dev/network-drive-sources.md`,
  `dev/cloud-accounts.md`, `framework/capabilities.md`, `framework/sdk-network.md`,
  `dev/trimmed-publish.md`, `features.md`, entrées `changes/unreleased`.
- Tests attendus : fournisseur factice installé à chaud puis monté ; type
  réservé ou en double refusé ; hôte non déclaré refusé ; fournisseur qui lève
  ou ne se charge pas, connexion de l'utilisateur intacte ; Feature retirée,
  lignes gardées puis lecteur revenu à la réinstallation ; publication avec
  les trois propriétés à `false` (assemblage absent, types non proposés) ;
  E2E sur l'OS rogné avec un fournisseur installé.

## Questions posées à Benoit

1. **Quelle forme prend un fournisseur tiers ?**
   - A) **(Recommandé)** Une Feature du Store, installée pour la machine par
     un administrateur ; chaque utilisateur connecte ensuite ses comptes.
   - B) Une app ordinaire, installée par chaque utilisateur : pas besoin
     d'administrateur, mais deux utilisateurs peuvent avoir deux versions du
     même type, et il faut une exception à l'ADR 0260 §5.
   - C) Seulement un paquet NuGet composé au build : rien dans le Store ; un
     fournisseur n'arrive qu'en republiant l'OS.
2. **Qui mène la connexion au service et garde les jetons ?**
   - A) **(Recommandé)** L'OS : le fournisseur déclare ses adresses OAuth ou
     ses champs ; il ne reçoit qu'un jeton d'accès.
   - B) Le fournisseur : il ouvre sa propre fenêtre et range ses jetons ;
     plus souple pour un service exotique, mais chaque auteur refait la
     connexion et garde un jeton de renouvellement.
3. **Comment désactiver les fournisseurs livrés ?**
   - A) **(Recommandé)** Une propriété de build par fournisseur (code absent
     du téléchargement) **et** un réglage d'exécution (sans republier). Les
     connecteurs changent d'assemblage : rupture pour un hôte tiers qui les
     nomme.
   - B) Le réglage d'exécution seulement : rien ne casse, mais le code reste
     dans le téléchargement.
   - C) Les connecteurs livrés deviennent des Features préinstallées, que
     l'administrateur retire dans les Paramètres : un seul mécanisme, mais il
     faut d'abord inventer les paquets préinstallés (plus long).
4. **Comment un fournisseur joint-il son service ?**
   - A) **(Recommandé)** Un mode direct dans `KazoHttpClient`, derrière une
     capacité dangereuse et une liste d'hôtes déclarés.
   - B) Chaque fournisseur écrit son module JavaScript `fetch` (comme Kazo AI
     aujourd'hui) : aucun travail dans l'OS, aucune liste d'hôtes à l'écran.
   - C) Par le relais du serveur Kazo : marche sans CORS, mais plus rien ne
     marche sans serveur, et le contenu des fichiers passe par le serveur.
5. **Que deviennent les lecteurs quand leur fournisseur est retiré ?**
   - A) **(Recommandé)** Ils restent dans la liste, grisés, et reviennent à
     la réinstallation ; les comptes restent jusqu'à ce que l'utilisateur les
     retire.
   - B) Ils sont retirés de la liste (règle actuelle pour un type inconnu) :
     plus simple, mais une mise à jour ratée fait tout reconnecter.
