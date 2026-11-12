using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Kazo.Os.Platform.Core.Apps.Packages;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The assemblies of the page as the library loader sees them (ADR 0244): what is loaded under a
/// simple name, and how one more assembly joins. The page has one implementation,
/// <see cref="DefaultContextLibraryRuntime"/>; tests give their own.
/// </summary>
public interface IInstalledLibraryRuntime
{
    /// <summary>Gets the version of the assembly loaded under a simple name, or <see langword="null"/>.</summary>
    /// <param name="simpleName">Simple assembly name, e.g. <c>Kazo.Irc.Protocol</c>.</param>
    Version? FindLoaded(string simpleName);

    /// <summary>Loads an assembly into the page.</summary>
    /// <param name="assembly">The assembly bytes.</param>
    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    void Load(byte[] assembly);
}

/// <summary>
/// The default load context of the page, where installed apps are loaded (ADR 0182 §5): an assembly
/// loaded there stays until the page is reloaded, and a second one of the same simple name cannot join.
/// </summary>
public sealed class DefaultContextLibraryRuntime : IInstalledLibraryRuntime
{
    /// <summary>Gets the shared instance.</summary>
    public static DefaultContextLibraryRuntime Instance { get; } = new();

    private DefaultContextLibraryRuntime()
    {
    }

    /// <inheritdoc />
    public Version? FindLoaded(string simpleName) => AssemblyLoadContext.Default.Assemblies
        .Select(static assembly => assembly.GetName())
        .FirstOrDefault(name => string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))?.Version;

    /// <inheritdoc />
    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    public void Load(byte[] assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(assembly, writable: false));
    }
}

/// <summary>
/// Loads the libraries of an installed app before its own assemblies (ADR 0244 §1), and one copy of
/// each shared library per page (§2): the copy of a feature that carries it, otherwise the newest
/// one of the same major version among the installed apps. A copy already in the page is never
/// replaced (§3): it serves the app, the choice is written to <c>/var/log/syslog</c> and the app
/// is shown as waiting for a restart.
/// </summary>
/// <remarks>
/// A library that is not shared goes with its app (ADR 0256 §1): it is loaded from the app's own
/// package and nothing else is decided about it. Every installed app shares the default load
/// context (ADR 0244, option A set aside), so when two apps carry a library of the same simple name
/// that is not shared, the first one loaded serves both and the second is written to the log
/// (ADR 0256 §4). For the same reason one major version of a shared library is in the page at a
/// time (ADR 0256 §3).
/// </remarks>
public sealed class InstalledAppLibraries : IInstalledLibraryState
{
    private readonly InstalledAppsBoot _installed;
    private readonly IInstalledLibraryRuntime _runtime;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _fromPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>Creates the loader.</summary>
    /// <param name="installed">The installed apps of this page.</param>
    /// <param name="logger">Writes each choice to <c>/var/log/syslog</c>.</param>
    /// <param name="runtime">The page's assemblies; <see langword="null"/> for the default load context.</param>
    public InstalledAppLibraries(InstalledAppsBoot installed, ILogger<InstalledAppLibraries>? logger = null, IInstalledLibraryRuntime? runtime = null)
    {
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _logger = logger;
        _runtime = runtime ?? DefaultContextLibraryRuntime.Instance;
    }

    /// <summary>Gets the loader of the libraries' satellite assemblies (ADR 0222 §2); <see langword="null"/> loads none.</summary>
    public InstalledAppSatellites? Satellites { get; init; }

    /// <inheritdoc />
    public IReadOnlySet<string> LoadedFromPackages
    {
        get
        {
            lock (_stateGate)
            {
                return new HashSet<string>(_fromPackages, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryRestart> GetPendingRestarts() =>
        InstalledLibraries.FindPendingRestarts(_installed.Current.Installed, _runtime.FindLoaded);

    /// <summary>
    /// Writes to the log what waits for a restart now (ADR 0244 §3), once per app, library and
    /// version: called after each install, update or removal, when a feature may have brought a
    /// newer copy of a library that is already in the page.
    /// </summary>
    /// <returns>What waits for a restart.</returns>
    public IReadOnlyList<LibraryRestart> ReportPendingRestarts()
    {
        IReadOnlyList<LibraryRestart> pending = GetPendingRestarts();
        foreach (LibraryRestart restart in pending)
        {
            ReportRestart(restart);
        }

        return pending;
    }

    /// <summary>Loads the libraries of <paramref name="app"/>, before its own assemblies.</summary>
    /// <param name="app">The installed app being launched.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="FileNotFoundException">A library the package lists is missing from the app's folder.</exception>
    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    public async Task LoadForAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Libraries.Count == 0)
        {
            return;
        }

        // One app at a time: two launches must not both find a library missing and load it twice.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (AppPackageLibrary library in app.Libraries)
            {
                await LoadAsync(app, library, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    private async Task LoadAsync(InstalledApp app, AppPackageLibrary library, CancellationToken cancellationToken)
    {
        Version? loaded = _runtime.FindLoaded(library.Name);
        if (!library.Shared)
        {
            if (loaded is null)
            {
                await LoadCopyAsync(new InstalledLibraryCopy(app, library), cancellationToken).ConfigureAwait(false);
            }
            else if (loaded != library.Version && MarkReported($"private|{app.AppId}|{library.Name}|{loaded}|{library.Version}"))
            {
                _logger?.LogWarning(
                    "libraries: {Library} {Version} of {AppId} not loaded, {Loaded} is already in the page and serves it",
                    library.Name, InstalledLibraries.Format(library.Version), app.AppId, InstalledLibraries.Format(loaded));
            }

            return;
        }

        SharedLibraryChoice choice = InstalledLibraries.Choose(app, library, _installed.Current.Installed);
        Version chosen = choice.Copy.Library.Version;
        if (loaded is null)
        {
            await LoadCopyAsync(choice.Copy, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "libraries: {Library} {Version} loaded for {AppId} ({Reason})",
                library.Name, InstalledLibraries.Format(chosen), app.AppId, choice.Describe());
        }
        else if (InstalledLibraries.NeedsRestart(loaded, library.Version, chosen))
        {
            ReportRestart(new LibraryRestart(app.AppId, library.Name, loaded, chosen, choice.Describe()));
        }
    }

    [RequiresUnreferencedCode("Installed assemblies arrive after publishing; AppPackageApiCheck verified their references at install time.")]
    private async Task LoadCopyAsync(InstalledLibraryCopy copy, CancellationToken cancellationToken)
    {
        Kazo.Os.App.Abstractions.VirtualPath path = copy.Owner.GetAssemblyPath(copy.Library.FileName);
        byte[] bytes = await _installed.ReadAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"{path.Value} is missing.", copy.Library.FileName);
        if (Satellites is { } satellites)
        {
            // ADR 0222 §2: the translations of the library are in the runtime before its code runs.
            await satellites.LoadAsync(copy.Owner, copy.Library.FileName, _installed.ReadAsync, cancellationToken).ConfigureAwait(false);
        }

        _runtime.Load(bytes);
        lock (_stateGate)
        {
            _fromPackages.Add(copy.Library.Name);
        }
    }

    private void ReportRestart(LibraryRestart restart)
    {
        if (MarkReported($"shared|{restart.AppId}|{restart.Library}|{restart.Loaded}|{restart.Wanted}"))
        {
            _logger?.LogWarning(
                "libraries: {Library} {Loaded} is already loaded and serves {AppId}; {Wanted} ({Reason}) applies at the next start of the OS",
                restart.Library, InstalledLibraries.Format(restart.Loaded), restart.AppId, InstalledLibraries.Format(restart.Wanted), restart.Reason);
        }
    }

    private bool MarkReported(string key)
    {
        lock (_stateGate)
        {
            return _reported.Add(key);
        }
    }
}
