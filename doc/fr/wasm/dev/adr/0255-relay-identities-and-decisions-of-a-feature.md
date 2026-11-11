> [English version](../../../../en/wasm/dev/adr/0255-relay-identities-and-decisions-of-a-feature.md)

# ADR 0255 : relais du serveur : identités par flux, décisions, limites et journal fournis par une Feature

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. La recommandation de chaque question posée à Benoit est
appliquée en attendant sa réponse. Décisions d'origine : D20 (une bouteille,
« préfixe », par programme, avec son espace réseau et son adresse ; une
décision réseau par préfixe), 161 A (réseau Ethernet privé d'Os-x86 entre
VM, une adresse par VM et par préfixe sans NAT, sortie réelle par le relais ;
aucune liaison avec le réseau simulé de Kazo en V1), 174 A (« la décision,
les limites (4 flux, 300 s, 80/443) et le journal vivent au relais côté
Kazo, source de vérité ; nftables dans l'invité en défense en profondeur ;
`relay_url` fixé par la Feature, jamais exposé à l'invité ; UDP 53 et IPv6
bloqués par règle explicite ») et 181 A (Os-x86 déclare `network.real.access`,
BoxedWine aucune permission réseau), 2026-11-11 ; essai E6 d'Os-x86
(l'adresse source par préfixe est une étiquette fiable jusqu'au relais ; deux
décisions appliquées et journalisées).

- **Complète :** l'[ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  et l'[ADR 0108](0108-server-relay-implementation-adjustments.md) (relais
  TCP et UDP, politique, quotas), l'[ADR 0233](0233-server-issued-real-network-grants.md)
  (accords émis par le serveur, `POST /api/proxy/grants`) et
  l'[ADR 0235](0235-real-network-for-chat-apps.md) (ports déclarés,
  `BlockedPorts`).
- **Ne change pas :** la capacité `network.real.access` et son approbation,
  les adresses toujours bloquées, l'épinglage DNS, la vérification de
  l'accord par le serveur, `AllowedPorts` et `BlockedPorts`.
- **Pour :** l'adaptateur réseau d'Os-x86 (côté page), qui relaie chaque
  flux TCP d'un préfixe vers le relais du serveur avec l'identité du
  préfixe, et la fenêtre de permission réseau d'Os-x86.

## Contexte

- Un flux du relais est identifié par **compte + appareil** (du jeton) et
  **`AppId`** (de la requête : `ProxyStreamRequest(Host, Port, AppId)`,
  `Server/Kazo.Os.Server.Contracts/Proxy/ProxyStreamContracts.cs:90` ;
  envoyé par `Platform/Kazo.Os.Platform.Core/ServerConnection/IProxyClient.cs:239-275`
  comme `ws(s)://…/api/proxy/tcp?host&port&appId[&tls&targetHost]`). Aucun
  identifiant d'usager ni de sous-identité ne voyage ; l'accord est vérifié
  **par compte et par app** (`Services/ProxyGrantAuthority.cs:71-77, 95-117`),
  tous usagers de l'OS confondus (ADR 0028 §1 : connexion par appareil).
- Les limites sont **par appareil** : `MaxStreamsPerDevice` 4
  (`Services/ProxyOptions.cs:108, :234`, appliqué `ProxyService.cs:283-289`),
  `StreamIdleTimeoutSeconds` 300 (:114, :339), `MaxResponseBytes` 10 Mio
  (:99), ports TCP `[80, 443]` (:163) plus ceux déclarés par l'app
  (ADR 0235 §3, `[real-network-ports]` d'`app-permissions.config`,
  `Policy/AppPermissionDecisions.cs:36-41`), `BlockedPorts` (:173, :180).
- Le journal du relais (`ProxyService.cs:328-329 PROXY_TCP_OPEN` : hôte,
  adresse, port, appId, tls ; `:377-378 CLOSE` : hôte, port, code, octets,
  **sans appId**) ne connaît ni l'usager ni le programme : pour Os-x86,
  toutes les bouteilles de tous les usagers seraient « org.kazoos.x86 ».
- Os-x86 donne à chaque préfixe (bouteille) une adresse sur son réseau
  Ethernet privé (E6 : l'adresse source est une étiquette fiable jusqu'au
  relais) et prend une **décision par préfixe** (fenêtre de permission :
  autoriser ou refuser, une fois, jusqu'à la fin du programme ou toujours,
  toute destination ou cet hôte et ce port, règles imposées par l'admin,
  D23). Benoit a choisi (174 A) que la décision, les limites et le journal
  vivent **au relais côté Kazo**, pour qu'un invité compromis ne puisse pas
  les contourner, et nftables dans l'invité seulement en défense.
- `KazoClientWebSocket` (ADR 0235 §1) et le WebSocket direct ne sont pas
  concernés : une VM n'ouvre pas de WebSocket, elle émet du TCP.

## Décision

### 1. Une identité par flux, opaque, fournie par la Feature

- `ProxyStreamRequest` gagne un champ **`Identity`** (chaîne, 128 caractères
  au plus, `[A-Za-z0-9._:-]`), que seule une **Feature** avec
  `network.real.access` peut poser (`AppNetworkGateway` le refuse à une app
  ordinaire : `UnauthorizedAccessException`, « identité de flux réservée aux
  fonctionnalités »). Os-x86 y met `prefix:<guid>` (ou `vm:<guid>` pour une
  VM complète = une identité, D22). Le serveur ne l'interprète pas : c'est
  une **étiquette** sous l'app.
- Côté SDK : `KazoTcpConnectOptions.FlowIdentity` (ADR 0235 §2 pour
  `KazoTcpConnectOptions`) ; `RelayedSocket` le transmet ; la requête de flux
  l'envoie comme `identity=`.
- La **source de vérité de l'étiquette** est la Feature côté page (elle
  lit l'adresse source du paquet Ethernet du préfixe, E6) ; l'invité ne voit
  jamais `relay_url` ni le jeton (174 A).

### 2. Décisions et limites par identité, enregistrées au relais

- `POST /api/proxy/grants` (ADR 0233 §1) accepte d'une **Feature** un
  enregistrement **par identité** : `ProxyGrantIssueRequest` gagne
  `Identity`, `Decision` (`Allow`, `Deny`), `Scope` (`Any`, ou une liste
  `host:port`), `Until` (`Session`, `ProgramEnd`, `Always`, avec une date
  limite pour les deux premiers), et `Limits` (`MaxStreams`,
  `IdleTimeoutSeconds`, `AllowedPorts`, chacun **au plus** égal à ce que la
  politique de l'appareil permet ; le serveur plafonne). L'enregistrement
  est écrit sous l'accord de l'app (`RecordIdOf(accountId, appId, userId)`,
  `ProxyGrantIssuer.cs:64`, étendu de `identity`) ; révision par identité ;
  audit `PROXY_IDENTITY_GRANT` / `_REVOKE` ; synchronisé comme les autres
  accords (lecture seule pour l'OS, ADR 0031).
- Une Feature ne peut écrire que sous **son** `AppId` (vérifié par le
  jeton et la capacité comme ADR 0233) : Os-x86 ne décide que pour ses
  préfixes.
- **Application** (`ProxyService`) : un flux avec `identity=` est accepté si
  l'accord de l'app existe **et** qu'un enregistrement d'identité `Allow`
  couvre `host:port` et n'est pas expiré ; sinon refusé avec le nouveau code
  de fermeture `4003 IDENTITY_DENIED` (ou `4004 IDENTITY_UNDECIDED` quand
  aucun enregistrement n'existe : c'est le signal pour la Feature d'ouvrir
  sa fenêtre de permission, puis de réessayer). Sans `identity=`, rien ne
  change pour les apps ordinaires.
- **Limites par identité** : `MaxStreams` et `IdleTimeout` de l'identité
  s'appliquent **en plus** des limites par appareil (une bouteille ne peut
  pas prendre les 4 flux de l'appareil à elle seule si sa limite est 2) ;
  `AllowedPorts` de l'identité est une intersection avec ceux de l'app et
  moins `BlockedPorts`. La politique de l'appareil reste le plafond
  (question 1 pour sa valeur par défaut avec des identités).
- **Règles explicites** (174 A) : le relais refuse UDP 53 et toute cible
  IPv6 pour un flux avec identité (`4003`), sans configuration ; la Feature
  fournit `/etc/hosts` à l'invité.

### 3. Journal par identité

- `PROXY_TCP_OPEN`, `PROXY_TLS_FAILED` et **`PROXY_TCP_CLOSE`** (qui gagne
  `appId`) portent `identity` ; un refus écrit `PROXY_IDENTITY_DENIED`
  (identité, hôte, port, raison). L'audit du serveur est la source de
  vérité (174 A) ; l'OS relit les entrées de ses identités par
  `GET /api/proxy/audit?appId=&identity=` (nouvelle route, accord de l'app
  requis, 7 jours, 1000 entrées) pour la page des règles d'Os-x86 et le
  journal de l'usager (ADR 0254, sujet = le préfixe).
- Le journal ne contient **pas** d'usager de l'OS (le serveur ne le connaît
  pas, ADR 0028) ; la Feature fait le lien identité → usager de son côté
  (le préfixe est rangé par GUID d'usager).

### 4. Ce qui ne change pas

- Sans serveur connecté, aucun vrai réseau pour une VM (comme pour toute
  app : le réseau simulé de Kazo n'est pas touché, 161 A).
- Les apps ordinaires (IRC, navigateur) ne posent pas d'identité et ne
  voient aucune différence ; leurs quotas restent ceux de l'appareil.
- La fenêtre de permission reste **celle d'Os-x86** (D23, D49) ; le relais
  n'affiche rien.

## Options écartées

- **A. Décision et journal dans l'adaptateur côté page seulement** (174 B) :
  moins d'ADR, mais le journal dépend du code de la page et un flux mal
  étiqueté passerait ; le serveur ne saurait pas refuser.
- **B. Décision dans l'invité seulement (nftables)** (174 C) : un invité
  compromis contourne.
- **C. Un accord d'app par préfixe** (chaque bouteille = un `AppId`
  distinct, `org.kazoos.x86.prefix.<guid>`) : des centaines d'apps fictives
  dans les accords, dans Paramètres ▸ Permissions et dans `GrantedApps` ;
  et une approbation d'administrateur par bouteille.
- **D. Un usager dans l'identité** : le serveur ne connaît pas les usagers
  de l'OS (ADR 0028 §1) ; l'ajouter changerait le modèle de session du
  serveur ; la Feature fait le lien.
- **E. Laisser la Feature contourner les limites de l'appareil** : non,
  « Autoriser » ne lève pas les limites du relais (conception d'Os-x86,
  D14) ; les identités partagent le plafond.

## Conséquences

- Contrats du serveur (`Kazo.Os.Server.Contracts/Proxy`) :
  `ProxyStreamRequest.Identity`, `ProxyGrantIssueRequest.Identity/Decision/Scope/Until/Limits`,
  codes `4003`, `4004`, route d'audit ; serveur : `ProxyGrantIssuer`,
  `ProxyGrantAuthority` (par identité), `ProxyService` (vérification,
  limites, règles UDP 53 et IPv6, journal), `ProxyOptions`
  (`MaxStreamsPerIdentityDefault`, question 1).
- OS : `KazoTcpConnectOptions.FlowIdentity` (réservé aux Features),
  `IProxyClient.OpenStreamAsync` (`identity=`), `RealNetworkGrantReconciler`
  (enregistrements d'identité émis à l'approbation et à chaque synchro),
  `KazoRelayAudit.ReadAsync` (SDK, Features) ; `PublicAPI.Unshipped.txt`,
  fixture `sdk-compat`.
- Docs : [`server-network-relay.md`](../server-network-relay.md) (section
  « Identités de flux »), [`sdk-network.md`](../../framework/sdk-network.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  entrée `changes/unreleased`.
- Tests : flux avec identité sans enregistrement → `4004` ; `Allow` sur
  `host:port` → accepté, autre hôte → `4003` ; `Deny` → `4003` ; expiration
  `ProgramEnd` ; limite par identité atteinte avant celle de l'appareil ;
  UDP 53 et IPv6 refusés ; app ordinaire qui pose une identité → exception ;
  Feature qui écrit sous un autre `AppId` → refus ; journal et route d'audit.

## Questions posées à Benoit

1. **Les limites par appareil (4 flux, 300 s) avec des identités ?**
   - A) **(Recommandé)** Le plafond de l'appareil reste 4 flux par défaut ;
     chaque identité a par défaut 2 flux et 300 s ; l'administrateur du
     serveur règle les deux (`MaxStreamsPerDevice`, `MaxStreamsPerIdentityDefault`).
     Deux bouteilles actives se partagent donc les 4 flux.
   - B) Le plafond de l'appareil monte à 8 quand des identités sont utilisées
     (un flux par identité au moins pour quatre bouteilles).
   - C) Pas de limite par identité ; seule celle de l'appareil (une bouteille
     peut tout prendre).
2. **Où vit la durée `Session` d'une décision ?**
   - A) **(Recommandé)** Au relais, avec une date limite envoyée par la
     Feature (fin de session = déconnexion, la Feature révoque alors, et la
     date limite de 24 h protège si elle n'a pas pu).
   - B) Côté page seulement : la Feature n'envoie au relais que `Always` et
     garde `Session` et `ProgramEnd` en mémoire (contredit « source de vérité
     au relais » pour ces deux durées).

Appliquées en attendant : 1 A, 2 A.
