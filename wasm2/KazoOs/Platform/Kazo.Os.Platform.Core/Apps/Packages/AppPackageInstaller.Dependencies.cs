using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>An installed app that a plan reactivates instead of downloading it again (ADR 0248 §1).</summary>
/// <param name="AppId">The app, uninstalled for the session (ADR 0048).</param>
/// <param name="Name">Its name, for the approval window and <c>kazopkg</c>.</param>
/// <param name="ForMachine">Whether <c>/etc/kazoos/apps.config</c> uninstalls it: an administrator then reactivates it.</param>
public sealed record AppPackageReactivation(string AppId, string Name, bool ForMachine);

/// <summary>
/// What one installation does (ADR 0248 §1): the requested package, then each required dependency
/// absent or out of range, transitively, dependencies first (ADR 0005), and the uninstalled
/// dependencies it reactivates. Everything is downloaded and checked already; nothing is written.
/// </summary>
/// <param name="Root">The package the user asked for.</param>
/// <param name="Dependencies">The packages installed with it, in installation order (each one after its own dependencies).</param>
/// <param name="Reactivations">Installed dependencies reactivated rather than installed.</param>
public sealed record AppPackageInstallPlan(
    AppPackagePlan Root,
    IReadOnlyList<AppPackagePlan> Dependencies,
    IReadOnlyList<AppPackageReactivation> Reactivations)
{
    /// <summary>Gets every package of the plan in installation order: the dependencies, then <see cref="Root"/>.</summary>
    public IReadOnlyList<AppPackagePlan> Packages => [.. Dependencies, Root];

    /// <summary>
    /// Gets whether an administrator approves the plan: one package of it is installed for the
    /// system or comes from an untrusted source, or a reactivation is for the machine. The approval
    /// is asked once, for the whole plan (ADR 0248 §1).
    /// </summary>
    public bool RequiresAdministrator =>
        Packages.Any(static package => package.RequiresAdministrator) || Reactivations.Any(static app => app.ForMachine);

    /// <summary>Gets whether the plan does more than the requested package.</summary>
    public bool HasDependencies => Dependencies.Count > 0 || Reactivations.Count > 0;
}

/// <summary>
/// Dependency resolution of the installer (ADR 0248 §1, §4), as <c>apt</c> computes what an
/// installation pulls in before it asks anything: <see cref="BuildPlanAsync"/> finds every
/// required dependency, <see cref="CommitPlanAsync"/> writes the plan as one transaction.
/// </summary>
public sealed partial class AppPackageInstaller
{
    /// <summary>A required dependency could not be found, or no published version satisfies its range (ADR 0248 §1).</summary>
    public const string DependencyUnresolvedCode = "apps.package.dependency-unresolved";

    /// <summary>The version a plan installs is outside the range of an installed app that needs it (ADR 0248 §4).</summary>
    public const string DependencyConflictCode = "apps.package.dependency-conflict";

    /// <summary>Most packages one plan downloads: a longer chain is refused rather than downloaded.</summary>
    public const int MaximumPlanPackages = 16;

    /// <summary>
    /// Prepares <paramref name="source"/> like <see cref="PrepareAsync(AppPackageSource, AppPackageInstallContext, IProgress{KazoPackageInstallProgress}?, CancellationToken)"/>,
    /// then every required dependency (<c>Optional = false</c>) that is absent or out of range, transitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dependency is looked up in the configured catalogs (the official one first, ADR 0208) by its
    /// id, or in the repository it is installed from when the installed version is out of range. The
    /// version chosen is the newest that satisfies every range of the plan and of the installed apps
    /// that need it, and runs on this OS. A dependency that is in no catalog stops the installation
    /// (<see cref="DependencyUnresolvedCode"/>, answer 1 A): <c>kazopkg</c> does not guess a repository.
    /// </para>
    /// <para>
    /// A dependency installed and in range but uninstalled for the session (ADR 0048) is reactivated,
    /// not downloaded. A feature always goes to the system (ADR 0260 §4), even when the requested app
    /// is installed for oneself (answer 2 A); a dependency of a system-wide app goes to the system too,
    /// since every user runs it. Every package of the plan, and every installed app that needs one of
    /// them, is checked against the version installed (<see cref="DependencyConflictCode"/>).
    /// </para>
    /// </remarks>
    /// <param name="source">The repository or catalog app the user asked for.</param>
    /// <param name="request">The sources, the block list, the scope and what is installed already.</param>
    /// <param name="progress">Receives the steps of the requested package; <see langword="null"/> reports nothing.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <exception cref="AppPackageException">A package of the plan is refused, or a dependency is unresolved or in conflict.</exception>
    public async ValueTask<AppPackageInstallPlan> BuildPlanAsync(
        AppPackageSource source,
        AppPackageInstallContext request,
        IProgress<KazoPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        AppPackagePlan root = await PrepareAsync(source, request, progress, cancellationToken).ConfigureAwait(false);
        PlanBuilder builder = new(this, request);
        await builder.AddAsync(root, cancellationToken).ConfigureAwait(false);
        return builder.ToPlan(root);
    }

    /// <summary>
    /// Writes a plan as one transaction (ADR 0248 §1, ADR 0214 §3 extended to the plan): each package
    /// is committed in order with its own journal; when one fails, the packages already written are
    /// undone in reverse order (a new one removed, an updated one rolled back to the version its
    /// update kept), so no app stays installed without its dependency.
    /// </summary>
    /// <param name="plan">The verified plan.</param>
    /// <param name="writerFor">The writer of each package's scope: the system's after the approval, or the user's own.</param>
    /// <param name="by">Login name of the user or administrator, for the log.</param>
    /// <param name="personalCopiesFor">Homes whose personal copy a system install of a package replaces; none when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="IOException">A write failed; the packages of the plan written before it were undone.</exception>
    public async ValueTask CommitPlanAsync(
        AppPackageInstallPlan plan,
        Func<AppPackagePlan, IAppFileSystemGateway> writerFor,
        string by,
        Func<AppPackagePlan, IReadOnlyList<string>>? personalCopiesFor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writerFor);
        List<AppPackagePlan> written = [];
        try
        {
            foreach (AppPackagePlan package in plan.Packages)
            {
                await CommitAsync(writerFor(package), package, by, cancellationToken, personalCopiesFor?.Invoke(package))
                    .ConfigureAwait(false);
                written.Add(package);
            }
        }
        catch (Exception exception) when (written.Count > 0
            && exception is IOException or AppPackageException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning(
                "apps: installation of {AppId} stopped after {Count} package(s): {Reason}; undoing them",
                plan.Root.Package.AppId, written.Count, exception.Message);
            await UndoAsync(written, writerFor, by).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Undoes the packages of a plan already written, newest first; a failure is logged and left to <c>kazopkg repair</c>.</summary>
    private async ValueTask UndoAsync(List<AppPackagePlan> written, Func<AppPackagePlan, IAppFileSystemGateway> writerFor, string by)
    {
        for (int index = written.Count - 1; index >= 0; index--)
        {
            AppPackagePlan package = written[index];
            try
            {
                if (package.PreviousVersion is null)
                {
                    await RemoveAsync(writerFor(package), package.Package.AppId, by, CancellationToken.None, package.Home).ConfigureAwait(false);
                }
                else
                {
                    await RollbackAsync(writerFor(package), package.Package.AppId, by, CancellationToken.None, package.Home).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or AppPackageException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    "apps: {AppId} {Version} of a stopped installation could not be undone: {Reason}",
                    package.Package.AppId, package.Package.Version, exception.Message);
            }
        }
    }

    /// <summary>Gets the required dependencies a package declares outside itself (a feature's children need their feature, which is the package).</summary>
    private static IEnumerable<AppDependencyManifest> RequiredDependencies(AppPackage package)
    {
        HashSet<string> own = new(package.Manifests.Select(static manifest => manifest.Id), StringComparer.Ordinal);
        return package.Manifests
            .SelectMany(static manifest => manifest.Dependencies)
            .Where(dependency => !dependency.Optional && !own.Contains(dependency.AppId))
            .OrderBy(static dependency => dependency.AppId, StringComparer.Ordinal);
    }

    /// <summary>Builds one <see cref="AppPackageInstallPlan"/>: the state of a resolution, dropped afterwards.</summary>
    private sealed class PlanBuilder(AppPackageInstaller installer, AppPackageInstallContext request)
    {
        private readonly Dictionary<string, AppPackagePlan> _planned = new(StringComparer.Ordinal);
        private readonly List<AppPackagePlan> _dependencies = [];
        private readonly Dictionary<string, AppPackageReactivation> _reactivations = new(StringComparer.Ordinal);

        public AppPackageInstallPlan ToPlan(AppPackagePlan root) => new(root, [.. _dependencies], [.. _reactivations.Values]);

        /// <summary>Takes a prepared package into the plan, checks it against the apps that need it, then resolves its own dependencies.</summary>
        public async ValueTask AddAsync(AppPackagePlan plan, CancellationToken cancellationToken)
        {
            foreach (AppManifest manifest in plan.Package.Manifests)
            {
                _planned[manifest.Id] = plan;
            }

            CheckInstalledDependents(plan);
            foreach (AppDependencyManifest dependency in RequiredDependencies(plan.Package))
            {
                await ResolveAsync(plan, dependency, cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask ResolveAsync(AppPackagePlan dependent, AppDependencyManifest dependency, CancellationToken cancellationToken)
        {
            string id = dependency.AppId;
            if (_planned.TryGetValue(id, out AppPackagePlan? planned))
            {
                if (!AppDependencyRange.Contains(dependency, planned.Package.Version))
                {
                    throw new AppPackageException(
                        DependencyConflictCode,
                        $"{Describe(dependent)} needs {id} {AppDependencyRange.Describe(dependency)}, but this installation installs {id} {planned.Package.Version}.");
                }

                return;
            }

            SemanticVersion? installed = InstalledVersion(id, dependent.SystemWide);
            if (installed is { } current && AppDependencyRange.Contains(dependency, current))
            {
                if (request.DisabledApps.TryGetValue(id, out bool forMachine) && !_reactivations.ContainsKey(id))
                {
                    string name = request.Catalog.Manifests.TryGetValue(id, out AppManifest? manifest) ? manifest.Name : id;
                    _reactivations[id] = new AppPackageReactivation(id, name, forMachine);
                }

                return;
            }

            if (_planned.Values.Distinct().Count() >= MaximumPlanPackages)
            {
                throw new AppPackageException(
                    DependencyUnresolvedCode,
                    $"{Describe(dependent)} pulls in more than {MaximumPlanPackages} packages; install its dependencies first.");
            }

            AppPackageSource source = await FindSourceAsync(dependent, dependency, installed, cancellationToken).ConfigureAwait(false);
            AppPackageIndex index = await installer.GetIndexAsync(source, request.Sources, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AppDependencyManifest> ranges = RangesOf(id, dependency);
            bool allowPreReleases = AppVersionChoice.AllowsPreReleases(installer._sdkVersion, installed)
                || ranges.Any(static range => SemanticVersion.TryParse(range.MinimumVersion, out SemanticVersion minimum) && minimum.PreRelease is not null);
            AppPackageIndexEntry entry = AppVersionChoice.Newest(
                    index.Versions.Where(candidate => ranges.All(range => AppDependencyRange.Contains(range, candidate.Version))),
                    static candidate => candidate.Version,
                    candidate => AppVersionChoice.IsCompatible(candidate.MinimumSdkVersion, candidate.MaximumSdkVersion, installer._sdkVersion),
                    allowPreReleases)
                ?? throw new AppPackageException(
                    DependencyUnresolvedCode,
                    $"{Describe(dependent)} needs {id} {AppDependencyRange.Describe(dependency)}: {source.Name} publishes no version in that range that runs on this OS.");

            // ADR 0248 §1 and answer 2 A: a dependency of a system-wide app is installed for the system;
            // otherwise Auto keeps a personal install personal, and sends a feature to the system.
            AppPackageInstallContext scoped = request with
            {
                Scope = dependent.SystemWide ? AppPackageScope.System : AppPackageScope.Auto,
            };
            AppPackagePlan plan = await installer
                .PrepareAsync(source.WithVersion(entry.Version), scoped, progress: null, cancellationToken)
                .ConfigureAwait(false);
            await AddAsync(plan, cancellationToken).ConfigureAwait(false);
            _dependencies.Add(plan);
        }

        /// <summary>
        /// Gets the version of <paramref name="appId"/> the dependent would run with: installed for the
        /// system, for the session user (a personal dependent only), or built into this OS.
        /// </summary>
        private SemanticVersion? InstalledVersion(string appId, bool systemWide)
        {
            if (request.SystemInstalled.Sources.TryGetValue(appId, out AppPackageSource? system))
            {
                return system.Version;
            }

            if (!systemWide && request.PersonalInstalled.Sources.TryGetValue(appId, out AppPackageSource? personal))
            {
                return personal.Version;
            }

            return request.Catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
                && !request.PackagedAppIds.Contains(appId)
                && SemanticVersion.TryParse(manifest.Version, out SemanticVersion builtIn)
                    ? builtIn
                    : null;
        }

        /// <summary>
        /// The repository a dependency comes from: the one it is installed from when its version is
        /// out of range, else the one catalog that lists it.
        /// </summary>
        private async ValueTask<AppPackageSource> FindSourceAsync(
            AppPackagePlan dependent, AppDependencyManifest dependency, SemanticVersion? installed, CancellationToken cancellationToken)
        {
            string id = dependency.AppId;
            string range = AppDependencyRange.Describe(dependency);
            if (installed is { } current)
            {
                if (request.SystemInstalled.Sources.TryGetValue(id, out AppPackageSource? system))
                {
                    return system;
                }

                if (request.PersonalInstalled.Sources.TryGetValue(id, out AppPackageSource? personal))
                {
                    return personal;
                }

                throw new AppPackageException(
                    DependencyUnresolvedCode,
                    $"{Describe(dependent)} needs {id} {range}; {id} {current} is part of this OS and cannot be replaced by a package.");
            }

            try
            {
                return await installer.FindInCatalogsAsync(id, null, request.Sources, cancellationToken).ConfigureAwait(false);
            }
            catch (AppPackageException exception) when (exception.Code == "apps.package.not-in-catalog")
            {
                throw new AppPackageException(
                    DependencyUnresolvedCode,
                    $"{Describe(dependent)} needs {id} {range}, which none of the configured catalogs publishes; add a source that publishes it, or install it first.");
            }
        }

        /// <summary>
        /// Every range <paramref name="appId"/> must satisfy: the dependency being resolved, the other
        /// packages of the plan, and the installed apps that need it and are not replaced by the plan.
        /// </summary>
        private IReadOnlyList<AppDependencyManifest> RangesOf(string appId, AppDependencyManifest dependency)
        {
            IEnumerable<AppManifest> planned = _planned.Values.Distinct().SelectMany(static plan => plan.Package.Manifests);
            IEnumerable<AppManifest> installed = request.Catalog.Manifests.Values.Where(manifest => !_planned.ContainsKey(manifest.Id));
            return
            [
                dependency,
                .. planned.Concat(installed)
                    .SelectMany(static manifest => manifest.Dependencies)
                    .Where(other => !other.Optional && string.Equals(other.AppId, appId, StringComparison.Ordinal)),
            ];
        }

        /// <summary>
        /// ADR 0248 §4: the version a plan installs must stay in the range of every installed app that
        /// needs it (the plan's own packages are checked as they are resolved). There is no
        /// <c>--force</c>: the dependent would be disabled in silence.
        /// </summary>
        private void CheckInstalledDependents(AppPackagePlan plan)
        {
            List<string> conflicts = [];
            foreach (AppManifest provided in plan.Package.Manifests)
            {
                SemanticVersion version = plan.Package.Version;
                foreach (AppManifest dependent in request.Catalog.Manifests.Values.OrderBy(static manifest => manifest.Id, StringComparer.Ordinal))
                {
                    if (_planned.ContainsKey(dependent.Id))
                    {
                        continue;
                    }

                    foreach (AppDependencyManifest dependency in dependent.Dependencies)
                    {
                        if (!dependency.Optional
                            && string.Equals(dependency.AppId, provided.Id, StringComparison.Ordinal)
                            && !AppDependencyRange.Contains(dependency, version))
                        {
                            conflicts.Add($"{dependent.Name} ({dependent.Id}) needs {provided.Id} {AppDependencyRange.Describe(dependency)}");
                        }
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                throw new AppPackageException(
                    DependencyConflictCode,
                    $"{plan.Package.AppId} {plan.Package.Version} is outside the range of installed apps that need it: {string.Join("; ", conflicts)}.");
            }
        }

        private static string Describe(AppPackagePlan plan) => $"{plan.Package.Manifest.Name} ({plan.Package.AppId} {plan.Package.Version})";
    }
}
