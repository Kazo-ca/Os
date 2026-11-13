> [Version française](../../../../fr/wasm/dev/adr/0269-cloud-tokens-stay-on-the-device.md)

# ADR 0269: cloud account tokens stay on the device, their key is in the keyring

## Status

Accepted on 2026-11-13. Benoit chose this option in the thread of work item
`cloud-drives/2-cloud-accounts`. Shipped by that work item
([`cloud-accounts.md`](../cloud-accounts.md)).

- **Clarifies:** [ADR 0267](0267-cloud-drives-as-network-drives.md), section 5,
  "Tokens" bullet. The rest of ADR 0267 is unchanged.
- **Builds on:** [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  (keyring) and ADR 0044 §10 (`~/.local/state` is device-local).

## Context

ADR 0267 §5 says tokens go "in the user's keyring" and that "nothing is
synchronized". The two sentences contradict each other: the keyring file
(`~/.local/share/keyrings/login.keyring`) is part of the home folder, which
synchronization sends to the server and to the other devices. Tokens stored in
the keyring would therefore leave the device, encrypted.

## Decision

- An account's tokens (access, refresh, expiry) are **sealed** with
  AES-256-GCM, a new nonce at every write, in
  `~/.local/state/kazoos/cloud-accounts.json` (`0600`). That folder is
  device-local: synchronization never sends it.
- The random 32-byte **key** sealing an account's tokens is an entry of the
  login keyring, owner `org.kazoos.cloud-accounts`, one per account. Without the
  keyring open, the tokens cannot be opened; the key alone, synchronized, is
  useless without the local file.
- `org.kazoos.cloud-accounts` entries are reserved to the OS: no app reads them,
  even with `keyring.read-all`.
- Removing an account erases its key and its row; a lost key (keyring set aside
  after a password reset) puts the account in "needs reconnect".

## Consequences

- Cloud accounts must be connected on each device, as ADR 0267 intended.
- A full ADR 0099 backup (root only) contains the sealed file and the keyring,
  encrypted, as for every secret.
- Rejected option: tokens directly in the keyring, which avoided reconnecting
  on each device but sent the tokens to the server.
