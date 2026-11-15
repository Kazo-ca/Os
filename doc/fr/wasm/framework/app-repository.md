> [English version](../../../en/wasm/framework/app-repository.md)

# Dépôt d'app : le modèle `kazoos-solution`

## Objet

Le point de départ d'une app Kazo OS dans son propre dépôt : une solution qui
se lance en débogage dans l'OS, dont la version de Kazo OS est fixée par un
sous-module, qui se publie dans le Kazo OS Store par un tag et qui suit seule
les nouvelles versions de Kazo OS. Décisions :
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md),
[ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) (canal
bêta, workflows réutilisables, mise à jour du SDK).
Le guide général d'un dépôt qui utilise Kazo OS est
[`building-on-kazoos.md`](building-on-kazoos.md).

## Créer le dépôt

Deux façons, même résultat :

- **Depuis GitHub** : « Use this template » sur
  [`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate),
  puis `git clone --recurse-submodules`. Le sous-module est déjà là.
- **Depuis la ligne de commande ou Visual Studio** :

  ```bash
  dotnet new install Kazo.Os.Templates
  dotnet new kazoos-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/Os-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
  git -C external/KazoOs checkout v<version de Kazo OS>
  ```

  `dotnet new` ne sait pas créer le lien du sous-module ; sans lui, le build
  s'arrête sur `KOSAPP0070` avec la commande à lancer. Dans Visual Studio
  (Nouveau projet) ou Rider, le modèle s'appelle « Kazo OS app solution ».

## Ce qui est généré

| Chemin | Contenu |
| --- | --- |
| `<Nom>.sln` | la solution (dossiers `src` et `tests`) |
| `src/<Nom>/` | l'app fenêtrée ; sa classe `[AppManifest]` (avec `Version`) génère `manifest.json` |
| `src/<Nom>.Os/` | l'hôte : tout Kazo OS, les apps de base et l'app ; le projet à démarrer |
| `tests/<Nom>.Tests/` | tests xUnit de l'app |
| `Directory.Build.props` | racine du sous-module, profil, import de `Versions.props` |
| `Directory.Build.targets` | import du mode source externe, contrôle du sous-module |
| `.gitmodules` | `external/KazoOs`, clone superficiel |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, `screenshots/` facultatif |
| `.github/workflows/ci.yml` | CI (push sur `main` et `beta`, pull requests) : sous-module vérifié, README conforme à `store/` (`.github/scripts/store-readme.mjs --check`), build et tests en `Release` |
| `.github/workflows/publish-to-kazoos-store.yml` | soumission au catalogue sur un tag `v*` (canal stable ou bêta selon la version) |
| `.github/workflows/tag-release.yml` | pose sur un commit de `main` (ou de `beta` pour une version bêta) le tag de la version du manifeste, puis publie (`workflow_dispatch`) |
| `.github/workflows/publish-packages.yml` | paquet non signé poussé sur la branche `kazoos-packages` sur un tag `v*` ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)) |
| `.github/workflows/sdk-update.yml` | **Kazo OS SDK update** : passe l'app sur une nouvelle version de Kazo OS, la teste et la publie (voir [plus bas](#mise-à-jour-du-sdk-kazo-os)) |
| `.github/scripts/` | `check-kazoos-submodule.sh`, `store-readme.mjs`, `bump-app-version.sh` (change `Version` de la classe `[AppManifest]` et de `manifest.json`) |

Chaque fichier de `.github/workflows/` tient en quelques lignes : il appelle un
[workflow réutilisable](#les-workflows-réutilisables) de
`Kazo-ca/Os-AppTemplate`.
| `README.md`, `readme-fr.md` | en haut, la fiche du Store de l'app (le texte de `store/description.{en,fr}.md` et les captures), une section « Essayer » et une courte présentation de Kazo OS avec le lien vers son dépôt ; puis ces étapes, pour le dépôt créé |

## La version de Kazo OS

`external/KazoOs/wasm2/KazoOs/build/Versions.props` est la version du
dépôt : `Directory.Build.props` l'importe, et chaque référence `Kazo.Os.*`
utilise `$(KazoOsVersion)`. Changer de version, c'est placer le sous-module
sur un autre tag de release et valider ce changement. On ne modifie jamais le
sous-module : la CI refuse un sous-module modifié ou hors de son tag
(`.github/scripts/check-kazoos-submodule.sh`).

## Les deux profils

| Profil | Kazo OS vient de | Sortie de restauration |
| --- | --- | --- |
| `Debug` (`KazoOsUseSource=true`) | les projets du sous-module : on entre dans le SDK et l'OS, points d'arrêt partout | `obj/source/` |
| `Release` (`KazoOsUseSource=false`) | les paquets `Kazo.Os.*` de nuget.org, à la version du sous-module | `obj/` |

`-p:KazoOsUseSource=true` ou `false` force l'un ou l'autre ;
`-p:KazoOsSourceRoot=<dossier>` pointe vers une autre copie de Kazo OS
(dossier de `Kazo.Os.sln`). Le premier build `Debug` compile tout l'OS.

### Comment marche le profil `Debug`

`build/Kazo.Os.ExternalSource.targets` (dans le dépôt Kazo OS) est importé
depuis le sous-module. Il :

1. trouve les projets qui peuvent être des paquets : `Shared/*`, `Platform/*`,
   `OS/*`, `Infrastructure/*`, `Server/*`, `Tools/*` et `Apps/*/*` (un paquet
   = le projet du même nom) ;
2. remplace chaque `PackageReference` vers l'un d'eux par une
   `ProjectReference`, en gardant `IsSystemApp` pour les apps de base de
   l'hôte ;
3. retire `Kazo.Os.App.Build` et importe `Kazo.Os.Apps.targets` pour les
   projets qui déclarent `KazoOsAppProject` ou `KazoOsAppHost` : même
   générateur de manifeste, mêmes analyseurs, même agrégation dans l'hôte que
   dans le dépôt Kazo OS (`KazoOsExternalSource=true`).

La frontière du SDK (`KOSAPP0030`) vérifie les références qui entrent dans le
sous-module ; un projet de votre dépôt peut en référencer un autre. Une app
ajoutée avec `dotnet new kazoos-window` (ou `-command`, `-service`, `-site`)
déclare déjà `KazoOsAppProject=true` ; ajoutez-la à l'hôte par
`dotnet add src/<Nom>.Os reference …`.

## Publier dans le Kazo OS Store

1. Remplir `store/` : icône PNG 256×256 (200 Ko au plus), description courte
   anglaise de 120 caractères au plus (le français est facultatif),
   descriptions, et dans `store.json` la catégorie (liste fermée du
   catalogue), les étiquettes et la licence. `repository` vient de
   `--repository` ; `project` nomme le `.csproj` de l'app, que le catalogue
   compile (à la racine, `KazoOsPack` tournerait aussi sur le projet de tests,
   qui n'a pas cette cible).
   Lancer ensuite `node .github/scripts/store-readme.mjs` (`npm run
   store:readme` dans un dépôt qui a un `package.json`) : il copie les
   descriptions, l'icône et les captures en haut de `README.md` et de
   `readme-fr.md`, entre les commentaires `Store listing` / `Fiche du Store`,
   pour que le dépôt montre la même fiche que le Store, suivie d'un lien vers
   Kazo OS. La CI le lance avec `--check` et échoue quand un README diffère de
   `store/`. La section
   « Essayer » pointe vers `https://os.kazo.ca/?autolaunch=<id de l'app>`
   ([ADR 0258](../dev/adr/0258-direct-app-launch-link.md), Kazo OS 3.3.3) : le
   lien démarre Kazo OS, installe l'app depuis le Store officiel et l'ouvre.
   Le modèle `kazoos-solution` y écrit l'id donné par `--appId`.
2. Changer `Version` dans la classe `[AppManifest]`, valider dans `main`, puis
   poser le tag : lancer le workflow **Tag a release** (`tag-release.yml`, depuis
   l'onglet Actions ou, pour un agent, via l'API GitHub), ou poser le tag
   `v<version>` et le pousser soi-même. Le workflow pose sur un commit de `main`
   (la tête par défaut) le tag de la version du manifeste et y lance les deux
   workflows de publication, car un tag poussé avec le jeton du workflow ne les
   lance pas ; avec `publish` désactivé, il pose seulement le tag (version déjà
   publiée).
3. Le workflow **Publish to the Kazo OS Store** vérifie le sous-module, compile
   l'app en `Release`, la met en paquet (`KazoOsPack`), refuse un tag qui
   n'est pas la version du manifeste et produit `apps/<appId>_<version>/`
   (contenu de `store/`, `manifest.json` **du paquet**, `store.json` avec le
   commit du tag), laissé en artefact du workflow. Le manifeste du paquet est le
   manifeste généré plus toutes les ressources statiques que `KazoOsPack` y a
   ajoutées ([ADR 0221](../dev/adr/0221-pack-all-static-web-assets-of-an-app.md)) :
   c'est celui que le catalogue compare au paquet que sa CI construit. Le paquet
   contient aussi les assemblages satellites de l'app (ses `.resx` par culture,
   [ADR 0222](../dev/adr/0222-installed-app-satellite-assemblies.md)).
4. Avec le secret `HACK_STORE_TOKEN` (un jeton de l'auteur qui peut créer un
   fork et ouvrir une pull request), le workflow ouvre la pull request vers
   `Kazo-ca/Os-AppMarketPlace` depuis votre fork. Sans lui, téléchargez
   l'artefact et ouvrez la pull request vous-même.

Le catalogue ne prend jamais de binaire : après relecture et fusion, sa CI
compile l'app au commit indiqué (ADR 0188 §4 à §6).

### Le secret `HACK_STORE_TOKEN`

C'est un jeton d'accès personnel GitHub de **l'auteur de l'app** : ni Kazo OS
ni le catalogue ne le fournissent. Avec lui, le workflow lit le nom de
l'auteur, forke `Kazo-ca/Os-AppMarketPlace` sur son compte, y pousse la
branche `<appId>_<version>` et ouvre la pull request vers le catalogue.

Créer le jeton (un jeton « classic ») :

1. GitHub ▸ photo du profil ▸ **Settings** ▸ **Developer settings** ▸
   **Personal access tokens** ▸ **Tokens (classic)** ▸ **Generate new token
   (classic)**.
2. *Note* : par exemple `<dépôt> → Kazo OS Store` ; *Expiration* : courte
   (90 jours par exemple), à renouveler.
3. Scopes : **`repo`** (forker, pousser, ouvrir la pull request) et
   **`workflow`** (GitHub refuse un push qui apporte au fork des changements de
   `.github/workflows` du catalogue que le fork n'a pas encore).
4. **Generate token**, puis copier le jeton : il ne s'affiche qu'une fois.

L'ajouter au dépôt de l'app : **Settings** ▸ **Secrets and variables** ▸
**Actions** ▸ **New repository secret**, nom `HACK_STORE_TOKEN`. Ou en ligne de
commande :

```bash
gh secret set HACK_STORE_TOKEN --repo <propriétaire>/<dépôt>
```

- **Pourquoi « classic »** : un jeton à grain fin ne vaut que pour un seul
  propriétaire (le compte de l'auteur **ou** une organisation), alors que le
  workflow touche le fork de l'auteur et le dépôt du catalogue. Si
  l'organisation du dépôt de l'app interdit les jetons classic, l'autoriser
  dans ses réglages (*Personal access tokens*).
- **Prudence** : `repo` donne accès à tous les dépôts de l'auteur. Garder une
  expiration courte, ne mettre le jeton que dans ce secret, le révoquer au
  moindre doute.
- **Sans le secret**, rien ne bloque : le dossier `apps/<appId>_<version>/`
  reste en artefact du workflow, et l'auteur ouvre la pull request lui-même.
- Ce jeton ne signe rien : les apps sont signées seulement dans le dépôt du
  marketplace ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)).

### Canal bêta

Une version d'app avec un suffixe (`1.2.1-beta.1`) est une version bêta
([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §5) :
elle se publie depuis la branche `beta` de l'app (**Tag a release**, branche
`beta`) et va dans la branche `beta` du catalogue, que seul un Kazo OS en
préversion lit. `main` refuse une version avec suffixe, `beta` une version
sans suffixe.

### Le jeton de l'automate

Pour les dépôts de `Kazo-ca`, les workflows utilisent la **GitHub App de
l'organisation** ([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md)
§7, Q6) au lieu de `HACK_STORE_TOKEN` : deux secrets d'organisation,
`KAZO_AUTOMATION_APP_ID` (l'identifiant de l'app) et `KAZO_AUTOMATION_APP_KEY`
(sa clé privée), transmis par `secrets: inherit`. Avec eux, la pull request du
catalogue part d'une branche `submit/<appId>_<version>` du catalogue lui-même,
sans fork, et la mise à jour du SDK ouvre et fusionne ses pull requests. Sans
eux, `HACK_STORE_TOKEN` (fork de l'auteur) puis l'artefact à soumettre à la
main restent les chemins d'un dépôt tiers.

## Publier sur `kazoos-packages`

[ADR 0219](../dev/adr/0219-app-authors-signing-tool.md) §4, sans signature
depuis le 2026-11-07. Le
même tag `v*` lance **Publish the Kazo OS packages** (`publish-packages.yml`,
seul workflow avec `contents: write`) : il récupère la branche
`kazoos-packages` (orpheline la première fois), y fait le paquet
(`KazoOsPack`, `KazoOsPackageOutput`), garde les versions déjà publiées, puis
pousse la branche. `kazopkg install <owner>/<repo>` installe alors l'app sans
le Store.

Ces paquets ne sont **pas signés** : aucun dépôt d'app ne signe, seul
`Kazo-ca/Os-AppMarketPlace` signe les paquets du Kazo OS Store. L'OS les
vérifie par leur SHA-256 et demande un administrateur pour une source non
fiable (ADR 0208, ADR 0214 §2.4). Un dépôt d'app qui a encore un secret
`HACKEROS_APP_SIGNING_KEY` (ADR 0219) peut le supprimer : plus rien ne le lit.

## Mise à jour du SDK Kazo OS

`sdk-update.yml` (« Kazo OS SDK update »,
[ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §4)
démarre quand Kazo OS annonce une version (`repository_dispatch` de type
`kazoos-sdk-released`, charge `{ "version": "3.5.0-preview.1", "channel": "beta" }`,
envoyé aux dépôts de `build/app-repositories.txt`), toutes les heures pour les
autres dépôts, ou à la main (onglet Actions, version facultative, `force` pour
refaire une version déjà traitée).

1. Il cherche la version la plus récente parmi les tags du dépôt du
   sous-module (ou celle demandée) et la compare à celle de `main` et de
   `beta`. Une version stable passe avant une préversion.
2. Il crée la branche `kazoos-sdk/<version de Kazo OS>` : le sous-module sur
   le tag, et la version de l'app montée par `bump-app-version.sh`.
   - **Version stable** : depuis `main` ; `x.y.z` devient `x.y.(z+1)`.
   - **Préversion** : depuis `beta` (créée depuis `main` la première fois) ;
     la version devient `x.y.(z+1)-beta.<n>`, où `x.y.z` est celle de `main`
     et `<n>` suit les tags déjà posés. `beta` est refaite depuis `main` quand
     elle ne contient que des commits de ce workflow ; sinon `main` y est
     fusionnée, après avoir aligné la version et le sous-module de `beta` sur
     ceux de `main` pour que ces lignes ne se contredisent pas.
   La branche reste ensuite : elle marque la version comme traitée, la
   vérification de l'heure suivante ne la refait pas.
3. La CI de l'app (`kazoos-ci.yml`) tourne sur cette branche.
4. Verte :
   - **préversion** : `beta` avance sur la branche, le tag est posé et la
     version est soumise au canal bêta du catalogue ;
   - **mineure ou majeure stable** : pull request vers `main` fusionnée par
     l'automate (Q5 = A), tag, publication (Q4 = B) ;
   - **corrective stable** : la pull request est fermée une fois verte ;
     l'app compile toujours, rien n'est publié.
5. Rouge (ou `main` qui ne se fusionne pas dans `beta`) : une issue `TODO`
   avec le lien de l'exécution ; rien n'est fusionné ni publié.

Le dépôt de l'app n'a rien à régler quand les secrets de l'automate sont là.
Sans eux, le jeton du workflow sert : il faut alors autoriser GitHub Actions à
créer des pull requests (**Settings** ▸ **Actions** ▸ **General**), et une
`main` protégée peut refuser la fusion (la pull request reste ouverte, avec un
commentaire). La dernière étape d'une préversion attend la branche `beta` du
catalogue (chantier 4 de la série `release-channels/base`).

## Les workflows réutilisables

Les workflows sont écrits une fois, dans
[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
(`on: workflow_call`), et chaque dépôt d'app les appelle en une ligne
([ADR 0271](../dev/adr/0271-beta-channel-and-automatic-app-updates.md) §7) :

```yaml
jobs:
  build:
    uses: Kazo-ca/Os-AppTemplate/.github/workflows/kazoos-ci.yml@v1
```

| Workflow réutilisable | Appelé par | Entrées |
| --- | --- | --- |
| `kazoos-ci.yml` | `ci.yml`, la mise à jour du SDK | `ref`, `solution`, `npm-test` (`npm ci` puis `npm test`), `e2e-test` (Playwright Chromium, `npm run test:e2e`), `timeout-minutes` |
| `kazoos-publish-to-store.yml` | `publish-to-kazoos-store.yml`, `kazoos-tag-release.yml` | `app-directory`, `ref` ; sorties `pull-request`, `channel` |
| `kazoos-publish-packages.yml` | `publish-packages.yml`, `kazoos-tag-release.yml` | `app-directory`, `ref` |
| `kazoos-tag-release.yml` | `tag-release.yml`, la mise à jour du SDK | `app-directory`, `commit`, `branch` (`main` ou `beta`), `publish` |
| `kazoos-sdk-update.yml` | `sdk-update.yml` | `app-directory`, `version`, `force` et les entrées de la CI |

Seul le sous-module `external/KazoOs` est récupéré : un autre sous-module (les
sources d'un moteur dont le build est versionné, comme les apps Craft) n'est
pas nécessaire. Un dépôt dont la CI a des étapes propres (un build épinglé, par
exemple) garde son workflow à côté de ces appels.

Le tag `v1` de `Os-AppTemplate` désigne la dernière version compatible : il
avance à chaque changement qui ne casse pas les appels ; un changement qui les
casse prend `v2`. Les scripts (`.github/scripts/`) restent dans chaque dépôt :
un auteur les lance aussi à la main.

## Le dépôt template GitHub

[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
est la sortie de `dotnet new kazoos-solution -n Kazo.Os.App --templateRepository true`
plus le lien du sous-module. On le régénère depuis le modèle, jamais à la main.
`--templateRepository true` ajoute les workflows réutilisables (`kazoos-*.yml`)
et fait appeler aux workflows du dépôt leur copie locale
(`uses: ./.github/workflows/kazoos-ci.yml`) : une pull request du template se
teste avant que `v1` avance. La mise à jour du SDK ne s'exécute pas dans ce
dépôt, et un id d'app `com.example.*` ne se soumet pas au Store. Il ne peut
fonctionner qu'avec un sous-module sur une release qui contient ce modèle
(2.2.0 ou plus récente, ADR 0207 §11).

## Tâches

- [x] Mode source externe (`Kazo.Os.ExternalSource.targets`, `Kazo.Os.Apps.targets`).
- [x] Modèle `kazoos-solution`, CI et workflow de publication.
- [x] `ide.host.json` des modèles.
- [x] Test des deux profils (`Kazo.Os.Templates.E2E.Tests`).
- [ ] Dépôt template régénéré sur la release qui contient ce modèle.
- [x] Workflow `publish-packages.yml` et signature des paquets (ADR 0219).
- [x] Signature retirée des dépôts d'apps et du modèle (ADR 0219).
- [x] Workflows réutilisables, canal bêta, mise à jour du SDK et `bump-app-version.sh` (ADR 0271, chantier 5).
- [ ] Tag `v1` de `Os-AppTemplate` posé après la fusion dans `main` (Benoit).
