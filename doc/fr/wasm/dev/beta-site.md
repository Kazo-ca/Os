> [English version](../../../en/wasm/dev/beta-site.md)

# Site bêta : os-beta.kazo.ca

Chantier 2 de la série `release-channels` ([ADR 0271](adr/0271-beta-channel-and-automatic-app-updates.md) §3 et §6,
[plan](plans/release-channels-plan.md)). Chaque push de la branche `beta` publie l'OS sur
`https://os-beta.kazo.ca`, une origine à part, hébergée par Cloudflare Pages ;
`https://os.kazo.ca/beta` y redirige.

## Pourquoi une origine à part

Sur la même origine que le site stable, la bêta ouvrirait la base IndexedDB `kazoos` de
l'utilisateur : une bêta qui monte le schéma rendrait l'OS stable incapable de la rouvrir, les
deux service workers effaceraient les caches l'un de l'autre et le worker de `/` servirait sa
propre page pour `/beta/`. Sur `os-beta.kazo.ca`, la bêta a sa base, ses caches et son worker ;
rien n'est copié d'un site à l'autre.

## Ce qui est en place

| Élément | Où | Rôle |
| --- | --- | --- |
| Workflow **Deploy Kazo OS beta site** | `.github/workflows/deploy-beta-site.yml` | Push sur `beta` (fichiers `wasm2/KazoOs/**` ou ce workflow) et lancement manuel depuis `beta` : vérifie le canal (`check-release-channel.sh beta`, la version doit avoir un suffixe), publie l'Ecosystem en Release comme `deploy-to-pages.yml` (sans `legacy/`), vérifie les limites de Cloudflare, lit la branche de production du projet Cloudflare Pages et déploie avec `wrangler pages deploy`. Une PR qui change ce workflow ou ses scripts construit et vérifie sans déployer. |
| Nom « Kazo OS Beta » | `build/tools/brand-beta-site.sh` | Avant la publication, renomme la PWA (`name`, `short_name` de `manifest.webmanifest`) et l'onglet (`<title>` de `index.html`). Avant et non après : le manifeste des ressources du service worker hache les fichiers renommés. Échoue si le texte attendu n'existe plus. |
| Limites de l'hébergeur | `build/tools/check-static-site-limits.sh` | Au plus 20 000 fichiers et 25 Mio par fichier. Mesure du 2026-11-14 : **913 fichiers, le plus gros 8,3 Mio** (`_framework/Kazo.Os.AppSdk.Icons.*.wasm`), 82 Mio en tout. |
| Pastille « Bêta » | `KazoOsVersionLabel` (Kazo.Os.AppSdk.Blazor) | Écran de connexion et Paramètres ▸ À propos : une version avec suffixe (`3.4.0-preview.1`) affiche la pastille. |
| Liens entre canaux | `KazoOsChannelLink` (Kazo.Os.AppSdk.Blazor), Paramètres ▸ À propos | Sur une bêta : « Revenir à la version stable » vers `https://os.kazo.ca/`. Sur la version stable servie par `os.kazo.ca` : « Essayer la bêta » vers `https://os-beta.kazo.ca/`. Ailleurs (serveur Kazo OS, build local) : rien. |
| `os.kazo.ca/beta` | `build/pages/beta-redirect.html`, copiée par `assemble-pages.ps1` dans `beta/index.html` | Redirection immédiate vers `os-beta.kazo.ca` en gardant la requête (`?autolaunch=<id>`). Le service worker laisse `beta/` au réseau comme `legacy/`. |
| Pages qui redirigent `/index.html` | `service-worker.published.js` (`withoutRedirectFlag`) | Cloudflare Pages répond à `/index.html` par une redirection vers `/` ; la coquille en cache est alors une réponse « redirigée » qu'un navigateur refuse pour une navigation. Le worker en sert une copie sans ce marqueur. |

Le canal vient de la version (`KazoOsSdk.Version`) : rien à configurer dans l'OS.
`version.json` et `HostReleaseMonitor` (ADR 0174, 0236) marchent sans changement, chaque site lit
le sien.

## Mise en place par Benoit (une fois)

1. **Créer le projet.** Tableau de bord Cloudflare → **Workers & Pages** → **Create** → onglet
   **Pages** → **Upload assets** (téléversement direct, pas de connexion Git). Nom du projet :
   `kazo-os-beta`. Déposer un dossier contenant un simple `index.html` (contenu sans importance)
   → **Deploy site**.
2. **Domaine.** Dans le projet → **Custom domains** → **Set up a custom domain** →
   `os-beta.kazo.ca` → **Continue** → **Activate domain**. La zone `kazo.ca` est déjà chez
   Cloudflare : l'enregistrement DNS (CNAME vers `kazo-os-beta.pages.dev`) est créé tout seul.
3. **Jeton d'API.** Avatar → **My Profile** → **API Tokens** → **Create Token** → **Create Custom
   Token**. Nom : `Kazo OS beta site`. Permission : **Account** · **Cloudflare Pages** · **Edit**.
   Account Resources : **Include** · le compte. **Continue to summary** → **Create Token** ; copier
   le jeton (il n'est montré qu'une fois).
4. **Identifiant du compte.** Page d'accueil du compte (Workers & Pages) : **Account ID** dans la
   colonne de droite ; le copier.
5. **Secrets GitHub.** Dépôt `Kazo-ca/Os` → **Settings** → **Secrets and variables** →
   **Actions** → **New repository secret** : `CLOUDFLARE_API_TOKEN` (le jeton), puis
   `CLOUDFLARE_ACCOUNT_ID` (l'identifiant).
6. Facultatif : si le projet porte un autre nom, variable de dépôt `BETA_SITE_PROJECT` (onglet
   **Variables**) avec ce nom.

Le workflow ne tourne qu'à partir de la création de la branche `beta` (chantier 8). Sans secrets,
il échoue avec un message qui renvoie à cette page.

## Vérifié

- `BetaSiteScriptTests` (Kazo.Os.Ecosystem.Tests) : nom renommé sur le vrai `wwwroot`, refus d'un
  fichier sans le texte attendu, limites de fichiers et de taille.
- `KazoOsChannelLinkTests` (Kazo.Os.AppSdk.Blazor.Tests) : canal lu de la version, cible du lien
  selon l'hôte (`os.kazo.ca`, un domaine qui l'imite, `localhost`).
- Essai dans Chromium d'une publication `3.4.0-preview.1` passée par `brand-beta-site.sh` : onglet
  « Kazo OS Beta », pastille BETA à l'écran de connexion, Paramètres ▸ À propos avec la pastille et
  « Back to the stable version ».

## Tâches

- [x] Mesurer le site publié contre les limites de Cloudflare Pages.
- [x] Workflow `deploy-beta-site.yml`, scripts `brand-beta-site.sh` et `check-static-site-limits.sh`.
- [x] Redirection `os.kazo.ca/beta`, exclue du service worker.
- [x] Pastille « Bêta », liens « Essayer la bêta » et « Revenir à la version stable ».
- [x] Réponse « redirigée » de Cloudflare servie sans marqueur par le service worker.
- [ ] Projet Cloudflare, domaine et secrets (Benoit, étapes ci-dessus).
- [ ] Premier déploiement réel (chantier 8, à la création de `beta`).
