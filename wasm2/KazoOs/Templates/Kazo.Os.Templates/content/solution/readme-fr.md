> [English version](README.md)

<!-- Fiche du Store : générée depuis store/ par .github/scripts/store-readme.mjs. Modifiez store/, puis lancez-le. -->

<img src="store/icon.png" alt="" width="96" align="right">

# Kazo.Os.Template.Solution

Décrivez votre app ici : ce qu'elle fait, comment s'en servir, ce dont elle a besoin.

Ce texte est affiché sur la fiche de l'app dans le Kazo OS Store.

<!-- /Fiche du Store -->

## Essayer

[Ouvrir Kazo.Os.Template.Solution dans Kazo OS](https://os.kazo.ca/?autolaunch=com.example.template-app) : le lien démarre Kazo OS, installe l'app depuis le Store officiel et l'ouvre.

Ou ouvrez [Kazo OS](https://os.kazo.ca/) et installez Kazo.Os.Template.Solution depuis le **Kazo OS Store**, ou dans le
Terminal de Kazo OS, en administrateur : `sudo kazopkg install <propriétaire>/<dépôt>`.

## À propos de Kazo OS

[Kazo OS](https://github.com/Kazo-ca/Os) est un système d'exploitation simulé qui tourne dans le navigateur, écrit en C# avec Blazor WebAssembly. Il offre un environnement complet pour développer et exécuter des applications : bureau et fenêtres, système de fichiers persistant, processus, shell POSIX, réseau simulé, le Kazo OS Store, et un SDK public (paquets NuGet) pour créer des apps comme celle-ci. Essayez-le sur <https://os.kazo.ca/> (PWA, fonctionne hors ligne).

## Développement

Un dépôt d'app Kazo OS, créé avec le modèle `kazoos-solution`.

| Dossier | Contenu |
| --- | --- |
| `src/Kazo.Os.Template.Solution` | L'app (app fenêtrée ; sa classe `[AppManifest]` génère `manifest.json`) |
| `src/Kazo.Os.Template.Solution.Os` | Un hôte qui lance tout Kazo OS avec les apps de base et cette app : le projet à démarrer |
| `tests/Kazo.Os.Template.Solution.Tests` | Les tests de l'app |
| `external/KazoOs` | Le dépôt Kazo OS en sous-module git, sur un tag de release |
| `store/` | Ce que montre le Kazo OS Store : icône (PNG 256×256), descriptions courtes, descriptions, `store.json` |

### Premiers pas

```bash
# Un dépôt créé depuis le template GitHub a déjà le sous-module :
git clone --recurse-submodules <votre dépôt>

# Un dossier créé par « dotnet new kazoos-solution » l'ajoute une fois :
git init
git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
git -C external/KazoOs checkout v<version de Kazo OS>
```

Ouvrez ensuite le `.sln` dans Visual Studio ou Rider et démarrez `Kazo.Os.Template.Solution.Os`,
ou lancez `dotnet run --project src/Kazo.Os.Template.Solution.Os`.

### La version de Kazo OS

`external/KazoOs/wasm2/KazoOs/build/Versions.props` est la version de Kazo OS de ce dépôt.
Pour changer de version, placez le sous-module sur son tag et validez-le :

```bash
git -C external/KazoOs fetch --depth 1 origin tag v2.2.0
git -C external/KazoOs checkout v2.2.0
git add external/KazoOs
```

Ne modifiez jamais le sous-module : on ne modifie pas l'OS depuis un dépôt d'app, et la CI refuse
un sous-module modifié ou hors de son tag.

### Deux profils de build

| Profil | Kazo OS vient de | Usage |
| --- | --- | --- |
| `Debug` | les projets du sous-module (`KazoOsUseSource=true`) | entrer dans le SDK et l'OS, points d'arrêt partout |
| `Release` | les paquets `Kazo.Os.*` de nuget.org, à la version du sous-module | CI, `dotnet publish`, le Kazo OS Store |

Forcez l'un ou l'autre avec `-p:KazoOsUseSource=true` ou `false`.

### Publier dans le Kazo OS Store

1. Remplissez `store/` (icône, descriptions courtes, descriptions, captures d'écran, catégorie et
   étiquettes dans `store.json`), puis lancez `node .github/scripts/store-readme.mjs` : il copie les
   descriptions et les captures en haut de `README.md` et de `readme-fr.md`, pour que le dépôt montre
   la même fiche que le Store. La CI échoue quand un README diffère de `store/`.
2. Changez `Version` dans la classe `[AppManifest]` de l'app et validez-la dans `main`, puis posez le
   tag : lancez le workflow **Tag a release** (onglet Actions, ou l'API GitHub pour un agent), qui pose
   sur la tête de `main` le tag de la version du manifeste et lance les deux workflows de
   publication ; ou poussez le tag vous-même : `git tag v1.0.0 && git push --tags`.
3. Le workflow **Publish to the Kazo OS Store** compile l'app et produit le dossier du catalogue
   `apps/<appId>_<version>/`. Avec un secret `HACK_STORE_TOKEN` (un jeton qui peut créer un fork et
   ouvrir des pull requests), il ouvre la pull request vers `Kazo-ca/Os-AppMarketPlace` depuis
   votre fork ; sans lui, téléchargez l'artefact du workflow et ouvrez la pull request vous-même.

`HACK_STORE_TOKEN` est un jeton d'accès personnel GitHub à vous, que Kazo OS ne fournit pas :

1. GitHub ▸ **Settings** ▸ **Developer settings** ▸ **Personal access tokens** ▸ **Tokens (classic)** ▸
   **Generate new token (classic)**, avec une expiration courte et les scopes **`repo`** et
   **`workflow`** (un jeton à grain fin ne peut pas couvrir à la fois votre fork et le dépôt du catalogue).
2. Copiez-le (affiché une seule fois) dans ce dépôt : **Settings** ▸ **Secrets and variables** ▸
   **Actions** ▸ **New repository secret**, nom `HACK_STORE_TOKEN` (ou `gh secret set HACK_STORE_TOKEN`).

`repo` donne accès à tous vos dépôts : gardez le jeton dans ce seul secret et révoquez-le au moindre doute.

Le catalogue ne prend jamais de binaire de votre part : une fois votre pull request relue et
fusionnée, sa CI compile l'app au commit du tag.

### Suivre les nouvelles versions de Kazo OS

Le workflow **Kazo OS SDK update** (`sdk-update.yml`) tourne toutes les heures, quand Kazo OS
annonce une version, ou à la main depuis l'onglet Actions. Quand Kazo OS a une version plus récente,
il y déplace le sous-module, monte la version de l'app (`.github/scripts/bump-app-version.sh`) sur
une branche `kazoos-sdk/<version>` et y lance la CI :

- une préversion de Kazo OS : la branche `beta` la reçoit, en version `x.y.(z+1)-beta.<n>`, qui va
  dans le canal bêta du Kazo OS Store ;
- une version stable mineure ou majeure : une pull request vers `main`, fusionnée, taguée et publiée ;
- une version stable corrective : vérifiée seulement, rien n'est publié.

Une CI rouge ouvre une issue `TODO` et ne change rien. Une version avec un suffixe (`1.2.1-beta.1`)
se publie toujours depuis `beta`, une version sans suffixe depuis `main`. Les workflows de ce dépôt
sont courts : ils appellent les workflows partagés de
[Kazo-ca/Os-AppTemplate](https://github.com/Kazo-ca/Os-AppTemplate) (`@v1`), qui s'améliorent sans
changement ici. Sans les secrets de l'automate de Kazo OS, autorisez GitHub Actions à créer des pull
requests (**Settings** ▸ **Actions** ▸ **General**) pour que la mise à jour ouvre sa pull request.

### Publier sur la branche `kazoos-packages`

Le même tag `v*` lance aussi **Publish the Kazo OS packages** : il fait le paquet de l'app et le
pousse sur la branche `kazoos-packages` de ce dépôt, en gardant les versions déjà publiées. Les
utilisateurs de Kazo OS l'installent alors avec `kazopkg install <owner>/<repo>`, sans le Kazo OS Store.

Ces paquets ne sont pas signés : les apps sont signées seulement dans le dépôt du Kazo OS Store
(`Kazo-ca/Os-AppMarketPlace`), quand il compile votre app depuis le catalogue (ADR 0219). Kazo OS
les vérifie par leur SHA-256 et demande un administrateur avant d'installer depuis une source non
fiable. Pour un paquet signé, installez l'app depuis le Kazo OS Store.
