> [English version](../../../../en/wasm/dev/adr/0278-real-network-relay-state-for-apps.md)

# ADR 0278 : une app réseau sait si le relais du serveur Kazo marche

## Statut

Acceptée le 2026-11-16 : Benoit a choisi Q1 = C (les deux). Série `todo-wins/base`, pour l'issue
[#418](https://github.com/Kazo-ca/Os/issues/418) (`TODO`).

- **Complète :** l'[ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  (relais TCP/UDP et sa politique), l'[ADR 0233](0233-server-issued-real-network-grants.md)
  (accords du vrai réseau émis par le serveur) et l'[ADR 0235](0235-real-network-for-chat-apps.md)
  (vrai réseau pour les apps de discussion, ports bloqués).
- **Ne change pas :** la capacité dangereuse `server.connection.manage` et
  `KazoServerConnection` restent réservées aux outils d'administration.

## Contexte

- Une app qui a `network.real.access` passe par le relais du serveur Kazo
  optionnel pour joindre un vrai hôte (`KazoTcpClient`, `KazoUdpClient`,
  `KazoHttp` vers un nom inconnu du réseau simulé).
- Elle ne peut pas savoir **avant d'essayer** si ce relais marche :
  `KazoServerConnection.GetStateAsync` demande `server.connection.manage`, qui
  permet aussi de connecter ou de déconnecter le serveur. C'est trop pour une
  app de discussion.
- Quand elle essaie, quatre causes différentes donnent la même erreur
  `KazoNetworkError.HostNotFound` (`SocketError.HostNotFound`) dans
  `AppNetworkGateway` :
  - aucun serveur connecté (ou plus de jeton d'accès) ;
  - relais arrêté par l'opérateur (`RELAY_DISABLED`) ;
  - accord de l'app pas encore reçu par le serveur (`GRANT_MISSING`, ADR 0233) ;
  - nom d'un domaine simulé refusé par le relais.

  Un port bloqué (`BLOCKED_PORT`) donne `ConnectionRefused`, comme un vrai port
  fermé.
- L'app IRC marque donc ses réseaux « Serveur Kazo requis » et ne peut dire la
  vraie raison d'un échec qu'en devinant.

## Question posée à Benoit

**Q1. Comment une app réseau sait-elle si le relais marche ?**

- A. Lire l'état (`GetRelayStateAsync`) et recevoir un événement quand il
  change.
- B. Seulement des erreurs distinctes : l'app essaie toujours, mais l'échec dit
  la vraie raison.
- **C. Les deux (recommandé).** L'app cache ou montre l'option « par le
  serveur Kazo » sans essayer, et un échec pendant la connexion dit quand même
  la vraie raison (l'état a pu changer entre la lecture et l'appel).

## Décision (C)

### 1. Lire l'état du relais

`Kazo.Os.AppSdk.Net.KazoRealNetwork`, nouvelle classe statique du SDK :

```csharp
public static ValueTask<KazoRelayState> GetRelayStateAsync(CancellationToken cancellationToken = default);
public static event EventHandler<KazoRelayState>? RelayStateChanged;

public sealed record KazoRelayState(KazoRelayStatus Status)
{
    public IReadOnlyList<int> TcpBlockedPorts { get; init; } = [];
    public IReadOnlyList<int> UdpBlockedPorts { get; init; } = [];
}

public enum KazoRelayStatus { NoServer, RelayDisabled, GrantPending, Ready }
```

- **Capacité :** `network.real.access` seule. Sans elle, l'appel lève
  `UnauthorizedAccessException`, comme les autres appels du vrai réseau.
- **Calcul**, dans `AppNetworkGateway`, avec ce que l'OS sait déjà :
  - `NoServer` : aucun serveur connecté, ou pas de jeton d'accès ;
  - `RelayDisabled` : la politique du relais (`KazoServerRelayPolicy.Enabled`)
    est fausse ;
  - `GrantPending` : l'app n'est pas dans `GrantedApps` de la politique ;
  - `Ready` : sinon. Les ports bloqués viennent de `TcpBlockedPorts` et
    `UdpBlockedPorts`.
- La politique est gardée 30 secondes et relue quand l'état de la connexion
  au serveur change. L'app ne voit **rien d'autre** de la politique (pas les
  autres apps autorisées, pas les quotas, pas l'adresse du serveur).
- **Événement :** `RelayStateChanged` part quand le statut change (connexion
  ou déconnexion du serveur, politique relue, accord synchronisé), pas à
  chaque lecture.
- Une app en Web Worker passe par sa passerelle (`RemoteNetworkGateway`) comme
  pour les autres appels réseau.

### 2. Dire la vraie raison d'un échec

- `KazoNetworkError` gagne trois valeurs : `RelayNoServer`, `RelayDisabled`,
  `RelayGrantPending`. Un port bloqué par l'opérateur donne
  `BlockedByAdministrator`, qui existe déjà (ADR 0210 §6).
- Pour ne casser aucune app, `KazoTcpClient` et `KazoUdpClient` lèvent
  toujours une `SocketException` avec le même `SocketError` qu'avant
  (`HostNotFound`, ou `ConnectionRefused` pour un port bloqué). C'est
  maintenant une sous-classe `KazoRelayException : SocketException`, dont
  `Reason` donne la nouvelle valeur. Le code qui attrape `SocketException`
  continue de marcher.
- `KazoHttp` garde son résultat et met la nouvelle valeur dans son erreur.
- Le domaine simulé refusé par le relais reste `HostNotFound` : le nom
  n'existe vraiment pas sur Internet.

### 3. Ce qui ne change pas

- Aucune nouvelle capacité ; `server.connection.manage` garde tout ce qu'elle
  permet.
- Le relais lui-même et ses codes (`ProxyErrorCode`) ne changent pas.

## Conséquences

- L'app IRC peut cacher l'option « par le serveur Kazo » sans serveur, la
  montrer quand il arrive, et dire « le serveur n'a pas encore reçu
  l'autorisation de Kazo IRC » au lieu de « hôte introuvable ».
- Ajout à l'API publique du SDK (`PublicAPI.Unshipped.txt`). Une app qui
  l'utilise demande la version de l'OS qui l'apporte (`sdkCompatibility`).
- Coût : une requête de politique au plus toutes les 30 secondes par appareil,
  et seulement quand une app lit l'état.

## Chantiers

1. `todo-wins/10-relay-state` : `KazoRealNetwork`, l'état dans
   `AppNetworkGateway` et `RemoteNetworkGateway`, l'événement, les erreurs
   distinctes. Tests : une app avec seulement `network.real.access` lit l'état
   sans serveur, avec un serveur sans accord, avec accord, relais arrêté ; un
   échec de connexion donne la bonne `Reason`. Docs `framework/sdk-network.md`
   et `dev/server-network-relay.md`.
2. Dans Kazo-ca/Os-App-IRC, après une version de l'OS qui contient l'API :
   option « par le serveur Kazo » selon l'état, messages d'échec précis.
