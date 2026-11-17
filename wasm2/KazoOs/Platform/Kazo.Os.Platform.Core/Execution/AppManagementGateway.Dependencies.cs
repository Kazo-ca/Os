using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Dependencies of installed packages (ADR 0248): the plan an installation pulls in, with one
/// consent and one transaction; the apps a removal disables and an installation reactivates, written
/// in <c>apps.config</c> (ADR 0048) like any other uninstall, so the installation service applies them.
/// </summary>
internal sealed partial class AppManagementGateway
{
    /// <summary>What one installation works with: the installer, both writers, the parsed source and the context of ADR 0189.</summary>
    private sealed record PackageInstallSession(
        AppPackageInstaller Packages,
        IAppFileSystemGateway System,
        IAppFileSystemGateway User,
        AppPackageSource Source,
        AppPackageInstallContext Context);

    /// <inheritdoc />
    public async ValueTask<KazoPackageInstallPlan> PlanPackageInstallAsync(
        string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
    {
        PackageInstallSession session = await OpenInstallSessionAsync(source, scope, cancellationToken).ConfigureAwait(false);
        AppPackageInstallPlan plan = await Translate(
                () => session.Packages.BuildPlanAsync(session.Source, session.Context, progress: null, cancellationToken))
            .ConfigureAwait(false);
        ICapabilityCatalog capabilities = PlanCapabilities(plan);
        return new KazoPackageInstallPlan(
            [.. plan.Packages.Select(package => new KazoPlannedPackage(
                package.Package.AppId,
                package.Package.Manifest.Name,
                package.Package.Version.ToString(),
                package.Source.ToString(),
                package.PreviousVersion?.ToString(),
                package.SizeBytes,
                package.SystemWide,
                package.Package.IsFeature,
                ToPublic(package.Trust),
                DangerousOf(package.Package, capabilities),
                ReferenceEquals(package, plan.Root)))],
            [.. plan.Reactivations.Select(static app => app.Name)],
            plan.RequiresAdministrator);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoPackageDependent>> GetPackageDependentsAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _ = RequirePackages();
        IReadOnlyList<KazoPackageDependent> dependents =
            [.. FindDependents(appId).Select(id => new KazoPackageDependent(id, _catalog.Manifests[id].Name))];
        return ValueTask.FromResult(dependents);
    }

    /// <summary>Reads what an installation decides with (ADR 0189): sources, block list, scope, what is installed and what is uninstalled.</summary>
    private async ValueTask<PackageInstallSession> OpenInstallSessionAsync(string source, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        AppPackageInstaller packages = RequirePackages();
        IAppFileSystemGateway system = CreateSystemWriter();
        IAppFileSystemGateway user = CreateUserWriter();
        AppSourcesConfig sources = await packages.ReadSourcesAsync(system, cancellationToken).ConfigureAwait(false);
        AppPackageSource parsed = await ResolveSourceAsync(packages, source, sources, cancellationToken).ConfigureAwait(false);
        InstalledAppsReadResult systemInstalled = await packages.ReadInstalledAsync(system, cancellationToken).ConfigureAwait(false);
        AppPackageInstallContext context = new(
            sources,
            await packages.ReadPolicyAsync(system, cancellationToken).ConfigureAwait(false),
            (AppPackageScope)scope,
            _principal.HomePath,
            _catalog,
            systemInstalled,
            await packages.ReadPersonalInstalledAsync(user, _principal.HomePath, cancellationToken).ConfigureAwait(false))
        {
            PackagedAppIds = (_installedPackages?.Invoke()?.Apps.Keys ?? []).Concat(systemInstalled.Sources.Keys).ToHashSet(StringComparer.Ordinal),
            // ADR 0261 §1, §2: the libraries the installed apps carry, and those already in the page.
            InstalledApps = [.. (_installedPackages?.Invoke()?.Apps.Values ?? []).Distinct()],
            LoadedPackageLibraries = InstalledLibraries?.Invoke()?.LoadedFromPackages ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            // ADR 0248 §1: an uninstalled dependency is reactivated rather than downloaded again.
            DisabledApps = await ReadDisabledAppsAsync(system, user, cancellationToken).ConfigureAwait(false),
        };
        return new PackageInstallSession(packages, system, user, parsed, context);
    }

    /// <summary>
    /// The catalog apps uninstalled for the session (ADR 0048), with whether the machine file does
    /// it: the apps listed in either <c>apps.config</c> and every app that needs one of them, as the
    /// installation service computes its effective state.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<string, bool>> ReadDisabledAppsAsync(
        IAppFileSystemGateway system, IAppFileSystemGateway user, CancellationToken cancellationToken)
    {
        SettingsFileReadResult machine = await SettingsTextFile.ReadAsync(system, AppsConfigDocument.MachinePath, cancellationToken).ConfigureAwait(false);
        VirtualPath userPath = AppsConfigDocument.GetUserPath(_principal.HomePath);
        SettingsFileReadResult personal = await SettingsTextFile.ReadAsync(user, userPath, cancellationToken).ConfigureAwait(false);
        HashSet<string> forMachine = AppsConfigDocument.ExpandDependents(
            _catalog, AppsConfigDocument.Read(machine.Content, AppsConfigDocument.MachinePath, _catalog).UninstalledAppIds);
        HashSet<string> forUser = AppsConfigDocument.ExpandDependents(
            _catalog, AppsConfigDocument.Read(personal.Content, userPath, _catalog).UninstalledAppIds);
        Dictionary<string, bool> disabled = new(StringComparer.Ordinal);
        foreach (string appId in forUser)
        {
            disabled[appId] = false;
        }

        foreach (string appId in forMachine)
        {
            disabled[appId] = true;
        }

        return disabled;
    }

    /// <summary>
    /// The installed apps that need <paramref name="appId"/> (or one of its feature's entry points)
    /// through a chain of required dependencies, system apps and entry points excepted (ADR 0248 §3).
    /// An optional dependency does not count: such an app receives <see langword="null"/> (ADR 0262).
    /// </summary>
    private IReadOnlyList<string> FindDependents(string appId)
    {
        HashSet<string> own = [appId, .. _catalog.GetFeatureChildren(appId)];
        return [.. AppsConfigDocument.ExpandDependents(_catalog, own)
            .Where(id => !own.Contains(id) && !_catalog.IsSystemApp(id) && _catalog.GetFeatureOf(id) is null)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// ADR 0248 §3: before a package is removed, disables the apps that need it, in the
    /// <c>apps.config</c> of the removal's scope, and records why, so installing it again reactivates
    /// exactly them. An app already uninstalled is left alone: the user's own choice stays.
    /// </summary>
    /// <returns>The apps disabled.</returns>
    private async ValueTask<IReadOnlyList<string>> DisableDependentsAsync(
        string appId, bool systemWide, IAppFileSystemGateway system, IAppFileSystemGateway user, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, bool> disabled = await ReadDisabledAppsAsync(system, user, cancellationToken).ConfigureAwait(false);
        string[] dependents = [.. FindDependents(appId).Where(id => !disabled.ContainsKey(id))];
        if (dependents.Length == 0)
        {
            return [];
        }

        (IAppFileSystemGateway writer, VirtualPath path, string header) = systemWide
            ? (system, AppsConfigDocument.MachinePath, AppsConfigDocument.MachineDefaultContent)
            : (user, AppsConfigDocument.GetUserPath(_principal.HomePath), AppsConfigDocument.UserDefaultContent);
        await EditMachineFileAsync(
                writer, path, content => AppsConfigDocument.MarkDisabledByDependency(content, dependents, appId, header), cancellationToken)
            .ConfigureAwait(false);
        return dependents;
    }

    /// <summary>
    /// ADR 0248 §1: puts back the uninstalled dependencies of a plan, with the dependencies they
    /// need (ADR 0048 §4), in the file that uninstalled them; the machine file only after the
    /// administrator approved the plan.
    /// </summary>
    private async ValueTask ReactivateDependenciesAsync(AppPackageInstallPlan plan, PackageInstallSession session, CancellationToken cancellationToken)
    {
        foreach (IGrouping<bool, AppPackageReactivation> group in plan.Reactivations.GroupBy(static app => app.ForMachine))
        {
            HashSet<string> appIds = new(StringComparer.Ordinal);
            foreach (AppPackageReactivation app in group)
            {
                appIds.UnionWith(AppsConfigDocument.ExpandDependencies(_catalog, app.AppId));
            }

            (IAppFileSystemGateway writer, VirtualPath path) = group.Key
                ? (session.System, AppsConfigDocument.MachinePath)
                : (session.User, AppsConfigDocument.GetUserPath(_principal.HomePath));
            await EditMachineFileAsync(writer, path, content => AppsConfigDocument.MarkInstalled(content, appIds), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR 0248 §3: installing a package again reactivates the apps its removal disabled, as
    /// <c>[disabled-by-dependency]</c> records them: in the session user's file always (a copy for
    /// the system serves them too), in the machine file only for a system install, which the
    /// administrator approved. A personal copy does not serve the other users, so the machine
    /// file's lines wait for a system install.
    /// </summary>
    /// <returns>The apps reactivated.</returns>
    private async ValueTask<IReadOnlyList<string>> ReactivateDependentsAsync(
        AppPackageInstallPlan plan, PackageInstallSession session, CancellationToken cancellationToken)
    {
        List<string> reactivated = [];
        IEnumerable<(AppPackagePlan Package, IAppFileSystemGateway Writer, VirtualPath Path)> files = plan.Packages.SelectMany(package =>
            package.SystemWide
                ? new[]
                {
                    (package, session.System, AppsConfigDocument.MachinePath),
                    (package, session.User, AppsConfigDocument.GetUserPath(_principal.HomePath)),
                }
                : [(package, session.User, AppsConfigDocument.GetUserPath(_principal.HomePath))]);
        foreach ((AppPackagePlan package, IAppFileSystemGateway writer, VirtualPath path) in files)
        {
            SettingsFileReadResult file = await SettingsTextFile.ReadAsync(writer, path, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> appIds = AppsConfigDocument.ReadDisabledByDependency(file.Content, package.Package.AppId);
            if (appIds.Count == 0)
            {
                continue;
            }

            await EditMachineFileAsync(
                    writer,
                    path,
                    content => AppsConfigDocument.ForgetDisabledByDependency(AppsConfigDocument.MarkInstalled(content, appIds), appIds),
                    cancellationToken)
                .ConfigureAwait(false);
            reactivated.AddRange(appIds);
        }

        return reactivated;
    }

    /// <summary>
    /// The operation the administrator prompt names: the requested package when it needs the
    /// administrator itself, otherwise the dependencies that do (a feature for a personal app, answer 2 A).
    /// </summary>
    private string DescribeInstallOperation(AppPackageInstallPlan plan)
    {
        AppPackagePlan root = plan.Root;
        AppManifest manifest = root.Package.Manifest;
        IStringLocalizer text = Strings.For(_language());
        if (root.RequiresAdministrator)
        {
            return text[
                root.SystemWide
                    ? root.Trust == AppSourceTrust.Untrusted ? "Admin.App.InstallUntrustedPackage" : "Admin.App.InstallPackage"
                    : "Admin.App.InstallUntrustedPackageForUser",
                manifest.Name,
                root.Package.Version.ToString(),
                root.Source.Name,
                _principal.LoginName.Value];
        }

        IEnumerable<string> forAdministrator = plan.Dependencies
            .Where(static package => package.RequiresAdministrator)
            .Select(static package => $"{package.Package.Manifest.Name} {package.Package.Version}")
            .Concat(plan.Reactivations.Where(static app => app.ForMachine).Select(static app => app.Name));
        return text["Admin.App.InstallDependencies", manifest.Name, root.Package.Version.ToString(), string.Join(", ", forAdministrator)];
    }

    /// <summary>The second step of ADR 0189 §4 for a whole plan (ADR 0248 §1): the requested package, then every package it brings.</summary>
    private AppInstallApprovalRequest ToApprovalRequest(AppPackageInstallPlan plan)
    {
        AppPackagePlan root = plan.Root;
        AppManifest manifest = root.Package.Manifest;
        ICapabilityCatalog capabilities = PlanCapabilities(plan);
        return new AppInstallApprovalRequest(
            manifest.Id,
            manifest.Name,
            root.Package.Version.ToString(),
            root.PreviousVersion?.ToString(),
            root.SizeBytes,
            root.Source.ToString(),
            root.Trust,
            manifest.Capabilities,
            root.PreviousVersion is null ? AppInstallApprovalOperation.Install : AppInstallApprovalOperation.Update,
            root.SystemWide,
            string.Empty,
            _principal,
            _caller)
        {
            BlockReason = root.BlockReason,
            Signed = root.Signed,
            // ADR 0260 §3: the window shows the feature's set once, with what it brings.
            IsFeature = root.Package.IsFeature,
            FeatureEntryPoints = DescribeEntryPoints(root.Package),
            Dependencies = [.. plan.Dependencies.Select(package => new AppInstallApprovalDependency(
                package.Package.AppId,
                package.Package.Manifest.Name,
                package.Package.Version.ToString(),
                package.PreviousVersion?.ToString(),
                package.SizeBytes,
                package.Source.ToString(),
                package.Trust,
                package.SystemWide,
                package.Package.IsFeature,
                DangerousOf(package.Package, capabilities)))],
            Reactivated = [.. plan.Reactivations.Select(static app => app.Name)],
            PlannedFeatures = [.. plan.Packages.Where(static package => package.Package.IsFeature).Select(static package => package.Package.Manifest)],
        };
    }

    /// <summary>
    /// The capability catalog of the running OS plus the permissions the features of the plan declare
    /// (ADR 0246, answer 268 A): an app's permission that a feature of the plan provides is known and
    /// classified before the feature is installed.
    /// </summary>
    private ICapabilityCatalog PlanCapabilities(AppPackageInstallPlan plan)
    {
        ICapabilityCatalog running = Capabilities?.Invoke() ?? BuiltInCapabilityCatalog.Instance;
        AppManifest[] features = [.. plan.Packages.Where(static package => package.Package.IsFeature).Select(static package => package.Package.Manifest)];
        return features.Length == 0 ? running : new FeatureCapabilityCatalog(running, features);
    }

    /// <summary>
    /// The dangerous capabilities of a package as <paramref name="capabilities"/> classifies them; for
    /// a feature, the set of the feature and of its entry points, approved with it (ADR 0260 §3).
    /// </summary>
    private static IReadOnlyList<string> DangerousOf(AppPackage package, ICapabilityCatalog capabilities) =>
        [.. (package.IsFeature ? package.Manifests.SelectMany(static manifest => manifest.Capabilities) : package.Manifest.Capabilities)
            .Where(capabilities.IsDangerous)
            .Distinct(StringComparer.Ordinal)];
}
