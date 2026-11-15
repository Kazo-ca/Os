> [English version](../../../../en/wasm/dev/adr/0274-single-copy-of-app-files.md)

# ADR 0274 : une seule copie des fichiers d'une app installée

## Statut

Proposée le 2026-11-15, série `storage/base`
([plan](../plans/browser-storage-series-plan.md)). Benoit a choisi le
2026-11-14 l'option A de l'analyse du stockage (§7, décision 2) : le système
de fichiers reste la seule source, le service worker sert `_content/…` depuis
le magasin de fichiers et la copie dans Cache Storage disparaît. Cette ADR dit
comment. Aucune question ouverte.

- **Remplace :** dans l'[ADR 0206](0206-serve-installed-app-assets.md), la
  copie des fichiers dans le cache `kazoos-installed-apps` ; dans
  l'[ADR 0225](0225-serve-installed-app-files-to-every-page.md) §2, le ménage
  de ce cache. Les adresses `_content/<assemblage>/<chemin>`, la prise de
  contrôle de la page par le worker (ADR 0225 §1, 0237, 0241) et la page d'une
  app dans une iframe (ADR 0243) ne changent pas.
- **S'appuie sur :** l'[ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §5 (SHA-256
  de chaque fichier dans le manifeste), l'[ADR 0221](0221-pack-all-static-web-assets-of-an-app.md)
  (toutes les ressources statiques dans le paquet), l'[ADR 0272](0272-binary-file-content.md)
  (forme des blocs) et l'[ADR 0273](0273-short-orphan-content-retention.md)
  (références d'un contenu).
- **Analyse :** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  constat C8, piste 6.

## Contexte

- Une app installée a ses fichiers dans
  `/usr/lib/kazoos/apps/<appId>/<version>/` (ou sous
  `~/.local/share/kazoos/apps/`). L'installeur garde la version courante et la
  précédente.
- Au premier lancement de chaque session, `InstalledAppAssetPublisher` relit
  chaque fichier `wwwroot` déclaré dans le manifeste, vérifie son SHA-256, et
  le **recopie** dans le cache `kazoos-installed-apps` de Cache Storage. Le
  service worker (`installedAppAssets.sw.js`) répond aux adresses
  `_content/<assemblage>/<chemin>` depuis ce cache.
- Une app de 64 Mio occupe donc environ 236 Mio : deux versions en base64 dans
  le système de fichiers, plus la copie du cache. La copie est refaite à chaque
  session (lecture, vérification et écriture de tous les octets).
- Le contenu d'un fichier est adressé par le SHA-256 du fichier entier
  (`contentHash`) : c'est la même valeur que le SHA-256 du manifeste.

## Décision

### 1. Le système de fichiers est la seule source

Les octets d'un fichier d'app ne vivent que dans `fsContent`. Le cache
`kazoos-installed-apps` ne reçoit plus aucun fichier ; il est supprimé au
premier démarrage de la version qui apporte cette ADR.

### 2. Une table des adresses servies

Un nouveau magasin `servedFiles` de la base `kazoos` (version 5, ADR 0272 §6)
associe chaque adresse servie à un contenu :

```text
{ url, contentHash, length, contentType, appId }
```

- Clé : `url`, relative à la base de la page (`_content/Kazo.Sample/x.js`).
- Index : `contentHash` (pour la passe de nettoyage) et `appId`.
- La table est partagée par tous les onglets, comme le cache l'était.

### 3. Publier une app n'écrit plus d'octets

Au premier lancement d'une app dans la session, `InstalledAppAssetPublisher`
écrit pour chaque fichier déclaré une ligne de la table, **sans lire le
contenu** : il compare le `contentHash` de l'entrée du fichier au SHA-256 du
manifeste. S'ils diffèrent, ou si le fichier manque, l'adresse n'est pas
servie et une ligne part dans `/var/log/syslog`, comme aujourd'hui pour un
fichier abîmé. La première publication retire aussi les lignes qu'aucune app
installée ne déclare plus (règle de l'ADR 0225 §2, inchangée).

La vérification reste aussi forte : le contenu servi est celui dont le
hachage est dans le manifeste, puisque le contenu est adressé par ce hachage.

### 4. Une adresse servie est une référence

La passe de nettoyage (ADR 0273 §6) ne supprime pas un contenu qu'une ligne de
`servedFiles` porte encore. Une app désinstallée ou mise à jour pendant qu'une
autre fenêtre la fait tourner garde donc ses fichiers jusqu'à la prochaine
publication, qui retire ses lignes ; le contenu devient alors orphelin et suit
la rétention ordinaire.

### 5. Le service worker lit le magasin de fichiers

Pour une requête `GET` sous `_content/` :

1. le worker cherche l'adresse dans `servedFiles` ; sans ligne, la requête
   part au réseau, comme aujourd'hui (fichiers des apps intégrées) ;
2. il répond par un flux (`ReadableStream`) qui lit les blocs
   `[contentHash, numéro]` de `fsContent` par lots, sans charger le fichier
   entier ;
3. en-têtes : `Content-Type` et `Content-Length` de la table,
   `Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: no-cache`,
   `ETag` égal au `contentHash`, et les en-têtes d'isolation de la page en mode
   « apps installées seulement » (inchangé) ;
4. une requête `Range` reçoit une réponse `206` avec les seuls blocs demandés.
   Ce lecteur est aussi celui de l'adresse de fichier
   ([ADR 0275](0275-partial-read-and-file-address.md) §3).

Le worker lit les deux formes de bloc (base64 et binaire, ADR 0272 §3).

**Connexion à la base.** Le worker ouvre `kazoos` sans numéro de version, et
ferme sa connexion sur `versionchange` et après chaque réponse : une connexion
gardée ouverte par le worker bloquerait la mise à niveau de la base par la
page (`onblocked` fait échouer l'ouverture).

**Version du worker.** Un worker d'avant cette ADR sert depuis le cache, que
la page ne remplit plus. La publication demande donc, comme aujourd'hui, que
le worker qui contrôle la page soit celui de sa version (`prepare`,
ADR 0225 §1 et 0237) ; sinon elle recommence au lancement suivant.

### 6. Ce qui ne change pas

- Les adresses, et donc les apps : aucune app n'a rien à changer.
- La version précédente d'une app reste gardée par l'installeur. Deux versions
  partagent déjà le contenu de leurs fichiers identiques (déduplication par
  fichier).
- Le lien de la feuille de style isolée de l'app (ADR 0221 §2).
- Les permissions : le worker ne sert que les adresses de la table, que seule
  la publication écrit, pour les fichiers déclarés par le manifeste d'une app
  installée. C'est ce que le cache exposait déjà.

## Conséquences

- Une app de 64 Mio occupe 128 Mio avec la version précédente (64 Mio sans),
  au lieu d'environ 236 Mio, une fois le contenu en binaire.
- Le premier lancement d'une app dans la session ne recopie plus rien : une
  app de 60 Mo démarre sans relire ni réécrire ses 60 Mo.
- Servir depuis IndexedDB est un peu plus lent que depuis Cache Storage
  (0,19 s contre 0,08 s pour lire 52 Mio en binaire, 0,56 s tant que le
  contenu est en base64). Le chantier mesure le démarrage d'une app Craft
  avant et après.
- Modifier à la main un fichier d'une app installée arrête de le servir à la
  session suivante (son hachage ne correspond plus au manifeste), comme
  aujourd'hui.
- Le service worker gagne du code qui lit IndexedDB ; il est partagé par les
  deux fichiers de service worker de l'hôte, comme `installedAppAssets.sw.js`.

## Options écartées

- **B. Les fichiers servis seulement dans Cache Storage**, sans les extraire
  dans le système de fichiers : écartée par Benoit ; une app ne serait plus un
  dossier de fichiers comme sur une vraie machine, et `ls`, la sauvegarde et la
  synchro ne la verraient plus.
- **Garder la copie mais ne la faire qu'une fois** (pas à chaque session) :
  gagne du temps, pas d'espace.
- **Une table dans Cache Storage** plutôt que dans IndexedDB : pas de magasin
  à ajouter, mais la table ne serait pas dans la même transaction que les
  fichiers et la passe de nettoyage ne pourrait pas la vérifier en supprimant.

## Tâches

- [ ] Magasin `servedFiles` (base en version 5), dépôt, tests.
- [ ] `InstalledAppAssetPublisher` : lignes au lieu de copies, comparaison des
  hachages, ménage des lignes.
- [ ] Lecteur de blocs du service worker (flux, `Range`, deux formes), partagé
  par les deux fichiers de service worker.
- [ ] La passe de nettoyage compte `servedFiles` comme référence.
- [ ] Suppression du cache `kazoos-installed-apps` et de `IInstalledAppAssetCache.PutAsync`.
- [ ] Tests E2E : app installée dans l'OS publié (`TrimmedPublish.E2E`), page
  chargée par Ctrl+F5, app dans une iframe, worker d'une app, deux onglets.
- [ ] Mesure du démarrage d'une app Craft avant et après.
- [ ] Docs (FR et EN) : `browser-infrastructure.md`, paquets d'apps, mémoire,
  `features.md`, entrée de changement.
