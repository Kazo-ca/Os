> [English version](../../../../en/wasm/dev/adr/0276-welcome-wizard-at-first-sign-in.md)

# ADR 0276 : un assistant de bienvenue à la première ouverture de session

## Statut

Acceptée le 2026-11-15 : Benoit a choisi Q1 = A, Q2 = A, Q3 = A, Q4 = C et
Q5 = A (voir [Questions](#questions-posées-à-benoit)). Série
`first-run-theme/base`.

- **Complète :** l'[ADR 0104](0104-localization.md) §2
  (un changement de langue attend la prochaine ouverture de session) :
  l'assistant fait partie de l'ouverture de session, sa langue s'applique tout
  de suite.
- **S'appuie sur :** l'[ADR 0044](0044-settings-as-files.md) §8 et §10
  (réglages en fichiers, état local de l'utilisateur), l'[ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  (thèmes), l'[ADR 0202](0202-desktop-wallpaper-images.md) et
  l'[ADR 0217](0217-mobile-home-wallpaper.md) (fonds d'écran),
  l'[ADR 0193](0193-local-time-and-time-zone.md) (fuseau horaire),
  l'[ADR 0187](0187-guest-start-and-sign-in-credits.md) (compte invité) et
  l'[ADR 0258](0258-direct-app-launch-link.md) (lien direct).

## Contexte

Benoit, 2026-11-15 :

> à la première connection, j'aimerais que l'utilisateur puisse choisir son thème.

Kazo OS a 7 thèmes de bureau et 2 thèmes mobiles, des fonds d'écran, deux
langues et un fuseau horaire réglable, mais un nouveau visiteur arrive sur le
thème par défaut, en anglais, sans savoir que ces choix existent : il faut
ouvrir les Paramètres pour les trouver. Les vrais systèmes posent ces
questions une fois, avant le premier bureau.

Chaque réglage est déjà un fichier de l'utilisateur, lu en direct par le shell
(sauf la langue, lue une fois par session). Il ne manque que l'écran qui les
propose au bon moment, et la mémoire de « déjà fait ».

## Décision

1. **Un écran plein avant le bureau (Q1 A).** L'hôte gagne la vue
   `EcosystemHostView.Welcome`, affichée entre la progression d'ouverture de
   session et le bureau. La session est déjà ouverte : l'assistant écrit dans
   les fichiers de l'utilisateur, avec ses droits. Le bureau (et donc
   `MarkSystemStarted`, la notification de nouveautés et le lien direct)
   n'apparaît qu'à la fin.
2. **Quatre étapes (Q4 C)**, dans cet ordre, chacune appliquée dès le clic,
   comme dans les Paramètres :
   1. **Langue** : « Suivre la langue du système » ou une des langues de
      `KazoLocale.AvailableLocales` (sans `C.UTF-8`). Le choix est la ligne
      `LANG` de `~/.config/locale.conf` ; les autres variables du fichier
      sont gardées. L'environnement d'ouverture de session est relu aussitôt
      (`LoginEnvironmentProvider.ReloadAsync`), donc l'assistant, le bureau et
      les apps lancées ensuite parlent la langue choisie. Les processus déjà
      démarrés par l'ouverture de session gardent la leur.
   2. **Thème** : les thèmes intégrés du format de l'appareil et les thèmes de
      l'utilisateur (ADR 0103), avec l'aperçu `ThemePreview` des Paramètres.
      Tout l'écran prend le thème dès le clic (`desktopThemeId` ou
      `mobileThemeId` de `appearance.config`).
   3. **Fond d'écran** : « Fond du thème » ou un des fonds intégrés
      (`WallpaperCatalog.BuiltIn`) : clé `wallpaper` sur ordinateur,
      `mobileWallpaper` sur téléphone. Un fichier personnel se choisit plus
      tard dans les Paramètres.
   4. **Fuseau horaire** : « Automatique » (le fuseau de l'appareil) ou un
      fuseau de `KazoTimeZone.Available`, avec recherche et l'heure qu'il
      donne (`timeZone` de `datetime.config`).

   Boutons : **Précédent**, **Suivant**, **Terminer** à la dernière étape, et
   **Passer** qui ferme l'assistant en gardant ce qui a déjà été choisi. Un
   texte rappelle que tout se change dans les Paramètres.
3. **Qui le voit (Q2 A).** Chaque compte, à sa première ouverture de session,
   invité compris. Avec un lien direct `?autolaunch=<app>` en attente,
   l'assistant est sauté pour cette session, sans être marqué fait : il
   apparaît à l'ouverture de session suivante.
4. **Mémoire par compte et par format (Q3 A, Q5 A).** Le fichier
   `~/.local/state/kazoos/welcome.json` (`{"schemaVersion":1,"completed":["desktop"]}`)
   liste les formats (`desktop`, `mobile`) pour lesquels l'assistant est
   terminé ou passé. Fichier absent ou illisible : rien n'est fait.
   - Aucun format fait : les quatre étapes. Un compte qui existait avant la
     mise à jour n'a pas le fichier, il voit donc l'assistant une fois, avec
     ses réglages actuels sélectionnés.
   - L'autre format déjà fait : seulement **Thème** et **Fond d'écran**, qui
     sont propres au format ; la langue et le fuseau sont communs.
   - Le format courant fait : rien.

   L'état est local à l'appareil (ADR 0044 §10) : il n'est pas synchronisé.
5. **Réglages verrouillés.** Une étape dont la clé est verrouillée par
   l'administrateur (`/etc/kazoos/*.config`) n'est pas montrée. S'il ne reste
   aucune étape, le format est marqué fait sans écran.
6. **Jamais bloquant.** Si l'état ne peut pas être lu (erreur de stockage),
   l'assistant n'est pas montré. Si une écriture échoue, l'étape affiche
   l'erreur et l'utilisateur peut continuer. Si l'état ne peut pas être écrit,
   le bureau s'ouvre quand même ; l'assistant reviendra.
7. **Où est le code.** `WelcomeSetupService` (Platform Core, dossier
   `Welcome/`) porte l'état, le plan des étapes et l'écriture de la langue ;
   le composant `WelcomeWizard` est dans l'hôte (`Kazo.Os.Ecosystem.Host`), à
   côté de `LoginProgressScreen`. Aucune API du SDK des apps ne change.

## Conséquences

- Un nouveau visiteur choisit sa langue et son apparence avant de voir le
  bureau ; un visiteur pressé clique « Passer » une fois.
- Les comptes existants voient l'assistant une fois après la mise à jour.
- **Tests de bout en bout.** Tout parcours qui crée un profil puis attend le
  bureau doit passer l'assistant. Les aides de test de ce dépôt le font
  (`E2ESupport`, `PwaTestSupport` et les parcours directs). Les dépôts d'apps
  (`tests/e2e/host.mjs`, `tests/installed/*.mjs`) cassent le jour où ils
  passent à la version de l'OS qui contient l'assistant ; la correction est
  un clic sur « Passer ». Une issue `TODO` la suit.
- La langue choisie dans l'assistant s'applique dans la session, alors que
  celle des Paramètres attend toujours la prochaine ouverture de session
  (ADR 0104 §2 inchangée pour les Paramètres).

## Questions posées à Benoit

Posées dans le fil le 2026-11-15. Réponse de Benoit : « Q4. C, pour le reste je
suis d'accord avec tes recommendations ».

**Q1. Où le choix apparaît-il ?**

- **A (recommandé, choisi) :** un écran « Bienvenue » plein écran, entre la
  progression d'ouverture de session et le bureau.
- **B :** une fenêtre de bienvenue sur le bureau. Presque aucun test ne casse,
  mais la fenêtre peut être fermée ou cachée par mégarde.
- **C :** dans le formulaire de création du premier compte. Ne couvre ni
  l'invité ni les comptes créés plus tard.

**Q2. Qui voit cet écran ?**

- **A (recommandé, choisi) :** chaque compte à sa première ouverture de
  session, invité compris ; sauté quand on arrive par un lien direct.
- **B :** tous sauf l'invité.
- **C :** seulement le premier compte de la machine.

**Q3. Les comptes qui existent déjà au moment de la mise à jour ?**

- **A (recommandé, choisi) :** ils voient l'écran une fois, avec leurs
  réglages actuels sélectionnés.
- **B :** seuls les nouveaux comptes ; demande une migration qui marque les
  comptes existants.

**Q4. Que contient l'écran ?**

- **A (recommandé) :** le thème seulement, avec « Passer ».
- **B :** le thème et la couleur d'accent.
- **C (choisi par Benoit) :** un assistant à plusieurs étapes : langue, thème,
  fond d'écran, fuseau horaire.

**Q5. Bureau et téléphone ?**

- **A (recommandé, choisi) :** les thèmes de l'appareil utilisé ; l'autre
  format demande son thème une fois, à sa première session.
- **B :** les deux listes d'un coup.
- **C :** une seule fois, pour le format du jour.

## Tâches

- [ ] `WelcomeSetupService` (état, plan des étapes, langue) et
  `LoginEnvironmentProvider.ReloadAsync`, avec tests.
- [ ] Vue `Welcome` de l'hôte et composant `WelcomeWizard` (quatre étapes,
  bureau et téléphone, EN/FR).
- [ ] Tests : unitaires, E2E `WelcomeWizardTests`, aides de test mises à jour.
- [ ] Docs FR/EN (`welcome-wizard.md`, `features.md`, mémoire), captures.
- [ ] Issue `TODO` pour les tests des dépôts d'apps.
