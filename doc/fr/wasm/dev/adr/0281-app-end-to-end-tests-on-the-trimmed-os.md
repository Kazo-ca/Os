> [English version](../../../../en/wasm/dev/adr/0281-app-end-to-end-tests-on-the-trimmed-os.md)

# ADR 0281 : les tests de bout en bout des apps tournent sur l'OS rogné publié

## Statut

Acceptée le 2026-11-17 : Benoit a choisi Q1 = A. Série `trimmed-e2e/base`
(troisième verrou contre le refus de Kazo AI 0.1.1).

- **Complète :** l'[ADR 0277](0277-trimmed-api-check-before-publishing.md)
  (vérification statique des API, au build et dans le Store) par une
  vérification à l'exécution, dans l'OS que les utilisateurs chargent.
- **S'appuie sur :** l'[ADR 0113](0113-trimming-implementation-adjustments.md)
  (publication rognée), l'[ADR 0173](0173-automatic-releases-from-the-version-file.md)
  (la version publie `kazoos-wasm-<version>.zip`), l'[ADR 0182](0182-install-apps-from-github.md)
  (`kazopkg install` depuis un dépôt GitHub), l'[ADR 0176](0176-app-repository-template.md)
  (sous-module `external/KazoOs` des apps) et l'[ADR 0271](0271-beta-channel-and-automatic-app-updates.md) §7
  (workflow partagé `kazoos-ci.yml` d'Os-AppTemplate).

## Contexte

- Kazo AI 0.1.1 a été refusée par le Store de os.kazo.ca 3.4.1 : elle appelait
  `MatchEvaluator`, `Enumerable.Sum<double>` et `ChannelWriter.WriteAsync`, que
  la publication rognée avait retirés (corrigé par l'ADR 0280, version 3.4.2).
- Ses tests, comme ceux de toutes les apps, n'ont rien vu. Les tests de bout en
  bout d'une app (`npm run test:e2e` : Chess, IRC, Webamp et Word) lancent
  l'hôte de l'app avec `dotnet run` : rien n'y est rogné, et l'app est un
  assemblage du projet, que le rognage garderait de toute façon.
- L'ADR 0277 attrape les membres absents par lecture des métadonnées, au build
  (`KazoOsPack`) et dans le Store. Elle ne voit pas ce qui ne se révèle qu'à
  l'exécution : réflexion, sérialisation, ressources, fichiers `wwwroot/`
  d'une app installée, comportement du paquet tel que `kazopkg` l'installe.
- IRC a déjà un test manuel qui fait la bonne chose
  (`tests/installed/install-and-chat.mjs`) : il sert une publication rognée de
  l'OS, installe IRC avec `sudo kazopkg install` et discute. Il ne tourne pas
  en CI parce qu'il faut publier l'OS (une dizaine de minutes).

## Décision

### 1. L'OS du test est celui de la version publiée

- La CI d'une app télécharge `kazoos-wasm-<version>.zip` de la version GitHub
  de Kazo-ca/Os que nomme son sous-module (`build/Versions.props` de
  `external/KazoOs`). C'est la publication Release rognée de cette version,
  avec les réglages de os.kazo.ca. Rien n'est recompilé.
- Le site est servi par un serveur statique simple, comme un hôte de version :
  pas de COOP/COEP, pas de cache.

### 2. L'app est installée comme le Store l'installe

- La CI construit le paquet comme le Store : `dotnet build -c Release
  -t:KazoOsPack`, paquets NuGet de nuget.org.
- Le banc de test répond aux adresses
  `https://raw.githubusercontent.com/<dépôt>/kazoos-packages/` avec ce dossier
  (route Playwright : aucune requête ne sort), puis installe l'app avec
  `sudo kazopkg install <dépôt>` dans le Terminal, en administrateur. La
  vérification des API à l'installation (ADR 0182 §5.3, ADR 0242) fait partie
  du test.
- Une capacité dangereuse demandée au premier lancement est accordée par
  l'administrateur du test.

### 3. Un banc de test commun, versionné avec l'OS

- Le banc est un module JavaScript du dépôt Kazo OS,
  `wasm2/KazoOs/build/app-e2e/kazoos-app-e2e.mjs` : servir l'OS, créer le
  premier compte, installer l'app, l'ouvrir depuis le lanceur, accorder une
  capacité. Une app l'atteint par son sous-module, donc le banc a toujours la
  version de l'OS qu'il teste.
- C'est du code de test, pas de l'OS : il ne passe pas par les règles des
  ressources des composants (§I.2 d'`AGENTS.md`). Il ne dépend que de
  `playwright`, déjà utilisé par les tests des apps.

### 4. Un test court pour chaque app

- `kazoos-ci.yml` lance pour **chaque** app, même sans tests de bout en bout,
  `wasm2/KazoOs/build/app-e2e/smoke.mjs` : premier démarrage, installation,
  lancement depuis le lanceur, la fenêtre de l'app reste ouverte 15 s.
- Il échoue sur une erreur de page, une erreur de console qui nomme
  `MissingMethodException`, `MissingFieldException`, `TypeLoadException` ou
  `FileNotFoundException`, un refus de `kazopkg`, ou une panne d'app signalée
  par l'OS.
- Il est actif par défaut. L'entrée `trimmed-smoke-test: false` le coupe pour
  une app qui ne peut pas tourner sans service extérieur ; le résumé du run le
  dit.

### 5. Les suites des apps tournent sur l'OS rogné

- Avec `e2e-test: true`, `kazoos-ci.yml` lance `npm run test:e2e` avec
  `KAZOOS_E2E_WWWROOT` (le site du §1), `KAZOOS_E2E_PACKAGES` (le dossier du
  §2) et `KAZOOS_E2E_REPOSITORY` (`<propriétaire>/<dépôt>`).
- Le banc de l'app (aujourd'hui `tests/e2e/host.mjs`) utilise le banc commun
  quand ces variables existent, et son hôte `dotnet run` sinon. La boucle
  locale du développeur reste rapide ; la CI teste ce que les utilisateurs
  exécutent.
- Chaque app adapte son banc une fois (un chantier par app). Une suite pas
  encore adaptée ignore les variables et tourne comme avant.

### 6. Une version sans le banc

- Le banc arrive avec la première version de Kazo OS qui le contient. Pour une
  app dont le sous-module est plus ancien, le test court est sauté avec un
  avertissement ; la mise à jour du SDK (ADR 0271) l'active.

## Questions posées à Benoit

Posée dans le fil le 2026-11-17.

**Q1. Comment faire tourner les tests E2E des apps sur l'OS rogné ?**

- **A (recommandé, choisi) :** l'OS publié (`kazoos-wasm-<version>.zip`, le
  build de os.kazo.ca) et l'app installée par `kazopkg`. Les suites des apps
  tournent dessus, et un test court commun couvre aussi les apps sans tests
  de bout en bout.
- **B :** publier l'hôte de l'app en rognant. Moins de travail, mais le
  rognage garde ce que l'app appelle : Kazo AI serait passée.
- **C :** le test court commun seulement. Les suites restent sur
  `dotnet run`, donc une panne plus loin dans l'app passe encore.

## Conséquences

- Une app qui casse sur l'OS rogné échoue dans sa CI, donc aussi dans sa mise à
  jour automatique du SDK (ADR 0271) : rien n'est publié.
- La CI d'une app télécharge environ 50 Mio de plus et dure quelques minutes de
  plus (premier démarrage de l'OS, une installation par test).
- Le test dépend de la version GitHub de Kazo OS (fichier
  `kazoos-wasm-<version>.zip`), pas du serveur ni de os.kazo.ca.

## Chantiers

1. `trimmed-e2e/1-adr` : cette ADR.
2. `trimmed-e2e/2-harness` : `build/app-e2e/` (banc commun, test court, README),
   vérifié sur la publication rognée avec un vrai paquet ; docs
   `framework/app-repository.md`.
3. Os-AppTemplate : `kazoos-ci.yml` (télécharger le site, construire le paquet,
   test court, variables des suites, entrées `app-directory` et
   `trimmed-smoke-test`), exemple de banc dans le modèle, docs.
4. Un chantier par app qui a des suites (IRC, Webamp, Word, Chess) : son banc
   utilise le banc commun quand la CI le demande. Pour Chess, seulement les
   tests.
