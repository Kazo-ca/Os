> [English version](../../../en/wasm/dev/welcome-wizard.md)

# Assistant de bienvenue à la première connexion

Décision : [ADR 0276](adr/0276-welcome-wizard-at-first-sign-in.md). Série
`first-run-theme/base`.

## But

À sa première ouverture de session, l'utilisateur choisit sa langue, son
thème, son fond d'écran et son fuseau horaire avant que le bureau s'ouvre.
Chaque choix s'applique au clic : l'écran change de langue ou de thème tout de
suite. L'assistant n'est jamais obligatoire (« Passer ») et tout reste
modifiable dans Paramètres.

## Pour l'utilisateur

| Situation | Ce qui se passe |
| --- | --- |
| Première connexion d'un compte (compte créé, invité, compte ajouté par un administrateur) | L'assistant s'affiche en plein écran entre la connexion et le bureau, avec quatre étapes : langue, thème, fond d'écran, fuseau horaire. |
| Compte qui existait avant cette version | Il voit l'assistant une fois, à sa prochaine connexion. Ses réglages actuels sont déjà sélectionnés. |
| Connexions suivantes | Le bureau s'ouvre directement. |
| Première session sur l'autre format (téléphone après ordinateur, ou l'inverse) | L'assistant demande seulement le thème et le fond d'écran de ce format, une fois. |
| Arrivée par un lien direct `?autolaunch=<appId>` | L'assistant ne s'affiche pas : l'app s'ouvre. Il s'affichera à la prochaine connexion. |
| Réglage verrouillé par l'administrateur (`/etc/kazoos/*.config`) | L'étape de ce réglage n'est pas montrée. Si aucune étape ne reste, le bureau s'ouvre directement. |
| Une mise à jour est prête pendant l'assistant | Le bandeau « Mise à jour prête » des écrans de connexion ne s'affiche pas sur l'assistant (il couvrirait ses boutons) : la notification attend sur le bureau. |

Les boutons du bas : **Passer** (ouvre le bureau, l'assistant ne revient
plus), **Précédent**, **Suivant**, et **Terminer** à la dernière étape. Un
choix déjà fait reste fait quand on passe.

## Étapes

| Étape | Choix proposés | Fichier écrit |
| --- | --- | --- |
| Langue | « Langue du système » ou une langue de l'OS (`KazoLocale.AvailableLocales`, sans `C`) | `~/.config/locale.conf` (`LANG=…`) |
| Thème | Les thèmes du format de l'appareil : 7 sur ordinateur, 2 sur téléphone, plus les thèmes de l'utilisateur | `~/.config/kazoos/appearance.config` (`desktopThemeId` ou `mobileThemeId`) |
| Fond d'écran | Le fond du thème ou une des images livrées (`WallpaperCatalog.BuiltIn`) | `appearance.config` (`wallpaper` ou `mobileWallpaper`) |
| Fuseau horaire | « Automatique » (le fuseau de l'appareil) ou un fuseau cherché par son nom ; l'heure de ce fuseau s'affiche | `~/.config/kazoos/datetime.config` (`timeZone`) |

La langue est appliquée à la session en cours : l'environnement de connexion
est relu (`LoginEnvironmentProvider.ReloadAsync`), puis la langue de la
session est rechargée. Le bureau s'ouvre donc dans la langue choisie.

## État par compte

`~/.local/state/kazoos/welcome.json` :

```json
{"schemaVersion":1,"completed":["desktop","mobile"]}
```

`completed` liste les formats où l'assistant a été terminé ou passé. Fichier
absent : rien n'est fait, l'assistant s'affiche. Fichier illisible ou écriture
refusée : le bureau s'ouvre quand même (l'assistant ne bloque jamais), une
ligne est écrite dans le journal, et l'assistant revient à la prochaine
connexion si l'état n'a pas pu être écrit.

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `WelcomeSetupService` | `Platform.Core/Welcome` | `GetPendingStepsAsync(format)` rend les étapes à montrer ; `MarkCompletedAsync(format)` écrit l'état ; `SetLanguageAsync`, `SetWallpaperAsync`, `SetTimeZoneAsync` écrivent les choix par `IOsSettingsService`. |
| `WelcomeStep` | `Platform.Core/Welcome` | `Language`, `Theme`, `Wallpaper`, `TimeZone`. |
| `LoginEnvironmentProvider.ReloadAsync` | `Platform.Core/Processes` | Oublie l'environnement gardé pour la session et le relit, pour prendre la langue écrite pendant la session. |
| `EcosystemHostView.Welcome`, `EcosystemHostState.BeginWelcome()` | `Ecosystem.Host` | La vue entre la connexion et le bureau. |
| `App.razor` (`EnterSessionAsync`) | `Ecosystem.Host` | Après chaque ouverture de session : pas de lien direct en attente et des étapes à montrer → vue `Welcome` ; sinon le bureau. |
| `WelcomeWizard` | `Ecosystem.Host` | Le composant (`.razor`, `.razor.cs`, `.razor.css`) : paramètres `Steps`, `Platform`, `OnFinished`. Le thème est écrit par `ThemePreferenceService`, les aperçus viennent de `ThemePreview`. Les couleurs viennent des variables du thème actif, donc l'écran se repeint avec le thème choisi. |

Le service est enregistré par `AddKazoOsEcosystemHost` ; un hôte qui ne
l'enregistre pas ouvre le bureau directement.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/Welcome/WelcomeSetupServiceTests.cs` : plan
  des étapes, état, verrous, écritures, erreurs.
- `Tests/Kazo.Os.Ecosystem.Tests/EcosystemHostStateTests.cs` : la vue `Welcome`.
- `Tests/Kazo.Os.UI.E2E.Tests/WelcomeWizardTests.cs` : parcours complet dans
  Chromium (français, thème, fond d'écran, fuseau, fichiers écrits, pas
  d'assistant à la deuxième connexion).
- Les autres tests qui ouvrent une session passent l'assistant avec
  `E2ESupport.PassWelcomeAsync(page)` (clic sur `.welcome-skip` s'il est
  affiché). Les sélecteurs utiles : `.welcome-surface`, `.welcome-skip`,
  `.welcome-next`, `.welcome-finish`, `[data-locale]`, `[data-theme-id]`,
  `[data-wallpaper]`, `[data-zone]`.
- Les tests des dépôts d'apps : issue
  [#553](https://github.com/Kazo-ca/Os/issues/553).

## Tâches

- [x] Service, état par compte et tests.
- [x] Vue de l'hôte et composant, ordinateur et téléphone, anglais et français.
- [x] Tests de bout en bout et aides de test.
- [x] Docs, `features.md`, entrée de changement.
