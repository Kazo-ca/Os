> [English version](../../../../en/wasm/dev/adr/0251-shell-route-to-associations-and-binfmt.md)

# ADR 0251 : route du shell vers l'association de fichier et binfmt par en-tête d'une Feature

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. La recommandation de chaque question posée à Benoit est
appliquée en attendant sa réponse. Décisions d'origine : 13 (un ELF tapé au
terminal va à Os-x86 par un binfmt), 62 B (`./setup.exe` va à BoxedWine par
l'association `.exe` ; « Os-x86 n'a pas d'association d'extension », 64),
111 (un `.exe` DOS tapé au terminal va directement à Os-x86/FreeDOS), 162 A
avec la précision de Benoit (l'en-tête décide quand il existe, l'extension
sinon, une contradiction refuse ; « sauf dans le cas que c'est l'application
Terminal » : le Terminal exécute lui-même un programme console par Os-x86
quand la redirection de console est livrée, sinon l'envoie à BoxedWine) et
184 A (enfant du shell, nouvelle instance par lancement, flux du shell, le
terminal attend ; Ctrl+C ferme l'instance), 2026-11-11.

- **Complète :** l'[ADR 0050](0050-shell-grammar-and-process-environment.md)
  et l'[ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  (scripts, apps lancées par leur nom), l'[ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (`xdg-open`, associations) et l'[ADR 0243](0243-feature-app-type.md) §5
  (seule une Feature enregistre un binfmt).
- **Ne change pas :** le double-clic et « Ouvrir avec » (toujours
  l'association), la confirmation d'un changement d'app par défaut
  (D61.10, 124.10).
- **Pour :** `./setup.exe` (Windows → BoxedWine), `./prog.exe` MZ pur (DOS →
  Os-x86), `./a.out` (ELF → Os-x86), `./script.bat` (→ BoxedWine, jamais
  lu comme script `hsh`).

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
  d'une app fenêtrée (`AppManifestValidator.cs:318-321`).
- `IFullScreenTerminalSession` (ADR 0252) n'est donné qu'à une **commande**
  d'une app `Terminal` ; une fenêtre lancée du shell n'y a pas accès.

## Décision

### 1. Deux déclarations nouvelles

- **Extension exécutable** : `FileHandler` gagne `Executable = true`
  (`FileHandler(mediaType, [".exe", ".com", ".bat", ".cmd", ".msi"],
  ["open"], Executable: true)`). Elle dit au shell : « un fichier de cette
  extension lancé par son chemin m'est destiné ». Toute app fenêtrée peut la
  déclarer (BoxedWine le fait) ; elle n'a aucun effet hors du shell.
- **Format exécutable par en-tête (binfmt)** : `FeatureDefinition.BinaryFormats`,
  **réservé aux Features** (ADR 0243 §5), liste des `BinaryFormat` :
  `Magic` (octets et décalage, par exemple `7F 45 4C 46` à 0 pour ELF,
  `4D 5A` à 0 pour MZ), `Name` (« ELF », « MZ »), et `Handler` : l'enfant
  `Terminal` de la Feature qui exécute (`org.kazoos.x86.cli`), avec les
  arguments que le shell lui passe (`--binfmt <nom> --file <chemin> --
  <args>`). Deux Features avec le même `Magic` : la plus ancienne gagne, la
  seconde reçoit `catalog.binfmt.duplicate` (comme les commandes).

### 2. La route de `./chemin`, dans l'ordre

Avant le test du bit d'exécution et avant toute lecture comme texte :

1. **Le fichier existe et n'est pas un dossier** (comme aujourd'hui, 126/127).
2. **En-tête** : le shell lit les premiers octets (64 au plus, par
   `KazoFile.OpenRead`, sans charger le fichier). S'ils correspondent au
   `Magic` d'un binfmt d'une Feature **installée et activée** : le shell
   lance le `Handler` de cette Feature comme une commande du catalogue
   (enfant du shell, flux du shell, `IFullScreenTerminalSession` disponible,
   ADR 0252), avec le chemin et les arguments ; le code de sortie est celui
   que le handler rend. Le handler applique sa propre règle (Os-x86 : MZ+PE
   ou MZ+NE → confie à l'app associée à l'extension par la route 3 ci-dessous,
   `IAppIntentGateway.OpenFileAsync` avec l'expéditeur marqué, ADR 0253 ;
   MZ pur → FreeDOS dans le terminal ; ELF → Linux dans le terminal). Si
   l'extension **contredit** l'en-tête (`photo.png` avec un en-tête PE), le
   handler refuse avec un message et le code 126 ; jamais exécuté.
3. **Extension déclarée exécutable** par une app fenêtrée installée et
   activée, et aucun binfmt n'a reconnu l'en-tête : le shell lance l'app
   associée (résolution de `FileAssociationResolver`, gestionnaire par
   défaut ou candidat unique ; plusieurs candidats → la fenêtre « Ouvrir
   avec » de l'OS, jamais une question au terminal, D61.5) **comme une
   fenêtre lancée par son nom** : enfant du shell, **nouvelle instance par
   lancement** (une app `SingleInstancePerUser` est lancée quand même en
   instance séparée pour ce lancement, ou refusée avec 126 si elle ne le
   permet pas, question 2), flux du shell, le terminal attend la fermeture ;
   code 0/1/130 comme aujourd'hui (limite nommée : ce n'est pas le code du
   programme, 110) ; **Ctrl+C ferme l'instance** (184 A) tant que l'ADR
   « Ctrl+C transmis » du lot 3 n'existe pas. L'expéditeur (ADR 0253) est
   l'app Terminal et l'usager. Aucun bit d'exécution requis ; le fichier
   n'est pas lu comme texte.
4. Sinon, **comme aujourd'hui** : bit d'exécution, texte, `#!`, script `hsh`.

- **Sans gestionnaire** pour une extension déclarée exécutable par aucune app
  (BoxedWine absent) : le shell ne tombe pas sur l'étape 4 pour `.exe`
  (lire un binaire comme script est ce qu'il faut éviter) : message « Aucune
  application n'ouvre les fichiers .exe » avec la suggestion de l'ADR 0248
  §2 quand le catalogue du Store en propose une, code 126. La liste des
  extensions « connues comme exécutables » vient du catalogue du Store
  (formats déclarés, lot 2 d'Os-x86) ; en V1, sans catalogue, l'étape 4
  s'applique et un `.exe` rend « cannot execute binary file » comme
  aujourd'hui.
- Le même chemin par `xdg-open ./setup.exe` reste l'ouverture par
  association sans attendre (inchangé).

### 3. L'exception de l'app Terminal (162, précision de Benoit)

- La route 2 donne au handler d'une Feature la session du terminal : c'est
  là que « le Terminal exécute lui-même le programme console par Os-x86 ».
  Le handler d'Os-x86 décide, pour un MZ+PE **console** : exécuter dans le
  terminal (ADR 0252) s'il le peut ; sinon, et pour tout programme
  **graphique**, passer à l'app associée (route 3, BoxedWine). Le shell n'a
  rien à savoir de Wine ni de DOS : la règle est dans le handler.
- Au double-clic, `xdg-open` et `OpenFileAsync`, **rien ne change** : le
  fichier va à l'association (BoxedWine), qui appelle Os-x86 par `Ix86`.

### 4. Ce que le shell journalise

`/var/log/syslog`, source `shell` : `exec route=binfmt format=ELF
handler=org.kazoos.x86.cli path=…` ou `route=association app=… ext=.exe`,
et les refus (`route=refused reason=header-extension-mismatch`).

## Options écartées

- **A. Toutes les extensions associées sont exécutables** (comme `cmd` de
  Windows, où `photo.png` ouvre l'image) : `./notes.txt` lancerait l'éditeur
  et le terminal attendrait ; contraire à l'usage du shell POSIX.
- **B. Un binfmt générique dans l'OS** (le shell reconnaît lui-même MZ, PE,
  ELF, 13 A d'origine) : l'OS aurait des règles sur des formats qu'il
  n'exécute pas ; la précision 162 (console dans le terminal, DOS sans
  BoxedWine) vit mieux dans la Feature.
- **C. Le binfmt avant l'association, toujours, avec `.exe` → Os-x86** : 62 B
  a tranché pour BoxedWine ; le handler délègue.
- **D. Instance unique de BoxedWine qui reçoit les lancements successifs**
  (184 B) : le terminal rendrait 0 tout de suite sans flux (code actuel
  :282-284) ; contredit 163.
- **E. Lire le fichier entier pour détecter** : 64 octets suffisent ; un
  disque de VM de 4 Gio ne doit pas être lu pour savoir qu'il n'est pas un
  ELF.

## Conséquences

- `FileHandler.Executable`, `FeatureDefinition.BinaryFormats`, `BinaryFormat`
  (SDK, `PublicAPI.Unshipped.txt`) ; validateur (`Magic` bien formé, binfmt
  réservé aux Features) ; `AppCatalog` (registre des binfmt,
  `catalog.binfmt.duplicate`).
- `ShellInterpreter.RunPathAsync` : lecture d'en-tête, route binfmt, route
  association (lancement d'une fenêtre comme enfant avec instance séparée),
  message sans gestionnaire ; `ShellCommandResolver` pour le handler ;
  journal.
- Docs : [`shell-commands.md`](../shell-commands.md),
  [`file-associations-api.md`](../../framework/file-associations-api.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`, entrée
  `changes/unreleased`.
- Tests : `./x` ELF sans bit → handler lancé avec le chemin ; `.exe` PE →
  handler puis association (enfant, attend, 0 à la fermeture, 130 sur
  Ctrl+C) ; `.bat` → association, jamais `hsh` ; `photo.png` à en-tête PE →
  126 et journal ; sans BoxedWine → message 126 ; dossier et absent
  inchangés ; `xdg-open` inchangé ; app ordinaire avec `BinaryFormats` →
  refusée.

## Questions posées à Benoit

1. **Qui lit l'en-tête et applique « en-tête d'abord » ?**
   - A) **(Recommandé)** Le shell reconnaît le `Magic` déclaré par la Feature
     et lance son handler ; la règle fine (PE, NE, MZ pur, console ou
     graphique, délégation à BoxedWine) est dans le handler d'Os-x86 (72.2 :
     service central d'Os-x86).
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

Appliquées en attendant : 1 A, 2 A, 3 A.
