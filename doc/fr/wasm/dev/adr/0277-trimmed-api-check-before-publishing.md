> [English version](../../../../en/wasm/dev/adr/0277-trimmed-api-check-before-publishing.md)

# ADR 0277 : vérifier les API de l'OS rogné avant de publier une app

## Statut

Proposée le 2026-11-16, série `todo-wins/base`, pour l'issue
[#346](https://github.com/Kazo-ca/Os/issues/346) (`TODO`). La question Q1 a
été posée à Benoit dans le fil le 2026-11-16. En attendant sa réponse, la
décision applique la recommandation (A).

- **Complète :** l'[ADR 0182](0182-install-apps-from-github.md) §5.3 (la
  vérification des API à l'installation) et l'[ADR 0242](0242-framework-api-for-installed-apps.md)
  (signatures exactes, `System.Private.CoreLib` entière). Le même contrôle
  tourne maintenant **avant** la publication.
- **Réalise :** l'étape 5 de l'[ADR 0188](0188-hack-store-and-app-catalog.md) §5
  (« contrôle des API contre le runtime élagué ») dans la CI du catalogue.
- **S'appuie sur :** l'[ADR 0113](0113-trimming-implementation-adjustments.md)
  (publication élaguée) et l'[ADR 0261](0261-app-libraries-and-shared-versions.md)
  (bibliothèques de l'app dans `lib/`, vérifiées comme l'assemblage principal).

## Contexte

- La publication Release de l'OS élague le framework .NET (ADR 0113). Une app
  installée après la publication ne trouve que ce que l'OS a gardé.
- `AppPackageApiCheck` (ADR 0182 §5.3, signatures exactes depuis
  l'ADR 0242) refuse une telle app **à l'installation** :
  `kazopkg: <appId> uses APIs this OS does not have`.
- L'auteur ne le voit nulle part avant : sa compilation, ses tests et ses tests
  de bout en bout tournent sur le runtime complet, ou dans un hôte qui garde ce
  que l'app appelle. `KazoOsPack` ne vérifie rien, et la CI du catalogue
  (`validate.yml` d'Os-AppMarketPlace) compile et analyse le paquet mais ne
  lance pas ce contrôle.
- Cas réels : une app tierce refusée par `kazopkg install` pour
  `string.Create(int, TState, SpanAction)` et `Enumerable.ToHashSet`
  (2026-11-01) ; IRC et Webamp, qui passaient l'ancien contrôle et échouaient
  au premier appel (ADR 0242). Ce sont des API courantes, appelées sans y
  penser.

## Décision

### 1. La surface d'API de chaque version de l'OS est publiée

- `publish-trimmed.sh`, et la publication d'une version
  ([ADR 0173](0173-automatic-releases-from-the-version-file.md)), écrivent
  `kazoos-api-surface.json` : pour chaque assemblage du framework que garde la
  publication, ses types et, pour chaque type, ses méthodes (nom, arité
  générique, types exacts des paramètres sous la forme du décodeur de
  métadonnées de l'ADR 0242) et ses champs.
- Le fichier est lu dans les assemblages **après ILLink et avant Webcil**
  (`obj/Release/net10.0/linked/`), avec `System.Reflection.Metadata`. On ne lit
  pas les `.wasm` de `_framework`.
- `Kazo.Os.App.Build` emporte la surface de sa propre version
  (`build/api-surface/kazoos-api-surface.json`, compressé). Un auteur qui
  compile contre le SDK `X.Y.Z` a donc la surface de l'OS `X.Y.Z`, sans
  téléchargement.
- Les assemblages du SDK et de la plateforme (`Kazo.Os.*`) ne sont pas dans la
  surface : ils ne sont pas élagués pour les apps
  ([ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)) et leurs API
  sont déjà suivies par `PublicAPI.Shipped.txt` et la fixture `sdk-compat`.

### 2. `KazoOsPack` vérifie le paquet qu'il vient d'écrire

- Une classe `ApiSurfaceFile : IRuntimeApiSurface` lit le fichier. Le contrôle
  est **le même code** que celui de l'installation (`AppPackageApiCheck`) :
  un seul jugement, au build comme dans l'OS.
- Après l'écriture du `.kazoapp`, `KazoOsPack` contrôle chaque assemblage du
  paquet (points d'entrée, composants, bibliothèques de `lib/`, ADR 0261).
  Un membre absent donne l'erreur `KOSAPP0100`, une ligne par membre, par
  exemple :
  `Enumerable.ToHashSet<TSource>(IEnumerable<TSource>) in System.Linq is not in Kazo OS 3.4.1 (trimmed)`.
- Pour les manques fréquents, le message propose un remplacement, tiré d'une
  table courte et testée (par exemple `ToHashSet()` → `new HashSet<T>(source)`).
- `KazoOsApiCheck=false` désactive le contrôle : l'erreur devient
  l'avertissement `KOSAPP0101`, qui dit que le paquet risque d'être refusé à
  l'installation.
- Le modèle d'app (`Os-AppTemplate`, [ADR 0176](0176-app-repository-template.md))
  lance `KazoOsPack` en Release dans sa CI : il hérite du contrôle sans
  changement de workflow.

### 3. Le catalogue refait le contrôle

- `validate.yml` d'Os-AppMarketPlace ajoute l'étape 5 de l'ADR 0188 §5 :
  l'outil du catalogue lance le même contrôle sur le paquet construit, contre
  la surface de la **dernière version stable** de l'OS (lue dans le paquet
  NuGet `Kazo.Os.App.Build` de cette version).
- Un échec bloque la PR de soumission, et la revue (`pr-comment.yml`) liste les
  membres manquants. C'est la seconde barrière, pour un paquet qui ne passe pas
  par `KazoOsPack`.

### 4. Ce qui ne change pas

- La vérification à l'installation (`kazopkg`, Store) reste : c'est elle qui
  fait foi sur l'OS réellement chargé, qui peut être plus récent que celui du
  build.
- Un OS plus récent peut garder moins de membres qu'un plus ancien. Ce cas
  reste couvert par l'installation, et par la CI des apps quand le SDK change
  ([ADR 0271](0271-beta-channel-and-automatic-app-updates.md)).

## Questions posées à Benoit

Posée dans le fil le 2026-11-16.

**Q1. Où vérifier qu'une app n'appelle pas d'API retirée par l'OS rogné ?**

- **A (recommandé) :** au packaging (`KazoOsPack`) et dans la CI du Store.
  L'erreur arrive chez l'auteur, à son build, avant toute publication ; le
  Store reste une seconde barrière.
- **B :** seulement dans la CI du Store. Moins de travail, mais l'auteur ne
  l'apprend qu'en soumettant son app.
- **C :** une commande à lancer à la main. Rien n'empêche de publier une app
  qui ne s'installera pas.

## Conséquences

- Une app qui appelle une API retirée échoue à son build Release, avec la
  liste des membres et un remplacement quand on en connaît un.
- `Kazo.Os.App.Build` grossit de la surface compressée (taille à mesurer dans
  le chantier 1).
- Le contrôle n'ajoute aucune dépendance au serveur ni au réseau.

## Chantiers

1. `todo-wins/7-api-surface-file` : génération de `kazoos-api-surface.json`
   dans `publish-trimmed.sh` et la publication d'une version, `ApiSurfaceFile`,
   tests (une surface écrite puis relue, un membre absent trouvé).
2. `todo-wins/8-pack-api-check` : contrôle dans `KazoOsPack`, `KOSAPP0100`,
   `KOSAPP0101`, table des remplacements, fixture qui appelle une API rognée,
   docs `app-installation.md`, `framework/app-repository.md` et le guide du SDK.
3. Os-AppMarketPlace : étape 5 de `validate.yml` (PR dans ce dépôt).
