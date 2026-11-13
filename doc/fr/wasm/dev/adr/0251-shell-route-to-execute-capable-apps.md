> [English version](../../../../en/wasm/dev/adr/0251-shell-route-to-execute-capable-apps.md)

# ADR 0251 : route du shell vers les apps à capacité d'exécution

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base` ; réécrite le même jour après la réponse du mainteneur à la
question 1 (D, hors des options proposées) et aux quatre précisions posées
ensuite (252 à 255, A). Les questions posées à Benoit ont reçu leur réponse ;
elles sont consignées à la fin de cette ADR et la décision les applique.
Décisions d'origine : 13 (un ELF tapé au terminal va à Os-x86), 62 B
(`./setup.exe` va à BoxedWine ; « Os-x86 n'a pas d'association
d'extension », 64), 111 (un `.exe` DOS tapé au terminal va directement à
Os-x86/FreeDOS), 162 A avec la précision de Benoit (l'en-tête décide quand
il existe, l'extension sinon, une contradiction refuse ; « sauf dans le cas
que c'est l'application Terminal ») et 184 A (enfant du shell, nouvelle
instance par lancement, flux du shell, le terminal attend ; Ctrl+C ferme
l'instance), 2026-11-11 ; réponse 240 D du mainteneur, en ses mots :
« Os-x86 fournit une méthode pour déterminer le type d'application. C'est
lui qui interprète le magic et l'extension. Ensuite le shell demande aux
applications qui exposent des capacités d'exécution d'application si elles
peuvent ouvrir l'application. Je crois qu'on devrait ajouter le support de
MimeType (optionnel) et s'en servir aussi plus tard pour les protocoles
(comme pour irc). » ; précisions 252 A (détecteur de format déclaré par une
Feature), 253 A (action `execute` des `FileHandlers`), 254 A (association
par défaut de l'usager, candidat unique, fenêtre « Ouvrir avec ») et 255 A
(types MIME de freedesktop et de l'IANA, protocoles par
`x-scheme-handler/<schème>`), 2026-11-11 ; hôte de programmes du shell,
251 A (ADR 0252 §1).

- **Complète :** l'[ADR 0050](0050-shell-grammar-and-process-environment.md)
  et l'[ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  (scripts, apps lancées par leur nom), l'[ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (`xdg-open`, associations, `x-scheme-handler/<schème>`),
  l'[ADR 0121](0121-user-file-associations.md) (associations par défaut de
  l'usager, `xdg-mime`), l'[ADR 0148](0148-sdk-contracts-and-os-provided-controls.md)
  (un type d'une app instancié par l'OS hors de tout processus) et
  l'[ADR 0260](0260-feature-app-type.md) §5 (seule une Feature déclare des
  détecteurs de format).
- **Remplace :** la première rédaction de cette ADR (le shell lisait le
  `Magic` déclaré par une Feature et lançait son handler ; `FileHandler`
  gagnait `Executable`), écartée par 240 D (option F).
- **Ne change pas :** le double-clic, « Ouvrir avec », `xdg-open` et
  `OpenFileAsync` (toujours l'association), la confirmation d'un changement
  d'app par défaut (D61.10, 124.10), l'hôte de programmes du shell
  (ADR 0252 §1).
- **Pour :** `./setup.exe` (PE → BoxedWine), `./prog.exe` MZ pur (DOS →
  commande `x86` d'Os-x86), `./a.out` (ELF → `x86`), `./script.bat` (→
  BoxedWine, jamais lu comme script `hsh`) ; `xdg-open irc://…` pour
  l'agent d'Os-x86.

## Contexte

- `./x` au shell (`Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.Scripts.cs:26-128`,
  `RunPathAsync`) : dossier → 126 (:42-47) ; absent → 127 (:50-55) ; **bit
  d'exécution testé avant toute lecture** → « Permission denied », 126
  (:57-66) ; lecture **comme texte** (:68) ; un octet nul → « cannot execute
  binary file », 126 (:78-83) ; `#!` (:86-125) ; sinon script `hsh` (:127).
  Il ne regarde **jamais** ni les associations ni un en-tête MZ, PE ou ELF.
  Un `.exe` téléchargé sans bit d'exécution échoue ; un `.bat` avec le bit
  tourne comme script `hsh`.
- Ordre de résolution d'une commande (`ShellInterpreter.SimpleCommands.cs:124-184`) :
  alias, builtins, fonctions, chemin, commandes du catalogue (apps `Terminal`
  seulement, `ShellCommandResolver.cs:106`), `sh`/`bash`/`hsh`, puis le nom
  court d'une fenêtre, sinon 127.
- Une fenêtre lancée par son nom (:259-309) est un **enfant du shell**
  (`ParentPid`), avec les flux du shell (:263-278, `AttachedAppConsole`,
  `AppLifecycleOrchestrator.cs:548-558`, texte ligne par ligne, pas de mode
  brut) ; le terminal attend (:289) ; code 0 à la fermeture, 1 si plantage,
  130 après Ctrl+C qui **tue la fenêtre** (:291-295,
  `InterruptAsync` :1345-1358) ; une app à instance unique déjà ouverte rend
  0 tout de suite sans flux (:282-284).
- Les associations se résolvent **par extension** seulement (ou le type MIME
  donné par l'appelant) : `Intents/FileAssociationResolver.cs:198-214,
  386-393` ; `Priority` d'un gestionnaire n'est jamais lu ; un candidat
  unique est choisi sans demander ; sans gestionnaire, `xdg-open` rend 3
  (`XdgOpenCommand.cs:89`). Un gestionnaire se déclare par `FileHandlers`
  (`FileHandlerManifest(MediaType, Extensions, Actions, Priority)`,
  `AppManifest.cs:224`) d'une app fenêtrée (`AppManifestValidator.cs:318-321`).
  La fenêtre « Ouvrir avec » de l'OS existe (`DialogCoordinator.OpenWithAsync`,
  `Platform/Kazo.Os.Platform.Blazor/Dialogs/DialogCoordinator.cs:240`).
- **Kazo n'a pas de table extension → type MIME** : `xdg-mime query
  filetype` (`Apps/Commands/Kazo.Os.Commands/XdgMime/XdgMimeCommand.cs:84-118`)
  prend le `MediaType` du premier `FileHandler` d'une app installée qui
  déclare l'extension, sinon `application/octet-stream` ; il ne lit pas le
  fichier.
- **Kazo n'appelle jamais la classe d'une app installée sans la lancer** :
  le point d'entrée est construit au lancement, dans un processus
  (`AppLifecycleOrchestrator.cs:250-253`, `CreateAppInstance`). La seule
  exception est un composant intégrable (ADR 0148) : un type nommé dans le
  manifeste (`AppComponentManifest(Contract, Assembly, Type)`,
  `AppManifest.cs:250`), chargé à la demande avec l'assemblage du point
  d'entrée et instancié par l'OS hors de tout processus d'app
  (`Platform/Kazo.Os.Platform.Blazor/Components/KazoComponentHost.cs:245-265`,
  `descriptor.Assembly.GetType(component.Type)`).
- `IFullScreenTerminalSession` (ADR 0252) n'est donné qu'à une **commande**
  d'une app `Terminal` ; une fenêtre lancée du shell n'y a pas accès.

## Décision

### 1. Trois déclarations

- **Détecteur de format** (252 A) : `FeatureDefinition.FormatDetectors`,
  **réservé aux Features** (ADR 0260 §5), liste des `FormatDetector`
  (`MediaTypes` : les types MIME que la Feature reconnaît ; `Type` : un type
  de l'assemblage du point d'entrée de la Feature, qui implémente
  `IFormatDetector` du SDK). `DetectAsync(en-tête de 64 octets au plus,
  extension)` rend un `FormatDetection` : le type MIME (§3) et le détail
  (`PeConsole`, `PeGraphical`, `Ne`, `Mz`, `Elf`, `DosBatch`), ou
  `Mismatch` avec une raison si l'extension contredit l'en-tête
  (`photo.png` avec un en-tête PE), ou null (« pas à moi »). L'OS
  l'instancie comme un composant de l'ADR 0148 (type nommé dans le
  manifeste, chargé à la demande), **sans contexte d'app ni passerelle** :
  il reçoit les octets et l'extension, rien d'autre, et ne peut rien faire
  d'autre. Os-x86 déclare les cinq types du §3 ; c'est lui qui interprète le
  magic et l'extension (240 D).
- **Action `execute`** (253 A) : l'action `execute` s'ajoute aux
  `FileHandlers` existants (`FileHandler("application/vnd.microsoft.portable-executable",
  [".exe"], ["open", "execute"])`) ; elle dit au shell : « un programme de
  ce type lancé par son chemin m'est destiné ». Le validateur l'accepte sur
  une app fenêtrée et, nouveauté, sur une commande d'une app `Terminal` dont
  les actions se limitent à `execute`. BoxedWine déclare `execute` pour PE
  (`.exe`), NE et le lot DOS (`.bat`, `.cmd`) ; la commande `x86` d'Os-x86
  pour MZ pur et ELF. Elle n'a aucun effet hors du shell.
- **Refus au lancement** (253 A, selon le code) : l'app candidate n'est pas
  interrogée avant d'être lancée, parce que Kazo n'instancie la classe
  d'une app qu'au lancement (contexte). Elle reçoit le programme à son
  démarrage (`ProgramLaunch(Path, MediaType, Detail, Arguments)`, dans
  `AppLaunchRequest`) et peut le **refuser avant sa première fenêtre ou sa
  première sortie** (`AppLaunchResult.Refused(reason)`, nouveau dans le SDK)
  : le shell journalise le refus et passe au candidat suivant (§2, étape 5)
  ; sans candidat restant, 126. Une app qui ne répond rien accepte.

### 2. La route de `./chemin`, dans l'ordre

Avant le test du bit d'exécution et avant toute lecture comme texte :

1. **Le fichier existe et n'est pas un dossier** (comme aujourd'hui, 126/127).
2. **L'hôte du shell** (251 A) : si l'app qui a démarré le shell a fourni un
   `ProgramHost` (`KazoShellOptions`, `IKazoShellProgramHost.TryRunAsync`,
   ADR 0252 §1 : l'app Terminal, qui exécute elle-même un programme console
   par `Ix86`) et qu'il prend le fichier, le code de sortie est le sien et
   la route s'arrête là.
3. **Détection du type** (252 A) : le shell lit les premiers octets (64 au
   plus, `KazoFile.OpenRead`, sans charger le fichier) et interroge les
   détecteurs des Features **installées et activées**, dans l'ordre
   d'installation ; le premier qui répond décide. `Mismatch` → refus avec
   son message, 126, journal ; jamais exécuté. Sans détecteur, ou aucun ne
   répondant : le type vient des `FileHandlers` des apps installées comme
   pour `xdg-mime query filetype` (première app qui déclare l'extension avec
   un `MediaType`), sinon l'extension seule.
4. **Apps à capacité d'exécution** (253 A) : les apps installées et activées
   dont un `FileHandler` déclare `execute` pour ce type MIME, ou pour
   l'extension à défaut de type (`FileAssociationResolver`, action
   `execute`).
5. **Choix du candidat** (254 A) : l'association par défaut de l'usager pour
   ce type MIME et l'action `execute` (ADR 0121, Paramètres ▸ Associations,
   `xdg-mime default <app> <type>`) ; sinon le candidat unique ; sinon la
   fenêtre « Ouvrir avec » de l'OS (`DialogCoordinator.OpenWithAsync`,
   candidats limités à ceux de l'étape 4), **jamais une question au
   terminal** (D61.5). `Priority` n'est pas utilisée. Un refus au lancement
   (§1) retire le candidat et reprend ici.
6. **Lancement** : comme une fenêtre lancée par son nom (enfant du shell,
   **nouvelle instance par lancement**, une app `SingleInstancePerUser`
   étant lancée en instance séparée pour ce lancement ou refusée avec 126 si
   elle ne le permet pas, 241 A ; flux du shell ; le terminal attend la
   fermeture ; code 0/1/130 comme aujourd'hui, limite nommée : ce n'est pas
   le code du programme, 110 ; **Ctrl+C ferme l'instance**, 184 A, tant que
   l'ADR « Ctrl+C transmis » du lot 3 n'existe pas) ; une commande
   candidate (`x86`) est lancée comme une commande du catalogue, avec
   `IFullScreenTerminalSession` (ADR 0252). L'expéditeur (ADR 0253) est l'app
   hôte du shell et l'usager ; le programme est passé par `ProgramLaunch`
   avec le détail de l'étape 3. Aucun bit d'exécution requis ; le fichier
   n'est pas lu comme texte. **Sans candidat** (242 A) : « Aucune
   application n'ouvre les fichiers .exe » avec la suggestion de
   l'ADR 0248 §2 quand le catalogue du Store liste un format (lot 2
   d'Os-x86), code 126 ; sans catalogue, l'étape 7 s'applique et un `.exe`
   rend « cannot execute binary file » comme aujourd'hui.
7. Sinon, **comme aujourd'hui** : bit d'exécution, texte, `#!`, script `hsh`.

Le même chemin par `xdg-open ./setup.exe` reste l'ouverture par association
(action `open`) sans attendre (inchangé).

### 3. Types MIME et protocoles (255 A)

| Format | Type MIME | Source | `execute` déclaré par |
|---|---|---|---|
| PE (Windows, console ou graphique) | `application/vnd.microsoft.portable-executable` | IANA | BoxedWine |
| NE (Windows 16 bits) | `application/vnd.microsoft.portable-executable`, détail `Ne` | IANA | BoxedWine |
| MZ pur (DOS) | `application/x-dosexec` | freedesktop | commande `x86` |
| ELF (Linux) | `application/x-executable` | freedesktop | commande `x86` |
| Lot DOS (`.bat`, `.cmd`) | `application/x-bat` | freedesktop | BoxedWine |
| Installeur Windows (`.msi`) | `application/x-msi` | freedesktop | BoxedWine |

- Les types sont fixés par cette ADR ; une Feature qui en reconnaît d'autres
  les nomme dans `MediaTypes` avec un type de freedesktop ou de l'IANA.
- **Protocoles** : rien de nouveau. Un schème se déclare par le
  `x-scheme-handler/<schème>` existant (ADR 0096 §3, ADR 0121) ; `open-url`
  de l'agent d'Os-x86 passe par `xdg-open`, qui résout `irc://…` comme
  aujourd'hui.

### 4. L'exception de l'app Terminal (162, précision de Benoit)

- « Le Terminal exécute lui-même le programme console par Os-x86 » : c'est
  l'app Terminal, par `Ix86` (ADR 0252 §1), qui prend le fichier à
  l'étape 2 quand elle héberge le shell ; elle décide pour un PE **console**
  (exécution dans l'onglet), un MZ pur (DOS dans l'onglet) et un ELF (Linux
  dans l'onglet), et passe un programme **graphique** à la suite de la
  route (BoxedWine). Le shell n'a rien à savoir de Wine ni de DOS : la règle
  est dans le Terminal (par `Ix86`) et dans le détecteur d'Os-x86.
- **Hors du Terminal** (shell démarré par une autre app sans `ProgramHost`,
  script), les étapes 3 à 6 sont la seule route : un PE console va à
  BoxedWine comme un PE graphique (BoxedWine l'affiche dans sa propre
  fenêtre), un MZ pur ou un ELF à la commande `x86` dans les flux du shell.
- Au double-clic, `xdg-open` et `OpenFileAsync`, **rien ne change** : le
  fichier va à l'association `open` (BoxedWine), qui appelle Os-x86 par
  `Ix86`.

### 5. Ce que le shell journalise

`/var/log/syslog`, source `shell` : `exec route=host app=org.kazoos.terminal
path=…`, `route=execute app=ca.kazo.boxedwine
type=application/vnd.microsoft.portable-executable detail=pe-graphical
path=…`, `route=execute app=ca.kazo.x86.cli type=application/x-executable
path=…`, et les refus (`route=refused reason=header-extension-mismatch`,
`reason=no-handler`, `reason=refused-by-app app=…`).

## Options écartées

- **A. Toutes les extensions associées sont exécutables** (comme `cmd` de
  Windows, où `photo.png` ouvre l'image) : `./notes.txt` lancerait l'éditeur
  et le terminal attendrait ; contraire à l'usage du shell POSIX.
- **B. Un binfmt générique dans l'OS** (le shell reconnaît lui-même MZ, PE,
  ELF, 13 A d'origine) : l'OS aurait des règles sur des formats qu'il
  n'exécute pas ; la précision 162 (console dans le terminal, DOS sans
  BoxedWine) vit dans l'app Terminal et dans le détecteur de la Feature,
  pas dans le shell.
- **C. L'en-tête avant l'association, toujours, avec `.exe` → Os-x86** : 62 B
  a tranché pour BoxedWine ; BoxedWine déclare `execute` pour PE.
- **D. Instance unique de BoxedWine qui reçoit les lancements successifs**
  (184 B) : le terminal rendrait 0 tout de suite sans flux (code actuel
  :282-284) ; contredit 163.
- **E. Lire le fichier entier pour détecter** : 64 octets suffisent ; un
  disque de VM de 4 Gio ne doit pas être lu pour savoir qu'il n'est pas un
  ELF.
- **F. Le `Magic` lu par le shell et un handler par Feature** (première
  rédaction de cette ADR, question 1 A) : écartée par 240 D ; le shell ne
  connaît aucun octet magique, c'est la Feature qui interprète l'en-tête et
  l'extension, et le shell interroge des apps déclarées.
- **G. Le shell résout `Ix86` pour détecter** (252 B) : le shell dépendrait
  d'une interface d'une Feature nommée ; un détecteur déclaré vaut pour
  toute Feature.
- **H. Déclaratif seul, sans détecteur** (253 B) ni **déclaration à part
  des `FileHandlers`** (253 C) : l'extension seule ne distingue pas un PE
  d'un MZ pur ; une déclaration à part dupliquerait les associations
  (`Priority`, Paramètres ▸ Associations) que les `FileHandlers` ont déjà.
- **I. `Priority` ou « Ouvrir avec » à chaque fois** (254 B, C) : la
  préférence de l'usager existe déjà (ADR 0121) ; une fenêtre à chaque
  lancement contredirait l'usage du shell.
- **J. Un champ `protocols` à part** (255 B) : `x-scheme-handler/<schème>`
  le fait déjà pour le navigateur et l'app IRC.
- **K. `CanExecuteAsync` sur l'app avant de la lancer** : Kazo n'appelle
  jamais la classe d'une app installée hors de son lancement (contexte) ;
  un type nommé dans le manifeste, comme un composant, le permettrait, mais
  sans contexte d'app il ne saurait rien de plus que le détecteur ; le refus
  au lancement (§1) suffit.

## Conséquences

- SDK (`PublicAPI.Unshipped.txt`) : `FeatureDefinition.FormatDetectors`,
  `FormatDetector`, `IFormatDetector`, `FormatDetection` ; action `execute`
  des `FileHandlers` ; `ProgramLaunch` dans `AppLaunchRequest` et
  `AppLaunchResult.Refused`. `FileHandler.Executable` et
  `FeatureDefinition.BinaryFormats` de la première rédaction n'existent pas.
- Validateur : détecteurs réservés aux Features (`manifest.feature.only`,
  ADR 0260 §5), `MediaTypes` bien formés, type du détecteur dans
  l'assemblage du point d'entrée (comme un composant) ; `FileHandlers` avec
  la seule action `execute` permis sur une commande d'une app `Terminal`.
- `AppCatalog` : registre des détecteurs dans l'ordre d'installation ;
  `FileAssociationResolver` : action `execute`, résolution par type MIME
  puis extension ; `xdg-mime query filetype` consulte les détecteurs avant
  les `FileHandlers`, pour rendre le même type que le shell.
- `ShellInterpreter.RunPathAsync` : hôte du shell d'abord (`ProgramHost`,
  ADR 0252 §1), détection, candidats, association par défaut, fenêtre
  « Ouvrir avec », lancement d'une fenêtre ou d'une commande comme enfant
  avec instance séparée, refus au lancement, message sans candidat ;
  journal.
- Docs : [`shell-commands.md`](../shell-commands.md),
  [`file-associations-api.md`](../../framework/file-associations-api.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`, entrée
  `changes/unreleased`.
- Tests : `./a.out` ELF sans bit → détecteur d'Os-x86, commande `x86` lancée
  avec `ProgramLaunch` ; hôte avec `ProgramHost` qui prend le fichier → ni
  détection ni candidat, code de l'hôte ; `.exe` PE → BoxedWine (enfant,
  attend, 0 à la fermeture, 130 sur Ctrl+C) ; deux candidats → association
  par défaut, sinon « Ouvrir avec » ; refus au lancement → candidat suivant
  puis 126 ; `.bat` → BoxedWine, jamais `hsh` ; `photo.png` à en-tête PE →
  126 et journal ; sans détecteur → type par les `FileHandlers` ; sans
  BoxedWine → message 126 ; dossier et absent inchangés ; `xdg-open`
  inchangé ; app ordinaire avec `FormatDetectors` → refusée ; `xdg-mime
  query filetype ./a.out` → `application/x-executable`.

## Questions posées à Benoit

1. **Qui lit l'en-tête et applique « en-tête d'abord » ?**
   - A) **(Recommandé à l'origine)** Le shell reconnaît le `Magic` déclaré
     par la Feature et lance son handler ; la règle fine (PE, NE, MZ pur,
     console ou graphique, délégation à BoxedWine) est dans le handler
     d'Os-x86 et dans l'app Terminal (ADR 0252 §1).
   - B) Le shell applique toute la règle (PE → association, MZ pur → DOS,
     ELF → Linux) à partir d'une table déclarée par la Feature : plus de
     logique dans l'OS, moins dans la Feature.
2. **Une app associée `SingleInstancePerUser` déjà ouverte, lancée par
   `./x.exe` ?**
   - A) **(Recommandé)** Le shell lance une instance séparée pour ce
     lancement (184 A : « BoxedWine n'est pas à instance unique » ; une app
     qui l'est reçoit une seconde instance avec les flux).
   - B) Refus avec 126 « déjà ouverte » : l'app doit être fermée d'abord.
3. **Sans aucun gestionnaire pour `.exe` (BoxedWine absent), en V1 ?**
   - A) **(Recommandé)** Message « Aucune application n'ouvre les fichiers
     .exe » et 126 dès que le catalogue du Store liste un format
     (lot 2) ; avant, « cannot execute binary file » comme aujourd'hui.
   - B) Toujours « cannot execute binary file » (rien de nouveau).

Réponses du mainteneur (2026-11-11) : 1 **D**, hors des options, en ses mots
(cités dans le statut) : Os-x86 détermine le type (magic et extension), le
shell demande aux apps à capacité d'exécution, types MIME facultatifs
réutilisés pour les protocoles ; 2 A ; 3 A.

Précisions posées après 240 D, réponses du mainteneur le 2026-11-11 (« je
suis d'accord avec tes recommandations ») :

- 252 A : un détecteur de format déclaré par une Feature
  (`FormatDetectors`, §1) plutôt que le shell qui résout `Ix86`.
- 253 A : l'action `execute` des `FileHandlers` existants plutôt qu'une
  déclaration à part ou le déclaratif seul ; la confirmation par l'app se
  fait au lancement, Kazo n'appelant pas une app sans la lancer (§1,
  contexte).
- 254 A : association par défaut de l'usager, sinon candidat unique, sinon
  fenêtre « Ouvrir avec », jamais une question au terminal ; `Priority`
  inutilisée.
- 255 A : types MIME fixés par l'ADR depuis freedesktop ou l'IANA (§3) ;
  protocoles par `x-scheme-handler/<schème>` existant.
