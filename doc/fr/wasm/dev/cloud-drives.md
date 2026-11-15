> [English version](../../../en/wasm/dev/cloud-drives.md)

# Lecteurs cloud : vue d'ensemble de la série

Document d'entrée de la série [`cloud-drives/base`](plans/cloud-drives-plan.md)
([ADR 0267](adr/0267-cloud-drives-as-network-drives.md),
[ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md),
[ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md)). Il dit ce que la série
livre, où trouver le détail de chaque pièce, ce qui a été vérifié, et les essais
réels qui restent à faire avant la fusion dans `main`.

## But

- L'utilisateur monte **son** stockage cloud comme un lecteur réseau sous
  `/mnt` : Dropbox, OneDrive (personnel ou professionnel) ou un stockage
  compatible S3 (AWS S3, Cloudflare R2, Backblaze B2, MinIO…).
- Tout se passe dans le navigateur. Kazo OS reste un site statique : aucun
  serveur ne reçoit un jeton ou un fichier, et rien ne coûte.
- Pour Dropbox et OneDrive, l'utilisateur choisit à la connexion entre **tout
  son stockage** et **un dossier réservé à Kazo OS** (ADR 0267 §8).
- Les apps passent par le SDK public, derrière la capacité dangereuse
  `cloud-accounts.manage` ; elles ne voient jamais de jeton.

## Ce que voit l'utilisateur

- **Explorateur de fichiers ▸ Connecter un lecteur réseau…** : dossier de
  l'ordinateur, comptes déjà connectés, Dropbox, OneDrive, S3. Après la
  connexion, le lecteur s'ouvre dans l'explorateur.
- **Paramètres ▸ Réseau ▸ Comptes cloud** : les comptes, leur état et leurs
  lecteurs ; Reconnecter, Retirer.
- **Terminal** : `mount -t dropbox|onedrive|s3 …`, `umount`, `df`,
  `/proc/mounts`.
- Un compte invité, ou une session ouverte par une clé d'accès sans PRF, n'a
  pas de trousseau : la fenêtre l'explique et ne propose que le dossier de
  l'ordinateur.

## Carte des documents

| Pièce | Chantier | Document |
| --- | --- | --- |
| Décisions | 0 | [ADR 0267](adr/0267-cloud-drives-as-network-drives.md), [plan](plans/cloud-drives-plan.md) |
| Sources de lecteurs réseau, quota, révision | 1 | [`network-drive-sources.md`](network-drive-sources.md) |
| Comptes cloud : PKCE, page de retour, jetons scellés, identifiants d'app, adresses de retour, pages publiques | 2 | [`cloud-accounts.md`](cloud-accounts.md), [ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md) |
| Base commune des connecteurs, Dropbox, suite de contrat | 3 | [`cloud-drive-connectors.md`](cloud-drive-connectors.md) |
| OneDrive (Microsoft Graph) | 4 | [`onedrive-drives.md`](onedrive-drives.md) |
| Fenêtre, page Paramètres, commandes, SDK | 5 | [`cloud-drives-ui.md`](cloud-drives-ui.md), [ADR 0270](adr/0270-cloud-drives-in-the-app-sdk.md) |
| S3 compatible et règle CORS | 6 | [`s3-drives.md`](s3-drives.md) |
| Vérification de la série, essais réels | 7 | ce document |

## Inscriptions de Kazo OS

Benoit a inscrit les apps le 2026-11-15. Les identifiants sont publics et sont
dans `OS/Kazo.Os.Ecosystem/wwwroot/appsettings.json`
([`cloud-accounts.md`](cloud-accounts.md#identifiants-dapp)) :

| Fournisseur | Inscription | Étendue | Identifiant |
| --- | --- | --- | --- |
| Dropbox | « Kazo Os Drive » (Full Dropbox) | Tout le stockage | `im7y4ma8wtsospi` |
| Dropbox | « Kazo OS Folder » (App folder) | Dossier réservé | `mcp8j0gt4h4v6s3` |
| Microsoft Entra | « Kazo OS » (application monopage, locataire `common`) | Les deux | `7f89ba67-5655-4721-991a-53c64998f45e` |

Aucun secret n'est utilisé ni copié (OAuth avec PKCE). Les pages publiques que
demandent les fournisseurs sont `https://os.kazo.ca/privacy.html` et
`https://os.kazo.ca/terms.html`. La section « Chooser, Saver et Embedder » de la
console Dropbox ne sert pas : Kazo OS n'utilise pas ces composants JavaScript,
il appelle l'API v2 directement.

## Vérification de la série

Faite le 2026-11-15 sur la branche `cloud-drives/7-final` (`cloud-drives/base`
avec `main` 3.3.4 fusionné). Les résultats détaillés sont dans la PR du
chantier 7.

- Build Release de `Kazo.Os.sln` : 0 erreur, 0 avertissement.
- Tests hors navigateur, une suite à la fois.
- Suites E2E, une à la fois.
- `build/publish-trimmed.sh` et `Kazo.Os.TrimmedPublish.E2E.Tests`.
- Compatibilité du SDK : `PublicAPI.*.txt` et `Kazo.Os.SdkCompatibility.Tests`.

Aucun test automatique n'appelle un vrai fournisseur : faux serveur OAuth, faux
Dropbox, faux Graph et faux S3 en mémoire.

## Essais réels

À faire par Benoit avant la fusion de la série dans `main`, sur un Kazo OS qui
contient la série : une préversion servie depuis `cloud-drives/base`, ou
`dotnet run` du projet Ecosystem sur `http://localhost:5000` (adresse de retour
déjà inscrite). Chaque essai note : réussi, échoué (avec la capture et le texte
de l'erreur), ou non fait.

### Préparation

1. Ouvrir une session avec un mot de passe (pas l'invité) : les comptes cloud
   ont besoin du trousseau.
2. Autoriser les fenêtres surgissantes pour le site si le navigateur les bloque.

### Dropbox, tout le stockage (« Kazo Os Drive »)

1. Explorateur ▸ Connecter un lecteur réseau ▸ Dropbox ▸ « Tout mon stockage
   Dropbox » ▸ Se connecter et monter. La fenêtre Dropbox s'ouvre, puis se
   ferme seule après l'acceptation.
2. Le lecteur s'ouvre et montre les dossiers du compte.
3. Ouvrir un fichier texte, le modifier dans l'Éditeur de texte, enregistrer ;
   vérifier le changement sur dropbox.com.
4. Créer un dossier, renommer un fichier, supprimer un fichier (confirmation
   demandée) ; vérifier sur dropbox.com.
5. Copier sur le lecteur un fichier d'environ 100 Mio : il arrive entier.
   Un fichier de plus de 128 Mio (limite de l'OS, ADR 0245) est refusé avec
   un message clair.
6. Changer un fichier sur dropbox.com pendant qu'il est ouvert, puis
   enregistrer dans Kazo OS : un conflit est signalé, rien n'est écrasé.
7. Fermer la session, la rouvrir : le lecteur revient sans nouvelle connexion.
8. Paramètres ▸ Réseau ▸ Comptes cloud ▸ Retirer : le lecteur disparaît ; sur
   dropbox.com, Paramètres ▸ Apps connectées, « Kazo Os Drive » n'est plus
   listée.

### Dropbox, dossier réservé (« Kazo OS Folder »)

1. Même connexion avec « Un dossier réservé à Kazo OS ».
2. Le lecteur est vide au départ ; un fichier créé apparaît sur dropbox.com dans
   `Apps/Kazo OS Folder` (« Applications » en français ; le dossier porte le nom
   de l'app).
3. Les autres fichiers du compte ne sont pas visibles.
4. Les deux comptes (tout le stockage et dossier réservé) peuvent être montés
   en même temps sur deux lecteurs.

### OneDrive personnel (compte Microsoft)

1. Connecter OneDrive, « Tout mon stockage OneDrive », avec un compte
   outlook.com ou hotmail.com. Microsoft propose de choisir le compte.
2. Les essais 2 à 7 de Dropbox ; le fichier d'environ 100 Mio passe par une
   session d'envoi en morceaux.
3. Recommencer avec le dossier réservé : un fichier créé apparaît dans
   `Apps/Kazo OS` (« Applications ») sur onedrive.live.com.
4. **Après 24 heures** : le compte passe « à reconnecter » ; Reconnecter dans
   Paramètres ouvre la fenêtre de Microsoft et le lecteur revient sans autre
   geste.

### OneDrive professionnel ou scolaire (Microsoft Entra)

1. Même connexion avec un compte d'organisation.
2. Si l'organisation exige le consentement d'un administrateur, noter le
   message exact de Microsoft (résultat attendu : un refus propre, pas une
   erreur de Kazo OS).
3. Sinon, les essais 2 à 4 de OneDrive personnel.

### PWA installée sur iOS

1. Sur iPhone ou iPad, Safari ▸ adresse de la préversion ▸ Partager ▸ Sur
   l'écran d'accueil, puis ouvrir Kazo OS depuis l'icône.
2. Connecter Dropbox : la fenêtre de connexion s'ouvre et revient dans la PWA
   (et non dans Safari) ; le lecteur se monte.
3. Même essai avec OneDrive.
4. Fermer la PWA, la rouvrir : le lecteur revient.
5. Si la connexion revient dans Safari au lieu de la PWA, noter la version
   d'iOS : c'est le risque connu de l'ADR 0267, et il faudra une autre page de
   retour.

### S3 compatible (facultatif)

Avec un compartiment (Cloudflare R2 a une offre gratuite) : poser la règle CORS
de [`s3-drives.md`](s3-drives.md#règle-cors-à-poser-sur-le-compartiment), puis
connecter, lire, écrire, supprimer.

## Après les essais

- Dropbox : demander la production de chaque app avant 50 utilisateurs (revue
  gratuite, icône et description demandées).
- Microsoft : la vérification de l'éditeur (Microsoft Partner Network) retire
  l'avertissement « éditeur non vérifié » de l'écran de consentement ; elle
  n'est pas requise pour fonctionner.
- La série va dans `main` quand Benoit le décide.

## Limites connues

- OneDrive : une inscription « application monopage » reçoit un jeton de
  renouvellement de 24 heures ; il faut se reconnecter chaque jour (règle de
  Microsoft, sans contournement possible sans serveur).
- Pas de cache hors ligne (décision de Benoit, question 6 de l'évaluation).
- Pas de Google Drive ni de WebDAV dans cette version (ADR 0267 §7).

## Liste des tâches du chantier 7

- [x] Fusionner `main` (3.3.4) dans la branche du chantier.
- [x] Identifiants des apps dans `appsettings.json` (PR #545).
- [x] Pages `privacy.html` et `terms.html` (PR #547).
- [x] Build Release de la solution.
- [x] Tests, suites E2E, publication élaguée, compatibilité du SDK.
- [x] Ce document, les index et `features.md`.
- [ ] Essais réels par Benoit (ci-dessus).
