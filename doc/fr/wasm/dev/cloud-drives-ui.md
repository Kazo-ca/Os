> [English version](../../../en/wasm/dev/cloud-drives-ui.md)

# Lecteurs cloud : fenêtre de connexion, page Paramètres et commandes

Chantier `cloud-drives/5-ui` de la série `cloud-drives`
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md) §11,
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). Ce document décrit ce
que voit l'utilisateur et comment les apps système y arrivent par le SDK public.

## Rôle

- Dans l'Explorateur de fichiers, **Connecter un lecteur réseau…** ouvre une
  fenêtre qui propose un dossier de l'ordinateur, les comptes déjà connectés,
  les fournisseurs configurés (Dropbox, OneDrive) et un stockage S3 compatible.
- **Paramètres ▸ Réseau ▸ Comptes cloud** liste les comptes : fournisseur,
  nom, état, étendue, lecteurs montés, date de connexion. On y reconnecte un
  compte et on le retire (ses lecteurs sont démontés).
- Le terminal monte les mêmes lecteurs : `mount -t dropbox|onedrive|s3`.

## Architecture

| Pièce | Rôle |
| --- | --- |
| `KazoCloudAccounts` / `IAppCloudAccountsGateway` (SDK) | Fournisseurs, comptes, connexion, reconnexion, retrait. Références seulement, jamais de jeton. |
| `KazoDriveInfo.GetNetworkDriveTypesAsync` / `MountNetworkDriveAsync` (SDK) | Types montables et montage d'un lecteur réseau de n'importe quel type, avec `KazoNetworkDriveRequest` (compte, racine, options, secrets). |
| Capacité `cloud-accounts.manage` (dangereuse) | Exigée par toute la passerelle des comptes (sauf `CanConnectAsync`) et par le montage d'un type autre que `hostfs`. Déclarée par l'Explorateur, Paramètres et `mount`. |
| `AppCloudAccountsGateway`, `CapabilityCheckedDriveGateway` (Platform.Core) | Côté OS : vérifient la capacité et traduisent les refus en `KazoCloudAccountException` ou en exceptions .NET. |
| Passerelles distantes (WorkerHost) | Les commandes tournent dans un worker : `drv.types`, `drv.mountnet`, `cld.*` traversent la frontière avec un code d'erreur `ce`. |
| `ConnectNetworkDriveDialog` (Explorateur) | La fenêtre : étape de choix, étape du fournisseur (étendue, dossier de départ facultatif), formulaire S3. Plein écran sous 600 px. |
| `NetworkDrivesSidebar` | Ouvre la fenêtre ; en mode `Compact`, des puces dans la barre de l'explorateur mobile. |
| `CloudAccountsPanel` (Paramètres) | La page des comptes. |
| `MountCommand` | Les options `-t` et `-o`. |

## La fenêtre « Connecter un lecteur réseau »

1. À l'ouverture, elle lit les types montables, puis, s'il y a un type cloud,
   les fournisseurs, les comptes et appelle `PrepareAsync` (chargement de la
   page de retour avant le clic).
2. **Choix** : comptes existants (un clic monte), dossier de l'ordinateur,
   un bouton par fournisseur configuré dont la source est présente, S3.
3. **Fournisseur** : l'utilisateur choisit l'étendue (tout le stockage ou le
   dossier réservé à Kazo OS) quand le fournisseur offre les deux, et un
   dossier de départ facultatif. « Se connecter et monter » appelle
   `ConnectAsync` puis `MountNetworkDriveAsync` sans `await` avant, pour que
   la fenêtre de connexion parte du clic (geste de l'utilisateur).
4. **S3** : adresse, compartiment, région, préfixe, adressage
   (automatique, chemin, hôte virtuel), clé d'accès et clé secrète (champ
   masqué). Les clés partent dans `Secrets` ; la source les range dans le
   trousseau.
5. Une erreur s'affiche dans la fenêtre avec le texte traduit de
   `CloudDrive.Error.{raison}`. Le lecteur monté s'ouvre dans l'explorateur.

Sans trousseau (invité, passkey sans PRF), la fenêtre l'explique et ne propose
que le dossier de l'ordinateur.

## Page Paramètres « Comptes cloud »

- Un compte « à reconnecter » montre **Reconnecter** : `ReconnectAsync` dans le
  clic, puis `KazoDriveInfo.ReconnectAsync` sur chacun de ses lecteurs.
- **Retirer** demande une confirmation dans la ligne, puis `RemoveAsync`
  (démonte ses lecteurs, révoque le jeton quand le fournisseur le permet).
- Les lecteurs d'un compte viennent de `KazoDriveInfo.Account`.

## Commandes

```
mount -t dropbox|onedrive [-o account=NOM,scope=full|app,root=/Dossier] [/mnt/nom]
mount -t s3 -o endpoint=URL,bucket=NOM[,region=R,prefix=P,addressing=path|virtual,account=ID] [/mnt/nom]
mount -o remount /mnt/nom
umount /mnt/nom
```

- Le compte : `account=` (identifiant ou nom), sinon le seul compte du
  service, sinon une nouvelle connexion (la touche Entrée est le geste). S'il
  y a plusieurs comptes, `mount` les nomme et demande `account=`.
- L'étendue d'une nouvelle connexion : `scope=`, obligatoire quand le
  fournisseur offre les deux (choix de l'utilisateur, ADR 0267 §8).
- S3 : les clés sont demandées sans écho, jamais sur la ligne de commande
  (l'historique la garde). `account=` réutilise les clés d'un compte S3.
- Codes de sortie de util-linux : 0, 1 (usage), 32 (échec du montage).

## Décisions

- Les apps système passent par le SDK public, derrière une capacité
  dangereuse (ADR 0270, réponse A de Benoit).
- `KazoNetworkDriveRequest.ToString()` masque les secrets.
- La fenêtre ne garde pas les clés S3 après le montage.

## Tests

- `MountCommandTests` : `-t dropbox|onedrive|s3`, options, comptes, étendue,
  clés lues sans écho, refus.
- `AppCloudAccountsGatewayTests` : capacité, références, raisons des refus,
  montage cloud réservé à la capacité.
- `NetworkDriveSourceTests` : types disponibles et montage par le SDK avec la
  source factice.
- `WireCodecDriveTests` : compte du lecteur, requête avec options et secrets,
  comptes et fournisseurs à travers le worker.
- `SettingsWindowTests` : la page est dans le groupe Réseau.
- E2E `CloudDrivesUiTests` : fenêtre (bureau et mobile) et page Paramètres,
  axe sans défaut grave, captures dans `Tests/TestResults/cloud-drives/`.

## Liste des tâches

- [x] SDK : comptes cloud, types montables, montage d'un lecteur réseau (ADR 0270).
- [x] Capacité `cloud-accounts.manage`, passerelles OS et worker.
- [x] Fenêtre « Connecter un lecteur réseau » (bureau et mobile).
- [x] Page Paramètres « Comptes cloud ».
- [x] `mount -t dropbox|onedrive|s3`, `-o remount`, `umount`.
- [x] Textes français et anglais, axe, captures.
- [ ] Captures des étapes fournisseur et S3 avec les vrais connecteurs (chantier 7).
