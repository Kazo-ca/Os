> [English version](../../../en/wasm/dev/legal-pages.md)

# Politique de confidentialité et conditions d'utilisation

> Ce document et les pages qu'il décrit ne sont pas un avis juridique. Ils
> décrivent ce que fait le code ; faites-les relire par un juriste avant de
> vous y fier.

## But

Dropbox (passage en production) et Microsoft Entra (vérification d'éditeur,
« Personnalisation ») demandent une politique de confidentialité et des
conditions d'utilisation publiques pour approuver les lecteurs cloud
([`cloud-drives.md`](cloud-drives.md)). Elles servent aussi aux utilisateurs :
elles disent exactement ce que Kazo OS fait des données.

## Adresses

| Page | Adresse stable | Langues |
| --- | --- | --- |
| Politique de confidentialité | `https://os.kazo.ca/privacy.html` (`#fr`, `#en`) | Français puis anglais, sur la même page |
| Conditions d'utilisation | `https://os.kazo.ca/terms.html` (`#fr`, `#en`) | Français puis anglais, sur la même page |

Ce sont des pages statiques de l'hôte (`OS/Kazo.Os.Ecosystem/wwwroot/`), sans
WebAssembly ni script, stylées par `legal.css`. Le service worker publié les
garde hors ligne ; la bêta les sert aussi (`os-beta.kazo.ca/privacy.html`).

## Où elles sont liées

| Endroit | Composant | Lien |
| --- | --- | --- |
| Pied de page de l'écran de connexion et de récupération | `OS/Kazo.Os.Ecosystem.Host/ProjectInformation.razor` (`a.project-privacy`, `a.project-terms`) | `privacy.html#fr` ou `#en` selon la langue des textes (`Project.LegalSection`) |
| Paramètres ▸ À propos | `Apps/System/Kazo.Os.Apps.Settings/SettingsWindow.razor` (`a.about-privacy`, `a.about-terms`) | idem (`About.LegalSection`) |
| Écrans de consentement | Console Dropbox, Entra ▸ Personnalisation | adresses ci-dessus |

## Faits vérifiés dans le code (2026-11-17)

Chaque phrase des pages repose sur ces faits. Un changement de l'un d'eux
demande de mettre les pages à jour et leur date « En vigueur le ».

| Fait | Source |
| --- | --- |
| Aucun serveur de l'éditeur : os.kazo.ca est servi par GitHub Pages, la bêta par Cloudflare Pages | [`github-pages-deployment.md`](github-pages-deployment.md), [`beta-site.md`](beta-site.md), `kazoos-host.json` (`isServer: false`) |
| Comptes, fichiers, réglages dans IndexedDB ; mots de passe hachés ; trousseau chiffré | [`sensitive-settings.md`](sensitive-settings.md) |
| OAuth avec PKCE, sans secret ; permissions Graph `Files.ReadWrite` ou `Files.ReadWrite.AppFolder`, `User.Read`, `offline_access` | `Platform.Core/CloudAccounts/CloudOAuthProvider.cs` |
| Identité lue : nom affiché et identifiant du compte (`users/get_current_account`, `/me`) | `CloudOAuthProvider.cs`, `CloudAccountStore.cs` (`DisplayName`, `ProviderAccountId`) |
| Jetons scellés AES-256-GCM sur l'appareil, clé dans le trousseau, jamais synchronisés | [ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md), [`cloud-accounts.md`](cloud-accounts.md) |
| Retrait : démontage, effacement, révocation Dropbox | `CloudAccountService.cs` (`RemoveAsync`) |
| Police Roboto servie par le site (aucun appel à Google Fonts) | `OS/Kazo.Os.Ecosystem.Host/wwwroot/fonts/roboto/`, `OS/Kazo.Os.Ecosystem/wwwroot/index.html` |
| Catalogue du Store et paquets sur `raw.githubusercontent.com` | `Platform.Core/Apps/Packages/AppPackageDefaults.cs`, `AppPackageSource.cs` |
| Réseau réel seulement avec `network.real.access`, approuvée par un administrateur | [`dangerous-capability-approval.md`](dangerous-capability-approval.md) |
| Kazo AI : clé envoyée seulement au service choisi ; modèle local depuis Hugging Face | dépôt `Kazo-ca/Os-App-AI`, `doc/fr/providers.md` |
| Serveur facultatif : synchronisation du dossier personnel (donc du trousseau chiffré), relais réseau | [`synchronization.md`](synchronization.md), [`server-network-relay.md`](server-network-relay.md) |
| Licence du code : CC BY-NC 4.0 | `LICENSE.md` |

## Décisions de Benoit (2026-11-17)

- Éditeur : Benoit Robin, particulier, Québec (Canada), aussi responsable de la
  protection des renseignements personnels (Loi 25).
- Contact publié : `confidentialite@kazo.ca` (adresse dédiée).
- Loi applicable : lois du Québec et lois fédérales du Canada, tribunaux du
  Québec, sous réserve des droits des consommateurs.
- Google Fonts : retiré ; Roboto (`@fontsource/roboto` 5.3.0, SIL OFL 1.1,
  sous-ensembles latin et latin-ext, graisses 300 à 700) est servie par le
  site depuis `_content/Kazo.Os.Ecosystem.Host/fonts/roboto/roboto.css`.
- Une seule paire de pages bilingues pour tous les fournisseurs (choix du
  2026-11-15, [`cloud-accounts.md`](cloud-accounts.md#pages-publiques-pour-les-fournisseurs)).

## À faire par Benoit

- Créer ou rediriger `confidentialite@kazo.ca`.
- Inscrire les deux adresses dans la console Dropbox (les deux apps) et dans
  Entra ▸ Personnalisation.
- Vérification d'éditeur Microsoft : un identifiant du programme partenaire
  Microsoft (Microsoft AI Cloud Partner Program) et le domaine `kazo.ca`
  vérifié dans Entra.

## Tests

- `Tests/Kazo.Os.Ecosystem.Tests/ProjectInformationTests.cs` : les liens du
  pied de page et leur section selon la langue (anglais, français, autre).
- `Tests/Kazo.Os.Apps.Settings.Tests/SettingsWindowTests.cs` : libellés de la
  ligne « Mentions légales » d'À propos.
- `Tests/Kazo.Os.Pwa.E2E.Tests/PublishedPagesTests.cs` : les liens sur la PWA
  publiée (connexion et À propos).
- `Tests/Kazo.Os.Pwa.E2E.Tests/CloudSignInWindowTests.cs` : les deux pages sont
  statiques, sans script, servies et gardées hors ligne par le service worker.

## Liste des tâches

- [x] Lire le code des lecteurs cloud, du Store, du serveur facultatif et de Kazo AI.
- [x] Réécrire `privacy.html` et `terms.html` (FR + EN).
- [x] Lier les pages depuis le pied de page de connexion et À propos.
- [x] Tests unitaires et E2E.
- [x] Documentation FR + EN et index.
- [x] Héberger Roboto sur le site et retirer Google Fonts de la politique.
