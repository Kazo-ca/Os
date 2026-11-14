> [English version](../../../en/wasm/dev/cloud-accounts.md)

# Comptes cloud : connexion, jetons et renouvellement

Chantier `cloud-drives/2-cloud-accounts` de la série
[`cloud-drives/base`](plans/cloud-drives-plan.md), selon
l'[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), section 5. Ce document
décrit le code livré ; l'ADR reste la référence des décisions.

## But

- L'utilisateur connecte **son** compte Dropbox ou OneDrive, sans serveur et sans
  secret : OAuth 2.0 par code, avec PKCE (`S256`) et `state`.
- La connexion se fait dans une fenêtre du navigateur qui revient sur une page
  statique de Kazo OS. L'OS n'est jamais quitté : une redirection de la page
  entière le redémarrerait.
- Les jetons restent sur l'appareil, protégés par le trousseau de l'utilisateur
  ([ADR 0100](adr/0100-sensitive-settings-and-protected-write-audit.md)), et
  sont renouvelés avant d'expirer.
- Les connecteurs (chantiers 3 et 4) demandent un jeton d'accès valable au
  service ; les apps ne voient jamais aucun jeton.

## Architecture

| Pièce | Projet | Rôle |
| --- | --- | --- |
| `IOAuthAuthorizationWindow`, `OAuthAuthorizationResult` | Simulation.Abstractions (`CloudAccounts/`) | Contrat de la fenêtre de connexion. |
| `BrowserOAuthAuthorizationWindow` | Infrastructure.Browser (`CloudAccounts/`) | Importe `oauthWindow.js`, donne l'adresse de retour. |
| `oauthWindow.js` | Infrastructure.Browser (`wwwroot/`) | Ouvre la fenêtre (`window.open`), attend la page de retour sur un `BroadcastChannel`. |
| `oauth-callback.html`, `.js`, `.css` | Infrastructure.Browser (`wwwroot/`) | Page de retour : poste le code, efface l'adresse, se ferme. |
| `OAuthPkce` | Platform.Core (`CloudAccounts/`) | Vérificateur, défi `S256` et `state` aléatoires (RFC 7636). |
| `CloudOAuthProvider` | Platform.Core | Points d'accès, identifiants d'app par étendue, paramètres propres (Dropbox, Microsoft). |
| `CloudProviderSettings`, `CloudDrivesSettingsDocuments`, `CloudProviderCatalog` | Platform.Core | Identifiants d'app de l'hôte, remplacés par `/etc/kazoos/cloud-drives.config`. |
| `CloudOAuthTokenClient` | Platform.Core | Échange du code, renouvellement, identité, révocation, par `HttpClient` (`fetch` dans le navigateur). |
| `CloudAccountStore` (interne) | Platform.Core | Liste des comptes, jetons scellés, clés dans le trousseau. |
| `CloudAccountService` | Platform.Core | Connexion, reconnexion, jeton d'accès, retrait. |

### La fenêtre et la page de retour

Kazo OS est servi avec `Cross-Origin-Opener-Policy: same-origin`
([ADR 0131](adr/0131-cross-origin-isolation.md)). Une fenêtre ouverte vers un
fournisseur passe dans un autre groupe de contextes : l'OS ne peut pas la lire
et elle perd `window.opener`. Les bibliothèques de connexion par fenêtre des
fournisseurs (MSAL, Google Identity) ne marchent donc pas telles quelles.

1. L'OS appelle `window.open` **tout de suite**, dans le clic de l'utilisateur ;
   sinon le bloqueur de fenêtres refuse. `PrepareAsync` charge le module à
   l'ouverture du dialogue pour que le clic n'attende rien.
2. Le fournisseur renvoie la fenêtre sur
   `<base>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html?code=…&state=…`.
3. La page poste `{ state, code, error, errorDescription }` sur le canal
   `kazoos-oauth-callback`, retire le code de la barre d'adresse et se ferme.
   `BroadcastChannel` relie toutes les pages d'une même origine, quel que soit
   leur groupe de contextes.
4. L'OS ne garde que la réponse qui porte le `state` attendu. Sans réponse en
   10 minutes, la connexion est abandonnée.

Le service worker publié sert cette page telle quelle, avec sa requête, et la
garde dans son cache hors ligne. Avant ce chantier, toute navigation dont
l'adresse complète n'était pas dans le manifeste recevait `index.html` : la
fenêtre aurait démarré un second Kazo OS. Désormais une navigation vers un
fichier de la version (chemin connu, requête quelconque) reçoit ce fichier.

### Où vivent les jetons

| Élément | Emplacement | Synchronisé |
| --- | --- | --- |
| Liste des comptes et jetons **scellés** (AES-256-GCM, nonce neuf à chaque écriture) | `~/.local/state/kazoos/cloud-accounts.json` (`0600`) | Non : `~/.local/state` est local à l'appareil (ADR 0044 §10) |
| Clé aléatoire de 32 octets qui scelle les jetons d'un compte | Trousseau de connexion, propriétaire `org.kazoos.cloud-accounts`, une entrée par compte | Oui, chiffré avec le trousseau |

Le fichier du trousseau est synchronisé avec le dossier personnel ; y mettre les
jetons les enverrait au serveur et sur les autres appareils, contre l'ADR 0267
(« rien n'est synchronisé »). La clé seule ne sert à rien sans le fichier local.
Une sauvegarde complète de l'ADR 0099 (réservée à root) contient, comme pour
tout secret, les deux fichiers chiffrés.

Les entrées `org.kazoos.cloud-accounts` sont réservées à l'OS : une app ne peut
pas les lire, même avec `keyring.read-all` (`AppKeyringGateway`).

Le fichier ne contient que des références et du texte chiffré :

```json
{
  "schemaVersion": 1,
  "accounts": [
    {
      "id": "Xq3…",
      "provider": "dropbox",
      "scope": "Full",
      "displayName": "alice@example.com",
      "providerAccountId": "dbid:…",
      "state": "Connected",
      "connectedAtUtc": "2026-11-13T22:00:00+00:00",
      "tokens": { "nonce": "…", "data": "…" }
    }
  ]
}
```

### Compte invité et trousseau verrouillé

Un compte sans mot de passe (l'invité de l'[ADR 0187](adr/0187-guest-start-and-sign-in-credits.md))
n'a pas de trousseau ouvert, comme une connexion par clé d'accès sans PRF.
`CanConnect` est alors faux et `ConnectAsync` refuse avec
`CloudAccountError.KeyringLocked` et un message qui propose `passwd`.

## Usage

```csharp
CloudAccountService accounts = services.GetRequiredService<CloudAccountService>();
await accounts.PrepareAsync();                                    // à l'ouverture du dialogue
IReadOnlyList<CloudProviderInfo> providers = await accounts.GetProvidersAsync();
CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.AppFolder); // dans le clic
string token = await accounts.GetAccessTokenAsync(account.Id);    // pour un connecteur
await accounts.ReconnectAsync(account.Id);                        // après NeedsReconnect, dans un clic
await accounts.RemoveAsync(account.Id);                           // démonte, révoque, efface
```

| Erreur (`CloudAccountError`) | Quand |
| --- | --- |
| `KeyringLocked` | Trousseau fermé (invité, clé d'accès sans PRF). |
| `ProviderNotConfigured` | Fournisseur inconnu ou sans identifiant d'app pour cette étendue. |
| `WindowBlocked`, `Cancelled`, `Denied` | Fenêtre bloquée ; fermée ou sans réponse ; refus du fournisseur. |
| `ProviderUnavailable` | Hors ligne, erreur réseau ou 5xx : réessayer plus tard, le compte reste connecté. |
| `ReconnectRequired` | Le fournisseur refuse le jeton de renouvellement (OneDrive après 24 heures, accès retiré) ou la clé a quitté le trousseau : le compte passe `NeedsReconnect`, ses lecteurs restent. |
| `AccountMismatch` | Une reconnexion a ouvert un autre compte du fournisseur : refusée, le nouveau jeton est révoqué. |

- Le jeton d'accès est renouvelé quand il expire dans moins de 2 minutes. Un
  jeton de renouvellement renvoyé par le fournisseur (Microsoft le change à
  chaque fois) remplace l'ancien.
- Se connecter de nouveau au même compte avec la même étendue renouvelle ce
  compte au lieu d'en ajouter un second.
- Le retrait appelle `ICloudAccountDrives.ReleaseAsync` (`NetworkDriveService`
  démonte et oublie les lecteurs du compte), révoque le jeton chez
  Dropbox (Microsoft n'a pas de révocation pour un client public), efface la
  clé du trousseau et la ligne du compte.

## Identifiants d'app

Ils sont publics. L'hôte les lit dans `wwwroot/appsettings.json` ; ils sont
livrés vides, donc aucun fournisseur n'est proposé :

```json
"CloudDrives": {
  "Dropbox": { "FullAccessClientId": "", "AppFolderClientId": "" },
  "OneDrive": { "ClientId": "", "Tenant": "common" }
}
```

Un administrateur qui sert Kazo OS à une autre adresse inscrit ses propres apps
et écrit `/etc/kazoos/cloud-drives.config` (couche système seulement) ; une clé
présente et non vide y remplace celle de l'hôte :

```ini
[dropbox]
full-access-client-id = …
app-folder-client-id = …

[onedrive]
client-id = …
tenant = common
```

| Fournisseur | Étendue `Full` | Étendue `AppFolder` | Autres paramètres |
| --- | --- | --- | --- |
| Dropbox | App « Full Dropbox » | App « App folder » | `token_access_type=offline` ; révocation `2/auth/token/revoke` ; identité `2/users/get_current_account` |
| OneDrive | `Files.ReadWrite offline_access User.Read` | `Files.ReadWrite.AppFolder offline_access User.Read` | Une inscription SPA ; `response_mode=query`, `prompt=select_account` ; identité `GET /v1.0/me` |

## Adresses de retour à inscrire

L'adresse de retour est
`<adresse de base de l'OS>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html`,
à l'octet près :

| Hôte | Adresse |
| --- | --- |
| os.kazo.ca | `https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| `dotnet run` sans profil de lancement | `http://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Publication servie par `python3 -m http.server 8080` | `http://localhost:8080/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |

Dropbox exige l'adresse exacte, port compris, et n'accepte `http` que pour
`localhost`. Les deux apps Dropbox et l'inscription Microsoft (plateforme
« Application monopage ») reçoivent les mêmes adresses.

## Comptes à clés (S3)

Un compte S3 n'a ni fenêtre de connexion ni jeton : l'utilisateur saisit
l'adresse du service et ses clés, et l'OS signe chaque requête. Le connecteur
S3 ([`s3-drives.md`](s3-drives.md)) range ces clés dans un compte de
fournisseur `s3` par `SaveKeysAccountAsync` (interne à Platform.Core) : elles
sont scellées comme des jetons, sous la clé du compte dans le trousseau, et la
ligne du compte porte `"credential": "keys"`. `GetKeysAsync` les rouvre pour le
connecteur ; des clés illisibles mettent le compte en `NeedsReconnect`. Un
compte à clés refuse `GetAccessTokenAsync` et `ReconnectAsync`
(`ProviderNotConfigured`) : on le reconnecte en saisissant de nouveau ses clés.
Le retrait est le même que pour les autres comptes.

## Décisions

- **Jetons scellés sur l'appareil, clé dans le trousseau**
  ([ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md)) plutôt que les jetons
  dans le trousseau : le trousseau est synchronisé, les jetons ne doivent pas
  quitter l'appareil.
- **Page de retour statique et `BroadcastChannel`** : seul moyen de rendre le
  code à une page isolée par COOP sans serveur et sans quitter l'OS.
- **Le service worker sert les pages de la version avec leur requête** : sans
  cela la fenêtre recevait `index.html`.
- **Reconnexion au même compte seulement** : les lecteurs d'un compte restent
  liés au même stockage.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudAccounts/` : PKCE (vecteur de la
  RFC 7636), requêtes d'autorisation de Dropbox et de Microsoft, étendues
  offertes, `cloud-drives.config`, et `CloudAccountService` contre un faux
  serveur OAuth (`FakeOAuthServer`, PKCE vérifié) : jetons jamais en clair ni
  dans le trousseau, liste `0600` locale à l'appareil, renouvellement et
  rotation, refus → `NeedsReconnect`, hors ligne, mauvais compte, retrait,
  fenêtre bloquée ou refusée, invité refusé, clé perdue, autre utilisateur,
  apps exclues.
- `Tests/Kazo.Os.Infrastructure.Browser.Tests/CloudAccounts/` : adresse de
  retour selon l'adresse de base, réponses du module.
- `Tests/Kazo.Os.Pwa.E2E.Tests/CloudSignInWindowTests.cs` : sur la PWA publiée
  et isolée, avec un faux fournisseur sur une autre origine : fenêtre, retour,
  code reçu par `BroadcastChannel`, fenêtre fermée, échange PKCE et
  renouvellement par CORS ; et la page de retour servie par le service worker
  avec sa requête et `COOP`, en cache hors ligne. Les deux tests échouent sans
  le correctif du service worker (le titre reçu est « Kazo OS » et la fenêtre
  n'a jamais répondu).

## Liste des tâches

- [x] Contrat de la fenêtre et page de retour statique avec `BroadcastChannel`.
- [x] PKCE `S256` et `state` en C#.
- [x] Fournisseurs Dropbox et OneDrive, deux étendues.
- [x] Identifiants d'app dans `appsettings.json`, remplaçables par `/etc/kazoos/cloud-drives.config`.
- [x] Échange, renouvellement, identité et révocation par `HttpClient`.
- [x] Jetons scellés sur l'appareil, clé dans le trousseau, entrées réservées à l'OS.
- [x] État « à reconnecter », reconnexion au même compte, retrait.
- [x] Refus clair sans trousseau (invité).
- [x] Service worker publié : la page de retour avec sa requête.
- [x] Tests unitaires et E2E sur la PWA publiée.
- [x] Brancher `ICloudAccountDrives` sur `NetworkDriveService` (`ReleaseAsync`).
- [ ] Interface (chantier 5) et connecteurs (chantiers 3 et 4).
