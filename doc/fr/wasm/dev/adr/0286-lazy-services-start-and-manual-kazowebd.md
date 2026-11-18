> [English version](../../../../en/wasm/dev/adr/0286-lazy-services-start-and-manual-kazowebd.md)

# ADR 0286 : les services automatiques démarrent avant le chargement de leur code, et `kazowebd` devient manuel

## Statut

**Acceptée** le 2026-11-18 : Benoit a répondu « 295. C) avec une nuance » à la
question 295 du correctif #625 (consignée à la fin). Version corrective 3.5.2,
branche `fix/625-feature-service-autostart`.

- **Ajuste :** l'[ADR 0077](0077-hosted-static-sites.md) : `kazowebd` n'est plus
  démarré de lui-même ; il devient un service manuel.
- **Complète :** l'[ADR 0097](0097-service-health-and-startup.md) §4 (quand les
  services démarrent) et l'[ADR 0262](0262-feature-service-registry-and-async-resolution.md)
  (services des Features).
- **S'appuie sur :** l'[ADR 0047](0047-app-definition-and-generated-manifests.md)
  (le code d'une app est chargé à la demande, une DLL par app) et le correctif
  #621 (un enfant est découvert avec sa Feature).

## Contexte

Sur Kazo OS 3.5.1, l'enfant `Service` automatique d'une Feature restait
`inactive (dead)` après la connexion (#625). Une app qui l'appelait avant le
lancement d'un autre enfant de la Feature recevait une erreur.

La cause dépasse les Features. À la connexion (et au boot pour un service
système), l'orchestrateur choisissait les services à démarrer parmi les
**descripteurs déjà découverts**. Dans l'hôte navigateur, le code de chaque app
n'est découvert qu'à son premier lancement. Aucun service automatique ne
démarrait donc avant que quelque chose ne lance son app, `kazowebd` compris,
alors que `LaunchAsync` sait charger ce code.

## Décision

1. **Les services à démarrer sont choisis dans le catalogue.** Un service
   activé, automatique pour ce moment (connexion, ou boot pour une app système),
   est candidat même si son code n'est pas chargé. Son mode de démarrage
   (`service.conf`, `systemctl enable` et `disable`) est lu **avant** tout
   chargement : un service manuel ou désactivé ne charge rien. Le lancement
   charge ensuite le code du service, avec sa Feature (#621). Un hôte sans
   chargeur à la demande ne démarre que les services qu'il a découverts, comme
   avant.
2. **`kazowebd` devient un service manuel** (`AutoStart = false`). Il ne
   démarrait déjà pas de lui-même dans le navigateur ; le rendre automatique
   aurait chargé le serveur web à chaque connexion. Un administrateur le démarre
   (`systemctl start kazowebd`, Web Server Manager) ou le rend automatique
   (`systemctl enable kazowebd`).
3. **Une Feature règle les services qu'elle fournit.** Un enfant peut démarrer,
   arrêter et régler le type de démarrage d'un service de sa propre Feature sans
   `services.manage`. La règle vient du catalogue (le parent des deux manifestes)
   et non des assemblages chargés : le code du service frère n'est souvent pas
   encore chargé. La règle « même assemblage » reste valable pour les autres
   apps.
4. **Hors de cette version**, notés en issues `TODO` :
   - onglet Services dans System Monitor (#626) ;
   - une app ordinaire qui fournit son propre service et le contrôle (#627) ;
   - applications lancées à l'ouverture de session, comme les applications de
     démarrage de Windows (#628).

## Conséquences

- Dans le navigateur, un service automatique (d'une Feature, d'une app système
  ou d'une app installée) démarre maintenant à la connexion, et seul le code des
  services qui démarrent est chargé.
- `systemctl status kazowebd` répond `inactive (dead)` après la connexion tant
  qu'un administrateur ne l'a pas démarré ou rendu automatique. Les tests qui
  utilisent `kazowebd` le démarraient déjà par `systemctl start`.
- Tests : `AppLifecycleOrchestratorTests.LazyServices` (le service d'une Feature
  non chargé démarre ; un service manuel ne charge rien) et
  `AppIntentDispatcherTests.A_feature_child_controls_a_service_of_its_feature_whose_code_is_not_loaded_yet`.

## Question posée

**295. Portée du correctif 3.5.2.**

- A) **(Recommandé)** Tout service automatique activé du catalogue, Feature ou
  non ; `kazowebd` démarre à chaque connexion.
- B) Seulement les enfants `Service` d'une Feature.
- C) **(Choisi)** A, et `kazowebd` passe en démarrage manuel.

Réponse de Benoit : « 295. C) avec une nuance : Chaque service déclare leur type
de démarrage à l'installation et l'utilisateur peut les arrêter et changer le
type de démarrage. Une feature peut Controller le type de démarrage des services
qu'il fournis. On doit donc avoir une tab dans `System Monitor` avec les
services. Dans le meilleur des mondes, une application devrais aussi pouvoir
fournir son service et pouvoir le controler. Dans windows, on a la possibilité
de démarrer une application à l'ouverture de windows, on devrais avoir la même
expérience utilisateur. (on peut créer une TODO pour ce que on a pas de besoins
pour déployer le dossier en cours) »
