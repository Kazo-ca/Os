> [English version](../../../../en/wasm/dev/plans/cloud-drives-plan.md)

# Plan de la série « lecteurs cloud » (`cloud-drives/base`)

Validé par Benoit le 2026-11-13. Décisions :
[ADR 0267](../adr/0267-cloud-drives-as-network-drives.md). Le stockage cloud de
l'utilisateur (Dropbox, OneDrive, S3 compatible) se monte comme un lecteur
réseau, sans serveur et sans coût.

## But

- L'utilisateur connecte **son** stockage ; Kazo OS n'en fournit pas.
- Tout se passe dans le navigateur : Kazo OS reste un site statique.
- Première version : Dropbox, OneDrive, S3 compatible. Google Drive et WebDAV
  n'en font pas partie (ADR 0267, section 7).

## Chantiers

| # | Branche | Contenu | Dépend de |
| --- | --- | --- | --- |
| 0 | `cloud-drives/0-adr` | ADR 0267, ce plan, `features.md`, mémoire. **Fait.** | — |
| 1 | `cloud-drives/1-drive-sources` | `NetworkDriveService` à sources multiples (ADR 0267 §3), table des lecteurs avec type et compte, interfaces facultatives de quota et de révision (§4), `/proc/mounts`, `df`, `KazoDriveInfo`. Une source distante factice en mémoire sert aux tests. Aucun fournisseur réel. **Fait** ([`network-drive-sources.md`](../network-drive-sources.md)). | 0 |
| 2 | `cloud-drives/2-cloud-accounts` | Service de comptes cloud (§5) : PKCE, fenêtre de connexion, page de retour statique et `BroadcastChannel`, jetons dans le trousseau, renouvellement, retrait, identifiants d'app dans les réglages. Faux serveur OAuth pour les tests E2E. Vérifie le trousseau du compte invité. Donne à Benoit les adresses de retour exactes à inscrire. **Fait** : [`cloud-accounts.md`](../cloud-accounts.md). | 0 |
| 3 | `cloud-drives/3-dropbox` | Connecteur Dropbox (API v2), les deux étendues (§8), suite de tests de contrat commune à tous les connecteurs. **Fait** : [`cloud-drive-connectors.md`](../cloud-drive-connectors.md). | 1, 2 |
| 4 | `cloud-drives/4-onedrive` | Connecteur Microsoft Graph, les deux étendues, reconnexion après 24 heures. | 1, 2 |
| 5 | `cloud-drives/5-ui` | Dialogue « Connecter un lecteur réseau » avec les fournisseurs et le choix de l'étendue, page Paramètres des comptes cloud, explorateur mobile, commandes `mount` et `umount` (§11). Textes en français et en anglais. **Fait** ([`cloud-drives-ui.md`](../cloud-drives-ui.md), [ADR 0270](../adr/0270-cloud-drives-in-the-app-sdk.md)). | 1, 2 |
| 6 | `cloud-drives/6-s3` | Connecteur S3 compatible (SigV4), clés dans le trousseau, guide de la règle CORS. | 1 |
| 7 | `cloud-drives/7-final` | Vérification de la série : build, toutes les suites, publication élaguée, essais avec de vrais comptes, doc `cloud-drives.md`, `features.md`. | 3 à 6 |

Les chantiers 1 et 2 vont en parallèle. Les chantiers 3, 4, 5 et 6 vont en
parallèle ensuite ; le chantier 5 se développe avec la source factice du
chantier 1.

## Règles de la série

- Une branche par chantier, créée depuis `cloud-drives/base`, une PR vers
  `cloud-drives/base`. Jamais de fusion dans `main` : Benoit décide.
- Chaque PR ajoute son entrée de changement (ADR 0186) et met à jour les docs
  en français et en anglais.
- Barre de fusion : build et tests locaux verts, suites E2E une à la fois,
  `build/publish-trimmed.sh` quand le chantier touche le code publié.
- Les tests automatiques n'appellent jamais un vrai fournisseur : faux
  gestionnaire HTTP et faux serveur OAuth.
- Aucun secret dans le dépôt. Les identifiants d'app sont publics ; les secrets
  d'app que les consoles affichent ne servent pas et ne sont jamais copiés.
- Aucun jeu sur ce repository.

## Ce que Benoit crée lui-même

Tout est gratuit. Les adresses de retour exactes viennent du chantier 2
([`cloud-accounts.md`](../cloud-accounts.md#adresses-de-retour-à-inscrire)) ; les
inscriptions peuvent se préparer avant et recevoir les adresses ensuite.

1. **Deux apps Dropbox** (console des développeurs Dropbox, « Scoped access ») :
   - une app de type **Full Dropbox** et une app de type **App folder** (son
     nom devient le dossier `Apps/<nom>` ; un nom d'app ne peut pas contenir
     « Dropbox ») ;
   - permissions des deux : `account_info.read`, `files.metadata.read`,
     `files.metadata.write`, `files.content.read`, `files.content.write` ;
   - adresses de retour : celle d'os.kazo.ca et celles de `localhost` pour le
     développement ;
   - « Allow public clients (Implicit Grant & PKCE) » activé ;
   - à remettre : les deux **App key**. Les « App secret » ne sont pas utilisés.
   - Plus tard : demander la production quand une app approche 50 utilisateurs
     (revue gratuite ; icône et description demandées).
2. **Une inscription d'app Microsoft Entra** (centre d'administration Entra,
   « Inscriptions d'applications » ; un locataire Entra gratuit suffit) :
   - types de comptes : comptes de tout annuaire d'organisation **et comptes
     Microsoft personnels** ;
   - plateforme **« Application monopage »** (SPA) avec les mêmes adresses de
     retour ;
   - autorisations déléguées de Microsoft Graph : `Files.ReadWrite`,
     `Files.ReadWrite.AppFolder`, `offline_access`, `User.Read` ;
   - à remettre : l'**ID d'application (client)**. Aucun secret à créer.
3. **Une page de politique de confidentialité** sur os.kazo.ca. Elle dit que
   les jetons et les fichiers restent dans le navigateur de l'utilisateur et
   que Kazo OS n'a aucun serveur qui les reçoit. Dropbox la demande pour la
   production. Le chantier 7 en propose le texte.
4. **Un essai par connecteur avec un vrai compte**, avant la fusion de la série
   dans `main`, et un essai de la PWA installée sur iOS.

Rien à créer pour S3 : l'utilisateur apporte son compartiment et ses clés.

## Preuves faites avant la série

Le 2026-11-13, depuis Chromium sur une page isolée comme Kazo OS : les API de
fichiers et de jetons de Dropbox, de Google et de Microsoft Graph répondent et
sont lisibles ; Box est bloqué. Non vérifié, faute de compte : un échange PKCE
complet et les durées réelles des jetons. Les chantiers 3 et 4 les confirment
avec les apps de Benoit.
