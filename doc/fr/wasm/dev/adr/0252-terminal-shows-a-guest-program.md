> [English version](../../../../en/wasm/dev/adr/0252-terminal-shows-a-guest-program.md)

# ADR 0252 : le Terminal affiche un programme invité (attributs par cellule, F1 à F12, accord Échap, Ctrl+C au programme)

## Statut

Proposée le 2026-11-11, pour la fonctionnalité Os-x86
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 de la série
`feature/base`. La recommandation de chaque question posée à Benoit est
appliquée en attendant sa réponse. Décisions d'origine : 154 B (« le
terminal de Kazo affiche » la console d'un programme Windows et un programme
DOS texte ; Os-x86 n'ouvre aucune fenêtre), 162 A (exception de l'app
Terminal : le Terminal exécute lui-même un programme console par Os-x86,
en-tête lu par lui), 215 A (contenu demandé au Terminal : surface « écran plein » à
largeur fixe, 80 colonnes et 25 lignes ou plus, attributs de couleur par
cellule et curseur, rafraîchie à ~30 Hz ; entrée touche par touche avec
Ctrl+lettre ; accord Échap+lettre / Échap+chiffre traduit en Alt+lettre /
F1-F12 ; Home, End, Suppr, Page ; taille connue à l'ouverture ;
redimensionnement à chaud en V2) et 216 A (Ctrl+C va au programme invité ;
tuer par Ctrl+Pause si Kazo l'a, sinon un bouton du Terminal ou `x86 kill`),
2026-11-11 ; réponse du mainteneur du 2026-11-11 sur la dépendance du
Terminal (B, §1), en ses mots : « pour pouvoir host éventuellement les
sessions terminal. Il deviendra le seul application système à utiliser la
feature. Il l'utilise pareil que boxedWine » ; essai E14 d'Os-x86 (`cmd` de Wine et FreeDOS utilisables dans
une grille de ce type sous 100 ms ; sans Alt ni F-touches, EDIT ne quitte
pas par son menu, `nano` perd Méta, `mc` ses F-touches).

- **Complète :** l'[ADR 0061](0061-xterm-terminal-emulator.md) (xterm.js
  comme écran et clavier), l'[ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  §6-7 (entrée standard, `nano`, commandes plein écran), l'[ADR 0245](0245-feature-service-registry-and-async-resolution.md)
  (le Terminal résout `Ix86` par le registre) et l'[ADR 0251](0251-shell-route-to-associations-and-binfmt.md)
  (route du shell hors du Terminal).
- **Ne change pas :** `IKazoTerminal`, la discipline de ligne, le mode brut
  complet (D27 : V2 d'Os-x86).
- **Pour :** l'app Terminal de Kazo, qui **appelle Os-x86 par `Ix86`** comme
  BoxedWine le fait (§1) : un programme console Windows, un programme DOS en
  mode texte, un shell Linux dans l'onglet du terminal ; et tout handler de
  commande plein écran, dont le handler binfmt d'Os-x86 (ADR 0251) et
  `x86 shell`, qui reçoivent le même contrat étendu (§2 à §5).

## Contexte

- L'app Terminal (`Apps/System/Kazo.Os.Apps.Terminal/TerminalApp.cs:20`,
  `org.kazoos.terminal`, fenêtrée :26) est référencée par l'hôte comme app
  système (`OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj:53`,
  `IsSystemApp="true"` ; `AppCatalog.cs:173`) et ne référence que le SDK
  (`Kazo.Os.Apps.Terminal.csproj:18-20`). Elle démarre le shell par
  `KazoShell.StartAsync(KazoShellOptions)` (`Shared/Kazo.Os.AppSdk/Shell/KazoShell.cs:31`,
  options `IKazoShell.cs:114-137` : dossier, invite d'identifiants, flux) et
  le shell connaît son app hôte (`Platform/Kazo.Os.Platform.Core/Shell/ShellSession.cs:90`,
  `RunPathAsync` s'en sert, `ShellInterpreter.Scripts.cs:30-36`). Aucune
  app système ne consomme aujourd'hui une interface hors du SDK.
- Le contrat plein écran existe : `Shared/Kazo.Os.AppSdk/FullScreenTerminalContracts.cs`
  (86 lignes). `IFullScreenTerminalSession` (:69-85) donne `Viewport`,
  `EnterAlternateScreenAsync`, `RenderAsync(TerminalScreenFrame)`,
  `ReadKeyAsync`, `LeaveAlternateScreenAsync`. Un `TerminalScreenFrame`
  (:60-63) est une **liste de chaînes** plus un curseur ; les couleurs ne
  passent que par des séquences SGR dans la ligne (`TerminalAnsi.cs:116-157`,
  `FitToColumns` :159 et suivantes), un `ESC[0m` fermant chaque rangée. Les
  commandes `htop`, `top`, `nano`, `watch` s'en servent.
- `TerminalKey` (:4-34) a Character, Enter, Backspace, Delete, les flèches,
  Home, End, PageUp, PageDown, Resize et Escape : **ni Tab (arrive en
  Character `\t`), ni F1 à F12, ni Insert**. Le décodeur
  (`Shared/Kazo.Os.AppSdk.Terminal/TerminalInputDecoder.cs`) traduit
  Ctrl+lettre (:82-83) et un préfixe ESC en Alt (:120-128), mais **abandonne
  les séquences des F-touches** (:177-196 : SS3 P/Q/R/S et CSI 15~…24~ ne
  donnent rien) ; une séquence inconnue est ignorée (:27). **F1 à F12
  n'atteignent jamais une commande.** Alt+lettre y arrive, sauf Alt+Q (fermer
  la fenêtre), Alt+Space sous verrou et ce que le navigateur garde.
- En plein écran, **toute touche va déjà à la commande, Ctrl+C compris**
  (`TerminalPanel.razor:350-379`, `DispatchKey` :356-360) ; hors plein
  écran, Ctrl+C annule la commande (:362-366, :1132-1139, code 130).
  Côté JavaScript, Ctrl+C **avec une sélection** copie et est avalé
  (`TerminalPanel.xterm.entry.js:217-226`) ; Ctrl+Shift+C copie toujours ;
  Ctrl+V colle (:229-231).
- xterm.js 6 dessine (`TerminalPanel.xterm.entry.js:155`, ADR 0061) ; la
  grille est redessinée rangée par rangée à chaque `RenderAsync`
  (`TerminalPanel.razor:1290-1313`, smcup, rangées, rmcup). La taille
  (`UpdateViewport`, `TerminalFullScreenSession.cs:76-85`) est connue au
  début de la commande et un redimensionnement envoie `Resize`.
- Kazo prend ses raccourcis avant toute app (`WindowManagerOverlays.razor.js:60-77`,
  capture sur `document`) : Alt+F3, F7 à F11 et Alt+Q, Alt+Tab, Alt+F4
  sous verrou (`WindowKeyBindings.cs:312-329`). Un programme DOS ou Linux ne
  peut donc pas compter sur Alt+F-touche ; l'accord Échap de 215 A est la
  réponse.

## Décision

### 1. Le Terminal utilise Os-x86 par `Ix86`, comme BoxedWine

- L'app Terminal **référence le paquet de contrats `Os-x86.Interface`** en
  dépendance **facultative** : bibliothèque partagée embarquée dans son
  paquet (ADR 0244, même règle de copie que pour toute app), résolue au
  besoin par `KazoFeatures.GetServiceAsync<Ix86>()` (ADR 0245), qui rend
  `null` quand la Feature est absente ou désactivée : le Terminal se
  comporte alors comme aujourd'hui. L'OS ne connaît toujours ni Wine ni v86 :
  seule l'app Terminal connaît `Ix86`, et elle l'appelle exactement comme
  BoxedWine (ADR 0245 §2).
- Le Terminal est **la seule app système de Kazo qui utilise une Feature**
  (réponse du mainteneur, 2026-11-11) : la liste est tenue ici ; une autre
  app système qui voudrait une Feature demande une ADR.
- **Ce que le Terminal fait lui-même** (162 A) quand un fichier est lancé
  dans un de ses onglets : il lit l'en-tête (64 octets au plus, `KazoFile.OpenRead`),
  décide (MZ+PE **console** → exécution dans l'onglet par `Ix86` ; MZ pur →
  DOS dans l'onglet ; ELF → Linux dans l'onglet ; PE/NE **graphique** →
  l'app associée à l'extension, `OpenFileAsync` avec l'expéditeur marqué,
  ADR 0253 ; extension qui contredit l'en-tête → refus, 126) et affiche la
  console du programme dans sa propre session plein écran (§2 à §5). Pour
  cela, `KazoShellOptions` gagne `ProgramHost` (`IKazoShellProgramHost` :
  `TryRunAsync(path, header, arguments, session)`), que le Terminal fournit
  au démarrage du shell : la route 2 de l'ADR 0251 demande d'abord à l'hôte
  du shell (`ShellSession.Host`) s'il prend le fichier ; « non » (hôte sans
  `ProgramHost`, `Ix86` absent, en-tête inconnu) → le handler binfmt de la
  Feature, puis l'association, comme l'ADR 0251 l'écrit. Hors du Terminal
  (script lancé par une autre app, `hsh` sans hôte de programmes), le
  handler binfmt reste la route.
- **Héberger des sessions** : le Terminal peut ouvrir un onglet qui est une
  session d'Os-x86 (shell Linux par pty, invite DOS) offerte par `Ix86`,
  sans passer par la commande `x86 shell` (qui reste pour le script et le
  pipeline) ; l'onglet montre le nom de la Feature et se ferme comme une
  commande plein écran (§4).
- Expéditeur, permissions, cgroup : le lancement est celui de l'app Terminal
  pour l'usager en session (ADR 0253) ; les permissions `x86.*` demandées
  sont celles de l'ADR 0246 ; l'arrêt tue le cgroup du lancement (206 A).

### 2. Attributs par cellule dans `TerminalScreenFrame`

- `TerminalScreenFrame` gagne une forme à cellules : `TerminalScreenFrame.FromCells(TerminalCell[,] cells, cursor, label)`
  avec `TerminalCell(char Glyph, TerminalColor Foreground, TerminalColor Background, TerminalCellStyle Style)` ;
  `TerminalColor` = les 16 couleurs ANSI (index 0 à 15 ; 256 couleurs et
  truecolor en V2 d'Os-x86, question 1) ; `Style` = `Bold`, `Underline`,
  `Inverse`, `Blink` (blink rendu par Inverse ou ignoré selon xterm.js).
  La forme à chaînes reste (compatibilité des commandes existantes) ;
  `FitToColumns` convertit l'une dans l'autre.
- Le panneau rend une trame à cellules **par remplacement des rangées
  changées** (comparaison avec la trame précédente), en séquences SGR vers
  xterm.js ; une trame à ~30 Hz de 80×25 coûte au plus 25 rangées par
  trame. La cadence est au programme (Os-x86 sonde à 33 ms, 214 A) ; le
  panneau fusionne deux trames arrivées pendant un même rendu (la dernière
  gagne).
- Une trame peut dépasser la fenêtre : `Viewport` reste la vérité, la
  commande dessine 80×25 et le panneau coupe ou centre (fond noir autour,
  police inchangée) ; le redimensionnement à chaud reste `Resize` tel quel
  (V2 d'Os-x86 pour le passer au programme).

### 3. Touches F1 à F12, Insert, Tab, et l'accord Échap

- `TerminalKey` gagne `F1` … `F12`, `Insert` et `Tab` (Tab reste aussi
  livré en `Character "\t"` aux commandes existantes qui l'attendent : les
  deux formes pendant une version, puis `Tab` seul, question 2). Le décodeur
  traduit les séquences SS3 P/Q/R/S, CSI 11~ à 24~ et CSI 2~ ; les
  modificateurs CSI (Shift+F5…) remplissent `Shift`, `Control`, `Alt`.
- **Accord Échap** : en plein écran, quand le programme l'a demandé
  (`IFullScreenTerminalSession.Options.EscapeChord = true`), une touche
  Échap suivie **dans les 500 ms** d'une lettre donne `Character` avec
  `Alt = true`, et d'un chiffre `1` à `9`, `0`, `-`, `=` donne `F1` à `F12` ;
  Échap suivi d'Échap donne un Échap seul ; Échap puis rien pendant 500 ms
  donne un Échap seul. Le décodeur fait déjà « ESC puis octet = Alt »
  (:120-128) ; l'accord étend la règle aux chiffres et la rend explicite
  avec un délai. Le panneau affiche « Échap » en surbrillance dans sa barre
  d'état pendant l'attente.
- Les touches que Kazo garde (Alt+Tab, Alt+F4, Alt+Q, Alt+F3/F7-F11) ne
  sont pas revendiquées : le programme les obtient par l'accord.

### 4. Ctrl+C va au programme ; tuer autrement

- En plein écran (déjà le cas, :356-360) : Ctrl+C est livré au programme
  (`Character "c"` avec `Control`) ; le programme invité reçoit SIGINT par
  le pty (213 A) ou un Ctrl+C DOS. Le panneau **ne copie plus** sur Ctrl+C
  en plein écran, même avec une sélection : Ctrl+Shift+C copie (déjà le
  cas), Ctrl+Insert aussi.
- **Tuer le programme** : Ctrl+Pause (`Pause` avec `Control`, ajouté à
  `TerminalKey` comme `Break`, livré au panneau et non au programme) ferme
  la commande plein écran comme Ctrl+C le fait hors plein écran
  (`CancelActiveCommand`, code 130) ; la commande d'Os-x86, ou le Terminal
  par `Ix86` (§1), reçoit l'annulation de son jeton et tue le cgroup du
  lancement (206 A). Un
  **bouton « Arrêter »** dans la barre d'onglets du Terminal, visible pendant
  une commande plein écran, fait la même chose (les claviers sans Pause ;
  mobile, ADR 0171). `x86 kill` depuis un autre onglet reste possible.
- Hors plein écran, rien ne change (Ctrl+C annule la commande).

### 5. Taille et options à l'ouverture

- `IFullScreenTerminalSession.Options` (`EscapeChord`, `MinimumViewport`
  demandé, `CellMode`) : la commande, ou le Terminal pour un programme qu'il
  exécute lui-même (§1), les pose avant `EnterAlternateScreenAsync` ;
  `Viewport` est garanti connu à ce moment (déjà le cas). Si la fenêtre est
  plus petite que `MinimumViewport` (80×25), le panneau le dit (« Agrandis
  la fenêtre : 80×25 requis ») et la commande décide (Os-x86 refuse ou
  coupe).

### 6. Ce qui ne change pas

- `IKazoTerminal`, l'entrée standard canonique, `nano`, `htop` : la forme
  à chaînes et l'ensemble de touches existant restent valides ; l'accord Échap
  est **à la demande** (une commande existante ne le voit pas).
- Le mode brut complet (toutes les touches, dont celles que Kazo garde) et
  le redimensionnement à chaud vers le programme : V2 d'Os-x86 (D27).

## Options écartées

- **A. Demander le mode brut complet dès V1** (215 C) : D27 l'a reporté en
  V2 ; les touches que Kazo garde ne peuvent de toute façon pas être livrées
  sans verrou clavier (Chromium seulement).
- **B. Sans l'accord Échap** (215 B) : EDIT ne quitte pas par son menu,
  `nano` sans Méta, `mc` infirme (E14).
- **C. Ctrl+C ferme la commande et tue le cgroup** (216 B) : plus
  d'interruption douce d'un programme en boucle ; les programmes attendent
  SIGINT.
- **D. Couleurs 256 ou truecolor dès maintenant** : v86 livre 16 attributs
  VGA ; Wine `cmd` par pty en utilise 16 ; le coût de rendu monte sans usage.
- **E. Que le Terminal ne référence pas `Os-x86.Interface`** et laisse la
  commande `x86` de la Feature tenir seule le contrat plein écran (première
  rédaction de cette ADR) : écarté par le mainteneur le 2026-11-11 (§1) ; le
  Terminal doit pouvoir héberger des sessions d'Os-x86 et exécuter lui-même
  un programme console (162 A), ce qu'un handler lancé par le shell ne
  couvre pas ; l'OS reste sans connaissance de Wine ni de v86, seule l'app
  Terminal connaît `Ix86`.
- **F. Un hook du Terminal dans le shell par nom d'app** (le shell teste
  `org.kazoos.terminal`) : `ProgramHost` dans `KazoShellOptions` fait la
  même chose sans que le shell connaisse une app.

## Conséquences

- App Terminal (`Apps/System/Kazo.Os.Apps.Terminal`) : référence
  `Os-x86.Interface` (bibliothèque partagée, ADR 0244), `ProgramHost`,
  lecture de l'en-tête, exécution par `Ix86`, onglet « session Os-x86 » ;
  manifeste : permissions `x86.*` de l'ADR 0246 déclarées comme BoxedWine.
- SDK (`Kazo.Os.AppSdk`) : `TerminalCell`, `TerminalColor`, `TerminalCellStyle`,
  `TerminalScreenFrame.FromCells`, `TerminalKey.F1`…`F12`, `Insert`, `Tab`,
  `Break`, `IFullScreenTerminalSession.Options`, `KazoShellOptions.ProgramHost`,
  `IKazoShellProgramHost` ; `PublicAPI.Unshipped.txt`, fixture `sdk-compat`,
  version mineure du SDK. Shell : la route 2 de l'ADR 0251 demande d'abord à
  l'hôte.
- `Kazo.Os.AppSdk.Terminal` : décodeur (F-touches, Insert, accord Échap avec
  délai), rendu par cellules et par rangées changées, bouton « Arrêter »,
  Ctrl+C sans copie en plein écran, barre d'état de l'accord ;
  `TerminalPanel.xterm.entry.js` (Ctrl+C avec sélection seulement hors
  plein écran).
- Docs : [`terminal-api.md`](../../framework/terminal-api.md),
  [`terminal-emulator.md`](../terminal-emulator.md) (commandes plein écran,
  touches), `features.md`, entrée `changes/unreleased`.
- Tests : `ProgramHost` : fichier pris par l'hôte → pas de handler binfmt ;
  hôte sans `ProgramHost` ou `Ix86` null → route de l'ADR 0251 inchangée ;
  OS rogné sans Os-x86 → le Terminal démarre et `./x.exe` rend le message de
  l'ADR 0251 ; décodage de chaque F-touche et d'Insert ; accord Échap+e → Alt+e,
  Échap+3 → F3, Échap+Échap → Échap, Échap seul après 500 ms ; Ctrl+C livré
  en plein écran même avec une sélection ; Ctrl+Pause et le bouton → 130 ;
  trame à cellules rendue, rangées inchangées non réécrites ; `htop` et
  `nano` inchangés.

## Questions posées à Benoit

1. **Palette des attributs par cellule ?**
   - A) **(Recommandé)** 16 couleurs ANSI (VGA, `cmd`), styles gras,
     souligné, inverse ; 256 couleurs et truecolor en V2 d'Os-x86.
   - B) 256 couleurs dès maintenant (xterm.js les rend ; un shell Linux les
     utilise avec `TERM=xterm-256color`).
2. **Tab comme touche nommée ?**
   - A) **(Recommandé)** Ajouter `TerminalKey.Tab` et livrer les deux
     formes pendant une version, puis `Tab` seul (ADR 0019, dépréciation).
   - B) Garder `Character "\t"` seulement.

Appliquées en attendant : 1 A, 2 A.

Point réglé le 2026-11-11 : le Terminal dépend-il d'`Os-x86.Interface` → oui,
en dépendance facultative, utilisée comme BoxedWine le fait (§1).
