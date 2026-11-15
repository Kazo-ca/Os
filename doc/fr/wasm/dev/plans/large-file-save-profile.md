> [English version](../../../../en/wasm/dev/plans/large-file-save-profile.md)

# Mesure : enregistrer et ouvrir un gros fichier dans l'OS réel

Mesure faite le 2026-11-14 sur `main` (version 3.3.4) pour l'issue
[#538](https://github.com/Kazo-ca/Os/issues/538), qui suit
[#520](https://github.com/Kazo-ca/Os/issues/520) (« lire ou écrire un fichier de 50 Mo
prend environ 40 s »). **Aucun code du produit n'est changé.** Ce document dit où va le
temps, couche par couche, et ce qu'il faut corriger.

## 1. Résumé

1. **Les 40 s de l'issue #520 viennent d'un hôte compilé en Debug.** Dans la publication
   Release (celle du site et du Store), enregistrer 52,5 Mio prend **4,1 à 4,9 s** et
   l'ouvrir **3,8 à 4,1 s**. Le même parcours sur un hôte Debug prend 51 à 55 s et 48 à
   51 s. Les tests de Kazo Photo, d'où vient la mesure de #520, lancent l'hôte en Debug.
2. **Presque tout le temps est dans la couche de stockage** : 96 % en Release, 99 % en
   Debug, dans `IndexedDbFileContentRepository`. Le passage entre l'iframe et .NET et les
   copies de `KazoFile` comptent pour 0,16 s.
3. **Trois postes font ce temps** : le SHA-256 calculé en .NET, le base64, et le passage
   en JSON vers IndexedDB. Ce sont exactement ceux que retire le chantier « contenu
   binaire » de la série `storage/base`. Ce chantier règle donc #520 à lui seul.
4. **La mémoire monte d'environ 780 Mio** au premier enregistrement de 52,5 Mio et ne
   redescend pas. L'écriture garde plusieurs copies entières du fichier.
5. **`head -c N /dev/urandom` ne finit jamais**, quelle que soit la taille. C'est un
   bogue de la commande `head`, sans rapport avec le stockage.

## 2. Méthode

- Chromium sans interface, 4 cœurs, profil neuf, octets aléatoires (le pire cas, rien ne
  se compresse ni ne se déduplique).
- **L'OS réel** : la publication Release de `build/publish-trimmed.sh` (la même que le
  site), puis l'hôte `OS/Kazo.Os.Ecosystem` lancé par `dotnet run -c Debug`.
- **Le chemin d'une app Craft** : la fenêtre de test `Tests/fixtures/craft-app`, installée
  par `kazopkg`, qui utilise le module `Kazo.Os.AppSdk.Craft` comme Kazo Photo. La fausse
  app Craft de l'iframe émet un `save` de N octets ; l'ouverture passe par `xdg-open`.
- **Des chronomètres temporaires** dans chaque couche (`craft-host.js`, `CraftFrame`, la
  fenêtre, `KazoFile`, `KazoFileSystemCore`, `FileSystemService`, le fournisseur
  IndexedDB, `IndexedDbFileContentRepository`), écrits dans la console avec l'heure du
  système. Ils sont retirés : rien n'en reste dans le dépôt.
- Mémoire : somme de la mémoire résidente de tous les processus de Chromium.

Le banc (correctif des chronomètres, parcours Playwright, scripts, relevés bruts) est
gardé dans les fichiers du projet, dossier `kazofile-profile/`. Il n'est pas dans le dépôt.

## 3. Résultats

### 3.1 De bout en bout

Enregistrer : de l'événement `save` de l'app au `saved` qu'elle reçoit. Ouvrir : de la
commande `xdg-open` aux octets reçus par l'app (fenêtre et iframe comprises).

| Taille | Release, enregistrer | Release, ouvrir | Debug, enregistrer | Debug, ouvrir |
| --- | --- | --- | --- | --- |
| 5 Mio | 0,45 à 0,62 s | 0,72 à 0,76 s | 5,3 à 5,4 s | 5,6 à 5,9 s |
| 16 Mio | 1,3 à 1,7 s | 1,5 à 1,6 s | non mesuré | non mesuré |
| 52,5 Mio | 4,1 à 4,9 s (4 mesures) | 3,8 à 4,1 s (4 mesures) | 51 à 55 s | 48 à 51 s |

Le temps suit la taille : environ 80 à 90 ms par Mio en Release, 1 s par Mio en Debug.
Le Debug est environ 12 fois plus lent.

### 3.2 Enregistrer 52,5 Mio, couche par couche

| Couche | Release | Debug |
| --- | --- | --- |
| Iframe vers .NET (`craft-host.js`, `byte[]` de Blazor) | 0,08 s | 0,03 s |
| `KazoFile.WriteAllBytesAsync` : copie du tableau, `stat` | 0,08 s | 0,14 s |
| Passerelle, permissions, résolution du chemin | 0,01 s | 0,05 s |
| Découpage en blocs de 256 Kio | 0,08 s | 0,14 s |
| SHA-256 du fichier, en .NET | 1,5 s | 20 s |
| Base64 des blocs | 0,35 s | 7,6 s |
| Appel JavaScript en JSON et transaction IndexedDB (4 lots) | 2,0 s | 23 s |
| Métadonnées, événement de changement, retour à l'app | 0,01 s | 0,02 s |
| **Total** | **4,1 s** | **51 s** |

### 3.3 Ouvrir 52,5 Mio, couche par couche

| Couche | Release | Debug |
| --- | --- | --- |
| Lancer la fenêtre, charger l'iframe | 0,35 s | 0,7 s |
| Appel JavaScript, IndexedDB et lecture du JSON (4 lots) | 0,95 s | 13 s |
| Décodage base64 et tampon | 0,69 s | 17 s |
| SHA-256 de vérification, en .NET | 1,6 s | 20 s |
| Copies de `KazoFile.ReadAllBytesAsync` | 0,12 s | 0,09 s |
| .NET vers l'iframe (`open`) | 0,08 s | 0,12 s |
| **Total** | **3,8 s** | **51 s** |

### 3.4 Mémoire

Release, mémoire de tous les processus de Chromium, avec une fenêtre Craft ouverte :

| Moment | Mémoire |
| --- | --- |
| Avant | 1 930 Mio |
| Pic du premier enregistrement de 52,5 Mio | 2 708 Mio |
| Pic du deuxième | 2 833 Mio |
| Pic de la deuxième ouverture | 3 061 Mio |

Le premier enregistrement ajoute environ 780 Mio, soit 15 fois le fichier. La mémoire
WebAssembly ne se rend pas au système : ce qui est monté reste. Les copies entières vues
dans le code pendant une écriture :

- le `byte[]` reçu de l'iframe ;
- la copie de `KazoFile.WriteAllBytesAsync` (`bytes.Clone()`) ;
- la liste de tous les blocs (`IndexedDbFileContentRepository.WriteAsync`) ;
- le tampon d'`IncrementalHash` : le temps du hachage tombe en entier dans l'appel final,
  ce qui indique que les octets sont gardés jusque-là ;
- par lot de 16 Mio : les chaînes base64 (2 octets par caractère en .NET, donc 43 Mio),
  leur JSON, puis les mêmes chaînes côté JavaScript.

### 3.5 `/proc/diskstats`

L'issue #538 proposait de comparer le temps total au temps des transactions de
`/proc/diskstats`. Ce compteur ne sert pas de chronomètre de couche : son temps inclut la
mise en JSON de l'appel, et il additionne les transactions en cours en même temps. Une
ouverture de 3,8 s y compte 5,3 s de lecture. Les chronomètres du §3.2 et du §3.3
remplacent cette comparaison.

### 3.6 `head -c 1048576 /dev/urandom > a.bin`

`head -c 16 /dev/urandom` ne finit pas non plus : après 45 s, rien n'est écrit et la
mémoire a monté de 800 Mio. `head -c 16 /etc/hostname` finit en 0,5 s.

Cause : `HeadCommand` lit le fichier **en entier** (`CommandIO.ReadTextAsync`, donc
`ReadToEndAsync`), puis coupe le texte. `/dev/urandom`, `/dev/random` et `/dev/zero` sont
sans fin : la lecture ne s'arrête jamais et la mémoire monte jusqu'à l'arrêt de l'onglet.
La documentation de `DevFileSystemProvider` donne pourtant `head -c 16 /dev/urandom` en
exemple. Deux autres limites du même code : `-c` compte des caractères et non des octets,
et la sortie passe par du texte, donc elle ne produit pas un fichier binaire fidèle.

Ce bogue est propre au terminal. Il ne touche ni `KazoFile` ni IndexedDB.

## 4. Ce que ça change

- **L'issue #520 décrit un coût de développement, pas de production.** Une app du Store
  tourne dans la publication Release : environ 4 s pour 50 Mo, plus le travail de l'app
  elle-même (2,8 s pour PhotoCraft). En Debug, chaque développeur d'app Craft attend
  50 s : c'est encore à corriger, pour les tests et le confort.
- **L'analyse du stockage disait que 90 % du temps était ailleurs.** Elle comparait son
  banc Release (3,4 s) à la mesure Debug de #520 (43 s). Mesuré dans la même
  configuration, le stockage fait presque tout le temps. La phrase est à corriger dans
  l'analyse ([PR #536](https://github.com/Kazo-ca/Os/pull/536), §1 point 4 et §3.4), dans
  l'ADR 0272 et dans le plan de la série ([PR #539](https://github.com/Kazo-ca/Os/pull/539)).
- **Rien à changer dans `craft-host.js`, `CraftFrame` ni `KazoFile` pour la vitesse** :
  0,16 s sur 4,1 s.

## 5. Propositions

1. **Lancer le chantier « contenu binaire » de `storage/base` (ADR 0272) comme correctif
   de #520.** Il retire le SHA-256 géré (WebCrypto : 0,09 s), le base64 et le JSON
   (octets passés par `[JSImport]` et `MemoryView`). L'analyse a mesuré la couche de
   stockage à 0,40 s en écriture et 0,36 s en lecture avec ce chemin. Estimation de bout
   en bout pour 52,5 Mio en Release : environ 0,6 s au lieu de 4,1 s. Le gain en Debug
   devrait être plus grand encore, parce que le travail passe de l'interpréteur .NET au
   navigateur ; à mesurer quand le chantier est fait.
2. **Garder le chantier « écriture en flux »** (ADR 0275 §4). Le pic de mémoire est
   confirmé. Le chantier 1 enlève déjà les copies base64 et JSON ; il reste le `Clone` de
   `KazoFile`, la liste des blocs et le tampon du hachage.
3. **Mesurer les performances sur la publication Release.** Une mesure faite sur un hôte
   Debug est environ 12 fois trop pessimiste pour tout ce qui calcule en .NET. Les
   parcours des dépôts d'apps (`tests/e2e/host.mjs`) lancent l'hôte en Debug par défaut :
   pour un chiffre de performance, passer la configuration Release ou un OS publié. Le
   piège est ajouté à [`../pitfalls.md`](../pitfalls.md).
4. **Corriger `head` sur un fichier sans fin** dans une issue séparée : lire au plus N
   octets du flux pour `-c N`, et s'arrêter après N lignes pour `-n N` sur un fichier,
   comme c'est déjà fait pour l'entrée standard. Question posée à Benoit.

## 6. Limites

- Une seule machine, un seul navigateur (Chromium sans interface). Les proportions
  comptent plus que les valeurs absolues.
- La fausse app Craft ne fait aucun travail sur les octets. Le temps d'une vraie app
  (lire ou produire un `.psd`) s'ajoute.
- La mesure de #520 n'a pas été refaite avec Kazo Photo lui-même : son script n'est pas
  versionné. La cause s'appuie sur deux faits : son harnais lance l'hôte en Debug par
  défaut, et le même parcours sur l'hôte Debug d'Os donne des durées du même ordre.
- L'estimation du point 1 du §5 additionne deux mesures différentes (ce document et le
  banc de l'analyse) ; ce n'est pas une mesure.

## 7. Liens

- Issues [#538](https://github.com/Kazo-ca/Os/issues/538),
  [#520](https://github.com/Kazo-ca/Os/issues/520),
  [#379](https://github.com/Kazo-ca/Os/issues/379)
- Analyse du stockage : [PR #536](https://github.com/Kazo-ca/Os/pull/536) ; plan de la
  série `storage/base` et ADR 0272 à 0275 : [PR #539](https://github.com/Kazo-ca/Os/pull/539)
- ADR [0245](../adr/0245-raise-the-maximum-file-size.md) (taille maximale d'un fichier,
  lots de 16 Mio), [0259](../adr/0259-shared-craft-app-host-module.md) (module des apps
  Craft)
- [`../browser-infrastructure.md`](../browser-infrastructure.md),
  [`../pitfalls.md`](../pitfalls.md)

## Tâches de ce document

- [x] Mesurer un enregistrement et une ouverture de 50 Mo dans l'OS réel, couche par couche.
- [x] Comparer avec `/proc/diskstats`.
- [x] Mesurer la mémoire.
- [x] Trouver pourquoi `head -c … /dev/urandom` ne finit pas.
- [x] Proposer le correctif.
- [ ] Refaire la mesure après le chantier « contenu binaire ».
