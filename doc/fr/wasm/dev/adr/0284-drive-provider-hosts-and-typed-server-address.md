> [English version](../../../../en/wasm/dev/adr/0284-drive-provider-hosts-and-typed-server-address.md)

# ADR 0284 : hôtes d'un fournisseur de lecteur, permis par `drives.provide`, et adresse de serveur tapée par l'utilisateur

## Statut

**Acceptée** le 2026-11-17 : Benoit a répondu « 1. A 2. A » aux deux
questions du chantier 7 (consignées à la fin). Série `drive-providers/base`,
chantier `drive-providers/7-developer-kit`.

- **Complète :** l'[ADR 0282](0282-third-party-drive-providers.md) §4 (HTTP
  direct, hôtes déclarés). Elle ne change pas sa décision : elle dit comment la
  phrase « pour un fournisseur de lecteur, la liste est celle de sa
  déclaration » s'applique, et ajoute le cas d'un service dont l'adresse n'est
  connue qu'à la connexion.
- **S'appuie sur :** l'[ADR 0049](0049-dangerous-capabilities-and-admin-approval.md)
  (approbation des capacités dangereuses) et l'[ADR 0233](0233-server-issued-real-network-grants.md) §4
  (une mise à jour qui élargit la déclaration redemande l'approbation).

## Contexte

Le chantier 1 a livré `KazoDirectHttpClient` : une requête directe exige
`network.direct.access` et un hôte de `directNetworkHosts`. Deux trous sont
apparus en écrivant le fournisseur d'exemple WebDAV (chantier 7) :

1. Les `hosts` de `driveProviders` (ADR 0282 §1 et §4) étaient validés par le
   manifeste mais **jamais lus** par la passerelle réseau. Une Feature qui
   fournit un lecteur devait donc aussi déclarer `network.direct.access` et
   recopier ses hôtes dans `directNetworkHosts`, avec deux lignes d'approbation
   pour la même chose.
2. Un serveur WebDAV (Nextcloud, ownCloud, un NAS) n'a **pas d'hôte connu
   d'avance** : chaque utilisateur tape le sien. Aucune déclaration ne peut le
   nommer, et `*` seul est refusé (ADR 0282 §4).

## Décision

### 1. `drives.provide` suffit pour les hôtes déclarés

- Une Feature qui détient `drives.provide` peut envoyer une requête directe à
  tout hôte de la liste `hosts` d'une de ses déclarations `driveProviders`,
  **sans** `network.direct.access`. Les deux capacités restent possibles
  ensemble : `directNetworkHosts` sert alors au reste du code de la Feature.
- L'écran d'approbation de `drives.provide` (fenêtre du bureau sécurisé et
  mode texte du terminal) nomme ces hôtes, comme celui de
  `network.direct.access` nomme les siens.
- La liste approuvée par la machine (section `[direct-network-hosts]`) garde
  les hôtes des deux capacités. Une mise à jour qui ajoute un hôte à un
  fournisseur remet `drives.provide` en attente, comme pour
  `network.direct.access` (ADR 0233 §4).
- La passerelle décide dans cet ordre : hôte de `directNetworkHosts` →
  `network.direct.access` exigée ; sinon hôte des fournisseurs ou serveur tapé
  (§2) → `drives.provide` exigée ; sinon refus.

### 2. Un champ « adresse du serveur » dans le formulaire

- Un champ de formulaire peut porter `serverAddress: true`
  (`AppDriveFormFieldManifest.ServerAddress`). Règles du validateur : type
  `url` seulement, jamais secret, obligatoire, **un seul** par fournisseur, et
  seulement dans une connexion `form`.
- Quand un lecteur de ce type s'ouvre, l'OS lit la valeur de ce champ, en
  garde l'hôte s'il est valide (adresse `https` absolue, sans nom ni mot de
  passe dans l'URL, hôte accepté par les règles de `directNetworkHosts` : pas
  d'adresse IP, pas de `localhost`, un nom avec un point), et permet cet hôte
  **à la Feature qui fournit ce type**, et à elle seule, **pendant que le
  lecteur est ouvert** (du montage au démontage). C'est ce que « tant que le
  lecteur existe » veut dire : un lecteur démonté n'envoie rien.
- La permission est comptée par lecteur : deux lecteurs sur le même serveur
  le gardent permis jusqu'à la fermeture du second.
- Elle passe aussi par `drives.provide` : sans la capacité (refusée par
  l'utilisateur, bloquée par la politique), le serveur tapé est refusé.
- Le dialogue de connexion écrit l'hôte en clair avant d'enregistrer
  (« Ce fournisseur joindra dav.exemple.fr »), et l'écran d'approbation de
  `drives.provide` dit que le fournisseur joindra le serveur que chaque
  utilisateur tape dans ce champ. Le dialogue est le travail du chantier 6.

## Sécurité

- L'hôte tapé est choisi par l'utilisateur, pas par l'auteur du fournisseur :
  le fournisseur ne peut pas s'ouvrir un hôte de son choix par ce chemin, car
  la valeur vient du formulaire que l'OS dessine et enregistre.
- Le même fournisseur reste borné aux hôtes de sa déclaration pour tout le
  reste. Les autres apps, et les autres Features, ne gagnent rien.
- Les limites de l'ADR 0282 restent vraies : pas de bac à sable, le
  JavaScript d'un paquet peut appeler `fetch`. La revue du Store reste la
  barrière réelle ; le guide du développeur le dit.
- Le serveur doit accepter CORS depuis la page de l'OS. Nextcloud et la
  plupart des serveurs WebDAV ne le font pas par défaut : le guide explique
  comment le vérifier et quoi régler.

## Options écartées

- **Q1 B. Déclarer les hôtes d'avance seulement** : rend WebDAV impossible
  sans un joker trop large.
- **Q1 C. Un joker `*` réservé aux fournisseurs** : ouvre tout Internet au
  code du fournisseur pour la durée de sa vie.
- **Q2 B. Garder `network.direct.access` obligatoire** : deux approbations et
  deux listes pour une même chose, et des `hosts` de déclaration qui ne
  servent à rien.

## Conséquences

- `Kazo.Os.App.Abstractions` : `AppDriveFormFieldManifest.ServerAddress`,
  `AppDriveProviders.HostsOf`, `AppDriveProviders.ServerAddressField`, schéma
  `manifest.schema.v1.json` et fixture.
- `Kazo.Os.Platform.Core` : `DriveServerHosts` (registre compté des serveurs
  tapés), passerelle réseau, évaluateur et écrans d'approbation,
  `FeatureDriveFolder` qui prend et rend le bail.
- Exemple WebDAV : son champ « Adresse du serveur » porte `ServerAddress`.
- Docs : `framework/drive-providers.md`, `dev/dangerous-capability-approval.md`,
  `dev/network-drive-sources.md`.

## Questions posées à Benoit

1. **Comment un fournisseur joint-il un serveur que l'utilisateur tape (WebDAV) ?**
   - A) **(Recommandé, choisi)** La déclaration marque un champ du formulaire
     comme « adresse du serveur ». L'hôte que l'utilisateur y tape est permis
     à ce fournisseur tant que le lecteur existe, et le dialogue de connexion
     l'écrit en clair.
   - B) Seulement des hôtes déclarés d'avance : l'exemple vise un service
     précis, et WebDAV reste impossible.
   - C) Un joker `*` permis aux fournisseurs : simple, mais tout Internet est
     ouvert au code du fournisseur.
2. **`drives.provide` suffit-elle pour les hôtes de la déclaration ?**
   - A) **(Recommandé, choisi)** Oui : les hôtes de ses `driveProviders` sont
     permis à la Feature, et l'écran d'approbation de `drives.provide` les
     nomme.
   - B) Non : la Feature déclare aussi `network.direct.access` et recopie ses
     hôtes dans `directNetworkHosts`.
