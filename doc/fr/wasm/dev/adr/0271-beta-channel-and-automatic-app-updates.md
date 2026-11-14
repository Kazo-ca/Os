> [English version](../../../../en/wasm/dev/adr/0271-beta-channel-and-automatic-app-updates.md)

# ADR 0271 : canal bêta de Kazo OS et mises à jour automatiques des apps

## Statut

**Proposée** le 2026-11-14 (évaluation, aucun code). Les questions de la
section [Questions posées à Benoit](#questions-posées-à-benoit) sont posées
dans le fil ; la décision ci-dessous suit les recommandations en attendant ses
réponses. Série `release-channels/base`, plan :
[`release-channels-plan.md`](../plans/release-channels-plan.md).

- **Complète :** l'[ADR 0173](0173-automatic-releases-from-the-version-file.md)
  (publication depuis le fichier de version), l'[ADR 0176](0176-app-repository-template.md)
  et l'[ADR 0207](0207-solution-template-implementation.md) (dépôt d'app,
  sous-module et paquets), l'[ADR 0188](0188-hack-store-and-app-catalog.md)
  (catalogue).
- **Couvre le point 1 de l'issue [#456](https://github.com/Kazo-ca/Os/issues/456)**
  (SDK en préversion). Les points 2 (vérifier les apps installées avant une
  mise à jour de l'OS) et 3 (maintien des anciennes versions) restent dans
  cette issue.

## Contexte

Benoit, 2026-11-14 :

> Faire que chaque application puisse publier une mise à jour automatiquement
> aussitôt qu'une version bêta soit disponible ou quand un nouveau NuGet est
> déployé. Cela implique : une version hébergée de Kazo OS sur os.kazo.ca/beta
> déployée automatiquement ; un tag de version en bêta (preview) et des NuGet
> en pre-release ; que chaque application soit capable de compiler et publier
> une mise à jour ; mettre à jour le dépôt template.

Ce qui existe (vérifié dans le code le 2026-11-14) :

| Sujet | État |
| --- | --- |
| Publication de l'OS | `release.yml` publie quand `build/Versions.props` de `main` porte une version sans tag. Il accepte déjà un suffixe (`3.4.0-preview.1`) : la Release est alors marquée « pre-release » et l'image du serveur ne reçoit pas `:latest`. Mais il ne regarde que `main`. |
| Site | `deploy-to-pages.yml` publie la tête de `main` sur GitHub Pages (`os.kazo.ca`, fichier `CNAME`) à **chaque** push, pas à chaque version. Un seul artefact Pages par dépôt, donc un seul domaine. |
| Stockage du navigateur | Une seule base IndexedDB `kazoos`, des caches `kazoos-cache-*` que chaque worker nettoie par préfixe, et un service worker de portée `/`. Deux versions de l'OS sur la **même origine** partagent tout cela. |
| Compatibilité des apps | `KazoOsSdk.IsCompatible` : même version majeure et `minimum ≤ SDK de l'OS`. Le minimum d'une app est la version du SDK avec laquelle elle est compilée. Une app compilée sur 3.3.4 s'installe sur 3.4.0-preview.1 et sur 3.4.0 ; une app compilée sur 3.4.0-preview.1 ne s'installe **pas** sur 3.3.4 ; une app compilée sur 3.x est refusée par 4.0. |
| Préversions d'apps | Le catalogue et l'installateur savent déjà ignorer une version d'app en préversion (`latestVersion`, `AppPackageIndex.Latest`) ; un utilisateur déjà sur une préversion suit les préversions. |
| Dépôt d'app | Sous-module `external/KazoOs` sur un tag `v<version>` (la CI le vérifie, préversion comprise), paquets nuget.org en `Release`. Publier = changer `Version` du manifeste, fusionner dans `main`, lancer « Tag a release », puis la PR du catalogue (secret `HACK_STORE_TOKEN`, absent de plusieurs dépôts). Tout est manuel. |
| Catalogue | `Os-AppMarketPlace` : un dossier `apps/<id>_<version>/` par version, compilé par la CI du catalogue au commit indiqué, signé là seulement, 3 versions gardées par app. Fusion dans `main` du catalogue = relecture humaine. |
| Workflows des apps | Chaque dépôt d'app a sa **copie** des workflows du modèle : 11 dépôts à modifier pour chaque changement. |

## Décision

### 1. Deux canaux : stable et bêta

- **Stable** ne change pas : `main`, version sans suffixe, `os.kazo.ca`.
- **Bêta** est une branche permanente **`beta`** du dépôt `Kazo-ca/Os`, créée
  depuis `main`. Sa version porte toujours un suffixe :
  `<prochaine version>-preview.<n>`. Les séries que Benoit veut essayer avant
  `main` y sont fusionnées ; un agent peut fusionner dans `beta` une PR verte
  (ce n'est pas `main`). `main` est fusionnée dans `beta` après chaque version
  stable.
- Garde-fous dans `release.yml` : `main` refuse une version avec suffixe,
  `beta` refuse une version sans suffixe.
- Une version bêta devient stable par une PR `beta` → `main` qui retire le
  suffixe. **Benoit seul la fusionne.**

### 2. Préversion du SDK : tag, GitHub Release, nuget.org

- `release.yml` (même fichier, pour garder la politique Trusted Publishing de
  nuget.org) démarre aussi après une vérification réussie sur `beta`. Quand la
  version de `beta` n'a pas de tag : paquets `Kazo.Os.*` poussés sur nuget.org
  en préversion, tag `v<version>`, GitHub Release « pre-release », zip du site.
- Publier une bêta = changer `<n>` dans `build/Versions.props` sur `beta`
  (même geste que l'ADR 0173). Le **site** bêta, lui, suit chaque push de
  `beta`, comme `os.kazo.ca` suit `main`.
- L'API publique n'est pas versée dans `PublicAPI.Shipped.txt` pour une
  préversion ; `changes/unreleased/` n'est pas regroupé.

### 3. Le site bêta

- Le site bêta vit sur une **origine séparée, `beta.os.kazo.ca`** ;
  `os.kazo.ca/beta` y redirige. Raison : sur la même origine, la bêta ouvrirait
  la base `kazoos` de l'utilisateur. Une bêta qui monte le schéma de la base
  rend l'OS stable incapable de la rouvrir (IndexedDB ne redescend pas de
  version), les deux workers effacent les caches l'un de l'autre, et le worker
  de `/` sert sa propre page pour `/beta/`.
- Hébergement : un second site statique (Cloudflare Pages, déjà utilisé pour
  la redirection de `hackos.kazo.ca`), déployé par un workflow à chaque push de
  `beta`. GitHub Pages ne sert qu'un domaine par dépôt.
- La bêta se présente comme telle : nom de PWA « Kazo OS Beta », pastille
  « Bêta » à l'écran de connexion et dans Paramètres ▸ À propos. Ses données
  sont séparées de celles du site stable ; rien n'est copié de l'un à l'autre.
- `version.json` et `HostReleaseMonitor` (ADR 0174, 0236) marchent sans
  changement : chaque site lit son propre `version.json`.

### 4. Les apps suivent le SDK

Un workflow commun **« Kazo OS SDK update »** dans chaque dépôt d'app :

- **Déclencheurs :** un `repository_dispatch` envoyé par `release.yml` aux
  dépôts listés dans `build/app-repositories.txt`, et une vérification
  planifiée (toutes les heures) des tags de `Kazo-ca/Os`, pour les dépôts de
  tiers qui ne reçoivent pas l'envoi.
- **Sur une préversion :** la branche `beta` du dépôt d'app reçoit `main`, le
  sous-module passe sur le tag de préversion, la version devient
  `<prochaine version corrective>-beta.<n>`. La CI compile et teste en
  `Release` sur les paquets de préversion. Verte : la version bêta est soumise
  au canal bêta du catalogue, sans intervention. Rouge : une issue `TODO` est
  ouverte dans le dépôt de l'app avec le journal. Rien ne touche `main`.
- **Sur une version stable :** une PR vers `main` de l'app déplace le
  sous-module et monte la version corrective. La CI tourne toujours : c'est la
  vérification que l'app compile encore. La publication dans le Store suit la
  réponse à Q4 (recommandation : pour une mineure ou une majeure du SDK, pas
  pour une corrective).
- **Numéro de version de l'app :** un script du modèle
  (`.github/scripts/bump-app-version.sh`) modifie `Version` de la classe
  `[AppManifest]` ; un changement de fond de l'app reste fait par son auteur.

### 5. Le catalogue a un canal bêta

- Les versions bêta des apps vont dans la branche `beta` de
  `Os-AppMarketPlace` (son sous-module suit la préversion de l'OS, pour
  valider un manifeste qui utilise une nouveauté). La publication écrit
  `catalog-beta.json` sur la branche `packages` : le contenu de `catalog.json`
  plus les versions bêta. Les versions bêta ne comptent pas dans les 3 versions
  gardées et sont retirées quand la version stable correspondante sort.
- **Soumission « reconstruction seule » :** l'outil du catalogue compare le
  commit soumis au dernier commit publié de l'app. Quand seuls le pointeur du
  sous-module et la version du manifeste diffèrent, et que les capacités
  déclarées sont identiques, la PR peut être fusionnée par l'automate une fois
  la validation verte : aucun code d'app nouveau n'est à relire. Toute autre
  soumission attend une relecture humaine, comme aujourd'hui.
- La signature reste dans le seul dépôt du catalogue
  (`KAZO_STORE_SIGNING_KEY`), bêta comprise.

### 6. Côté utilisateur

- Le canal suit l'OS : un OS en préversion lit `catalog-beta.json` et propose
  les versions bêta des apps ; l'OS stable lit `catalog.json` et ne voit
  jamais de version bêta. Pas de réglage à comprendre.
- Paramètres ▸ À propos de l'OS stable reçoit un lien « Essayer la bêta »
  vers `beta.os.kazo.ca`, et la bêta un lien « Revenir à la version stable ».

### 7. Un seul jeu de workflows pour les apps

- Les workflows des apps deviennent des **workflows réutilisables** hébergés
  dans `Kazo-ca/Os-AppTemplate` (`on: workflow_call`), appelés par une ligne
  `uses: Kazo-ca/Os-AppTemplate/.github/workflows/<nom>.yml@v1` dans chaque
  dépôt. Un changement futur se fait une fois, dans le template.
- Le modèle `kazoos-solution` (dans ce dépôt) génère ces appels ; le dépôt
  template est régénéré, puis les 11 dépôts d'apps sont alignés.
- **Jeton :** un secret d'organisation remplace les secrets par dépôt (réponse
  à Q6), ce qui règle aussi les dépôts sans `HACK_STORE_TOKEN`. Pour un dépôt
  de `Kazo-ca`, la PR du catalogue part d'une branche du catalogue, sans fork ;
  le chemin par fork reste pour les tiers.

### 8. Ce qui reste une décision humaine

| Geste | Qui |
| --- | --- |
| Fusionner dans `main` de `Kazo-ca/Os`, dont `beta` → `main` | Benoit |
| Choisir le numéro de la prochaine version stable | Benoit |
| Fusionner dans `main` d'une app et du catalogue une soumission qui change du code ou des capacités | Benoit (ou le fil autorisé, comme pour Os-App-Word) |
| Fusionner une « reconstruction seule » stable | Selon Q5 |
| Créer le site bêta, le DNS, les jetons et les secrets | Benoit, une fois |
| Publier une bêta (changer `<n>` sur `beta`), fusionner dans `beta` | Agents, PR verte |
| Reconstruire et publier les apps en bêta | Automate |

## Conséquences

- Une bêta coûte 42 paquets sur nuget.org (une préversion ne se supprime pas,
  elle se masque) et une reconstruction de chaque app : d'où la publication
  par changement de `<n>` et non à chaque push.
- Reconstruire une app sur un SDK plus récent monte son minimum : elle ne
  s'installe plus sur un OS plus ancien (PWA hors ligne pas encore mise à
  jour, serveur installé resté sur une version précédente). Le catalogue garde
  3 versions avec le SDK minimal de chacune, mais aujourd'hui seule la
  suggestion d'app pour un fichier (ADR 0257) choisit la plus récente
  compatible : le Store et `kazopkg` proposent la dernière, que l'installateur
  refuse (`apps.package.sdk-incompatible`). Le chantier 3 fait choisir partout
  la version la plus récente compatible ; c'est aussi la raison de la
  recommandation de Q4.
- Les apps Craft (Word, Spreadsheets, Pdf, Fig, Photo, Deck…) embarquent un
  build WASM de leur moteur : une reconstruction pour le SDK réutilise le build
  épinglé, elle ne recompile pas le moteur.
- Deux branches longues (`main`, `beta`) dans trois dépôts (`Os`, chaque app,
  le catalogue) : `beta` est toujours régénérable depuis `main`, on ne la
  répare pas, on la refait.
- Le dépôt `Os-AppTemplate` devient une dépendance d'exécution des CI des
  apps : ses workflows réutilisables sont versionnés par tag (`@v1`).

## Options écartées

- **Préversions sur `main`.** `os.kazo.ca` suit `main` : tous les utilisateurs
  recevraient la bêta.
- **Bêta depuis n'importe quelle `<série>/base`.** Un seul site bêta : deux
  séries s'écraseraient. La branche `beta` les réunit.
- **`os.kazo.ca/beta` sur la même origine, données partagées.** Risque de
  rendre l'OS stable inutilisable (voir §3).
- **Canal bêta par réglage dans le Store stable.** Une app bêta compilée sur un
  SDK en préversion ne s'installe pas sur l'OS stable ; le réglage ne
  montrerait presque rien.

## Questions posées à Benoit

Posées dans le fil le 2026-11-14 ; la recommandation est en gras.

| # | Question | Choix |
| --- | --- | --- |
| Q1 | Où vit le site bêta ? | **A. `beta.os.kazo.ca`, `os.kazo.ca/beta` redirige** · B. `os.kazo.ca/beta`, même origine, avec isolation à coder (base, caches, worker) · C. `os.kazo.ca/beta`, données partagées avec le stable |
| Q2 | D'où viennent les bêtas ? | **A. Branche permanente `beta`** · B. Une `<série>/base` choisie à la main, une à la fois · C. `main` |
| Q3 | Quand une préversion NuGet part-elle ? | **A. Quand `<n>` change sur `beta`** · B. À chaque push de `beta`, numéro automatique · C. Workflow lancé à la main |
| Q4 | Publier une nouvelle version de chaque app à chaque version **stable** du SDK ? | A. Toujours · **B. Vérifier toujours, publier pour une mineure ou une majeure** · C. Vérifier toujours, publier pour une majeure seulement |
| Q5 | Qui fusionne les « reconstructions seules » ? | A. L'automate, en bêta et en stable · **B. L'automate en bêta, un humain en stable** · C. Un humain partout |
| Q6 | Quel jeton pour les automates ? | **A. Une GitHub App de l'organisation** · B. Un jeton personnel en secret d'organisation · C. Un secret par dépôt, comme aujourd'hui |

## Tâches

Voir le plan : [`release-channels-plan.md`](../plans/release-channels-plan.md).

- [x] Évaluation, ADR et plan (chantier 0).
- [ ] Réponses de Benoit reportées ici.
- [ ] Chantiers 1 à 8 du plan.
