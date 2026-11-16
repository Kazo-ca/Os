> [English version](../../../../en/wasm/dev/adr/0279-storage-full-on-refused-write.md)

# ADR 0279 : une écriture refusée par le navigateur signale l'espace plein

## Statut

Acceptée le 2026-11-16 : Benoit a choisi « Réagir aux écritures refusées »
(issue #493, série `known-failures/base`).

- **Complète :** l'[ADR 0099](0099-os-backup-and-low-storage.md) §2 (espace
  faible et plus de place). La mesure par `navigator.storage.estimate()` reste ;
  une écriture refusée devient un second signal.
- **S'appuie sur :** l'[ADR 0273](0273-short-orphan-content-retention.md) (rétention
  courte des contenus orphelins quand l'espace est faible).

## Contexte

L'avertissement « Espace de stockage faible » de l'ADR 0099 vient de
`navigator.storage.estimate()`. Depuis Chromium 153 (2026-11-16, Playwright
1243), cette fonction ne donne plus le quota réel : elle l'arrondit (4 Gio,
puis 3 Gio après un rechargement), même quand le quota réel n'est que de
500 Ko. Avec `Storage.overrideQuotaForOrigin`, le protocole DevTools
(`Storage.getUsageAndQuota`) voit bien 500 Ko, et IndexedDB refuse bien une
écriture de 2 Mo avec `QuotaExceededError`, mais `estimate()` ne baisse jamais.

L'OS ne peut donc plus voir l'espace baisser : l'avertissement n'arrive jamais,
et le test `LowStorageSpaceTests` échoue à chaque fois. Le seul signal fiable est
celui d'un vrai disque plein : l'écriture refusée.

Benoit a eu trois choix : réagir aux écritures refusées (recommandé), garder
l'estimation seule (le test reste un échec connu), ou simuler l'estimation dans
le test (l'interface est testée, mais l'avertissement ne marche toujours pas
dans un vrai Chrome). Il a choisi le premier.

## Décision

1. **Signal.** Quand le fournisseur de fichiers persistant répond
   `NoSpace` (une `QuotaExceededError` du navigateur, ADR 0099 §2),
   `FileSystemService` appelle `IStorageSpaceGuard.ReportWriteRefused()` au lieu
   de `RequestRefresh()`. Un refus de la réserve de root (`CheckReservedSpace`)
   n'est pas un refus du navigateur et n'appelle pas cette méthode.
2. **Contrat.** `ReportWriteRefused()` est un membre par défaut de
   `IStorageSpaceGuard` qui appelle `RequestRefresh()` : les implémentations
   existantes restent valides.
3. **Moniteur.** `StorageSpaceMonitor.ReportWriteRefused()` :
   - tient l'espace pour faible pendant `RefusalHoldTime` (10 minutes), quelle
     que soit la mesure : `IsLowSpace` reste vrai et la rétention courte de
     l'ADR 0273 s'applique ;
   - récupère d'abord les contenus orphelins (rétention courte), comme pour une
     mesure faible ;
   - poste l'avertissement habituel, une fois par session et par utilisateur ;
     sans mesure fiable, le message dit que le navigateur a refusé d'enregistrer
     des données (`Storage.Full.Message`) au lieu de donner les Mio libres.
4. **Réserve de root.** Elle reste calculée d'après la mesure. Une écriture
   refusée ne l'active pas : la limite réelle est déjà atteinte, la réserve ne
   protège plus rien.
5. **Test.** `LowStorageSpaceTests` abaisse le quota juste au-dessus de
   l'usage réel (lu par `Storage.getUsageAndQuota`) : la première écriture du
   système qui dépasse est refusée et l'avertissement doit arriver. La suite du
   test vérifie que l'administrateur se reconnecte quand même.

## Conséquences

- L'avertissement marche de nouveau dans Chromium, mais au moment où une
  écriture échoue, pas avant : il n'y a plus d'alerte précoce à 10 % libres
  tant que Chromium arrondit `estimate()`.
- Si un navigateur rend de nouveau un quota réel, la mesure prévient avant le
  refus, comme avant.
- `IStorageSpaceGuard` gagne un membre public (`PublicAPI.Unshipped.txt`).
