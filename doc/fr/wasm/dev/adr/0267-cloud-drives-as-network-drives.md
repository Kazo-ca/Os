> [English version](../../../../en/wasm/dev/adr/0267-cloud-drives-as-network-drives.md)

# ADR 0267 : le stockage cloud de l'utilisateur monté comme lecteur réseau

## Statut

Acceptée le 2026-11-13. Benoit a répondu aux six questions posées dans le fil
(voir la fin du document). Série `cloud-drives/base`, plan dans
[`cloud-drives-plan.md`](../plans/cloud-drives-plan.md). Rien n'est codé.

- **S'appuie sur :** l'[ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  (lecteurs réseau, `IHostFolder`, `NetworkDriveService`),
  l'[ADR 0185](0185-file-explorer-improvements.md) (lecteurs suivis),
  l'[ADR 0231](0231-network-drives-count-as-personal-files.md) (accès des apps),
  l'[ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  (trousseau), l'[ADR 0131](0131-cross-origin-isolation.md) (isolation
  cross-origin) et l'[ADR 0245](0245-raise-the-maximum-file-size.md) (128 Mio).
- **Ne modifie aucune ADR.** Les règles de l'ADR 0181 s'appliquent aux lecteurs
  cloud sauf mention contraire ci-dessous.

## Contexte

Benoit veut que l'utilisateur branche **son** stockage (Dropbox, OneDrive,
Google Drive…) dans le système de fichiers, sans serveur à maintenir et sans
coût : Kazo OS est un site statique (os.kazo.ca). Une app enregistrée chez un
fournisseur est acceptable si elle est gratuite.

Vérifié le 2026-11-13, sans compte réel, depuis Chromium sur une page servie
avec `COOP: same-origin` et `COEP: credentialless` comme Kazo OS :

- Les API de fichiers et de jetons de Dropbox, de Google et de Microsoft Graph
  acceptent les requêtes CORS d'une page isolée ; Box les refuse.
- **Dropbox** : OAuth par code et PKCE sans secret, jeton de rafraîchissement
  sans expiration. Gratuit ; revue gratuite demandée au 50ᵉ utilisateur.
- **OneDrive** : OAuth par code et PKCE avec une adresse de retour de type
  « SPA ». Le jeton de rafraîchissement dure 24 heures, sans prolongation.
  Gratuit.
- **Google Drive** : l'échange du code exige un secret, donc un serveur ; sans
  serveur il ne reste que le flux implicite (une heure, pas de
  rafraîchissement). L'étendue `drive` est restreinte : vérification et
  évaluation de sécurité payante chaque année. L'étendue gratuite `drive.file`
  ne montre que les fichiers créés par l'app.
- **WebDAV / Nextcloud** : pas d'en-têtes CORS par défaut.
- **S3 compatible** : marche avec les clés de l'utilisateur et une règle CORS
  qu'il pose sur son compartiment ; aucune app à enregistrer.
- Avec `COOP: same-origin`, une fenêtre ouverte vers un fournisseur perd son
  lien avec l'OS (`window.opener`) : les bibliothèques de connexion par fenêtre
  des fournisseurs ne marchent pas telles quelles.

L'OS a déjà les lecteurs réseau de l'ADR 0181 : montage à chaud dans `/mnt`,
état « déconnecté » et « Reconnecter », suppression définitive, barre latérale
de l'explorateur. `HostFolderFileSystemProvider` fait un système de fichiers de
tout `IHostFolder`.

## Décision

### 1. Un lecteur cloud est un lecteur réseau

Un stockage cloud se monte comme un lecteur de l'ADR 0181, par un nouveau
`IHostFolder`. Tout ce que l'ADR 0181 décide reste vrai : montage sur un enfant
direct de `/mnt`, propriétaire l'utilisateur qui a monté, modes `0700` et
`0600`, `chmod` refusé, racine protégée, suppression définitive après
confirmation, hors synchronisation et hors sauvegarde, démontage à la
déconnexion de l'utilisateur. L'accès des apps suit l'ADR 0231. Aucune capacité
nouvelle.

### 2. Connecteurs intégrés à l'OS (décidé par Benoit)

Les connecteurs sont livrés dans l'OS, pas comme apps du Store. Rien ne
s'active tant que l'utilisateur ne connecte pas un compte. Un contrat du SDK
qui laisserait une app du Store fournir un système de fichiers reste possible
plus tard, par une autre ADR, sur les interfaces de cette ADR.

- Un connecteur est écrit en C# avec `HttpClient` (qui devient `fetch` dans le
  navigateur), dans une bibliothèque de la plateforme sans dépendance au
  navigateur, testable avec un faux gestionnaire HTTP.
- Le seul JavaScript est la fenêtre de connexion (section 5), dans
  Infrastructure.Browser.

### 3. Sources de lecteurs

`NetworkDriveService` ne connaît aujourd'hui que le dialogue de dossiers du
navigateur. Il reçoit une liste de **sources de lecteurs**, une par type :
`hostfs` (l'existante), `dropbox`, `onedrive`, `s3`. Une source sait dire si
elle est disponible, créer un lecteur (avec ce qu'elle demande à
l'utilisateur), le rouvrir à la connexion de l'utilisateur et l'oublier.

- La table `~/.local/state/kazoos/network-drives.json` gagne le type du lecteur
  (absent = `hostfs`) et, pour un lecteur cloud, la référence du compte et la
  racine choisie. Elle reste locale à l'appareil.
- `DriveFormat` et `/proc/mounts` montrent le type :
  `dropbox:<compte> /mnt/Dropbox dropbox rw,nosuid,nodev 0 0`.
- Point de montage par défaut : `/mnt/Dropbox`, `/mnt/OneDrive`,
  `/mnt/<compartiment>`, puis `-2`, `-3`… si le nom est pris.

### 4. Interfaces facultatives d'un dossier

Un `IHostFolder` peut offrir en plus :

- **le quota** : taille totale et espace libre, pour `df` et `KazoDriveInfo`
  (un dossier de l'ordinateur continue d'afficher `-`) ;
- **la révision du service** (`rev` de Dropbox, `eTag` de Graph, `ETag` de S3) à
  la place de la révision dérivée de la date et de la taille ; une écriture
  avec une révision attendue est envoyée au service avec cette condition, et un
  refus du service devient le conflit de révision que l'OS connaît déjà ;
- **les changements distants**, par l'interface `IObservableHostFolder` de
  l'ADR 0185 quand le service le permet ; sinon la relecture sur minuterie
  existante s'applique.

### 5. Comptes cloud et connexion

Un service de comptes cloud gère la connexion, les jetons et leur
renouvellement.

- **Flux** : OAuth 2.0 par code avec PKCE (S256) et `state`, sans secret.
- **Fenêtre** : l'OS ouvre le fournisseur dans une fenêtre du navigateur, après
  un geste de l'utilisateur. Le fournisseur revient sur une **page statique de
  Kazo OS**, servie par la bibliothèque Infrastructure.Browser
  (`_content/…/oauth-callback.html`), qui rend le code à l'OS par
  `BroadcastChannel` puis se ferme. Les hôtes ne sont pas modifiés. Une
  redirection de la page entière est interdite : elle redémarrerait l'OS.
- **Jetons** : dans le trousseau de l'utilisateur (ADR 0100), chiffrés et
  ouverts à sa connexion. La liste des comptes
  (`~/.local/state/kazoos/cloud-accounts.json`, `0600`) ne garde que des
  références. Rien n'est synchronisé ni sauvegardé. Les apps ne reçoivent
  jamais un jeton : elles passent par le service de fichiers.
- **Retirer un compte** démonte ses lecteurs, révoque le jeton quand le
  fournisseur le permet et l'efface du trousseau.
- **Identifiants d'app** : publics, dans les réglages de l'hôte
  (`appsettings.json`), remplaçables par un fichier sous `/etc/kazoos/` pour un
  OS hébergé à une autre adresse. Un fournisseur sans identifiant n'est pas
  proposé.
- Si le compte invité n'a pas de trousseau, la connexion d'un compte cloud lui
  est refusée avec un message clair ; le chantier 2 le vérifie.

### 6. Fournisseurs de la première version (décidé par Benoit)

**Dropbox, OneDrive et S3 compatible.**

- **Dropbox** : API v2. Le jeton se renouvelle sans l'utilisateur.
- **OneDrive** : Microsoft Graph. Après 24 heures le lecteur passe
  « déconnecté » ; « Reconnecter » rouvre la fenêtre de Microsoft. Le contenu se
  télécharge par l'adresse `@microsoft.graph.downloadUrl`, pas par `/content`.
- **S3 compatible** (Cloudflare R2, Backblaze B2, MinIO, AWS) : l'utilisateur
  saisit l'adresse, la région, le compartiment, un préfixe facultatif et ses
  clés ; les requêtes sont signées (SigV4) dans l'OS ; les clés vont dans le
  trousseau. Un guide décrit la règle CORS à poser sur le compartiment.

### 7. Google Drive, WebDAV et les autres (décidé par Benoit)

**Pas dans la première version.** Google Drive demanderait un serveur ou une
évaluation payante pour un résultat complet. Pour ces services, l'utilisateur
monte le dossier local que synchronise leur client (ADR 0181, Chrome et Edge
sur ordinateur). On y revient par une nouvelle ADR si les règles changent.

### 8. Étendue de l'accès (décidé par Benoit : l'utilisateur choisit)

À la connexion, l'utilisateur choisit entre **tout son stockage** et **un
dossier réservé à Kazo OS**.

- **Dropbox** fixe l'étendue par app : deux apps enregistrées, « Full Dropbox »
  et « App folder » (`Apps/Kazo OS`).
- **OneDrive** : une seule inscription ; l'OS demande `Files.ReadWrite` ou
  `Files.ReadWrite.AppFolder`.
- **S3** : le compartiment et le préfixe saisis sont l'étendue.

Le choix se voit dans la liste des comptes. Le changer demande de reconnecter
le compte.

### 9. Hors ligne (décidé par Benoit)

**Pas de cache.** Sans réseau, ou quand le jeton n'est plus valable, le lecteur
est « déconnecté » : il reste visible et toute opération répond
`ProviderUnavailable`, comme un lecteur de l'ADR 0181 sans permission. Il se
reconnecte seul au retour du réseau si le jeton est encore valable.

### 10. Limites

- Un fichier est lu et écrit en entier en mémoire : la limite de 128 Mio de
  l'ADR 0245 s'applique. Un fichier plus gros apparaît dans la liste ; sa
  lecture échoue avec l'erreur existante de taille.
- Les noms que Kazo OS refuse sont ignorés, comme pour un dossier de
  l'ordinateur.
- Copier un dossier se fait fichier par fichier ; une copie interrompue laisse
  une copie partielle.

### 11. Interface

- **Explorateur** : « Connecter un lecteur réseau… » propose le dossier de
  l'ordinateur (quand le navigateur l'a) et les fournisseurs configurés.
  L'explorateur mobile gagne la même entrée : les lecteurs cloud marchent dans
  tous les navigateurs.
- **Paramètres** : une page liste les comptes cloud, leur étendue et leurs
  lecteurs, et permet de les retirer.
- **Terminal** : `mount -t dropbox|onedrive|s3 [point]`, `mount -o remount`,
  `umount` et `df`, avec les codes de sortie de l'ADR 0181.

## Questions posées à Benoit

Posées dans le fil le 2026-11-13. Réponse : la recommandation pour toutes, sauf
la question 4.

1. Fournisseurs de la première version : **Dropbox, OneDrive et S3**
   (recommandé) ; Dropbox seul ; tout, y compris Google Drive limité et WebDAV.
2. Google Drive : **pas dans la première version** (recommandé) ; mode limité
   gratuit ; payer l'évaluation annuelle ; mode avancé où l'utilisateur crée sa
   propre app Google.
3. Connecteurs : **intégrés à l'OS** (recommandé) ; contrat du SDK puis apps du
   Store.
4. Étendue : tout le stockage (recommandé) ; dossier d'app seulement ;
   **l'utilisateur choisit à la connexion** (choix de Benoit).
5. Qui enregistre les apps : **Benoit**, avec des identifiants remplaçables
   (recommandé) ; chaque utilisateur apporte le sien.
6. Hors ligne : **pas de cache** (recommandé) ; cache de lecture ; cache avec
   écriture différée.

## Conséquences

- Benoit crée deux apps Dropbox et une inscription Microsoft Entra, toutes
  gratuites, et publie une page de politique de confidentialité sur os.kazo.ca.
  La liste exacte est dans le plan.
- Un essai avec un vrai compte par connecteur est nécessaire avant de fusionner
  la série dans `main` : les tests automatiques utilisent des faux serveurs.
- Un utilisateur de OneDrive clique sur « Reconnecter » une fois par jour.
- Tout script de l'origine de l'OS peut lire un jeton en mémoire pendant que le
  trousseau est ouvert : c'est le modèle de menace déjà accepté pour le
  trousseau.
- À essayer sur un vrai appareil : une PWA installée sur iOS a un stockage
  séparé de Safari, et la page de retour pourrait ne pas joindre l'OS.
- API publique nouvelle (sources de lecteurs, interfaces facultatives, comptes
  cloud) dans les `PublicAPI.Unshipped.txt` ; le SDK des apps ne change pas,
  sauf le format et la taille que `KazoDriveInfo` rapporte déjà.
- Tests : sources multiples et restauration, suite de contrat commune à tous
  les connecteurs sur un faux service HTTP, PKCE et page de retour avec un faux
  serveur OAuth en E2E, commandes `mount`, `umount` et `df`.
