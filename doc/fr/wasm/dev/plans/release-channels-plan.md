> [English version](../../../../en/wasm/dev/plans/release-channels-plan.md)

# Plan : canal bêta et mises à jour automatiques des apps

**Validé le 2026-11-14** par les réponses de Benoit à Q1 à Q5 (Q6 en attente) de
l'[ADR 0271](../adr/0271-beta-channel-and-automatic-app-updates.md). Série
`release-channels/base`. Rien n'est codé.

## But

Quand une version bêta ou stable de Kazo OS sort, chaque app se reconstruit,
se teste et propose sa mise à jour sans geste manuel ; une bêta de l'OS est
en ligne sur `os-beta.kazo.ca` (et par `os.kazo.ca/beta`) ; les fusions dans
`main` restent à Benoit.

## Le parcours visé

1. Un agent fusionne une série dans `beta` et monte
   `3.5.0-preview.1` → `3.5.0-preview.2` dans `build/Versions.props`.
2. `Verify Kazo OS WASM` passe sur `beta` ; `Release Kazo OS` pousse les
   paquets en préversion sur nuget.org, pose le tag `v3.5.0-preview.2`, crée la
   GitHub Release « pre-release » ; le site bêta est déployé.
3. `release.yml` prévient les dépôts d'apps. Chacun met sa branche `beta` sur
   la préversion, compile, teste, et soumet `x.y.z-beta.n` au canal bêta du
   catalogue ; la soumission « reconstruction seule » est fusionnée par
   l'automate, compilée et signée par le catalogue.
4. Sur `os-beta.kazo.ca`, le Store propose les versions bêta.
5. Benoit fusionne `beta` → `main` avec la version `3.5.0`. Chaque app reçoit
   une PR « Kazo OS 3.5.0 » ; verte, l'automate la fusionne et publie la version
   (mineure ou majeure du SDK ; pour une corrective, vérification seulement).

## Chantiers

| # | Branche | Dépôt | Contenu | Dépend de |
| --- | --- | --- | --- | --- |
| 0 | `release-channels/0-plan` | Os | Cette évaluation : ADR 0271, plan, questions. | rien |
| 1 | `release-channels/1-prerelease-from-beta` | Os | Branche `beta` ; `release.yml` et `deploy-wasm.yml` sur `beta` ; garde-fous (suffixe exigé sur `beta`, refusé sur `main`) ; préversion sur nuget.org ; `releases.md`, `ci-workflows.md`. | 0 |
| 2 | `release-channels/2-beta-site` | Os | Déploiement du site bêta à chaque push de `beta` ; redirection `os.kazo.ca/beta` ; nom de PWA et pastille « Bêta » ; liens « Essayer la bêta » et « Revenir à la version stable ». Benoit crée le projet d'hébergement, le DNS et le jeton. | 1, Q1 |
| 3 | `release-channels/3-os-channel` | Os | L'OS connaît son canal : adresse du catalogue officiel selon le canal (`catalog-beta.json`), versions bêta proposées et suivies par le Store et `kazopkg` sur un OS en préversion ; le Store et `kazopkg` choisissent la version la plus récente **compatible** avec le SDK de l'OS. API publique à déclarer, ADR de détail si nécessaire. | 1 |
| 4 | `release-channels/4-catalog-beta` | Os-AppMarketPlace | Branche `beta`, `catalog-beta.json`, rétention des versions bêta, contrôle « reconstruction seule » dans l'outil, fusion par l'automate, documentation. | 3 (format) |
| 5 | `release-channels/5-template-workflows` | Os puis Os-AppTemplate | Workflows réutilisables (`ci`, `publish-to-kazoos-store`, `publish-packages`, `tag-release`, nouveau `sdk-update`), script de version, branche `beta` des apps ; modèle `kazoos-solution` ; régénération du template ; `app-repository.md`. | 1 |
| 6 | `release-channels/6-notify-apps` | Os | `build/app-repositories.txt`, envoi `repository_dispatch` par `release.yml`, jeton d'organisation. | 1, 5, Q6 |
| 7 | `release-channels/7-<app>` | chaque dépôt d'app | Aligner le dépôt sur les workflows réutilisables : Chess (workflows seulement), Webamp, IRC, EmulatorJS, Word, Spreadsheets, Pdf, Fig, Photo, puis Deck, Design et Vector quand ils existent. Un fil par dépôt, en parallèle. | 4, 5, 6 |
| 8 | `release-channels/8-trial-and-close` | Os | Essai complet : une préversion, le site bêta, une app en bêta, puis la version stable ; `features.md`, mémoire, bilan. | 1 à 7 |

Les chantiers 2, 3 et 5 peuvent avancer en parallèle après le 1 ; le 4 démarre
quand le format de `catalog-beta.json` est fixé par le 3.

## Ce que Benoit doit faire lui-même

- Répondre à Q6.
- Créer le site bêta (projet Cloudflare Pages, domaine
  `os-beta.kazo.ca`) et le jeton de déploiement, en secret du dépôt `Os`.
- Selon Q6 : créer la GitHub App (ou le jeton) et le secret d'organisation.
- Vérifier que la politique Trusted Publishing de nuget.org accepte les
  exécutions de `release.yml` depuis la branche `beta`.
- Protéger la branche `beta` de `Os` (PR obligatoire, vérification verte).
- Fusionner la série dans `main` quand il le décide.

## Risques

| Risque | Parade |
| --- | --- |
| Une préversion cassée bloque les 11 apps | La CI d'une app rouge n'ouvre qu'une issue ; rien n'est publié, `main` de l'app n'est pas touché. |
| Bruit : 11 PR par version stable | On ne publie que pour une mineure ou une majeure, et l'automate fusionne les reconstructions seules. |
| Un automate signe du code non relu | Contrôle « reconstruction seule » : seuls le sous-module et la version changent, capacités identiques ; sinon relecture humaine. |
| Limites de Cloudflare Pages (25 Mio par fichier, 20 000 fichiers) | À mesurer au chantier 2 sur le zip `kazoos-wasm-<version>.zip` ; repli : un second dépôt GitHub Pages. |
| `beta` s'éloigne de `main` | `main` est fusionnée dans `beta` après chaque version stable ; en cas de doute, `beta` est recréée depuis `main`. |

## Hors du plan

- Points 2 et 3 de l'issue #456 (vérifier les apps installées avant une mise à
  jour de l'OS, maintien des anciennes versions).
- Copier les données d'un utilisateur entre le site stable et le site bêta.
- Aucun jeu sur ce repository.

## Tâches

- [x] Chantier 0 : évaluation, ADR 0271, plan.
- [ ] Chantier 1 : préversion depuis `beta`.
- [ ] Chantier 2 : site bêta.
- [ ] Chantier 3 : canal dans l'OS.
- [ ] Chantier 4 : canal bêta du catalogue.
- [ ] Chantier 5 : workflows réutilisables et template.
- [ ] Chantier 6 : prévenir les apps.
- [ ] Chantier 7 : aligner chaque dépôt d'app.
- [ ] Chantier 8 : essai complet et bilan.
