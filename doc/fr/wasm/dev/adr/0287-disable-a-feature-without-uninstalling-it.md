> [English version](../../../../en/wasm/dev/adr/0287-disable-a-feature-without-uninstalling-it.md)

# ADR 0287 : désactiver une Feature sans la désinstaller

## Statut

Accepté le 2026-11-20.

- **Chantier :** `feature/23-disable-feature-without-uninstall` (série
  `feature/base`).
- **Ajuste :** l'[ADR 0285](0285-settings-features-section-adjustments.md) §4
  (« Activer et désactiver sont l'installation et la désinstallation de
  l'ADR 0048 ») et son option écartée « Un état “désactivée” distinct de la
  désinstallation ». Le reste de l'ADR 0285 reste vrai, ainsi que l'état
  « activée, désactivée » de l'[ADR 0249](0249-settings-features-section.md) §1,
  que cette ADR met en œuvre.
- **Réponses de Benoit :** 283 B (série 24, PR #617), puis 306 à 311 A
  (2026-11-20), consignées à la fin de cette ADR.

## Contexte

L'ADR 0285 avait fait de « désactiver une Feature » sa désinstallation
(`/etc/kazoos/apps.config`, ADR 0048) et posé la question 283. Benoit a répondu
283 B : désactiver une Feature ne la désinstalle pas. Elle reste installée, ses
points d'entrée (enfants, services, commandes, pages des Paramètres) sont
arrêtés et cachés, et l'état « désactivée » est gardé dans un document
système. Activer la remet en service. Les questions 306 à 311 ont fixé ce que
283 B ne disait pas.

Comme sous Windows (« Activer ou désactiver des fonctionnalités Windows ») ou
avec `systemctl disable` puis `mask`, la Feature garde ses fichiers, ses
données, ses rôles et ses permissions accordées ; seul son droit de tourner
change.

## Décision

### 1. Le document `/etc/kazoos/features.config` (306 A)

- Document `.config` de l'[ADR 0011](0011-settings-scope-layout.md),
  `system:system 0644`, lu sans schéma. Seules comptent les clés de la section
  `[disabled]` :

  ```ini
  # /etc/kazoos/features.config
  [disabled]
  ca.kazo.x86=true
  ```

- Seul l'id d'une Feature ([ADR 0260](0260-feature-app-type.md)) compte. Une
  ligne qui nomme un enfant, une app ordinaire ou une app système est ignorée
  avec un avertissement dans `/var/log/syslog` ; un id inconnu de cette version
  est ignoré en silence (le même fichier peut servir plusieurs versions), comme
  dans `apps.config`.
- L'état vaut pour toute la machine. Seul un administrateur désactive ou active :
  la fenêtre d'authentification de l'OS s'affiche, sauf sous `sudo`. Il n'y a
  pas de fichier par usager.
- L'OS crée le fichier à la première désactivation et le surveille : une
  modification à la main s'applique à chaud.

### 2. Ce que fait la désactivation (307 A, 308 A)

- La Feature, ses enfants et les apps qui en dépendent par une dépendance non
  optionnelle (BoxedWine pour `ca.kazo.x86`) sont désactivés, dans l'ordre
  « dépendants d'abord ». Leurs processus en cours (services, fenêtres, pages
  des Paramètres, commandes) sont arrêtés comme à une désinstallation : arrêt
  propre, puis forcé après le délai de grâce
  ([ADR 0097](0097-service-health-and-startup.md) §5).
- Le contrôle est celui de toutes les apps : `IAppEnablementRegistry`. Une app
  désactivée n'apparaît plus dans le lanceur, parmi les commandes du terminal,
  dans les pages de Paramètres ▸ Fonctionnalités, ni comme fournisseur d'un
  service de Feature ([ADR 0262](0262-feature-service-registry-and-async-resolution.md)).
- La confirmation (Paramètres et `kazopkg`) liste les apps dépendantes et les
  processus qui seront arrêtés ; c'est le plan d'une désinstallation
  (`PlanUninstallAsync`), qui calcule le même ensemble.
- Rien n'est supprimé : fichiers du paquet, données, `roles.config`,
  permissions accordées et réglages restent.
- Une app désinstallée et désactivée à la fois est montrée désinstallée : la
  désinstallation l'emporte.

### 3. Ce que fait l'activation (307 A)

- La ligne de la Feature quitte `[disabled]`. La Feature, ses enfants et ses
  dépendants redeviennent utilisables, dépendances d'abord.
- Les services automatiques de ces apps démarrent aussitôt pour l'usager en
  session, sans attendre une nouvelle connexion
  ([ADR 0286](0286-lazy-services-start-and-manual-kazowebd.md)). Après une
  modification à la main du fichier, ils démarrent à la prochaine connexion.

### 4. État et contrat du SDK

- `KazoAppInstallState.Disabled` (5) : la Feature, ses enfants et les apps
  qu'elle entraîne. La Feature reste « installée » pour le Store, `kazopkg` et
  `apps.config`.
- `IAppManagementGateway.DisableFeatureAsync(id)` rend un `KazoDisableReport`
  (apps désactivées, processus arrêtés) ; `EnableFeatureAsync(id)` rend les
  apps réactivées. Façades `KazoAppManagement.DisableFeatureAsync` et
  `EnableFeatureAsync`. Les deux exigent `apps.manage` et un administrateur.
  Un id qui n'est pas une Feature est refusé (`InvalidOperationException`).
- Ce sont des membres par défaut de l'interface, qui lèvent
  `NotSupportedException` : une passerelle plus ancienne reste compatible.

### 5. Paramètres ▸ Fonctionnalités (311 A)

- La carte d'une Feature montre son état (« Activée » ou « Désactivée ») et un
  bouton *Désactiver* ou *Activer*, à côté de *Détails* et *Ouvrir dans le
  Store*. *Désactiver* ouvre la confirmation du §2. La page rappelle, comme
  avant, comment la retirer (Store ou `sudo kazopkg remove --system <id>`).
- Une Feature déjà désinstallée par `apps.config` garde la ligne d'installation
  d'avant, avec *Réinstaller*.
- Dans Paramètres ▸ Applications, une app désactivée porte le badge
  « Désactivée », sans bouton : elle s'active depuis Fonctionnalités.

### 6. `kazopkg disable` et `kazopkg enable` (309 A)

- `kazopkg disable <id>` et `kazopkg enable <id>` : un administrateur, ou
  `sudo`. `disable` liste les apps dépendantes et les processus, puis demande
  une confirmation ; `--yes` répond pour l'usager.
- `kazopkg list` et `kazopkg info` marquent une Feature désactivée.

### 7. Mise à jour et retrait (310 A)

- Mettre à jour, revenir à la version précédente ou réparer une Feature
  désactivée ne touche pas `features.config` : elle reste désactivée.
- Retirer le paquet (`kazopkg remove --system`, Store) oublie sa ligne : une
  nouvelle installation arrive activée.

### 8. Journaux

- `/var/log/syslog` : `apps: <id> disabled (by=<usager>)` et
  `apps: <id> enabled (by=<usager>)`, plus une ligne par processus arrêté.
- `/var/log/auth.log` : l'authentification de l'administrateur, opérations
  `apps.feature.disable` et `apps.feature.enable`.

## Options écartées

- **Garder la désinstallation de l'ADR 0285** : contraire à la réponse 283 B.
- **Une section `[disabled]` dans `apps.config`** : ce fichier dit ce qui est
  installé pour qui, a une couche par usager, et d'autres parties de l'OS le
  lisent (dépendances, paquets). Un fichier à part garde les deux états
  distincts.
- **Laisser visibles les dépendants, qui refuseraient de démarrer** (308 B) : un
  nouvel état de lancement pour chaque app, et des entrées mortes dans le
  lanceur.

## Conséquences

- Plateforme : `FeaturesConfigDocument`, `AppInstallationService`
  (`DisableFeatureAsync`, `EnableFeatureAsync`, état `Disabled`, surveillance
  du fichier), `AppLifecycleOrchestrator.StartAutomaticServicesAsync`,
  passerelle de gestion, retrait d'un paquet.
- SDK : `KazoAppInstallState.Disabled`, `KazoDisableReport`,
  `IAppManagementGateway.DisableFeatureAsync`/`EnableFeatureAsync`, façades.
- Paramètres : carte de Feature, badge « Désactivée ». `kazopkg` : `disable`,
  `enable`, marque dans `list` et `info`.
- Docs : [`feature-settings-pages.md`](../../framework/feature-settings-pages.md),
  `features.md`, entrée `changes/unreleased`.

## Réponses de Benoit

283. **Activer et désactiver une Feature** (ADR 0285) : **B**, un état
     « désactivée » à part, qui garde la Feature installée et arrête ses
     enfants, dans un document système.
306. **Qui peut désactiver, et pour qui ?** **A** : un administrateur seulement,
     pour toute la machine (`/etc/kazoos/features.config`).
307. **Les processus d'une Feature qu'on désactive.** **A** : arrêtés comme à la
     désinstallation (ADR 0097 §5), liste montrée avant de confirmer ; activer
     relance les services automatiques sans attendre une connexion.
308. **Les apps qui en dépendent.** **A** : désactivées avec elle, listées avant
     de confirmer, réactivées avec elle.
309. **Une commande au terminal.** **A** : `sudo kazopkg disable|enable <id>`,
     état dans `list` et `info`.
310. **Une mise à jour d'une Feature désactivée.** **A** : elle s'installe, la
     Feature reste désactivée.
311. **Les boutons de la carte.** **A** : *Activer*/*Désactiver*, *Détails*,
     *Ouvrir dans le Store* ; le retrait reste au Store et à `kazopkg` ; une
     Feature désinstallée garde *Réinstaller*.

## Références

- [ADR 0048](0048-system-apps-and-uninstall.md) : désinstallation par
  `apps.config`.
- [ADR 0248](0248-store-and-kazopkg-dependency-resolution.md) : apps qui
  dépendent d'un paquet retiré.
- [ADR 0285](0285-settings-features-section-adjustments.md) : la section
  ajustée.
