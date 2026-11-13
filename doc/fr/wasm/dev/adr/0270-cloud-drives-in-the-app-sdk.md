> [English version](../../../../en/wasm/dev/adr/0270-cloud-drives-in-the-app-sdk.md)

# ADR 0270 : les comptes cloud et les lecteurs cloud passent par le SDK public, derrière `cloud-accounts.manage`

## Statut

Acceptée le 2026-11-13. Benoit a choisi l'option A dans le fil du chantier
`cloud-drives/5-ui`. Livrée par ce chantier
([`cloud-drives-ui.md`](../cloud-drives-ui.md)).

- **Remplace :** la phrase de l'[ADR 0267](0267-cloud-drives-as-network-drives.md),
  section « Conséquences », qui disait que le SDK des apps ne change pas, sauf
  le format et la taille que `KazoDriveInfo` rapporte. Le reste de l'ADR 0267
  ne change pas.
- **S'appuie sur :** l'ADR 0055 (les apps système n'utilisent que le SDK
  public), l'ADR 0049 (capacités dangereuses), l'ADR 0181 (lecteurs réseau),
  l'[ADR 0269](0269-cloud-tokens-stay-on-the-device.md) (jetons sur l'appareil).

## Contexte

L'ADR 0267 §11 met l'interface dans l'Explorateur (« Connecter un lecteur
réseau… »), dans les Paramètres (page des comptes cloud) et dans le terminal
(`mount -t dropbox|onedrive|s3`). Ces trois endroits sont des apps : elles
n'atteignent l'OS que par le SDK public. Ce SDK savait seulement monter un
dossier de l'ordinateur (`KazoDriveInfo.MountHostFolderAsync`).

Trois options ont été posées à Benoit :

- **A** (choisie) : étendre le SDK public, derrière une capacité dangereuse.
- B : un dialogue dessiné par l'OS et une seule méthode SDK ; la page des
  Paramètres aurait quand même eu besoin d'une autre porte.
- C : un contrat interne injecté dans les apps système ; contraire à
  l'ADR 0055 et impossible dans un worker.

## Décision

- **`KazoDriveInfo`** gagne `GetNetworkDriveTypesAsync()` (les types que l'OS
  peut monter ici : `hostfs` si le navigateur a le dialogue de dossiers, les
  fournisseurs configurés, `s3`) et `MountNetworkDriveAsync(KazoNetworkDriveRequest, point)`.
  `KazoDriveInfo.Account` donne le compte d'un lecteur cloud.
- **`KazoNetworkDriveRequest`** : type, compte, racine, `Options` (réglages non
  secrets, par exemple l'adresse et le compartiment S3) et `Secrets` (clés S3).
  Les secrets ne passent qu'une fois, de l'app vers l'OS, qui les scelle
  (ADR 0269) ; `ToString` ne les écrit jamais.
- **`KazoCloudAccounts`** (espace `Kazo.Os.AppSdk.CloudAccounts`) :
  `CanConnectAsync`, `PrepareAsync`, `GetProvidersAsync`, `GetAccountsAsync`,
  `ConnectAsync(fournisseur, étendue)`, `ReconnectAsync`, `RemoveAsync`. Une
  app ne reçoit que des références : jamais un jeton ni une clé.
- **Capacité dangereuse `cloud-accounts.manage`** : exigée par
  `KazoCloudAccounts` (sauf `CanConnectAsync`) et par `MountNetworkDriveAsync`
  pour tout type autre que `hostfs`. Un dossier de l'ordinateur reste sans
  capacité, comme avant. L'Explorateur, les Paramètres et `mount` la déclarent.
- Les nouvelles méthodes des interfaces de passerelle ont un corps par défaut
  et `IAppExecutionContext.CloudAccounts` répond « non pris en charge » par
  défaut : les apps et les hôtes existants compilent sans changement.
- Les deux passerelles marchent aussi dans un worker (`mount` y tourne) ; la
  fenêtre de connexion s'ouvre sur la page, dans le geste de l'utilisateur.

## Conséquences

- API publique de plus dans `Kazo.Os.AppSdk` et `Kazo.Os.App.Abstractions`
  (`PublicAPI.Unshipped.txt`), sans rupture pour le fixture de compatibilité.
- `NetworkDriveRequest` (Simulation.Abstractions) gagne `Options` et
  `Secrets`, partagés avec la source S3 du chantier 6.
- Une app tierce peut demander `cloud-accounts.manage` ; comme toute capacité
  dangereuse, l'utilisateur doit l'approuver.
