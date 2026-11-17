> [English version](../../../../en/wasm/dev/adr/0280-keep-linq-regex-channels-whole.md)

# ADR 0280 : System.Linq, System.Text.RegularExpressions et System.Threading.Channels restent entières

## Statut

Acceptée le 2026-11-17 : Benoit a choisi de corriger l'OS plutôt que l'app
(« OS seulement »). Demande de départ : le Store de 3.4.1 refuse d'installer
Kazo AI 0.1.1 (`apps.package.missing-api`).

- **Ajuste :** l'[ADR 0242](0242-framework-api-for-installed-apps.md) §1 (trois
  assemblies du framework de plus restent entières, à côté de CoreLib).
- **Document :** [`trimmed-publish.md`](../trimmed-publish.md).

## Contexte

- L'ADR 0242 garde `System.Private.CoreLib` entière et élague le reste du
  framework. Une app installée ne trouve que ce que l'OS et ses apps intégrées
  appellent.
- Kazo AI 0.1.1 (`ca.kazo.ai`) est refusée par le Store de os.kazo.ca (3.4.1).
  Les fichiers `_framework` publiés n'ont vraiment pas ces membres (vérifié sur
  les métadonnées des `.wasm` servis, avec `AppPackageApiCheck` tel quel) :

  | Assembly | Membres absents |
  |---|---|
  | `System.Text.RegularExpressions` | `MatchEvaluator`, `Regex.Replace(string, MatchEvaluator)`, `Regex.Replace(string, string, string, RegexOptions, TimeSpan)`, aussi `Match.NextMatch()` et `Regex.Match(string, int)` |
  | `System.Linq` | `Enumerable.Sum(IEnumerable<double>)` |
  | `System.Threading.Channels` | `ChannelWriter<T>.WriteAsync(T, CancellationToken)` |

  Ce sont des appels courants que l'OS ne fait pas lui-même. Kazo AI avait été
  testée avec `dotnet run`, qui garde tout le framework.
- Les mêmes appels en Brotli dans 3.4.1 (élaguées) : Linq 34,7 Kio, Regex
  99,5 Kio, Channels 14,5 Kio.

## Décision

1. **`System.Linq`, `System.Text.RegularExpressions` et
   `System.Threading.Channels` restent entières** dans une publication trimmée
   d'un hôte (`_KazoOsRootKazoOsAssemblies` de `build/Kazo.Os.Apps.targets`,
   donc aussi pour les hôtes d'autres dépôts par `Kazo.Os.App.Build`).
   `KazoOsKeepAppFrameworkLibraries=false` les élague comme avant.
2. **L'app ne change pas.** Kazo AI 0.1.1 s'installe sur l'OS qui porte cette
   décision. La correction côté app (Os-App-AI#5) est fermée.
3. **Test de non-régression.** Le paquet de test `Tests/fixtures/installed-app`
   appelle les cinq membres de Kazo AI ; le test
   `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts` de
   `build/publish-trimmed.sh` vérifie leur résultat dans la fenêtre. Si ces
   assemblies étaient de nouveau élaguées, l'installation du paquet serait
   refusée.
4. **Le reste du framework reste élagué** ; la vérification d'installation
   (ADR 0182 §5.3, ADR 0242) refuse toujours un paquet qui appelle un membre
   retiré ailleurs.

## Options écartées

- **Adapter l'app seulement** (Os-App-AI#5, Kazo AI 0.1.2 sans ces appels) :
  aucune taille de plus, l'app s'installe aussi sur 3.4.1, mais chaque auteur
  d'app doit éviter des appels courants de LINQ, des expressions régulières et
  des canaux.
- **Les deux** : la même taille de plus que la décision, et une version d'app.
- **Tout le framework entier** : +3,8 Mio Brotli (ADR 0242).

## Conséquences

- Taille du premier chargement (Brotli, mesurée par `build/publish-trimmed.sh`
  le 2026-11-17) : +56 Kio au total.

  | Assembly | 3.4.1 (élaguée) | entière |
  |---|---|---|
  | `System.Linq` | 34,7 Kio | 57,7 Kio |
  | `System.Text.RegularExpressions` | 99,5 Kio | 127,8 Kio |
  | `System.Threading.Channels` | 14,5 Kio | 19,4 Kio |
- Kazo AI 0.1.1, sans changement, passe la vérification sur cette publication
  (0 membre absent ; 5 sur 3.4.1).
- Sur un OS 3.4.1 ou plus ancien, Kazo AI 0.1.1 reste refusée : elle déclare
  `sdkCompatibility.minimumVersion` 3.4.1, donc le Store de 3.4.1 la propose.
  Elle s'installe après la mise à jour de l'OS.
- La surface d'API de l'ADR 0277 (série `todo-wins/base`) est lue dans la
  publication : elle contiendra ces trois assemblies entières sans autre
  changement.
