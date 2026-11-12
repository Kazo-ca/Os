using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>One installed copy of a library: the app whose package carries it (ADR 0244).</summary>
/// <param name="Owner">The installed app, or feature, whose folder holds the file.</param>
/// <param name="Library">The library as its package lists it.</param>
public sealed record InstalledLibraryCopy(InstalledApp Owner, AppPackageLibrary Library);

/// <summary>Why a copy of a shared library was chosen (ADR 0244 §2).</summary>
public enum SharedLibraryChoiceReason
{
    /// <summary>An installed feature carries the library: its copy serves every app.</summary>
    Feature = 0,

    /// <summary>No feature carries it: the newest copy of the same major version among the installed apps.</summary>
    NewestCompatible = 1,
}

/// <summary>The copy of a shared library the OS loads for an app (ADR 0244 §2).</summary>
/// <param name="Copy">The chosen copy.</param>
/// <param name="Reason">The rule that chose it.</param>
public sealed record SharedLibraryChoice(InstalledLibraryCopy Copy, SharedLibraryChoiceReason Reason)
{
    /// <summary>Gets the reason as <c>/var/log/syslog</c> writes it, e.g. <c>feature ca.kazo.x86</c>.</summary>
    public string Describe() => Reason == SharedLibraryChoiceReason.Feature
        ? $"feature {Copy.Owner.AppId}"
        : $"newest compatible, from {Copy.Owner.AppId}";
}

/// <summary>
/// An app that cannot get the copy of a shared library chosen for it until the OS restarts
/// (ADR 0244 §3): another copy is already in the page, and .NET never unloads it.
/// </summary>
/// <param name="AppId">The installed app, or feature.</param>
/// <param name="Library">Simple name of the library.</param>
/// <param name="Loaded">The version in the page, which serves the app meanwhile.</param>
/// <param name="Wanted">The version chosen for the app.</param>
/// <param name="Reason">Why that version was chosen, as <see cref="SharedLibraryChoice.Describe"/> gives it.</param>
public sealed record LibraryRestart(string AppId, string Library, Version Loaded, Version Wanted, string Reason);

/// <summary>
/// What the page has loaded of the installed apps' libraries (ADR 0244 §3). The browser hosts
/// implement it; a host that cannot load installed apps has none.
/// </summary>
public interface IInstalledLibraryState
{
    /// <summary>
    /// Gets the apps that wait for a restart to get their copy of a shared library
    /// (« Restart to finish », ADR 0212 §3).
    /// </summary>
    IReadOnlyList<LibraryRestart> GetPendingRestarts();

    /// <summary>
    /// Gets the simple names of the libraries the page loaded from installed packages: they are in
    /// the runtime, yet they are not assemblies of the OS.
    /// </summary>
    IReadOnlySet<string> LoadedFromPackages { get; }
}

/// <summary>
/// The rules of ADR 0244 §2 and §3 over the installed apps: which copy of a shared library serves an
/// app, what a package may not bring, and what waits for a restart. No loading happens here.
/// </summary>
/// <remarks>
/// Only a library its author marked as shared enters these rules (ADR 0256 §1). A library that is
/// not shared goes with its app and nothing more: it is never compared between apps, never a
/// reason to refuse an installation on its version, never a reason to offer a restart.
/// </remarks>
public static class InstalledLibraries
{
    /// <summary>Gets every installed copy of a shared library, by its simple name.</summary>
    /// <param name="name">Simple assembly name.</param>
    /// <param name="apps">The installed apps of the running catalog.</param>
    public static IReadOnlyList<InstalledLibraryCopy> GetSharedCopies(string name, IEnumerable<InstalledApp> apps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(apps);
        return [.. apps.SelectMany(app => app.Libraries
            .Where(library => library.Shared && SameName(library.Name, name))
            .Select(library => new InstalledLibraryCopy(app, library)))];
    }

    /// <summary>
    /// Gets the copy an installed feature carries (ADR 0244 §2, rule 1), the newest one when several
    /// features carry it; <see langword="null"/> when no feature does.
    /// </summary>
    /// <remarks>
    /// Until features declare the services they provide (ADR 0245), a feature that carries the
    /// shared library is its provider.
    /// </remarks>
    /// <param name="name">Simple assembly name.</param>
    /// <param name="apps">The installed apps of the running catalog.</param>
    public static InstalledLibraryCopy? FindProvider(string name, IEnumerable<InstalledApp> apps) =>
        GetSharedCopies(name, apps)
            .Where(static copy => copy.Owner.Manifest.Kind == AppKind.Feature)
            .OrderByDescending(static copy => copy.Library.Version)
            .ThenBy(static copy => copy.Owner.AppId, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Chooses the copy of a shared library that serves an app (ADR 0244 §2).</summary>
    /// <param name="requester">The app being launched.</param>
    /// <param name="wanted">The shared library as the app's own package carries it.</param>
    /// <param name="apps">The installed apps of the running catalog.</param>
    /// <returns>
    /// The copy of a feature when one carries the same major version; otherwise the newest copy of
    /// that major version among the installed apps, the app's own included.
    /// </returns>
    public static SharedLibraryChoice Choose(InstalledApp requester, AppPackageLibrary wanted, IEnumerable<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(apps);
        InstalledApp[] installed = [.. apps];
        if (FindProvider(wanted.Name, installed) is { } provider && provider.Library.Version.Major == wanted.Version.Major)
        {
            return new SharedLibraryChoice(provider, SharedLibraryChoiceReason.Feature);
        }

        InstalledLibraryCopy own = new(requester, wanted);
        InstalledLibraryCopy newest = GetSharedCopies(wanted.Name, installed)
            .Where(copy => !string.Equals(copy.Owner.AppId, requester.AppId, StringComparison.Ordinal))
            .Append(own)
            .Where(copy => copy.Library.Version.Major == wanted.Version.Major)
            .OrderByDescending(static copy => copy.Library.Version)
            // The same version in several packages: the app's own file first, then a stable order.
            .ThenByDescending(copy => ReferenceEquals(copy, own))
            .ThenBy(static copy => copy.Owner.AppId, StringComparer.Ordinal)
            .First();
        return new SharedLibraryChoice(newest, SharedLibraryChoiceReason.NewestCompatible);
    }

    /// <summary>
    /// Gets whether an app must wait for a restart to get its shared library (ADR 0244 §3): the copy
    /// in the page lacks what the app was built against (another major version, or an older one),
    /// and the copy chosen for the app would bring it. A newer copy of the same major version only
    /// adds members, so it serves the app as it is.
    /// </summary>
    /// <param name="loaded">The version in the page.</param>
    /// <param name="own">The version the app's own package carries, which it was built against.</param>
    /// <param name="chosen">The version chosen for the app.</param>
    public static bool NeedsRestart(Version loaded, Version own, Version chosen)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(chosen);
        bool servesApp = loaded.Major == own.Major && loaded >= own;
        bool restartChanges = loaded.Major != chosen.Major || loaded < chosen;
        return !servesApp && restartChanges;
    }

    /// <summary>Gets a library version as <c>/var/log/syslog</c> writes it: <c>1.2.0</c>, the revision only when it is set.</summary>
    /// <param name="version">An assembly version.</param>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Revision > 0 || version.Build < 0 ? version.ToString() : version.ToString(3);
    }

    /// <summary>
    /// Gets the apps whose chosen copy of a shared library is not the one in the page (ADR 0244 §3):
    /// they are served by the loaded copy until the OS restarts.
    /// </summary>
    /// <param name="apps">The installed apps of the running catalog.</param>
    /// <param name="getLoadedVersion">Gets the version of a library in the page by its simple name, or <see langword="null"/> when it is not loaded.</param>
    /// <returns>One entry per app and library, ordered by app then library.</returns>
    public static IReadOnlyList<LibraryRestart> FindPendingRestarts(IEnumerable<InstalledApp> apps, Func<string, Version?> getLoadedVersion)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(getLoadedVersion);
        InstalledApp[] installed = [.. apps];
        List<LibraryRestart> pending = [];
        foreach (InstalledApp app in installed.OrderBy(static app => app.AppId, StringComparer.Ordinal))
        {
            foreach (AppPackageLibrary library in app.Libraries.Where(static library => library.Shared).OrderBy(static library => library.Name, StringComparer.Ordinal))
            {
                if (getLoadedVersion(library.Name) is not { } loaded)
                {
                    continue;
                }

                SharedLibraryChoice choice = Choose(app, library, installed);
                if (NeedsRestart(loaded, library.Version, choice.Copy.Library.Version))
                {
                    pending.Add(new LibraryRestart(app.AppId, library.Name, loaded, choice.Copy.Library.Version, choice.Describe()));
                }
            }
        }

        return pending;
    }

    /// <summary>
    /// Refuses a package whose shared library has another major version than the copy an installed
    /// feature carries (ADR 0244 §2). Between apps, without a feature, different major versions are
    /// both installed.
    /// </summary>
    /// <param name="package">The package being installed.</param>
    /// <param name="installed">The installed apps of the running catalog.</param>
    /// <exception cref="AppPackageException"><c>apps.package.library-major-mismatch</c>.</exception>
    public static void CheckMajorVersions(AppPackage package, IEnumerable<InstalledApp> installed)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(installed);
        // An update of the package itself replaces its own copy.
        InstalledApp[] others = [.. installed.Where(app => !string.Equals(app.AppId, package.AppId, StringComparison.Ordinal))];
        foreach (AppPackageLibrary library in package.Libraries.Where(static library => library.Shared))
        {
            if (FindProvider(library.Name, others) is { } provider && provider.Library.Version.Major != library.Version.Major)
            {
                throw new AppPackageException(
                    "apps.package.library-major-mismatch",
                    $"{package.AppId} carries {library.Name} {Format(library.Version)}, but the feature {provider.Owner.Manifest.Name} ({provider.Owner.AppId}) provides "
                    + $"{library.Name} {Format(provider.Library.Version)}: the major versions differ, so the app cannot use that feature.");
            }
        }
    }

    /// <summary>
    /// Protects the OS (ADR 0244 §1, ADR 0256 §6): refuses a package that would replace an assembly
    /// of the OS, of the .NET runtime or of a built-in app with a library of the same name. Only
    /// the name is looked at, never the version, and the library may be shared or not: this is not
    /// a management of the library, which simply goes with its app.
    /// </summary>
    /// <param name="package">The package being installed.</param>
    /// <param name="runtime">What the running OS has.</param>
    /// <param name="osAssemblies">File names of the assemblies of the apps the OS was built with.</param>
    /// <param name="packageAssemblies">
    /// Simple names the page may hold that came from installed packages (their libraries), which
    /// <paramref name="runtime"/> sees although the OS does not bring them.
    /// </param>
    /// <exception cref="AppPackageException"><c>apps.package.library-reserved</c>.</exception>
    public static void CheckReservedNames(
        AppPackage package, IRuntimeApiSurface runtime, IReadOnlySet<string> osAssemblies, IReadOnlySet<string> packageAssemblies)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(osAssemblies);
        ArgumentNullException.ThrowIfNull(packageAssemblies);
        foreach (AppPackageLibrary library in package.Libraries)
        {
            bool reserved = osAssemblies.Contains(library.FileName, StringComparer.OrdinalIgnoreCase)
                || (!packageAssemblies.Contains(library.Name, StringComparer.OrdinalIgnoreCase) && runtime.HasAssembly(library.Name));
            if (reserved)
            {
                throw new AppPackageException(
                    "apps.package.library-reserved",
                    $"{package.AppId} carries an assembly named {library.Name}, the name of an assembly of this OS or of the .NET runtime; a package cannot replace an assembly of the OS.");
            }
        }
    }

    private static bool SameName(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
