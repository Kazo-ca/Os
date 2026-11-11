> [English version](../../../../en/wasm/dev/adr/0250-storage-persistence-and-eviction.md)

# ADR 0250 : persistance et éviction du stockage du navigateur (`navigator.storage.persist()`, quota, avertissement)

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. Les questions posées à Benoit ont reçu leur réponse le
2026-11-11 (série 15 de la conception d'Os-x86) ; elles sont consignées à la
fin de cette ADR et la décision les applique. Décisions d'origine : 149
(images disque dans l'OPFS, petites images dans IndexedDB) et 177 A
(« `persist()` demandé après le premier geste dans BoxedWine ou `x86` ;
éviction détectée = avertissement et proposition d'export ; les disques
comptent dans l'espace du navigateur, affiché »), 2026-11-11 ; essais E2 et
E10 d'Os-x86 (OPFS 55 à 185 fois plus rapide par bloc qu'IndexedDB ; Firefox
limite à 10 Gio sans `persist()` ; Safari efface après 7 jours sans
interaction).

- **Complète :** l'[ADR 0015](0015-browser-storage-and-indexeddb-adapter.md)
  (socle du stockage ; `estimate()` et `persist()` nommés comme prérequis),
  l'[ADR 0099](0099-os-backup-and-low-storage.md) (espace faible) et
  l'[ADR 0227](0227-factory-reset-erases-everything.md) (l'OPFS est effacé à
  la réinitialisation).
- **Pour :** les disques de VM d'Os-x86 (jusqu'à plusieurs Gio dans l'OPFS,
  hors des Fichiers de Kazo qui refusent tout fichier de plus de 16 Mio,
  `FileContentStoragePolicy.cs:15`) ; et les propres données de Kazo OS
  (IndexedDB), que le navigateur peut tout autant évincer.

## Contexte

- Kazo OS lit déjà `navigator.storage.estimate()` et `persisted()`
  (`Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/storageManager.js:4-5`,
  `Storage/BrowserStorageManager.cs:33-49 GetStatusAsync`, :52-64
  `GetCapacityAsync`) à chaque sonde de capacité. `persist()` a un wrapper
  (`storageManager.js:17`, `RequestPersistenceAsync` :67-71) mais **aucun
  appelant** hors des tests ; `IsPersisted` n'est lu nulle part ; **rien ne
  détecte une éviction**.
- Le quota est **celui de l'origine entière** : `StorageSpaceMonitor.cs:123-135`
  mesure usage et quota pour tout IndexedDB, Cache Storage et OPFS ; la
  réserve de 5 % (256 Mio au plus, `StorageSpaceContracts.cs:55-70`) refuse
  les écritures des usagers non administrateurs quand elle est atteinte
  (`FileSystemService.cs:828-848`). Un disque de VM de 4 Gio dans l'OPFS
  réduit donc l'espace que Kazo croit avoir pour ses Fichiers, sans que
  Paramètres le montre.
- Les navigateurs : Chromium accorde ~60 % du disque, Firefox le plus petit
  de 10 % ou 10 Gio (50 % avec `persist()`, qui affiche une invite),
  Safari 60 % ; `persist()` n'est accepté que sur un geste de l'usager et,
  dans Chromium, selon l'engagement avec le site (PWA installée, favori) ;
  Safari peut effacer après 7 jours sans interaction ; Chrome « Memory
  Saver » et l'éviction de quota n'émettent aucun événement.
- L'OPFS appartient aujourd'hui aux Features (Kazo ne s'en sert que pour
  l'effacer, `hostFactoryReset.js:59-71`, et comme double de test,
  `hostFolder.js:1-15`) ; une Feature ne peut pas appeler
  `navigator.storage` elle-même sans JavaScript propre, que l'AGENTS.md
  limite aux API que C# n'atteint pas, et deux appels concurrents de
  `persist()` (OS et Feature) sont inutiles.

## Décision

### 1. Une API du SDK : `KazoStorage`

```csharp
public static class KazoStorage
{
    public static ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken ct = default); // usage, quota, persisted, par origine
    public static ValueTask<bool> RequestPersistenceAsync(CancellationToken ct = default);          // persist(), sur un geste
    public static event EventHandler<KazoStorageChangedEventArgs>? Changed;
}
```

- `GetEstimateAsync` : `Usage`, `Quota`, `IsPersisted`, `IsLowSpace`
  (seuils de l'ADR 0099), et la part attribuée à l'app appelante quand l'OS
  la connaît (§3). Permission : aucune (lecture seule, déjà donnée par `df`).
- `RequestPersistenceAsync` : appelle `navigator.storage.persist()` **une
  fois par origine** (l'OS garde le résultat et ne redemande pas si
  `persisted()` est déjà vrai) ; rend vrai si accordé. Réservée aux **Features
  et aux apps système** (`manifest.feature.only` sinon) ; une Feature l'appelle
  **après un geste de l'usager** (premier `x86 vm create`, premier
  téléchargement d'image), comme 177 A le demande. Un appel sans geste rend
  faux sans erreur (le navigateur refuse).
- L'OS lui-même ne demande pas la persistance au démarrage (question 1) : il
  la propose dans Paramètres ▸ Système ▸ Stockage (§3) par un bouton
  « Protéger le stockage contre l'effacement », qui est un geste.

### 2. Détection d'une éviction

- L'OS écrit à chaque démarrage réussi un **marqueur** en deux endroits :
  `localStorage` (`kazoos.storage.marker`, déjà le lieu de `kazoos.osVersion`,
  ADR 0230) et un document IndexedDB. Au démarrage suivant, si le marqueur
  de `localStorage` existe mais que la base IndexedDB est absente ou vide
  (`StorageSpaceMonitor`, avant que l'OS ouvre ses bases), l'OS déclare une
  **éviction** : écran « Le navigateur a effacé les données de Kazo OS »
  sur la page d'erreur détaillée (ADR 0240), avec la date du dernier
  démarrage et un lien vers la restauration d'une sauvegarde (ADR 0099), puis
  continue comme une première installation. `syslog` et `kern.log` reçoivent
  `storage.evicted`.
- Pour l'OPFS d'une Feature, l'OS ne sait pas ce qu'elle y gardait : la
  Feature écrit **son** marqueur (un fichier de l'OPFS listé dans un document
  IndexedDB de l'OS, `KazoStorage.RegisterMarkerAsync(path)`) et l'OS, à la
  même vérification, compare : fichier absent → événement
  `KazoStorage.Changed(Evicted, feature)` à la Feature au prochain lancement
  et **notification** à l'usager (« Les disques d'Os-x86 ont été effacés par
  le navigateur ») ; la Feature propose l'export de ce qui reste (177 A).
- `IsPersisted` faux après avoir été vrai (navigateur qui retire la
  persistance) : avertissement dans Paramètres ▸ Stockage, pas d'écran.

### 3. Paramètres ▸ Système ▸ Stockage

- Nouvelle page (ou section de « Mémoire » existante, question 2) : usage et
  quota de l'origine, état « protégé » (`persisted()`) avec le bouton du §1,
  répartition **par origine de données** : Fichiers de Kazo (IndexedDB),
  caches de l'OS, et **par Feature** l'espace de son OPFS (la Feature le
  déclare par `KazoStorage.ReportUsageAsync(bytes)`, que l'OS mémorise ;
  sans déclaration, « inconnu »). Un bouton par Feature ouvre sa page
  (ADR 0249) pour libérer de l'espace.
- La réserve de 5 % (`FileSystemService.CheckReservedSpace`) continue de
  compter l'origine entière : la page explique que les disques des
  fonctionnalités comptent dans l'espace des Fichiers. Aucun quota par
  Feature en V1 (V2 d'Os-x86).
- `df` (`IndexedDbStorageUsageProvider.cs:31-41`) ajoute une ligne par
  Feature qui a déclaré son usage (`/dev/feature/<id>`).

### 4. Ce qui ne change pas

- `estimate()` et `persisted()` restent sondés comme aujourd'hui ; la
  réinitialisation d'usine efface toujours l'OPFS (ADR 0227) ; la sauvegarde
  de Kazo (ADR 0099) n'inclut pas l'OPFS (l'export d'Os-x86 est le sien).

## Options écartées

- **A. `persist()` au démarrage de l'OS pour tout le monde** : refusé sans
  geste par les navigateurs ; dans Firefox, une invite pour chaque visiteur
  d'un OS qui n'a peut-être rien à protéger.
- **B. Laisser chaque Feature appeler `navigator.storage` en JavaScript** :
  deux demandes concurrentes, aucune vue d'ensemble dans Paramètres, et du
  JavaScript là où une API C# existe déjà (`BrowserStorageManager`).
- **C. Ne rien détecter, l'usager verra bien** : un disque de 4 Gio qui
  disparaît sans un mot est exactement le risque que 177 A refuse.
- **D. Un quota par Feature dès maintenant** : demanderait une mesure de
  l'OPFS par l'OS (parcours récursif coûteux) ; V2 d'Os-x86.

## Conséquences

- SDK : `KazoStorage` (`GetEstimateAsync`, `RequestPersistenceAsync`,
  `RegisterMarkerAsync`, `ReportUsageAsync`, `Changed`), `KazoStorageEstimate` ;
  `PublicAPI.Unshipped.txt`, fixture `sdk-compat`.
- Infrastructure : `BrowserStorageManager.RequestPersistenceAsync` enfin
  appelé ; marqueur d'éviction (`StorageEvictionDetector`, avant l'ouverture
  des bases) ; page d'erreur `storage.evicted` ; notification ;
  `kern.log`/`syslog`.
- Paramètres ▸ Système ▸ Stockage ; `df` ; docs
  [`system-administration.md`](../../framework/system-administration.md)
  (section Stockage), [`pitfalls.md`](../pitfalls.md) (éviction),
  [`building-and-testing.md`](../building-and-testing.md) (tests sous
  Firefox et Safari), `features.md`, entrée `changes/unreleased`.
- Tests : `RequestPersistenceAsync` appelle `persist()` une fois ; app
  ordinaire → exception ; marqueur présent et base absente → écran et journal ;
  marqueur de Feature absent → `Changed(Evicted)` et notification ; page de
  stockage montre l'usage déclaré ; E2E Playwright : éviction simulée par
  effacement d'IndexedDB entre deux démarrages.

## Questions posées à Benoit

1. **Qui déclenche `persist()` ?**
   - A) **(Recommandé)** Une Feature sur un geste de l'usager (177 A) et le
     bouton de Paramètres ▸ Stockage ; jamais l'OS seul au démarrage.
   - B) L'OS aussi, au premier geste de chaque session (premier clic) :
     protège les Fichiers de Kazo de tous les usagers, mais déclenche l'invite
     de Firefox chez tout le monde.
2. **Où vit l'affichage du stockage ?**
   - A) **(Recommandé)** Une page « Stockage » dans Système, à côté de
     « Mémoire ».
   - B) Une section de la page « Mémoire » existante.

Réponses du mainteneur (2026-11-11) : 1 A, 2 A.
