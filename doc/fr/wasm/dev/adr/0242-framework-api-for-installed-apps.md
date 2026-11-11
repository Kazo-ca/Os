> [English version](../../../../en/wasm/dev/adr/0242-framework-api-for-installed-apps.md)

# ADR 0242 : l'API .NET qu'une app installée trouve dans l'OS publié

## Statut

Proposée le 2026-11-09, avec la recommandation de la question posée à Benoit
appliquée en attendant sa réponse (option A : CoreLib entière). Demande de
départ : IRC, installé depuis le Store, s'arrête dans sa fenêtre avec
« MissingMethodException: Method not found: int
System.Int32.Parse(System.ReadOnlySpan`1<char>,System.IFormatProvider) ».

- **Ajuste :** l'[ADR 0113](0113-trimming-implementation-adjustments.md) §1-2
  (seul le framework est élagué : `System.Private.CoreLib` ne l'est plus) et
  l'[ADR 0182](0182-install-apps-from-github.md) §5.3 (la vérification compare
  maintenant les types des paramètres, pas leur nombre).
- **Document :** [`trimmed-publish.md`](../trimmed-publish.md).

## Contexte

- La publication Release de l'OS élague le framework .NET (`TrimMode=partial`,
  ADR 0113) : ILLink retire chaque membre que l'OS et ses apps intégrées
  n'appellent pas. Une app installée après la publication (Store, GitHub) n'est
  pas une racine : elle trouve ce que l'OS a gardé, rien de plus.
- L'ADR 0182 §5.3 vérifie à l'installation chaque référence d'un paquet contre
  le runtime chargé. Les méthodes étaient comparées par **nom, arité générique
  et nombre de paramètres**. ILLink retire une surcharge et en garde une autre de
  même longueur : `Int32.Parse(string, IFormatProvider)` est gardée,
  `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)` est retirée. IRC passait
  donc la vérification et échouait au premier message en couleurs mIRC.
- Vérification exacte (signatures complètes) des apps du Store contre la
  publication de `main` du 2026-11-09 :

  | App | Membres absents de l'OS publié |
  |---|---|
  | IRC 1.0.0 | `Int32.Parse(ReadOnlySpan<char>, IFormatProvider)` (couleurs), `String.Compare(string, int, string, int, int, StringComparison)` (liens) |
  | Webamp 1.1.0 | `String.Split(string[], StringSplitOptions)` (listes de lecture `.m3u`/`.pls`, pas encore déclenché) |
  | Chess 1.1.0 | aucun |

  Les trois membres sont dans `System.Private.CoreLib`.
- Tailles des `.wasm` de `_framework` (Brotli), même jour :

  | Publication | Fichiers | Taille | Brotli |
  |---|---|---|---|
  | élaguée (avant) | 111 | 30,0 Mio | 9,0 Mio |
  | CoreLib entière | 115 | 32,6 Mio | 9,6 Mio |
  | tout le framework entier | 243 | 43,0 Mio | 12,8 Mio |

## Décision

1. **`System.Private.CoreLib` reste entière** dans une publication trimmée d'un
   hôte (`_KazoOsRootKazoOsAssemblies` de `build/Kazo.Os.Apps.targets`, donc
   aussi pour les hôtes d'autres dépôts par `Kazo.Os.App.Build`). C'est le cœur
   que toute app utilise (types de base, `string`, spans, nombres, collections
   de base, tâches) ; le coût est de 0,6 Mio en Brotli. Les autres assemblies du
   framework restent élaguées. `KazoOsKeepCoreLib=false` revient à l'élagage
   précédent.
2. **La vérification d'installation compare les signatures.**
   `AppPackageApiCheck` décode la signature de chaque référence de méthode
   (`RuntimeTypeNames.MetadataProvider`) et la compare aux paramètres des
   méthodes du runtime chargé (`RuntimeTypeNames.Of`), dans une forme texte
   commune (`System.ReadOnlySpan`1<System.Char>`, `!0`, `!!0`, `[]`, `&`).
   `IRuntimeApiSurface` reçoit une surcharge `HasMethod(…, parameterTypes)` ;
   son implémentation par défaut garde l'ancienne comparaison par nombre, pour
   ne pas casser une implémentation existante. Le message
   `apps.package.missing-api` nomme la surcharge exacte. Les types de retour ne
   sont pas comparés.
3. **Les apps évitent les membres absents.** IRC 1.0.1 et Webamp 1.1.1
   n'appellent plus ces trois membres et fonctionnent aussi sur les OS déjà
   publiés (3.2.x). Le test `tests/installed` d'IRC envoie une ligne en
   couleurs avec un lien dans l'OS publié.
4. **Test de non-régression.** Le paquet de test installé par
   `build/publish-trimmed.sh` (`Tests/fixtures/installed-app`) appelle les trois
   membres : si CoreLib était de nouveau élaguée, la vérification refuserait le
   paquet et le test `An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts`
   échouerait.

## Options écartées

- **B. Tout le framework entier** : une app installée trouverait toute l'API
  .NET livrée, mais le premier chargement passe de 9,0 à 12,8 Mio et de 111 à
  243 fichiers, pour des apps qui n'en utilisent qu'une petite partie.
- **C. Rien de plus que la vérification exacte** : taille inchangée, mais des
  surcharges de base (`int.Parse` d'un span) seraient refusées à
  l'installation, et chaque auteur d'app devrait connaître ce que l'OS a gardé.

## Conséquences

- Une app qui appelle un membre retiré d'une autre assembly du framework
  (`System.Linq`, `System.Text.Json`…) est refusée à l'installation avec la
  surcharge exacte, au lieu de planter dans sa fenêtre.
- Une app installée avant une nouvelle version de l'OS n'est pas revérifiée
  quand l'OS change : un membre retiré par la nouvelle publication donne encore
  une erreur dans la fenêtre (ADR 0238), sans emporter l'OS.
- Le correctif de l'OS arrive avec la prochaine version (3.3.0) ; les versions
  d'apps de la décision 3 couvrent les OS déjà publiés.

## Question posée à Benoit

**Quelle partie du framework .NET l'OS garde-t-il entière pour les apps
installées ?** A (recommandée) CoreLib entière, +0,6 Mio ; B tout le framework,
+3,8 Mio ; C rien de plus, vérification exacte seulement. Appliquée : A.
