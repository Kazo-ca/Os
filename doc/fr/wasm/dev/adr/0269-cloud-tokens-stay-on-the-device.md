> [English version](../../../../en/wasm/dev/adr/0269-cloud-tokens-stay-on-the-device.md)

# ADR 0269 : les jetons des comptes cloud restent sur l'appareil, leur clé est dans le trousseau

## Statut

Acceptée le 2026-11-13. Benoit a choisi cette option dans le fil du chantier
`cloud-drives/2-cloud-accounts`. Livrée par ce chantier
([`cloud-accounts.md`](../cloud-accounts.md)).

- **Précise :** l'[ADR 0267](0267-cloud-drives-as-network-drives.md), section 5,
  puce « Jetons ». Le reste de l'ADR 0267 ne change pas.
- **S'appuie sur :** l'[ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  (trousseau) et l'ADR 0044 §10 (`~/.local/state` est local à l'appareil).

## Contexte

L'ADR 0267 §5 dit que les jetons vont « dans le trousseau de l'utilisateur » et
que « rien n'est synchronisé ». Les deux phrases se contredisent : le fichier du
trousseau (`~/.local/share/keyrings/login.keyring`) fait partie du dossier
personnel, que la synchronisation envoie au serveur et aux autres appareils.
Des jetons rangés dans le trousseau quitteraient donc l'appareil, chiffrés.

## Décision

- Les jetons d'un compte (accès, renouvellement, expiration) sont **scellés**
  avec AES-256-GCM, nonce neuf à chaque écriture, dans
  `~/.local/state/kazoos/cloud-accounts.json` (`0600`). Ce dossier est local à
  l'appareil : la synchronisation ne l'envoie jamais.
- La **clé** aléatoire de 32 octets qui scelle les jetons d'un compte est une
  entrée du trousseau de connexion, propriétaire `org.kazoos.cloud-accounts`,
  une par compte. Sans le trousseau ouvert, les jetons ne s'ouvrent pas ; la
  clé seule, synchronisée, ne sert à rien sans le fichier local.
- Les entrées `org.kazoos.cloud-accounts` sont réservées à l'OS : aucune app ne
  les lit, même avec `keyring.read-all`.
- Retirer un compte efface sa clé et sa ligne ; une clé perdue (trousseau mis
  de côté après une réinitialisation du mot de passe) fait passer le compte
  « à reconnecter ».

## Conséquences

- Il faut connecter ses comptes cloud sur chaque appareil, comme le voulait
  l'ADR 0267.
- Une sauvegarde complète de l'ADR 0099 (réservée à root) contient le fichier
  scellé et le trousseau, chiffrés, comme pour tout secret.
- Option écartée : les jetons directement dans le trousseau, qui évitait de se
  reconnecter sur chaque appareil mais envoyait les jetons au serveur.
