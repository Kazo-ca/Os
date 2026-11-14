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

## App ids

They are public. The host reads them from `wwwroot/appsettings.json`; they ship
empty, so no provider is offered:

```json
"CloudDrives": {
  "Dropbox": { "FullAccessClientId": "", "AppFolderClientId": "" },
  "OneDrive": { "ClientId": "", "Tenant": "common" }
}
```

An administrator serving Kazo OS at another address registers their own apps
and writes `/etc/kazoos/cloud-drives.config` (system layer only); a key present
and not empty there replaces the host's:

```ini
[dropbox]
full-access-client-id = …
app-folder-client-id = …

[onedrive]
client-id = …
tenant = common
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
- [ ] UI (work item 5) and connectors (work items 3 and 4).
