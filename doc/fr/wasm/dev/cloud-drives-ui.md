> [English version](../../../en/wasm/dev/cloud-drives-ui.md)

# Lecteurs cloud : fenêtre de connexion, page Paramètres et commandes

Chantier `cloud-drives/5-ui` de la série `cloud-drives`
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md) §11,
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). Ce document décrit ce
que voit l'utilisateur et comment les apps système y arrivent par le SDK public.
Depuis le chantier 6 de la série `drive-providers`
([ADR 0282](adr/0282-third-party-drive-providers.md) §5), l'interface ne nomme
plus aucun service dans son code : elle lit la description de chaque type que
l'OS lui donne (section « Types décrits par l'OS »).

## Rôle

- Dans l'Explorateur de fichiers, **Connecter un lecteur réseau…** ouvre une
  fenêtre qui propose un dossier de l'ordinateur, les comptes déjà connectés,
  les services décrits par l'OS : ceux de Kazo OS (Dropbox, OneDrive, S3) et
  ceux qu'une Feature fournit, toujours avec « Fourni par <Feature> ».
- **Paramètres ▸ Réseau ▸ Comptes cloud** liste les comptes : fournisseur,
  nom, état, étendue, lecteurs montés, date de connexion. On y reconnecte un
  compte et on le retire (ses lecteurs sont démontés).
- Le terminal monte les mêmes lecteurs : `mount -t <type>` pour tout type
  déclaré.
- Un lecteur dont le fournisseur manque reste dans l'explorateur, grisé, avec
  la raison et, pour une Feature absente, **Installer le fournisseur**.

## Architecture

| Pièce | Rôle |
| --- | --- |
| `KazoCloudAccounts` / `IAppCloudAccountsGateway` (SDK) | Fournisseurs, comptes, connexion, reconnexion, retrait. Références seulement, jamais de jeton. |
| `KazoDriveInfo.GetNetworkDriveTypesAsync` / `MountNetworkDriveAsync` (SDK) | Types montables et montage d'un lecteur réseau de n'importe quel type, avec `KazoNetworkDriveRequest` (compte, racine, options, secrets). |
| `KazoDriveInfo.DescribeNetworkDriveTypesAsync` (SDK) | La description de chaque type : nom, icône, forme de connexion (dossier de l'ordinateur, OAuth avec ses étendues, formulaire avec ses champs) et Feature qui le fournit (`KazoNetworkDriveTypeInfo`). |
| `KazoDriveInfo.GetProviderMissingDrivesAsync` (SDK) | Les lecteurs gardés sans fournisseur, avec `ProviderMissing` (retiré, désactivé, désactivé par l'administrateur) et `ProviderFeatureId`. |
| `IDescribedNetworkDriveSource` (Platform.Core) | Une source se décrit dans la langue de l'appelant : `HostFolderDriveSource`, `CloudDriveSource` (Dropbox, OneDrive), `S3DriveSource`, `FeatureDriveSource`. |
| Capacité `cloud-accounts.manage` (dangereuse) | Exigée par toute la passerelle des comptes (sauf `CanConnectAsync`) et par le montage d'un type autre que `hostfs`. Déclarée par l'Explorateur, Paramètres et `mount`. |
| `AppCloudAccountsGateway`, `CapabilityCheckedDriveGateway` (Platform.Core) | Côté OS : vérifient la capacité et traduisent les refus en `KazoCloudAccountException` ou en exceptions .NET. |
| Passerelles distantes (WorkerHost) | Les commandes tournent dans un worker : `drv.types`, `drv.describe`, `drv.missing`, `drv.mountnet`, `cld.*` traversent la frontière avec un code d'erreur `ce`. |
| `ConnectNetworkDriveDialog` (Explorateur) | La fenêtre : étape de choix, étape OAuth (étendue, dossier de départ facultatif), formulaire générique. Plein écran sous 600 px. |
| `NetworkDrivesSidebar` | Ouvre la fenêtre ; en mode `Compact`, des puces dans la barre de l'explorateur mobile. |
| `CloudAccountsPanel` (Paramètres) | La page des comptes. |
| `MountCommand` | Les options `-t` et `-o`. |

## La fenêtre « Connecter un lecteur réseau »

1. À l'ouverture, elle lit la description des types
   (`DescribeNetworkDriveTypesAsync`), puis, s'il y a un type cloud, les
   comptes, et appelle `PrepareAsync` (chargement de la page de retour avant
   le clic).
2. **Choix** : comptes existants (un clic monte), puis un bouton par type
   décrit : le dossier de l'ordinateur d'abord, les services de Kazo OS, puis
   ceux des Features, chacun avec son icône et, pour une Feature, « Fourni par
   <nom> (<id>) ».
3. **Fournisseur** : l'utilisateur choisit l'étendue (tout le stockage ou le
   dossier réservé à Kazo OS) quand le fournisseur offre les deux, et un
   dossier de départ facultatif. Avec le dossier réservé, le champ est précédé
   de l'endroit où le fournisseur met ce dossier dans le stockage
   (`/Apps/Kazo OS Folder/` pour Dropbox, `/Apps/Kazo OS/` pour OneDrive,
   `KazoCloudProvider.AppFolderPath`) : le dossier de départ est relatif à
   lui. « Se connecter et monter » appelle
   `ConnectAsync` puis `MountNetworkDriveAsync` sans `await` avant, pour que
   la fenêtre de connexion parte du clic (geste de l'utilisateur). Pendant
   que la fenêtre de connexion est ouverte, le dialogue l'indique, et Retour,
   Fermer et Échap restent utilisables : l'OS ne voit pas cette fenêtre se
   fermer (COOP, ADR 0131) et une page d'erreur du fournisseur ne revient
   jamais ; quitter annule l'attente (jeton de `ConnectAsync`) et ignore son
   résultat tardif, sans afficher d'erreur.
4. **Formulaire** (S3 et tout type déclaré avec un formulaire) : les champs
   viennent de la description, dans l'ordre déclaré : texte, adresse,
   nombre, liste de choix, secret (masqué). Un champ facultatif porte
   « (facultatif) ». Les valeurs secrètes partent dans `Secrets`, les autres
   dans `Options` ; l'OS les vérifie contre la déclaration avant de créer le
   compte (`FeatureDriveForm.Check`) et range les secrets dans le trousseau.
5. Une erreur s'affiche dans la fenêtre avec le texte traduit de
   `CloudDrive.Error.{raison}`. Le lecteur monté s'ouvre dans l'explorateur.

Sans trousseau (invité, passkey sans PRF), la fenêtre l'explique et ne propose
que le dossier de l'ordinateur.

## La section « Lecteurs réseau » de l'explorateur

Chaque lecteur cloud porte l'icône que la description de son type donne
(`KazoNetworkDriveTypeInfo.Icon`, écrite `bibliothèque:nom`, ou l'image
`IconUrl` d'une Feature) : la marque Dropbox ; Microsoft pour OneDrive, qui
n'a pas de glyphe propre dans les polices d'icônes ; une base de données pour
S3, tirées de Bootstrap Icons (MIT) et Lucide, déjà livrées par le SDK. Sous
le nom du compte, une seconde ligne donne le service et, s'il y en a un, le
dossier de départ (`KazoDriveInfo.Root`) : deux comptes portent souvent la même
adresse courriel. Sur téléphone, les puces gardent l'icône et le nom accessible
porte la même seconde ligne.

### Lecteur dont le fournisseur manque

`GetProviderMissingDrivesAsync` donne les lignes que l'OS garde sans les
monter (ADR 0282 §3, §5). L'explorateur les montre grisées, avec une icône de
nuage barré, la raison en seconde ligne et l'éjection pour oublier le lecteur :

| Raison (`KazoDriveProviderMissingReason`) | Texte | Action |
| --- | --- | --- |
| `Removed` (Feature non installée) | « Fournisseur non installé » | **Installer le fournisseur** : le Store s'ouvre sur la Feature (`--app=<id>`) ; le lecteur revient seul une fois elle installée. |
| `Disabled` (Feature désactivée) | « Fournisseur désactivé » | Aucune (`aria-disabled`). |
| `DisabledByAdministrator` (type livré coupé par `KazoOs:CloudDrives:Disabled` ou `/etc/kazoos/cloud-drives.config`) | « Désactivé par l'administrateur » | Aucune. |

Le lecteur garde l'id de la Feature qui l'a fourni : seule cette Feature le
remonte (réponse A de Benoit au chantier 4). Une autre Feature qui déclare le
même type ne le prend pas : le lecteur reste « non installé » avec l'id
d'origine. Sur téléphone, les mêmes lecteurs sont des puces grisées.

## Types décrits par l'OS (ADR 0282 §5)

`KazoNetworkDriveTypeInfo` porte, pour chaque type offert maintenant :

- `Type`, `DisplayName` (dans la langue de l'appelant pour les types de
  l'OS, tel que la Feature l'écrit pour les autres), `Icon` ou `IconUrl` ;
- `Connection` : `HostFolder`, `OAuth` (avec `Scopes` et `AppFolderPath`) ou
  `Form` (avec `Fields` : nom, libellé, type, obligatoire, valeur par défaut,
  exemple, choix) ;
- `ProviderFeatureId`, `ProviderFeatureName` et `IsThirdParty` pour un type
  qu'une Feature fournit.

Un type OAuth dont l'OS n'a pas d'identifiant d'app n'est pas décrit (il ne
peut pas se connecter). Une passerelle qui ne connaît pas encore la méthode
(SDK plus ancien) décrit chaque type par son seul nom.

## Page Paramètres « Comptes cloud »

- Le nom du service vient de la description du type ; un service fourni par
  une Feature montre « Fourni par <nom> (<id>) ». L'étendue et
  **Reconnecter** ne paraissent que pour un type OAuth.

- Un compte « à reconnecter » montre **Reconnecter** : `ReconnectAsync` dans le
  clic, puis `KazoDriveInfo.ReconnectAsync` sur chacun de ses lecteurs.
- **Retirer** demande une confirmation dans la ligne, puis `RemoveAsync`
  (démonte ses lecteurs, révoque le jeton quand le fournisseur le permet).
- Les lecteurs d'un compte viennent de `KazoDriveInfo.Account`.

## Commandes

```
mount -t TYPE [-o account=NOM,scope=full|app,root=/Dossier] [/mnt/nom]   # type OAuth (dropbox, onedrive…)
mount -t TYPE -o CHAMP=VALEUR,…[,account=ID] [/mnt/nom]                 # type à formulaire (s3, webdav…)
mount -o remount /mnt/nom
umount /mnt/nom
```

`mount` lit la description du type : il ne connaît aucun service. Pour un
type à formulaire, `-o` porte les champs non secrets déclarés ; une erreur
d'usage donne la syntaxe du type (`mount -t s3 -o endpoint=…,bucket=…[,region=…,prefix=…,addressing=path|virtual,account=ID]`).

- Le compte : `account=` (identifiant ou nom), sinon le seul compte du
  service, sinon une nouvelle connexion (la touche Entrée est le geste). S'il
  y a plusieurs comptes, `mount` les nomme et demande `account=`.
- L'étendue d'une nouvelle connexion : `scope=`, obligatoire quand le
  fournisseur offre les deux (choix de l'utilisateur, ADR 0267 §8).
- Formulaire : chaque champ secret est demandé sans écho, par son libellé.
  Un champ secret écrit dans `-o` est refusé (code 1), car l'historique garde
  la ligne de commande. `account=` réutilise les valeurs d'un compte. Pour
  S3, l'identifiant de clé d'accès est désormais secret lui aussi.
- Codes de sortie de util-linux : 0, 1 (usage), 32 (échec du montage).

## Décisions

- Les apps système passent par le SDK public, derrière une capacité
  dangereuse (ADR 0270, réponse A de Benoit).
- `KazoNetworkDriveRequest.ToString()` masque les secrets.
- La fenêtre ne garde pas les valeurs secrètes après le montage.
- Aucun nom de service dans le code de l'Explorateur, de Paramètres et de
  `mount` (ADR 0282 §5) : tout vient de la description.
- Les marques des services (Dropbox, Microsoft) viennent des polices d'icônes
  déjà présentes ; Benoit l'a permis le 2026-11-15 (l'utilisateur se connecte
  à son propre compte de ce service).

## Tests

- `MountCommandTests` : `-t dropbox|onedrive|s3` et un type de Feature
  (`webdav`), options, comptes, étendue, secrets lus sans écho, secret refusé
  sur la ligne de commande, syntaxe du type.
- `FeatureDriveFormTests` : description d'un type de Feature, vérification
  des valeurs contre la déclaration, compte créé avant le lecteur.
- `FeatureDriveMountTests` : description et raison d'un lecteur sans
  fournisseur (désactivé, retiré, autre Feature du même type).
- `S3DriveSourceTests` : description de S3 (champs, secrets, langue).
- `AppCloudAccountsGatewayTests` : capacité, références, raisons des refus,
  montage cloud réservé à la capacité.
- `NetworkDriveSourceTests` : types disponibles et montage par le SDK avec la
  source factice.
- `WireCodecDriveTests` : compte du lecteur, requête avec options et secrets,
  comptes et fournisseurs, descriptions des types et lecteurs sans
  fournisseur à travers le worker.
- `SettingsWindowTests` : la page est dans le groupe Réseau.
- E2E `CloudDrivesUiTests` : fenêtre (bureau et mobile) et page Paramètres,
  axe sans défaut grave, captures dans `Tests/TestResults/cloud-drives/`. Le
  test donne à l'OS de faux identifiants d'app Dropbox et OneDrive (en
  réécrivant `appsettings.json`) et répond lui-même à une fausse adresse S3 :
  étape Dropbox avec les deux étendues, formulaire S3 et refus du service
  affiché dans la fenêtre. Aucune requête ne quitte la machine. Un second
  test écrit la liste des lecteurs depuis le terminal (un lecteur S3 coupé par
  l'administrateur, un lecteur d'une Feature absente), se reconnecte et
  vérifie les lecteurs grisés sur bureau et téléphone.

## Liste des tâches

- [x] SDK : comptes cloud, types montables, montage d'un lecteur réseau (ADR 0270).
- [x] Capacité `cloud-accounts.manage`, passerelles OS et worker.
- [x] Fenêtre « Connecter un lecteur réseau » (bureau et mobile).
- [x] Page Paramètres « Comptes cloud ».
- [x] `mount -t dropbox|onedrive|s3`, `-o remount`, `umount`.
- [x] Textes français et anglais, axe, captures.
- [x] Captures des étapes fournisseur (Dropbox) et S3 avec les connecteurs des chantiers 3, 4 et 6.
- [x] Chantier 7 : quitter une connexion qui a échoué, marque du service et dossier de départ dans « Lecteurs réseau », chemin du dossier réservé devant le dossier de départ.
- [x] Chantier 6 de `drive-providers` : types décrits par l'OS, formulaire générique (S3 compris), « Fourni par », `mount -t` pour tout type, lecteur sans fournisseur grisé, captures dans `/mnt/project-files/drive-providers/ui/`.
