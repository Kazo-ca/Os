> [Version française](../../../../fr/wasm/dev/adr/0286-lazy-services-start-and-manual-kazowebd.md)

# ADR 0286: automatic services start before their code is loaded, and `kazowebd` becomes manual

## Status

**Accepted** on 2026-11-18: Benoit answered "295. C) with a nuance" to question
295 of the fix #625 (recorded at the end). Patch version 3.5.2, branch
`fix/625-feature-service-autostart`.

- **Adjusts:** [ADR 0077](0077-hosted-static-sites.md): `kazowebd` no longer
  starts on its own; it becomes a manual service.
- **Completes:** [ADR 0097](0097-service-health-and-startup.md) §4 (when
  services start) and [ADR 0262](0262-feature-service-registry-and-async-resolution.md)
  (services of features).
- **Builds on:** [ADR 0047](0047-app-definition-and-generated-manifests.md) (an
  app's code is loaded on demand, one DLL per app) and the fix #621 (a child is
  discovered with its feature).

## Context

On Kazo OS 3.5.1, the automatic `Service` child of a feature stayed
`inactive (dead)` after the login (#625). An app that called it before another
child of the feature was launched got an error.

The cause goes beyond features. At the login (and at boot for a system service),
the orchestrator chose the services to start among the **descriptors already
discovered**. In the browser host, each app's code is discovered only at its
first launch. So no automatic service started before something launched its app,
`kazowebd` included, although `LaunchAsync` knows how to load that code.

## Decision

1. **The services to start are chosen from the catalog.** An enabled service,
   automatic for that moment (login, or boot for a system app), is a candidate
   even if its code is not loaded. Its start mode (`service.conf`,
   `systemctl enable` and `disable`) is read **before** any loading: a manual or
   disabled service loads nothing. The launch then loads the service's code,
   with its feature (#621). A host with no on-demand loader starts only the
   services it discovered, as before.
2. **`kazowebd` becomes a manual service** (`AutoStart = false`). It already did
   not start on its own in the browser; making it automatic would have loaded
   the web server at every login. An administrator starts it
   (`systemctl start kazowebd`, Web Server Manager) or makes it automatic
   (`systemctl enable kazowebd`).
3. **A feature controls the services it provides.** A child may start, stop and
   set the start mode of a service of its own feature without
   `services.manage`. The rule comes from the catalog (the parent of both
   manifests), not from the loaded assemblies: the sibling service's code is
   often not loaded yet. The "same assembly" rule still holds for other apps.
4. **Outside this version**, recorded as `TODO` issues:
   - a Services tab in System Monitor (#626);
   - an ordinary app that provides its own service and controls it (#627);
   - apps launched at the session opening, like the Windows startup apps
     (#628).

## Consequences

- In the browser, an automatic service (of a feature, of a system app or of an
  installed app) now starts at the login, and only the code of the services that
  start is loaded.
- `systemctl status kazowebd` answers `inactive (dead)` after the login until an
  administrator has started it or made it automatic. The tests that use
  `kazowebd` already started it with `systemctl start`.
- Tests: `AppLifecycleOrchestratorTests.LazyServices` (a feature's service that
  is not loaded starts; a manual service loads nothing) and
  `AppIntentDispatcherTests.A_feature_child_controls_a_service_of_its_feature_whose_code_is_not_loaded_yet`.

## Question asked

**295. Scope of the 3.5.2 fix.**

- A) **(Recommended)** Every enabled automatic service of the catalog, feature or
  not; `kazowebd` starts at every login.
- B) Only the `Service` children of a feature.
- C) **(Chosen)** A, and `kazowebd` becomes a manual service.

Benoit's answer (translated from French): "295. C) with a nuance: each service
declares its start type at install, and the user can stop it and change its
start type. A feature can control the start type of the services it provides.
So we need a tab in `System Monitor` with the services. Ideally, an application
should also be able to provide its own service and control it. In Windows, you
can start an application when Windows opens; we should have the same user
experience. (We can create a TODO for what we do not need to ship the current
work.)"
