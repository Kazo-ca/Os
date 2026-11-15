> [English version](../../../../en/wasm/dev/adr/0273-short-orphan-content-retention.md)

# ADR 0273 : rétention courte des contenus orphelins

## Statut

Proposée le 2026-11-15. Benoit a décidé le 2026-11-14 que la rétention est
courte (de quelques heures à un jour) et que le contenu orphelin part tout de
suite quand l'espace est faible ; cette ADR fixe les valeurs et les règles de
sûreté. Une question reste posée à Benoit (à la fin) ; la décision applique sa
recommandation en attendant. C'est l'ADR que l'issue
[#537](https://github.com/Kazo-ca/Os/issues/537) demande : le code de #537
l'applique et l'amène dans `main` avec lui.

- **Remplace :** dans le point 2 de l'[ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md),
  la seule rétention de 30 jours (« un bloc qui n'est plus référencé est gardé
  30 jours avant d'être supprimé »). L'analyse et l'issue #537 nomment
  l'ADR 0099 : la valeur est en fait dans l'ADR 0094 ; l'ADR 0099 ne définit
  que l'espace faible.
- **S'appuie sur :** l'[ADR 0099](0099-os-backup-and-low-storage.md) (seuils de
  l'espace faible) et l'[ADR 0018](0018-indexeddb-failure-and-recovery-policy.md).
- **Analyse :** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  constats C1, C2, C3 et C10, piste 1.

## Contexte

- Un contenu est **orphelin** quand aucune entrée de fichier (`fsEntries`) ne
  porte plus son `contentHash` : le fichier a été réécrit ou supprimé pour de
  bon (un fichier dans la corbeille garde son entrée, donc son contenu).
- La passe de nettoyage existe mais aucun hôte ne l'appelle, et elle commence
  par charger tout `fsContent` en mémoire. Sur le site publié, chaque démarrage
  ajoute des orphelins.
- La rétention ne sert pas l'utilisateur : rien dans l'OS ne permet de
  retrouver un contenu orphelin. Elle protège seulement une **écriture en
  cours**, dont les blocs sont déjà écrits alors que l'entrée du fichier ne
  l'est pas encore (ADR 0245 §3), y compris depuis un autre onglet.
- Avec 30 jours, un document de 50 Mo enregistré dix fois occupe 500 Mo
  pendant un mois.

## Décision

### 1. Durées

| Situation | Un contenu orphelin est supprimé après |
| --- | --- |
| Espace normal | **6 heures** |
| Espace faible (seuils de l'ADR 0099) | **5 minutes** |

- `FileContentStoragePolicy.DefaultOrphanRetention` passe à 6 heures ; une
  deuxième valeur, `DefaultLowSpaceOrphanRetention`, vaut 5 minutes.
- Les 5 minutes ne sont pas une attente voulue : c'est la marge qui laisse
  finir une écriture en cours. « Tout de suite » veut dire que la passe part
  dès que l'espace devient faible, sans attendre l'heure suivante.

### 2. Âge d'un contenu

- L'âge d'un contenu est l'horodatage `createdUtcMs` de son **premier bloc**.
  La passe n'a donc besoin de lire qu'un bloc par contenu candidat, jamais
  tout le magasin.
- Une écriture qui retrouve un contenu déjà présent (`addIfAbsent` ne réécrit
  rien) **rafraîchit l'horodatage du premier bloc**. Sans cela, réenregistrer
  un contenu orphelin depuis 5 h 59 le laisserait à une minute de la
  suppression avant que son entrée soit écrite.

### 3. Deux garanties, quelle que soit la durée

1. **Suppression.** La passe revérifie, dans la transaction qui supprime,
   qu'aucune entrée ne référence le contenu (déjà le cas :
   `deleteAllByIndexIfUnreferenced`).
2. **Écriture.** La transaction qui écrit l'entrée d'un fichier vérifie que
   tous les blocs de son contenu sont présents. S'il en manque, l'écriture
   réécrit les blocs manquants et recommence une fois, puis échoue avec une
   erreur d'entrée-sortie.

Avec ces deux vérifications, une entrée ne peut pas pointer vers un contenu
supprimé, même si l'horloge saute ou si deux onglets travaillent ensemble. La
durée de rétention n'est plus qu'un confort : elle évite de réécrire.

### 4. Quand la passe tourne

- Après le démarrage, en arrière-plan, une fois le bureau affiché.
- Toutes les heures tant que l'OS tourne.
- Dès que l'espace devient faible (`StorageSpaceMonitor`), avec la durée de
  5 minutes.
- Après avoir vidé la corbeille.

Une passe traite un nombre borné de contenus (64 aujourd'hui) et se relance
tant qu'elle en trouve. Elle ne lit que des clés, plus le premier bloc de
chaque candidat. Elle ne tourne pas quand l'onglet est caché, et un seul
onglet la fait à la fois.

### 5. Fichiers que le système réécrit lui-même

Les journaux (`LogFileWriter`) et les réglages sont réécrits en entier,
souvent. Quand le système réécrit un de ses fichiers, il supprime l'ancien
contenu **dans la même opération**, s'il n'est plus référencé, sans attendre
la passe. La garantie 2 du §3 couvre le cas où un autre fichier allait
reprendre ce contenu.

### 6. Autres références

Un contenu est référencé par une entrée de fichier ou par une adresse servie
d'une app installée ([ADR 0274](0274-single-copy-of-app-files.md) §4). La
passe vérifie les deux.

## Conséquences

- L'espace d'un fichier réécrit ou supprimé revient en 6 heures au plus, en
  quelques minutes quand l'espace manque. Le cas des 500 Mo pendant un mois
  disparaît.
- Les blocs laissés par une écriture interrompue partent après la même durée,
  au lieu de 30 jours (ADR 0245 §3).
- Aucun changement de format : `createdUtcMs` existe déjà. Un bloc ancien sans
  horodatage est traité comme assez vieux, puisque la garantie 1 le protège
  s'il est référencé.
- La passe de nettoyage devient le lieu de la conversion en binaire
  ([ADR 0272](0272-binary-file-content.md) §3).
- La page Réglages ▸ Stockage de l'ADR 0250 (série `feature/base`) pourra
  montrer l'espace récupérable et lancer la passe ; ce n'est pas dans cette
  ADR.

## Options écartées

- **Aucune rétention** (supprimer à la réécriture, toujours) : une écriture en
  cours dans un autre onglet réécrirait ses blocs à chaque fois ; la garantie
  2 rendrait le résultat correct mais lent.
- **Garder 30 jours et seulement brancher la passe** : le constat C3 reste.
- **Compter les références par bloc** : utile pour la déduplication par bloc
  (analyse, piste 8), pas nécessaire ici.

## Question posée à Benoit

1. **Combien de temps garder un contenu orphelin quand l'espace ne manque
   pas ?** A. 1 heure ; B. 6 heures (recommandé) ; C. 24 heures. Dans les
   trois cas, 5 minutes quand l'espace est faible.

## Tâches

Faites par l'issue #537 :

- [ ] Passe qui ne lit que les clés et le premier bloc des candidats.
- [ ] Durées de 6 heures et de 5 minutes dans `FileContentStoragePolicy`.
- [ ] Rafraîchir l'horodatage du premier bloc à la réécriture d'un contenu
  présent.
- [ ] Vérifier la présence des blocs dans la transaction de l'entrée.
- [ ] Déclencheurs : démarrage, toutes les heures, espace faible, corbeille
  vidée.
- [ ] Suppression immédiate pour les fichiers réécrits par le système.
- [ ] Tests : un fichier réécrit plusieurs fois ne fait pas grossir
  `fsContent` ; écriture en cours pendant une passe ; deux onglets.
- [ ] Docs (FR et EN) : `browser-infrastructure.md`, mémoire, entrée de
  changement.
