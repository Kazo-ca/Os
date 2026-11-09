> [English version](../../../../en/wasm/dev/adr/0240-detailed-error-page.md)

# ADR 0240 : une page d'erreur détaillée, avec les secrets masqués

## Statut

Proposée le 2026-11-08, avec les recommandations de « Questions posées à
Benoit » appliquées en attendant sa réponse. Demande de départ : « On pourrait
afficher plus d'informations lors d'une erreur ? L'application est quand même
open source, quel genre d'informations sensibles on pourrait afficher ? »

- **Complète :** l'[ADR 0045](0045-logs-as-files.md) (la ligne `CRIT` de
  `HostExceptionReporter` dans `/var/log/syslog` reçoit le message et la pile).
- **Ne change pas :** la page reste celle de `HostErrorBoundary` dans
  `App.razor` ; une app qui plante emporte toujours tout l'OS (hors sujet ici).

## Contexte

- Quand un composant lève une exception non gérée, `HostErrorBoundary`
  remplace tout l'OS par « Kazo OS encountered an error », la phrase « The
  failure was recorded without exposing sensitive details » et
  « Correlation: ». La pile n'était visible qu'en développement.
- **La corrélation était toujours vide.** `ErrorBoundaryBase` appelle
  `OnErrorAsync`, puis dessine le contenu d'erreur sans l'attendre.
  L'identifiant n'était attribué qu'après l'écriture du journal (un `await`),
  et rien ne redessinait la page ensuite.
- Le journal ne gardait que le type d'exception : impossible de diagnostiquer
  après coup.
- Le code de Kazo OS est public : une pile d'appels ne révèle rien. Ce qui peut
  être privé, c'est **le contenu de l'utilisateur** qui finit dans un message :
  une adresse avec mot de passe (`wss://moi:motdepasse@…`), un jeton
  (`?access_token=…`), une valeur de configuration, une commande IRC `PASS`.
  Sur son propre écran, ce n'est pas un risque ; il le devient quand la
  personne fait une capture ou colle le texte dans une issue publique.

## Décision

1. **`HostFailureDetails`** (`OS/Kazo.Os.Ecosystem.Host/`) décrit une erreur de
   façon synchrone : identifiant de corrélation, heure, phase, type de
   l'exception, message, module et méthode d'origine, et le texte complet
   (`Exception.ToString()`, exceptions internes comprises).
   - La **source** est la première ligne de la pile hors du cadre .NET et
     Blazor (`System.`, `Microsoft.`, `Mono.`, `Interop.`, `MudBlazor.`), en
     préférant une app (`Kazo.Irc.Components.MessagePane.OnAfterRenderAsync`)
     à une ligne de Kazo OS (`Kazo.Os.*`). Une ligne de machine d'état
     asynchrone (`<Méthode>d__5.MoveNext`) redevient `Méthode`. La pile est lue
     comme texte : `StackFrame.GetMethod` est interdit par le découpage
     (*trimming*).
   - Le **module** est le plus long nom d'assemblage chargé qui préfixe la
     source (`Kazo.Irc`).
2. **`HostErrorBoundary.Failure`** est rempli **avant le premier `await`** de
   `OnErrorAsync` (`HostExceptionReporter.Describe`), puis le journal est écrit
   (`HostExceptionReporter.ReportAsync(HostFailureDetails)`). La page montre
   donc la corrélation dès son premier rendu. `CorrelationId` reste et lit
   `Failure`.
3. **`SensitiveTextRedactor`** (`Platform.Core/Diagnostics/`) masque par
   `***redacted***`, à l'écran, dans le texte copié et dans le journal :
   - le mot de passe d'une adresse (`schéma://utilisateur:***redacted***@hôte`) ;
   - la valeur d'une paire sensible `clé=valeur` ou `clé: valeur`, guillemets
     JSON compris (`password`, `passwd`, `pwd`, `passphrase`, `secret`,
     `client_secret`, `token`, `access_token`, `refresh_token`, `id_token`,
     `apikey`, `api_key`, `api-key`, `authorization`, `credential(s)`,
     `cookie`, `sig`, `signature`) ;
   - la valeur `Bearer` ou `Basic` d'un en-tête ;
   - les commandes IRC `PASS` et `IDENTIFY`.
   Les **chemins de fichiers restent visibles** : ils expliquent la plupart des
   erreurs et ne nomment que les fichiers de la personne.
4. **`HostFailurePanel`** est le corps de la page :
   - toujours visible : module, source, exception, message, heure locale,
     corrélation (sélectionnable d'un clic) ;
   - repliée : « Détails techniques », la pile complète ;
   - boutons **« Copier les détails »** (en-tête court puis pile, prêt à coller
     dans une issue ; module JS du composant `HostFailurePanel.razor.js`, pour
     ne pas dépendre du presse-papiers de l'OS qui peut être la partie en
     panne) et **« Recharger »** (rechargement complet de la page) ;
   - une phrase invite à relire le texte avant de le publier.
   La phrase « The failure was recorded without exposing sensitive details »
   devient « L'erreur a été enregistrée dans /var/log/syslog. Les mots de passe
   et les jetons sont masqués. »
5. La ligne `CRIT` de `syslog` reçoit les propriétés `message`, `stack`,
   `source` et `module`, masquées de la même façon (`LogLineFormat` garde les
   retours à la ligne échappés sur une seule ligne).

## Conséquences

- Une erreur d'app se diagnostique depuis une capture ou un texte copié, sans
  ouvrir la console du navigateur.
- Le masquage repose sur des motifs connus : un secret sans forme reconnaissable
  (un mot de passe seul dans un message) reste visible. La phrase sous les
  boutons le rappelle.
- `Kazo.Os.Ecosystem.Host` donne ses membres internes à
  `Kazo.Os.Ecosystem.Tests` (lecture de la pile testée directement).

## Questions posées à Benoit

Posées le 2026-11-08 dans le fil « Afficher plus de détails sur la page
d'erreur » ; les recommandations sont appliquées.

1. **Quoi afficher ?** A (recommandé) : résumé visible, pile repliable,
   « Copier les détails » et « Recharger ». B : tout déplié. C : type et
   message seulement.
2. **Masquer les secrets ?** A (recommandé) : motifs connus masqués à l'écran
   et dans la copie, chemins visibles. B : rien masquer. C : masquer seulement
   dans la copie.
3. **Le journal `/var/log/syslog` ?** A (recommandé) : y écrire aussi le
   message et la pile masqués. B : ne changer que la page.

## Liste des tâches

- [x] `HostFailureDetails` et lecture de la source et du module.
- [x] `SensitiveTextRedactor` (Platform.Core) et ses tests.
- [x] `HostExceptionReporter.Describe` et `ReportAsync(HostFailureDetails)` ;
  message et pile dans `syslog`.
- [x] `HostErrorBoundary.Failure` rempli avant le rendu (corrélation vide
  corrigée).
- [x] `HostFailurePanel` (résumé, détails repliés, copier, recharger), textes
  anglais et français.
- [x] Tests de rendu, de rapport et de masquage ; docs FR et EN.
