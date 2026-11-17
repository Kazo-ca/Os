> [Version française](../../../fr/wasm/dev/cloud-accounts.md)

# Cloud accounts: sign-in, tokens and renewal

Work item `cloud-drives/2-cloud-accounts` of the
[`cloud-drives/base`](plans/cloud-drives-plan.md) series, following
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), section 5. This document
describes the shipped code; the ADR remains the reference for the decisions.

## Purpose

- The user connects **their own** Dropbox or OneDrive account, with no server
  and no secret: OAuth 2.0 authorization code, with PKCE (`S256`) and `state`.
- Sign-in happens in a browser window that returns to a static Kazo OS page.
  The OS is never left: a full-page redirect would restart it.
- Tokens stay on the device, protected by the user's keyring
  ([ADR 0100](adr/0100-sensitive-settings-and-protected-write-audit.md)), and
  are renewed before they expire.
- Connectors (work items 3 and 4) ask the service for a valid access token;
  apps never see any token.

## Architecture

| Piece | Project | Role |
| --- | --- | --- |
| `IOAuthAuthorizationWindow`, `OAuthAuthorizationResult` | Simulation.Abstractions (`CloudAccounts/`) | Contract of the sign-in window. |
| `BrowserOAuthAuthorizationWindow` | Infrastructure.Browser (`CloudAccounts/`) | Imports `oauthWindow.js`, gives the redirect URI. |
| `oauthWindow.js` | Infrastructure.Browser (`wwwroot/`) | Opens the window (`window.open`), waits for the callback page on a `BroadcastChannel`. |
| `oauth-callback.html`, `.js`, `.css` | Infrastructure.Browser (`wwwroot/`) | Callback page: posts the code, clears the address, closes. |
| `OAuthPkce` | Platform.Core (`CloudAccounts/`) | Random verifier, `S256` challenge and `state` (RFC 7636). |
| `CloudOAuthProvider` | Platform.Core | Endpoints, app ids per scope, provider-specific parameters (Dropbox, Microsoft). |
| `CloudProviderSettings`, `CloudDrivesSettingsDocuments`, `CloudProviderCatalog` | Platform.Core | Host app ids, overridden by `/etc/kazoos/cloud-drives.config`. |
| `CloudOAuthTokenClient` | Platform.Core | Code exchange, renewal, identity, revocation, over `HttpClient` (`fetch` in the browser). |
| `CloudAccountStore` (internal) | Platform.Core | Account list, sealed tokens, keys in the keyring. |
| `CloudAccountService` | Platform.Core | Connect, reconnect, access token, removal. |

### The window and the callback page

Kazo OS is served with `Cross-Origin-Opener-Policy: same-origin`
([ADR 0131](adr/0131-cross-origin-isolation.md)). A window opened to a provider
moves to another browsing context group: the OS cannot read it and it loses
`window.opener`. The providers' pop-up sign-in libraries (MSAL, Google
Identity) therefore do not work as they are.

1. The OS calls `window.open` **immediately**, inside the user's click;
   otherwise the pop-up blocker refuses. `PrepareAsync` loads the module when
   the dialog opens so that the click waits for nothing.
2. The provider sends the window back to
   `<base>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html?code=…&state=…`.
3. The page posts `{ state, code, error, errorDescription }` on the
   `kazoos-oauth-callback` channel, removes the code from the address bar and
   closes. `BroadcastChannel` links every page of one origin, whatever their
   browsing context group.
4. The OS keeps only the answer carrying the expected `state`. Without an
   answer within 10 minutes, the sign-in is abandoned.

The published service worker serves this page as it is, with its query, and
keeps it in its offline cache. Before this work item, any navigation whose full
address was not in the manifest received `index.html`: the window would have
started a second Kazo OS. Now a navigation to a file of the release (known path,
any query) receives that file.

### Where the tokens live

| Item | Location | Synchronized |
| --- | --- | --- |
| Account list and **sealed** tokens (AES-256-GCM, new nonce at every write) | `~/.local/state/kazoos/cloud-accounts.json` (`0600`) | No: `~/.local/state` is device-local (ADR 0044 §10) |
| Random 32-byte key sealing one account's tokens | Login keyring, owner `org.kazoos.cloud-accounts`, one entry per account | Yes, encrypted with the keyring |

The keyring file is synchronized with the home folder; putting the tokens there
would send them to the server and to the other devices, against ADR 0267
("nothing is synchronized"). The key alone is useless without the local file.
A full ADR 0099 backup (root only) contains, as for every secret, both
encrypted files.

`org.kazoos.cloud-accounts` entries are reserved to the OS: an app cannot read
them, even with `keyring.read-all` (`AppKeyringGateway`).

The file only holds references and ciphertext:

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

### Guest account and locked keyring

An account without a password (the guest of [ADR 0187](adr/0187-guest-start-and-sign-in-credits.md))
has no open keyring, like a passkey sign-in without PRF. `CanConnect` is then
false and `ConnectAsync` refuses with `CloudAccountError.KeyringLocked` and a
message suggesting `passwd`.

## Usage

```csharp
CloudAccountService accounts = services.GetRequiredService<CloudAccountService>();
await accounts.PrepareAsync();                                    // when the dialog opens
IReadOnlyList<CloudProviderInfo> providers = await accounts.GetProvidersAsync();
CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.AppFolder); // inside the click
string token = await accounts.GetAccessTokenAsync(account.Id);    // for a connector
await accounts.ReconnectAsync(account.Id);                        // after NeedsReconnect, inside a click
await accounts.RemoveAsync(account.Id);                           // unmounts, revokes, erases
```

| Error (`CloudAccountError`) | When |
| --- | --- |
| `KeyringLocked` | Keyring closed (guest, passkey without PRF). |
| `ProviderNotConfigured` | Unknown provider, or no app id for that scope. |
| `WindowBlocked`, `Cancelled`, `Denied` | Window blocked; closed or no answer; provider refusal. |
| `ProviderUnavailable` | Offline, network error or 5xx: try again later, the account stays connected. |
| `ReconnectRequired` | The provider refuses the refresh token (OneDrive after 24 hours, access removed) or the key left the keyring: the account becomes `NeedsReconnect`, its drives stay. |
| `AccountMismatch` | A reconnection opened another account of the provider: refused, the new token is revoked. |

- The access token is renewed when it expires in less than 2 minutes. A refresh
  token returned by the provider (Microsoft changes it every time) replaces the
  old one.
- Signing in again to the same account with the same scope renews that account
  instead of adding a second one.
- Removal calls `ICloudAccountDrives.ReleaseAsync` (`NetworkDriveService`
  unmounts and forgets the account's drives), revokes the token at Dropbox
  (Microsoft has no revocation for a public client), erases the keyring key
  and the account's row.
- Connectors go through `ICloudAccountTokens` (work item 3,
  [`cloud-drive-connectors.md`](cloud-drive-connectors.md));
  `RenewAccessTokenAsync` renews a token the provider refused (401) before its
  known expiry, once for several requests.

## App ids

They are public. The host reads them from `wwwroot/appsettings.json`. They are
the ids of the Kazo OS registrations (Dropbox apps "Kazo OS Drive" and "Kazo OS
Folder", Entra registration "Kazo OS"), valid for the redirect URIs below; an
empty id removes the provider or the scope:

```json
"CloudDrives": {
  "Dropbox": { "FullAccessClientId": "im7y4ma8wtsospi", "AppFolderClientId": "mcp8j0gt4h4v6s3", "AppFolderPath": "Apps/Kazo OS Folder" },
  "OneDrive": { "ClientId": "7f89ba67-5655-4721-991a-53c64998f45e", "Tenant": "common", "AppFolderPath": "Apps/Kazo OS" }
}
```

`AppFolderPath` says where the provider puts the reserved folder in the user's
storage, as the user sees it on the service's site (the name of the registered
app). The sign-in dialog shows it before the starting folder; empty, nothing is
shown. No API call uses it.

An administrator serving Kazo OS at another address registers their own apps
and writes `/etc/kazoos/cloud-drives.config` (system layer only); a key present
and not empty there replaces the host's:

```ini
[dropbox]
full-access-client-id = …
app-folder-client-id = …
app-folder-path = Apps/…

[onedrive]
client-id = …
tenant = common
app-folder-path = Apps/…
```

A `disabled` key outside any section (`disabled = dropbox, s3`) hides shipped
drive types on this machine; it adds to the host's `KazoOs:CloudDrives:Disabled`
and never turns a disabled type back on (ADR 0282 §5,
[`cloud-drives.md`](cloud-drives.md#switchable-connectors-adr-0282-5)). A
disabled provider is no longer returned by `CloudProviderCatalog`.

A feature that declares an OAuth type (ADR 0282 §3) has its own section,
named after the type: `client-id` replaces the app id of the full access, or
of the only access declared; `app-folder-client-id` that of the reserved
folder when the type declares both. An absent or empty key keeps the
manifest's app id. The type `locked` cannot have a section (reserved name of
the settings files, ADR 0044).

```ini
[exemple]
client-id = …
app-folder-client-id = …
```

| Provider | `Full` scope | `AppFolder` scope | Other parameters |
| --- | --- | --- | --- |
| Dropbox | "Full Dropbox" app | "App folder" app | `token_access_type=offline`; revocation `2/auth/token/revoke`; identity `2/users/get_current_account` |
| OneDrive | `Files.ReadWrite offline_access User.Read` | `Files.ReadWrite.AppFolder offline_access User.Read` | One SPA registration; `response_mode=query`, `prompt=select_account`; identity `GET /v1.0/me` |

## Redirect URIs to register

The redirect URI is
`<OS base address>_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html`,
byte for byte:

| Host | Address |
| --- | --- |
| os.kazo.ca | `https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| `dotnet run` without a launch profile | `http://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Same host served over `https` (development certificate) | `https://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |
| Publish output served by `python3 -m http.server 8080` | `http://localhost:8080/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html` |

Dropbox requires the exact address, port included, and accepts `http` only for
`localhost`. Both Dropbox apps and the Microsoft registration ("Single-page
application" platform) get the same addresses.

## Keys accounts (S3)

An S3 account has no sign-in window and no token: the user types the address of
the service and their keys, and the OS signs every request. The S3 connector
([`s3-drives.md`](s3-drives.md)) keeps those keys in an account of provider
`s3` through `SaveKeysAccountAsync` (internal to Platform.Core): they are sealed
like tokens, under the account's key in the keyring, and the account line has
`"credential": "keys"`. `GetKeysAsync` opens them again for the connector;
unreadable keys put the account in `NeedsReconnect`. A keys account refuses
`GetAccessTokenAsync` and `ReconnectAsync` (`ProviderNotConfigured`): it is
reconnected by typing its keys again. Removal is the same as for the other
accounts.

## Types a feature declares (ADR 0282 §3)

A Store feature that provides a drive type
([`../framework/drive-providers.md`](../framework/drive-providers.md)) never
runs the sign-in: the OS runs it from its declaration.

- **OAuth**: `CloudProviderCatalog.DeclaredProviders` reads, at each call, the
  `oauth` types of the installed and enabled features
  (`AppCatalog.GetDriveProviders`, `IAppEnablementRegistry`), and
  `DeclaredCloudProviders.Build` turns each into a `CloudOAuthProvider`
  (addresses of the manifest, `https` only; `FeatureId` tells which feature
  declares it). They come after Dropbox and OneDrive; a type of the OS cannot
  be declared. From then on everything is as for Dropbox: the same window, the
  same callback page, PKCE, tokens sealed on the device, renewal by the OS.
  Without an identity address, the account takes the service's name and a
  reconnection cannot check it is the same account.
- **Form**: `CloudAccountService.SaveFormAccountAsync(type, feature, name,
  options, secrets)` keeps a keys account (`"credential": "keys"`): the non-secret
  values are readable in the list (`options`, given back by
  `CloudAccount.Options`), the secret fields are sealed like tokens. The same
  type with the same non-secret values finds the same account again (its
  service account id is `form:` followed by the SHA-256 of those values, never
  a secret): typing a new password replaces the old one.
- **The account's feature**: each account of a declared type keeps the id of
  the feature that served its type when it was added (`featureId` in the list,
  `CloudAccount.FeatureId`; `null` for Dropbox, OneDrive and S3). If another
  feature later serves the same type, it never reaches this account: its
  context is refused, a reconnection is refused (`ProviderNotConfigured`)
  without opening a window, and a new sign-in to the same service account
  creates a separate account. Same rule as drives, which only mount again with
  the same feature (Benoit's answer A, 2026-11-17).
- **What the provider is lent**:
  `CloudAccountService.CreateDriveProviderContextAsync(type, feature, account)`
  returns an `IKazoDriveProviderContext` bound to **one** account, **one** type
  and **one** feature. An account of another type or of another feature is
  refused (`AccountNotFound`), at creation and at each call. At each call the
  context reads the list again: `GetAccessTokenAsync` returns this account's
  access token (renewed by the OS), `GetSecretAsync` a secret field of this
  account, and nothing once the account is removed. The context keeps no token
  or secret, and the refresh token never leaves the OS. Refusals become
  `KazoDriveException`: `SignInRequired` (reconnection, locked keyring,
  removed account) or `Unavailable` (service unreachable).

Wiring this to drives (creating the context when a drive opens) is work item
4; the interface that offers these types, work item 6.

## Public pages for the providers

The consent screens of Dropbox and Microsoft link to two static pages of the
host (`OS/Kazo.Os.Ecosystem/wwwroot/`), in French and English, with no
WebAssembly and no script, styled by `legal.css`:

| Page | Address | Where to register it |
| --- | --- | --- |
| Privacy policy | `https://os.kazo.ca/privacy.html` | Dropbox (production request), Entra ("Branding") |
| Terms of use | `https://os.kazo.ca/terms.html` | Entra ("Branding") |

The published service worker serves them as they are and keeps them offline,
like the callback page. One pair of pages covers every provider, with a section
per service (Benoit's choice, 2026-11-15).

## Decisions

- **Tokens sealed on the device, key in the keyring**
  ([ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md)) rather than tokens in the
  keyring: the keyring is synchronized, tokens must not leave the device.
- **Static callback page and `BroadcastChannel`**: the only way to hand the code
  to a COOP-isolated page with no server and without leaving the OS.
- **The service worker serves release pages with their query**: otherwise the
  window received `index.html`.
- **Reconnection to the same account only**: an account's drives stay bound to
  the same storage.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudAccounts/`: PKCE (RFC 7636 vector),
  Dropbox and Microsoft authorization requests, offered scopes,
  `cloud-drives.config`, and `CloudAccountService` against a fake OAuth server
  (`FakeOAuthServer`, PKCE verified): tokens never in clear nor in the keyring,
  device-local `0600` list, renewal and rotation, refusal → `NeedsReconnect`,
  offline, wrong account, removal, blocked or denied window, guest refused,
  lost key, other user, apps excluded.
- `Tests/Kazo.Os.Platform.Core.Tests/CloudAccounts/DeclaredDriveProviderTests.cs`:
  declared types offered then withdrawn with their feature, app ids replaced
  by `cloud-drives.config`, OAuth sign-in of a declared type, form account
  (plain values, sealed secrets), context limited to the account and type of
  the open drive, refresh token never given.
- `Tests/Kazo.Os.Infrastructure.Browser.Tests/CloudAccounts/`: redirect URI from
  the base address, module answers.
- `Tests/Kazo.Os.Pwa.E2E.Tests/CloudSignInWindowTests.cs`: on the published,
  isolated PWA, with a fake provider on another origin: window, callback, code
  received over `BroadcastChannel`, window closed, PKCE exchange and renewal
  over CORS; and the callback page served by the service worker with its query
  and `COOP`, cached offline. Both tests fail without the service worker fix
  (the title received is "Kazo OS" and the window never answered).

## Task list

- [x] Window contract and static callback page with `BroadcastChannel`.
- [x] PKCE `S256` and `state` in C#.
- [x] Dropbox and OneDrive providers, two scopes.
- [x] App ids in `appsettings.json`, overridable by `/etc/kazoos/cloud-drives.config`.
- [x] Exchange, renewal, identity and revocation over `HttpClient`.
- [x] Tokens sealed on the device, key in the keyring, entries reserved to the OS.
- [x] "Needs reconnect" state, reconnection to the same account, removal.
- [x] Clear refusal without a keyring (guest).
- [x] Published service worker: the callback page with its query.
- [x] Unit tests and E2E on the published PWA.
- [x] Wire `ICloudAccountDrives` to `NetworkDriveService` (`ReleaseAsync`).
- [x] UI (work item 5): [`cloud-drives-ui.md`](cloud-drives-ui.md).
- [ ] Connectors (work items 3 and 4).
- [x] Types declared by a feature: OAuth, form, provider context
  (`drive-providers` series, work item 5).
