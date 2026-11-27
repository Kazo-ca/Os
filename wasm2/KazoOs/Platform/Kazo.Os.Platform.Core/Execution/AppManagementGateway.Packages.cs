using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Apps installed from their GitHub repository (ADR 0182, ADR 0189) behind <see cref="KazoAppPackages"/>.
/// The OS reads its own files and downloads by itself. A personal install from a trusted source is
/// written as the session user; a system install, an untrusted source or a blocked app needs the
/// two-step approval of ADR 0189 §4 (a fresh administrator sign-in, then the OS's approval window),
/// after which the OS writes as the system.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <inheritdoc />
    public async ValueTask<KazoPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        return ToPublic(await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        InstalledAppsReadResult system = await packages.ReadInstalledAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult personal = await packages
            .ReadPersonalInstalledAsync(CreateUserWriter(), _principal.HomePath, cancellationToken)
            .ConfigureAwait(false);
        List<KazoInstalledPackage> result = [];
        foreach ((string appId, AppPackageSource source) in system.Sources)
        {
            result.Add(await WithPreviousAsync(packages, ToPublic(appId, source, systemWide: true), source, null, cancellationToken).ConfigureAwait(false));
        }

        foreach ((string appId, AppPackageSource source) in personal.Sources.Where(pair => !system.Sources.ContainsKey(pair.Key)))
        {
            result.Add(await WithPreviousAsync(packages, ToPublic(appId, source, systemWide: false), source, _principal.HomePath, cancellationToken)
                .ConfigureAwait(false));
        }

        return [.. result.OrderBy(static package => package.AppId, StringComparer.Ordinal)];
    }

    private async ValueTask<KazoInstalledPackage> WithPreviousAsync(
        AppPackageInstaller packages, KazoInstalledPackage package, AppPackageSource source, string? home, CancellationToken cancellationToken)
    {
        if (source.Version is not { } current)
        {
            return package;
        }

        IAppFileSystemGateway reader = home is null ? CreateSystemWriter() : CreateUserWriter();
        SemanticVersion? previous = await packages.FindPreviousVersionAsync(reader, package.AppId, current, cancellationToken, home).ConfigureAwait(false);
        return package with { PreviousVersion = previous?.ToString() };
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway reader = CreateSystemWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
        AppPackageSource parsed = await ResolveSourceAsync(packages, source, sources, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult system = await packages.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult personal = await packages
            .ReadPersonalInstalledAsync(CreateUserWriter(), _principal.HomePath, cancellationToken)
            .ConfigureAwait(false);
        AppPackageIndex index = await Translate(() => packages.GetIndexAsync(parsed, sources, cancellationToken)).ConfigureAwait(false);
        AppPackageSource? current = null;
        bool? systemWide = null;
        if (system.Sources.TryGetValue(index.AppId, out AppPackageSource? forSystem) && forSystem.IsSameRepository(parsed))
        {
            (current, systemWide) = (forSystem, true);
        }
        else if (personal.Sources.TryGetValue(index.AppId, out AppPackageSource? forUser) && forUser.IsSameRepository(parsed))
        {
            (current, systemWide) = (forUser, false);
        }

        AppSourceTrust trust = sources.GetTrust(parsed);
        return new KazoPackageInfo(
            parsed.Name,
            index.AppId,
            [.. index.Versions.Select(static entry => entry.Version.ToString())],
            AppVersionChoice.Newest(index, packages.SdkVersion, current?.Version)?.Version.ToString() ?? index.Latest?.Version.ToString(),
            trust != AppSourceTrust.Untrusted,
            current?.Version?.ToString())
        {
            Trust = ToPublic(trust),
            InstalledSystemWide = systemWide,
            SignatureRequired = sources.GetSigningKeys(parsed).Count > 0,
        };
    }

    /// <inheritdoc />
    public ValueTask<KazoPackageInstallReport> InstallPackageAsync(
        string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
        InstallPackageAsync(source, scope, progress: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<KazoPackageInstallReport> InstallPackageAsync(
        string source, KazoPackageScope scope, IProgress<KazoPackageInstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        PackageInstallSession session = await OpenInstallSessionAsync(source, scope, cancellationToken).ConfigureAwait(false);
        // ADR 0248 §1: the whole plan is downloaded and checked first, so no administrator is asked
        // for a package that would be refused, and nothing is written when a dependency is missing.
        AppPackageInstallPlan plan = await Translate(
                () => session.Packages.BuildPlanAsync(session.Source, session.Context, progress, cancellationToken))
            .ConfigureAwait(false);
        AppPackagePlan root = plan.Root;
        AppManifest manifest = root.Package.Manifest;
        string by = _principal.LoginName.Value;
        if (plan.RequiresAdministrator)
        {
            progress?.Report(new KazoPackageInstallProgress(KazoPackageInstallStage.AwaitingApproval));
            // One consent for the plan: the window lists every package, the administrator signs in once.
            by = await ApproveTwoStepAsync(DescribeInstallOperation(plan), "apps.package.install", ToApprovalRequest(plan), cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new KazoPackageInstallProgress(KazoPackageInstallStage.Installing));
        Dictionary<string, IReadOnlyList<string>> personalCopies = new(StringComparer.Ordinal);
        foreach (AppPackagePlan package in plan.Packages.Where(static package => package.SystemWide))
        {
            personalCopies[package.Package.AppId] = await session.Packages
                .FindPersonalCopiesAsync(session.System, package.Package.AppId, cancellationToken)
                .ConfigureAwait(false);
        }

        await session.Packages
            .CommitPlanAsync(
                plan,
                package => package.SystemWide ? session.System : session.User,
                by,
                package => personalCopies.TryGetValue(package.Package.AppId, out IReadOnlyList<string>? homes) ? homes : [],
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> reactivatedDependents;
        try
        {
            foreach (AppPackagePlan package in plan.Packages.Where(static package => package.Package.IsFeature))
            {
                await ApproveFeatureAsync(package.Package, cancellationToken).ConfigureAwait(false);
            }

            // Before the catalog reloads: the reactivated apps join it enabled (ADR 0048, ADR 0248 §1, §3).
            await ReactivateDependenciesAsync(plan, session, cancellationToken).ConfigureAwait(false);
            reactivatedDependents = await ReactivateDependentsAsync(plan, session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The packages are written, each with its dependencies: whatever failed after them, the
            // catalog takes them, so the OS never shows a state older than its files.
            await ActivateInstalledAppsAsync(CancellationToken.None).ConfigureAwait(false);
        }

        WriteInstalledForUserEvents(plan);

        ICapabilityCatalog capabilities = PlanCapabilities(plan);
        return ToReport(root, personalCopies.TryGetValue(manifest.Id, out IReadOnlyList<string>? replaced) ? replaced.Count : 0, capabilities) with
        {
            Dependencies = [.. plan.Dependencies.Select(package => ToReport(
                package, personalCopies.TryGetValue(package.Package.AppId, out IReadOnlyList<string>? homes) ? homes.Count : 0, capabilities))],
            Reactivated = [.. plan.Reactivations.Select(static app => app.Name)],
            ReactivatedDependents = reactivatedDependents,
        };
    }

    /// <summary>The report of one package of a committed plan.</summary>
    private KazoPackageInstallReport ToReport(AppPackagePlan plan, int replacedPersonalCopies, ICapabilityCatalog capabilities)
    {
        AppManifest manifest = plan.Package.Manifest;
        return new KazoPackageInstallReport(
            manifest.Id,
            manifest.Name,
            plan.Package.Version.ToString(),
            plan.Source.ToString(),
            plan.PreviousVersion?.ToString(),
            manifest.Capabilities,
            plan.Trust != AppSourceTrust.Untrusted)
        {
            Trust = ToPublic(plan.Trust),
            SystemWide = plan.SystemWide,
            BlockedBy = plan.BlockReason?.ToString(),
            ReplacedPersonalCopies = replacedPersonalCopies,
            RestartRequired = !RunsVersion(manifest.Id, plan.Package.Version),
            Signed = plan.Signed,
            IsFeature = plan.Package.IsFeature,
            EntryPoints = DescribeEntryPoints(plan.Package),
            // ADR 0246: dangerous as the catalog of the running OS says, features' permissions included.
            DangerousCapabilities = [.. manifest.Capabilities.Where(capabilities.IsDangerous)],
        };
    }

    /// <summary>The children of a feature as the approval window and <c>kazopkg</c> name them: "name (kind)".</summary>
    private static IReadOnlyList<string> DescribeEntryPoints(AppPackage package) =>
        [.. package.Children.Select(static child => $"{child.Name} ({child.Kind.ToString().ToLowerInvariant()})")];

    /// <summary>
    /// ADR 0260 §3: the administrator's consent to the installation is the machine approval of the
    /// feature's dangerous capabilities. The <c>allow</c> lines of <c>/etc/kazoos/app-permissions.config</c>
    /// (ADR 0049 §4) are written for the feature and each child, with the real-network ports each one
    /// declares (ADR 0233 §4), so the first launch of an entry point shows no approval window. A
    /// user's own refusal (ADR 0049 §9) stays possible in Settings ▸ Permissions.
    /// </summary>
    private async ValueTask ApproveFeatureAsync(AppPackage package, CancellationToken cancellationToken)
    {
        if (PermissionStore?.Invoke() is not { } permissions)
        {
            return;
        }

        foreach (AppManifest manifest in package.Manifests)
        {
            string[] dangerous = [.. manifest.Capabilities.Where(AppCapabilities.IsDangerous).Distinct(StringComparer.Ordinal)];
            if (dangerous.Length == 0)
            {
                continue;
            }

            await permissions.WriteMachineDecisionsAsync(manifest.Id, dangerous, Policy.AppPermissionDecision.Allow, cancellationToken).ConfigureAwait(false);
            if (dangerous.Contains(AppCapabilities.NetworkRealAccess, StringComparer.Ordinal))
            {
                await permissions
                    .WriteApprovedRealNetworkPortsAsync(manifest.Id, RealNetworkPorts.Normalize(manifest.RealNetworkPorts) ?? [], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Removes the machine decisions ADR 0260 §3 wrote for a feature and its children, once it is removed.</summary>
    private async ValueTask ForgetFeatureApprovalsAsync(string featureId, CancellationToken cancellationToken)
    {
        if (PermissionStore?.Invoke() is not { } permissions)
        {
            return;
        }

        foreach (string appId in _catalog.GetFeatureChildren(featureId).Prepend(featureId))
        {
            if (!_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest))
            {
                continue;
            }

            string[] dangerous = [.. manifest.Capabilities.Where(AppCapabilities.IsDangerous).Distinct(StringComparer.Ordinal)];
            if (dangerous.Length > 0)
            {
                await permissions.WriteMachineDecisionsAsync(appId, dangerous, null, cancellationToken).ConfigureAwait(false);
                await permissions.WriteApprovedRealNetworkPortsAsync(appId, null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<KazoInstalledPackage> RemovePackageAsync(
        string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway user = CreateUserWriter();
        InstalledAppsReadResult personal = await packages.ReadPersonalInstalledAsync(user, _principal.HomePath, cancellationToken).ConfigureAwait(false);
        bool forUser = scope == KazoPackageScope.User || (scope == KazoPackageScope.Auto && personal.Sources.ContainsKey(appId));
        IAppFileSystemGateway system = CreateSystemWriter();
        if (forUser)
        {
            if (!personal.Sources.ContainsKey(appId))
            {
                throw new KazoPackageException("apps.package.not-installed", $"{appId} was not installed for you from a package.");
            }

            AppPackageSource removedForUser = await Translate(
                    () => packages.RemoveAsync(user, appId, _principal.LoginName.Value, cancellationToken, _principal.HomePath))
                .ConfigureAwait(false);
            // ADR 0248 §3: once it is gone, the apps that need it are disabled, never left broken;
            // before the catalog reloads, so they leave it disabled. A copy installed for the system
            // still provides it: then nothing is disabled.
            bool stillForSystem = (await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false)).Sources.ContainsKey(appId);
            IReadOnlyList<string> disabledForUser = stillForSystem
                ? []
                : await DisableDependentsAsync(appId, systemWide: false, system, user, cancellationToken).ConfigureAwait(false);
            await ActivateInstalledAppsAsync(CancellationToken.None).ConfigureAwait(false);
            return ToPublic(appId, removedForUser, systemWide: false, removed: true) with { DisabledDependents = disabledForUser };
        }

        InstalledAppsReadResult installed = await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source))
        {
            throw new KazoPackageException("apps.package.not-installed", $"{appId} was not installed from a package.");
        }

        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppManifest? manifest = _catalog.Manifests.TryGetValue(appId, out AppManifest? loaded) ? loaded : null;
        string name = manifest?.Name ?? appId;
        string operation = Strings.For(_language())["Admin.App.RemovePackage", name, source.Name];
        string by = await ApproveTwoStepAsync(
            operation,
            "apps.package.remove",
            new AppInstallApprovalRequest(
                appId,
                name,
                source.Version?.ToString() ?? string.Empty,
                null,
                null,
                source.ToString(),
                sources.GetTrust(source),
                manifest?.Capabilities ?? [],
                AppInstallApprovalOperation.Remove,
                true,
                string.Empty,
                _principal,
                _caller)
            {
                IsFeature = manifest?.Kind == AppKind.Feature,
                // ADR 0248 §3: the window says which apps stop working and are disabled.
                DisabledDependents = [.. FindDependents(appId).Select(id => _catalog.Manifests[id].Name)],
            },
            cancellationToken).ConfigureAwait(false);
        bool feature = manifest?.Kind == AppKind.Feature;
        if (feature)
        {
            // Before the catalog forgets the children: the approvals of ADR 0260 §3 go with the feature.
            await ForgetFeatureApprovalsAsync(appId, cancellationToken).ConfigureAwait(false);
        }

        AppPackageSource removed = await Translate(() => packages.RemoveAsync(system, appId, by, cancellationToken)).ConfigureAwait(false);
        IReadOnlyList<string> disabled = await DisableDependentsAsync(appId, systemWide: true, system, user, cancellationToken).ConfigureAwait(false);
        try
        {
            if (feature)
            {
                // ADR 0287 §7: a removed feature forgets its "disabled" line, so a new install arrives enabled.
                await ForgetDisabledFeatureAsync(appId, system, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // The package is gone either way: the running catalog must learn it.
            await ActivateInstalledAppsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return ToPublic(appId, removed, systemWide: true, removed: true) with { DisabledDependents = disabled };
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackageInstallReport> RollbackPackageAsync(
        string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway system = CreateSystemWriter();
        IAppFileSystemGateway user = CreateUserWriter();
        InstalledAppsReadResult personal = await packages.ReadPersonalInstalledAsync(user, _principal.HomePath, cancellationToken).ConfigureAwait(false);
        bool forUser = scope == KazoPackageScope.User || (scope == KazoPackageScope.Auto && personal.Sources.ContainsKey(appId));
        InstalledAppsReadResult installed = forUser ? personal : await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        string? home = forUser ? _principal.HomePath : null;
        IAppFileSystemGateway writer = forUser ? user : system;
        if (!installed.Sources.TryGetValue(appId, out AppPackageSource? source) || source.Version is not { } current)
        {
            throw new KazoPackageException("apps.package.not-installed", $"{appId} was not installed from a package.");
        }

        SemanticVersion previous = await packages.FindPreviousVersionAsync(writer, appId, current, cancellationToken, home).ConfigureAwait(false)
            ?? throw new KazoPackageException("apps.package.no-previous", $"{appId} {current} has no previous version kept.");
        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppSourceTrust trust = sources.GetTrust(source);
        AppManifest? manifest = _catalog.Manifests.TryGetValue(appId, out AppManifest? loaded) ? loaded : null;
        string name = manifest?.Name ?? appId;
        string by = _principal.LoginName.Value;
        // The approvals of an install of the same scope (ADR 0214 §4.1).
        if (!forUser || trust == AppSourceTrust.Untrusted)
        {
            by = await ApproveTwoStepAsync(
                Strings.For(_language())[
                    forUser ? "Admin.App.RollbackPackageForUser" : "Admin.App.RollbackPackage",
                    name,
                    previous.ToString(),
                    source.Name,
                    _principal.LoginName.Value],
                "apps.package.rollback",
                new AppInstallApprovalRequest(
                    appId,
                    name,
                    previous.ToString(),
                    current.ToString(),
                    null,
                    source.WithVersion(previous).ToString(),
                    trust,
                    manifest?.Capabilities ?? [],
                    AppInstallApprovalOperation.Rollback,
                    !forUser,
                    string.Empty,
                    _principal,
                    _caller),
                cancellationToken).ConfigureAwait(false);
        }

        (AppPackageSource from, AppPackageSource to) = await Translate(() => packages.RollbackAsync(writer, appId, by, cancellationToken, home))
            .ConfigureAwait(false);
        await ActivateInstalledAppsAsync(CancellationToken.None).ConfigureAwait(false);
        return new KazoPackageInstallReport(
            appId,
            name,
            to.Version?.ToString() ?? string.Empty,
            to.ToString(),
            from.Version?.ToString(),
            manifest?.Capabilities ?? [],
            trust != AppSourceTrust.Untrusted)
        {
            Trust = ToPublic(trust),
            SystemWide = !forUser,
            RestartRequired = to.Version is not { } version || !RunsVersion(appId, version),
        };
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> RepairPackagesAsync(bool system, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        List<string> repaired = [.. await packages.RepairAsync(CreateUserWriter(), cancellationToken, _principal.HomePath).ConfigureAwait(false)];
        if (system)
        {
            IAppFileSystemGateway writer = await AuthorizePackageAsync(
                Strings.For(_language())["Admin.App.RepairPackages"], "apps.package.repair", cancellationToken).ConfigureAwait(false);
            repaired.AddRange(await packages.RepairAsync(writer, cancellationToken).ConfigureAwait(false));
        }

        if (repaired.Count > 0)
        {
            await ActivateInstalledAppsAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return repaired;
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackageSources> AddPackageSourceAsync(string source, bool trusted, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourceSpecification parsed = ParseSourceSpecification(source);
        if (parsed.IsCatalog && parsed.CatalogAddress is null)
        {
            throw new KazoPackageException("apps.source.invalid", $"A new catalog needs its address: {parsed.Name}=https://…/catalog.json.");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppSources", $"+ {parsed.Name}{(trusted ? " (trusted)" : string.Empty)}"],
            "apps.sources.add",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppSourcesConfigDocument.MachinePath,
            content => AppSourcesConfigDocument.AddSource(content ?? AppSourcesConfigDocument.BuildDefaultContent(packages.Defaults), parsed, trusted),
            cancellationToken).ConfigureAwait(false);
        return await GetPackageSourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackageSources> RemovePackageSourceAsync(string source, CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        AppSourceSpecification parsed = ParseSourceSpecification(source);
        AppSourcesConfig current = await packages.ReadSourcesAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false);
        bool listed = (parsed.IsCatalog && current.Catalogs.ContainsKey(parsed.Name))
            || current.TrustedKeys.Contains(parsed.TrustKey)
            || current.UntrustedKeys.Contains(parsed.TrustKey);
        if (!listed)
        {
            throw new KazoPackageException("apps.source.not-found", $"{parsed.Name} is not in {AppSourcesConfigDocument.MachinePath.Value}.");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppSources", $"- {parsed.Name}"],
            "apps.sources.remove",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppSourcesConfigDocument.MachinePath,
            content => AppSourcesConfigDocument.RemoveSource(content, parsed),
            cancellationToken).ConfigureAwait(false);
        return await GetPackageSourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackagePolicy> GetPackagePolicyAsync(CancellationToken cancellationToken = default)
    {
        AppPackageInstaller packages = RequirePackages();
        return ToPublic(await packages.ReadPolicyAsync(CreateSystemWriter(), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<KazoPackagePolicy> SetPackagePolicyAsync(
        KazoPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        AppPackageInstaller packages = RequirePackages();
        AppBlockKind rule = (AppBlockKind)kind;
        string section = AppPolicyConfigDocument.SectionOf(rule);
        string key = value.Trim();
        if (key.Any(static c => char.IsWhiteSpace(c) || c is '=' or '[' or ']' or '#' or ';'))
        {
            throw new KazoPackageException("apps.policy.invalid", $"'{value}' cannot be a key of [{section}].");
        }

        IAppFileSystemGateway system = await AuthorizePackageAsync(
            Strings.For(_language())["Admin.App.EditAppPolicy", $"{(blocked ? '+' : '-')} [{section}] {key}"],
            blocked ? "apps.policy.block" : "apps.policy.unblock",
            cancellationToken).ConfigureAwait(false);
        await EditMachineFileAsync(
            system,
            AppPolicyConfigDocument.MachinePath,
            content => AppPolicyConfigDocument.Set(content ?? AppPolicyConfigDocument.BuildDefaultContent(packages.Defaults), rule, key, blocked),
            cancellationToken).ConfigureAwait(false);
        return await GetPackagePolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    private AppPackageInstaller RequirePackages()
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        return _packages?.Invoke() ?? throw new NotSupportedException("This host cannot install apps from a repository.");
    }

    // removed: the package was just removed; Loaded then says the app is still in the page,
    // whatever its libraries wait for (ADR 0261 §3).
    private KazoInstalledPackage ToPublic(string appId, AppPackageSource source, bool systemWide, bool removed = false) => new(
        appId,
        source.Name,
        source.Version?.ToString() ?? string.Empty,
        source.Version is { } installedVersion
            && (removed ? IsInCatalogAt(appId, installedVersion) : RunsVersion(appId, installedVersion)))
    {
        SystemWide = systemWide,
    };

    private static KazoPackageTrust ToPublic(AppSourceTrust trust) => (KazoPackageTrust)trust;

    private static KazoPackageSources ToPublic(AppSourcesConfig sources) =>
        new(sources.GitHubHost, [.. sources.TrustedKeys.Order(StringComparer.OrdinalIgnoreCase)], sources.Warnings)
        {
            Catalogs = [.. sources.Catalogs
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new KazoPackageCatalog(
                    pair.Key,
                    pair.Value.AbsoluteUri,
                    ToPublic(sources.GetCatalogTrust(pair.Key)),
                    sources.BuiltInKeys.Contains(AppSourcesConfigDocument.CatalogTrustPrefix + pair.Key)))],
            UntrustedSources = [.. sources.UntrustedKeys.Order(StringComparer.OrdinalIgnoreCase)],
            BuiltInSources = [.. sources.BuiltInKeys.Order(StringComparer.OrdinalIgnoreCase)],
            UpdateIntervalHours = sources.UpdateIntervalHours,
        };

    private static KazoPackagePolicy ToPublic(AppPolicy policy) => new(
        [.. policy.BlockedApps.Order(StringComparer.Ordinal)],
        [.. policy.BlockedCategories.Order(StringComparer.OrdinalIgnoreCase)],
        [.. policy.BlockedCapabilities.Order(StringComparer.Ordinal)],
        policy.Warnings);

    private static AppSourceSpecification ParseSourceSpecification(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return AppSourceSpecification.TryParse(source, out AppSourceSpecification? parsed)
            ? parsed
            : throw new KazoPackageException(
                "apps.source.invalid",
                $"'{source}' is not a source: use name=https://…/catalog.json, catalog:name, github:owner or github:owner/repo.");
    }

    private static async ValueTask<T> Translate<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AppPackageException exception)
        {
            throw new KazoPackageException(exception.Code, exception.Message);
        }
    }

    /// <summary>
    /// Removes the feature's line from <c>/etc/kazoos/features.config</c> (ADR 0287 §7). A missing file
    /// means it was never disabled; any other failure is reported, since the line would disable the
    /// next install.
    /// </summary>
    private static async ValueTask ForgetDisabledFeatureAsync(string featureId, IAppFileSystemGateway system, CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(
                system, FeaturesConfigDocument.MachinePath, content => FeaturesConfigDocument.MarkEnabled(content, featureId), createIfMissing: false, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null && error.Code != FileSystemErrorCode.NotFound)
        {
            throw new IOException(
                $"{featureId} was removed, but {error.Path?.Value ?? FeaturesConfigDocument.MachinePath.Value} could not be updated ({error.Code}): "
                + $"remove its line by hand, or a new install of {featureId} arrives disabled.");
        }
    }

    private static async ValueTask EditMachineFileAsync(
        IAppFileSystemGateway system, VirtualPath path, Func<string?, string> edit, CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(system, path, edit, createIfMissing: true, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            throw new IOException($"{error.Path?.Value ?? path.Value}: {error.Code}.");
        }
    }

    /// <summary>The system writer the OS acts with: reads of its own files, and writes once authorized.</summary>
    private AppFileSystemGateway CreateSystemWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

    /// <summary>The session user's writer, for their own personal installs (ADR 0189 §3).</summary>
    private AppFileSystemGateway CreateUserWriter() =>
        new(_fileSystem, _clock, OsSettingsService.CreateUserOperation(_principal), [.. _principal.GroupIds.Select(static id => id.ToString())]);

    /// <summary>
    /// One administrator authentication, as for a machine uninstall (ADR 0055 §5), then the system
    /// writer: the edits of <c>app-sources.config</c> and <c>app-policy.config</c>, which an
    /// administrator may also make with <c>sudo nano</c>.
    /// </summary>
    private async ValueTask<IAppFileSystemGateway> AuthorizePackageAsync(string operation, string action, CancellationToken cancellationToken)
    {
        AdministratorAuthenticationResult authentication = await AuthenticateAsync(operation, action, _isRootElevated, cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new KazoUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).", path: AppSourcesConfigDocument.MachinePath.Value);
        }

        return CreateSystemWriter();
    }

    /// <summary>
    /// The two steps of ADR 0189 §4, every time: a fresh administrator sign-in (no <c>sudo</c>
    /// timestamp, not even a root process: Q28 answer A), then the approval window showing the
    /// app, its source, its trust level and its capabilities.
    /// </summary>
    /// <returns>The login of the administrator who approved, for the log.</returns>
    private async ValueTask<string> ApproveTwoStepAsync(
        string operation, string action, AppInstallApprovalRequest request, CancellationToken cancellationToken)
    {
        AdministratorAuthenticationResult authentication = await AuthenticateAsync(operation, action, isRootElevated: false, cancellationToken).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            throw new KazoUnauthorizedAccessException(
                $"Administrator authentication failed ({authentication.FailureReason}).", path: InstalledAppsLayout.Root);
        }

        string administrator = authentication.AdministratorLogin ?? "root";
        IAppInstallApprovalPrompt prompt = _approval?.Invoke()
            ?? throw new KazoUnauthorizedAccessException(
                "No approval window is available on this host (ADR 0189 section 4).", path: InstalledAppsLayout.Root);
        AppInstallApprovalOutcome outcome = await prompt
            .RequestAsync(request with { AdministratorLogin = administrator }, cancellationToken)
            .ConfigureAwait(false);
        if (outcome != AppInstallApprovalOutcome.Approved)
        {
            throw new KazoUnauthorizedAccessException(
                $"The administrator {administrator} did not approve ({outcome.ToString().ToLowerInvariant()}).", path: InstalledAppsLayout.Root);
        }

        return administrator;
    }

    private async ValueTask<AdministratorAuthenticationResult> AuthenticateAsync(
        string operation, string action, bool isRootElevated, CancellationToken cancellationToken)
    {
        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new KazoUnauthorizedAccessException(
                "No administrator authentication agent is available on this host.", path: InstalledAppsLayout.Root);
        return await agent.AuthenticateAsync(
            new AdministratorAuthenticationRequest(_caller, operation, action, _principal, isRootElevated),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an event to the user's journal for each package of <paramref name="plan"/> installed for
    /// the user alone (ADR 0254, section 2; ADR 0208); a package installed for everyone is not personal.
    /// </summary>
    private void WriteInstalledForUserEvents(AppPackageInstallPlan plan)
    {
        if (UserEvents is not { } userEvents)
        {
            return;
        }

        foreach (AppPackagePlan package in plan.Packages.Where(static package => !package.SystemWide))
        {
            userEvents.TryWriteSystem(
                Diagnostics.UserEventOwner.Of(_principal),
                new Diagnostics.UserEventAuthor(Diagnostics.OsUserEvents.PackagesAppId, SenderAppId: _caller.Id),
                Diagnostics.OsUserEvents.Installed(package.Package.Manifest, package.Package.Version.ToString()));
        }
    }
}
