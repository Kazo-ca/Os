> [English version](../../../../en/wasm/dev/adr/0275-partial-read-and-file-address.md)

# ADR 0275 : lecture partielle et adresse de fichier pour les apps

## Statut

Proposée le 2026-11-15, série `storage/base`
([plan](../plans/browser-storage-series-plan.md)). C'est l'ADR que l'issue
[#379](https://github.com/Kazo-ca/Os/issues/379) demande (décision de Benoit
du 2026-11-06 : une API de l'OS pour plus tard), et la piste 4 puis 5 de
l'analyse du stockage, retenue par Benoit le 2026-11-14. Le §4 (écriture en
flux) est confirmé par le profilage de l'issue
[#538](https://github.com/Kazo-ca/Os/issues/538), rendu le 2026-11-15. Aucune question
ouverte : les noms de l'API sont une proposition à revoir dans la PR.

- **Complète :** l'[ADR 0245](0245-raise-the-maximum-file-size.md) (dont les
  conséquences annoncent cette lecture partielle) et
  l'[ADR 0070](0070-worker-hosted-processes.md) (lecture par blocs depuis un
  worker).
- **S'appuie sur :** l'[ADR 0272](0272-binary-file-content.md) §4 (un bloc se
  vérifie seul), l'[ADR 0274](0274-single-copy-of-app-files.md) §5 (lecteur de
  blocs du service worker) et l'[ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  (dossier de l'ordinateur monté comme lecteur réseau).
- **Analyse :** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  constat C6, pistes 4 et 5.

## Contexte

- Lire un fichier le charge en entier : le dépôt de contenu rend un
  `MemoryStream` complet et `KazoFile` fait ses propres copies. Écrire garde la
  liste de tous les blocs. L'issue #520 note une mémoire qui passe de 1391 à
  2588 Mio pour un fichier de 52 Mo.
- Une app qui joue un son ou une vidéo doit lire le fichier en entier par
  `KazoFile.ReadAllBytesAsync`, puis en faire un `Blob`. Pour une liste de
  lecture, tous les morceaux restent en mémoire. Avancer dans un morceau
  demande tout le fichier.
- Un fichier d'un lecteur réseau (ADR 0181) passe par la mémoire .NET alors que
  le navigateur sait le lire sans copie (`FileSystemFileHandle.getFile()`).
- Le service worker sait déjà répondre à une adresse sous la base de la page
  (ADR 0206, 0243), et l'ADR 0274 lui apprend à lire les blocs de `fsContent`.

## Décision

### 1. Lecture d'une plage dans le dépôt de contenu

Le dépôt de contenu lit une plage `[début, longueur]` d'un contenu : il ne
charge que les blocs qui la couvrent, par lots, et vérifie chaque bloc par son
hachage (ADR 0272 §4).

### 2. Un flux de lecture qui charge à la demande

`KazoFile.OpenReadAsync` rend, pour un fichier de plus de 16 Mio (un lot), un
flux qui charge les blocs quand on les lit et n'en garde qu'une fenêtre en
mémoire. Il reste positionnable (`Seek`). En dessous de 16 Mio, rien ne
change : le fichier est lu d'un coup. `ReadAllBytesAsync` rend toujours tout
le fichier ; il fait une copie de moins.

L'API publique ne change pas pour ce point. Depuis un worker, la lecture passe
déjà par blocs (ADR 0070).

### 3. Adresse de fichier

Une app demande à l'OS une adresse pour un fichier qu'elle a le droit de lire,
et la donne au navigateur (`<audio>`, `<video>`, `<img>`, `fetch`).

```csharp
public static class KazoFile
{
    /// Adresse du fichier, valable tant que l'objet rendu n'est pas libéré.
    public static ValueTask<KazoFileUrl> CreateUrlAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class KazoFileUrl : IAsyncDisposable
{
    public string Url { get; }   // par exemple _kazo/files/<jeton>/morceau.mp3
}
```

- **Droits.** `CreateUrlAsync` vérifie la lecture du chemin avec les
  permissions et les capacités ordinaires de l'app. Aucune capacité nouvelle :
  l'app pouvait déjà lire ces octets.
- **Adresse.** `_kazo/files/<jeton>/<nom>` sous la base de la page. Le jeton
  est aléatoire (128 bits) et n'est pas devinable ; le nom du fichier suit pour
  que le navigateur voie l'extension.
- **Résolution par la page.** Le service worker ne garde pas de table : pour
  une requête, il demande à la page de l'OS ce que vaut le jeton
  (`postMessage`). La page répond d'après l'état **courant** du fichier, en
  revérifiant les droits :
  - disque `/` : `{contentHash, length, contentType}` ; le worker sert les
    blocs par son lecteur (ADR 0274 §5), avec `Range` ;
  - lecteur réseau : le `File` rendu par `getFile()`, que le worker sert par
    `slice`, sans copie ;
  - autre fournisseur sans lecture partielle : un `Blob` que la page a lu en
    entier.

  Un jeton inconnu, libéré, ou dont l'app n'a plus le droit de lire le fichier
  reçoit `404`. Un worker redémarré par le navigateur redemande simplement.
- **Fichier modifié pendant la lecture.** La réponse porte un `ETag` égal au
  `contentHash`. Une requête `Range` avec `If-Range` d'un contenu remplacé
  reçoit le nouveau fichier en entier (`200`), comme le veut HTTP. Une réponse
  en cours continue sur l'ancien contenu, que la rétention des orphelins garde
  au moins 5 minutes ([ADR 0273](0273-short-orphan-content-retention.md)).
- **Durée de vie.** L'adresse vaut jusqu'à `DisposeAsync`. Toutes les adresses
  d'une instance d'app sont libérées quand elle se termine. Rien n'est écrit
  dans la base : une adresse ne survit pas à la page.
- **Apps en Web Worker.** `CreateUrlAsync` passe par le fil principal, comme
  le service de fichiers ; l'adresse s'utilise ensuite depuis le worker.
- **Isolation.** Les réponses portent `Cross-Origin-Resource-Policy:
  same-origin` et les en-têtes d'isolation que le worker met déjà.
- **Sans service worker** qui contrôle la page, `CreateUrlAsync` rend une
  adresse `blob:` après avoir lu le fichier en entier : l'app n'a pas deux
  chemins à écrire.

### 4. Écriture en flux

La clé d'un contenu est le SHA-256 du fichier entier, connu seulement à la
fin : on ne peut pas écrire les blocs à leur clé au fil de l'eau. Direction
retenue : les octets d'une écriture s'accumulent **du côté du navigateur**
(un `Blob` construit lot par lot), hors de la mémoire .NET ; à la fermeture,
le navigateur hache le tout et écrit les blocs. La mémoire .NET ne porte alors
qu'un lot à la fois.

Le profilage de l'issue #538 le justifie : le premier enregistrement de
52,5 Mio fait monter la mémoire de Chromium d'environ 780 Mio, sans
redescendre. Les copies en cause sont le tableau d'octets de l'interop, la
copie de `KazoFile`, la liste des blocs, le tampon du hachage, le base64 de
chaque lot et le JSON du lot. L'ADR 0272 enlève déjà les trois dernières ;
cette écriture en flux enlève les autres. Le passage de l'iframe à .NET et
`KazoFile` ne coûtent que 0,16 s : ils ne changent pas pour la vitesse.

## Conséquences

- Lire le début d'un gros fichier, ou avancer dans un morceau, ne charge plus
  le fichier entier.
- Webamp, et toute app de son, de vidéo ou d'images lourdes, peut jouer un
  fichier sans le copier en mémoire. Une issue sera ouverte dans
  Os-App-Webamp quand l'API existe.
- Les apps Craft (iframe) peuvent recevoir une adresse au lieu des octets pour
  ouvrir un gros document ; ce changement du module `Kazo.Os.AppSdk.Craft`
  n'est pas dans cette ADR.
- Ajouts à l'API publique : `KazoFile.CreateUrlAsync`, `KazoFileUrl`
  (`PublicAPI.Unshipped.txt`, donc une version mineure du SDK).
- Les lecteurs cloud (ADR 0267, série `cloud-drives/base`) passent d'abord par
  le cas « autre fournisseur » ; une lecture partielle par leur API pourra
  venir ensuite.
- Une adresse donne accès au fichier à tout code de la même origine qui
  connaît le jeton, tant qu'elle n'est pas libérée.

## Options écartées

- **Une adresse `blob:` seulement** : simple, mais le fichier est lu en
  entier en mémoire, ce que l'issue #379 veut éviter.
- **Une table des adresses dans IndexedDB**, lue par le worker : survivrait à
  la page, mais demanderait de nettoyer les adresses des sessions mortes et de
  revérifier les droits ailleurs que dans l'OS.
- **Figer le contenu à la création de l'adresse** : il faudrait épingler le
  contenu contre la passe de nettoyage ; `ETag` et `If-Range` donnent le bon
  comportement sans cela.
- **OPFS** pour la lecture partielle (0,1 ms) : voir l'ADR 0272, options
  écartées.

## Tâches

- [ ] Lecture d'une plage dans `IndexedDbFileContentRepository`, tests.
- [ ] Flux de lecture à la demande pour les fichiers de plus de 16 Mio ; mesure
  de la mémoire avant et après.
- [ ] Route `_kazo/files/` du service worker, résolution par la page, `Range`,
  `ETag`, `If-Range`.
- [ ] `KazoFile.CreateUrlAsync`, `KazoFileUrl`, libération avec l'instance ;
  apps en Web Worker ; repli `blob:`.
- [ ] Disque `/` et lecteurs réseau.
- [ ] Écriture en flux ; mesure de la mémoire avant et après.
- [ ] `PublicAPI.Unshipped.txt`, fixture de compatibilité du SDK, docs du
  framework (FR et EN), `features.md`, entrée de changement.
- [ ] Issue dans Os-App-Webamp.
