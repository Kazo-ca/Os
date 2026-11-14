> [English version](../../../en/wasm/dev/releases.md)

# Publier une version de Kazo OS

Une nouvelle version se publie en changeant **un seul fichier** :
`wasm2/KazoOs/build/Versions.props`. Une fois la modification fusionnée dans
`main`, GitHub Actions crée le tag, la GitHub Release et pousse les paquets sur
nuget.org ([ADR 0173](adr/0173-automatic-releases-from-the-version-file.md)).
Une préversion se publie de la même façon depuis la branche permanente `beta`
([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md), voir
[Publier une préversion](#publier-une-préversion-canal-bêta)).

## Le fichier de version

```xml
<KazoOsVersion>2.2.2</KazoOsVersion>
```

`Directory.Build.props` l'importe : chaque assembly, chacun des 42 paquets
`Kazo.Os.*`, les modèles `dotnet new`, les manifestes générés
(`sdkCompatibility.minimumVersion`) et `KazoOsSdk.Version` (affichée dans
Paramètres ▸ À propos et sur l'écran d'accueil) en découlent. Aucun code ni
test ne répète le numéro.

Choisir le numéro selon SemVer
([`sdk-packages.md`](../framework/sdk-packages.md#version-unique)) :

| Changement | Exemple |
| --- | --- |
| Correctif, aucune API publique ajoutée | `2.1.0` → `2.1.1` |
| Ajout dans l'API publique (`PublicAPI.Unshipped.txt` non vide) | `2.1.1` → `2.2.0` |
| Rupture du SDK (`*REMOVED*` dans un paquet du SDK) | `2.2.0` → `3.0.0` |
| Préversion (Release marquée « pre-release »), **seulement sur `beta`** | `3.4.0-preview.1` |

Chaque branche a son canal, vérifié par `build/tools/check-release-channel.sh`
dans *Verify Kazo OS WASM* (push et PR vers la branche) et dans *Release Kazo
OS* : `main` refuse une version avec suffixe, `beta` refuse une version sans
suffixe. Les autres branches (`<série>/base`, chantiers) acceptent les deux.

## Publier, pas à pas

1. Créer une branche depuis `main`.
2. Pour une mineure ou une majeure, verser l'API publique :
   `build/tools/ship-public-api.sh` (`--check` pour voir sans rien changer).
3. Changer `KazoOsVersion` dans `build/Versions.props`.
   Dans la même PR, regrouper les entrées de `changes/unreleased/` dans
   `changes/<version>.json` ([`change-list.md`](change-list.md), ADR 0186).
4. (Conseillé) compiler une fois (`dotnet build Kazo.Os.sln -c Release`) et
   committer les manifestes réécrits ; le build les régénère de toute façon.
5. Ouvrir la PR vers `main`, attendre la vérification verte, fusionner.
6. Suivre l'onglet **Actions** : *Verify Kazo OS WASM* tourne sur `main`, puis
   *Release Kazo OS*. La Release apparaît sous **Releases** avec le tag
   `v<version>`.

## Ce que fait le workflow `Release Kazo OS`

Fichier : `.github/workflows/release.yml`. Il démarre quand *Verify Kazo OS
WASM* se termine **avec succès** après un push sur `main` ou sur `beta`, puis :

1. lit `KazoOsVersion` du commit vérifié et la refuse si elle ne correspond
   pas au canal de la branche (`check-release-channel.sh`) ; si le tag
   `v<version>` existe déjà, il s'arrête (rien à publier) ;
2. empaquette avec `build/pack-local.sh --ci` ;
3. garde uniquement les `Kazo.Os.*.<version>.nupkg` et `.snupkg` ;
4. publie la PWA (`dotnet publish`) et la zippe en `kazoos-wasm-<version>.zip` ;
5. pousse les paquets sur nuget.org si c'est configuré (voir plus bas) ;
6. crée le tag et la GitHub Release : titre `Kazo OS <version>`, notes tirées
   de `changes/<version>.json` (ou de `changes/unreleased/` pour une
   préversion), paquets et zip joints ; une préversion est marquée
   « pre-release », une version stable « latest ».

Le tag est créé en dernier : si une étape échoue, relancer le workflow
(*Re-run failed jobs*) reprend tout, et les paquets déjà présents sur nuget.org
sont sautés (`--skip-duplicate`).

### Lancer à la main

Onglet **Actions** ▸ *Release Kazo OS* ▸ *Run workflow* (branche `main` ou
`beta`) :

- sans option : publie la version de la branche si elle n'est pas encore
  taguée ;
- avec **republish** : ré-empaquette la version déjà taguée et la pousse sur
  nuget.org. Sert quand une Release a été créée avant la configuration de
  NuGet.

## Publier une préversion (canal bêta)

La branche permanente `beta` publie le SDK en préversion comme `main` publie
la version stable ([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §1-2).
Sa version porte toujours un suffixe : `<prochaine version>-preview.<n>`.

1. Fusionner dans `beta` les séries à essayer (PR vertes ; un agent peut
   fusionner dans `beta`, jamais dans `main`).
2. Dans une PR vers `beta`, changer `<n>` dans `build/Versions.props`
   (`3.4.0-preview.1` → `3.4.0-preview.2`). **Ne pas** lancer
   `ship-public-api.sh` ni `collect-changes.sh` : l'API publique reste dans
   `PublicAPI.Unshipped.txt` et les entrées dans `changes/unreleased/`
   jusqu'à la version stable.
3. Après la fusion, *Verify Kazo OS WASM* tourne sur `beta`, puis *Release
   Kazo OS* : paquets `Kazo.Os.*` poussés sur nuget.org en préversion, tag
   `v3.4.0-preview.2`, GitHub Release « pre-release » avec le zip du site,
   image `ghcr.io/kazo-ca/kazoos-server:3.4.0-preview.2` (sans `:latest`).
4. Les notes de la Release reprennent toutes les entrées de
   `changes/unreleased/` : elles sont cumulatives d'une préversion à l'autre.

Un push sur `beta` qui ne change pas la version ne publie rien (le tag existe
déjà). Le site bêta (`os-beta.kazo.ca`) suivra chaque push de `beta`
(chantier 2 de la série `release-channels`).

**Passer en stable.** Une PR `beta` → `main` retire le suffixe, verse l'API
publique et regroupe `changes/unreleased/` comme une version ordinaire (voir
[Publier, pas à pas](#publier-pas-à-pas)) ; Benoit seul la fusionne. Après
chaque version stable, `main` est fusionnée dans `beta` et la version de
`beta` passe à la préversion suivante.

**Coût.** Une préversion pousse les 42 paquets sur nuget.org, où une version
ne se supprime pas (elle se masque seulement) : publier une préversion quand
il y a quelque chose à essayer, pas à chaque fusion.

## Annonce aux sessions ouvertes

Le site publié contient `/version.json` (`{ "product": "Kazo OS", "version": "2.1.0" }`),
écrit par le build depuis `build/Versions.props`. Chaque session ouverte le relit
toutes les 5 minutes ; quand le site sert une autre version, un bandeau dit
« Kazo OS <version> est disponible » avec « Redémarrer pour mettre à jour »
([ADR 0174](adr/0174-announce-a-new-version-to-open-sessions.md)).

### Numéro de build

Les workflows « Deploy to GitHub Pages » et « Release Kazo OS » calculent un
numéro de build avec `build/build-number.sh` (nombre de commits de `HEAD`, clone
complet obligatoire) et le passent à MSBuild par la variable d'environnement
`KazoOsBuildNumber`. Il est écrit dans l'assemblage de `OS/Kazo.Os.Ecosystem` et
dans `version.json` (`"build": 1234`). L'écran de connexion affiche
« Kazo OS 3.2.1 · build 1234 » et une pastille « À jour », « Obsolète : … » ou
« État inconnu (hors ligne) », vérifiée dès le démarrage ; une même version avec
un autre numéro est aussi une mise à jour. Une build locale dit « build dev »
([ADR 0236](adr/0236-build-number-and-version-status-at-sign-in.md)). Pour
reproduire la build de la CI en local :
`KazoOsBuildNumber="$(build/build-number.sh)" dotnet publish OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj -c Release`.

## Configurer la publication sur nuget.org

Deux possibilités ; la première est recommandée (aucune clé à stocker ni à
renouveler).

### Option A : Trusted Publishing (recommandé)

1. Se connecter à [nuget.org](https://www.nuget.org) avec le compte qui
   possédera les paquets.
2. Menu du compte (en haut à droite) ▸ **Trusted Publishing** ▸ **Create**
   (ou *Add new trusted publishing policy*).
3. Remplir :
   - *Policy name* : `Kazo OS release` ;
   - *Package owner* : votre compte (ou l'organisation nuget.org qui
     possédera les paquets) ;
   - *Publisher* : **GitHub Actions** ;
   - *Repository owner* : `Kazo-ca` ;
   - *Repository* : `Os` ;
   - *Workflow file* : `release.yml` (le nom seul, sans `.github/workflows/`) ;
   - *Environment* : laisser vide.
4. Dans GitHub : dépôt `Kazo-ca/Os` ▸ **Settings** ▸ **Secrets and
   variables** ▸ **Actions** ▸ onglet **Variables** ▸ **New repository
   variable** : nom `NUGET_USER`, valeur = votre **nom d'utilisateur**
   nuget.org (le nom du profil, pas l'adresse courriel).

Le workflow appelle `NuGet/login@v1`, qui échange le jeton OIDC de GitHub
contre une clé valable une heure.

### Option B : clé d'API

1. nuget.org ▸ menu du compte ▸ **API Keys** ▸ **Create**.
2. *Key name* : `Kazo OS GitHub Actions` ; *Expires in* : 365 jours ;
   *Select scopes* : **Push** ▸ *Push new packages and package versions* ;
   *Glob pattern* : `Kazo.Os.*`. Créer, puis **Copy**.
3. GitHub ▸ **Settings** ▸ **Secrets and variables** ▸ **Actions** ▸ onglet
   **Secrets** ▸ **New repository secret** : nom `NUGET_API_KEY`, valeur = la
   clé copiée.
4. Noter la date d'expiration : une clé expirée fait échouer l'envoi.

Si `NUGET_USER` existe, le workflow prend l'option A ; sinon `NUGET_API_KEY` ;
sans les deux, il publie la Release GitHub seule avec l'avertissement « nuget.org
publishing is not configured ».

### Facultatif : réserver le préfixe `Kazo.Os.`

Une réservation de préfixe affiche le badge « vérifié » et empêche un tiers de
publier un paquet `Kazo.Os.*`. Elle se demande par courriel à
`account@nuget.org` (voir la page *ID prefix reservation* de la documentation
NuGet), après la première publication.

## Vérifier une publication

- **Releases** du dépôt : la Release `Kazo OS <version>`, ses `.nupkg`,
  `.snupkg` et `kazoos-wasm-<version>.zip`.
- Résumé du job *Release Kazo OS* : la liste des paquets et la ligne
  « NuGet: packages pushed to nuget.org » (ou « not configured »).
- nuget.org : un paquet récent est indexé en quelques minutes
  (`https://www.nuget.org/packages/Kazo.Os.AppSdk`).

## Dépannage

| Symptôme | Cause et solution |
| --- | --- |
| *Release Kazo OS* ne démarre pas | *Verify Kazo OS WASM* a échoué ou a été annulé sur ce commit ; corriger, ou attendre le commit suivant de `main` (même version, pas encore taguée). |
| « already released » dans le résumé | Le tag `v<version>` existe : changer la version. |
| Erreur « Release channel » | La version ne correspond pas à la branche : `main` veut `X.Y.Z`, `beta` veut `X.Y.Z-preview.<n>`. Corriger `build/Versions.props` dans la PR. |
| `403` à l'envoi NuGet | Clé expirée, mauvaise portée (glob), ou politique Trusted Publishing qui ne correspond pas au dépôt ou au fichier `release.yml`. |
| `NuGet/login` échoue | `NUGET_USER` ne correspond pas au nom du profil nuget.org, ou la politique n'existe pas. |

Voir aussi [`ci-workflows.md`](ci-workflows.md) et
[`../framework/sdk-packages.md`](../framework/sdk-packages.md).
