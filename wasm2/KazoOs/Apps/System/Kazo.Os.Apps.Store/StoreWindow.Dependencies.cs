using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// Dependencies in the Store (ADR 0248): an app's page lists what it needs and where each one
/// stands; installing it installs its required dependencies in the same consent (the OS shows the
/// plan in its approval window); uninstalling a package that other apps need says first which ones
/// stop working and are disabled, and waits for a second click.
/// </summary>
public partial class StoreWindow
{
    private IReadOnlyDictionary<string, string> _runningApps = new Dictionary<string, string>(StringComparer.Ordinal);
    private PendingRemoval? _pendingRemoval;

    /// <summary>A removal waiting for its confirmation, with the apps it disables.</summary>
    /// <param name="AppId">The package to remove.</param>
    /// <param name="Dependents">Names of the installed apps that need it.</param>
    internal sealed record PendingRemoval(string AppId, IReadOnlyList<string> Dependents);

    /// <summary>Gets the removal of <paramref name="entry"/> waiting for its confirmation, or <see langword="null"/>.</summary>
    /// <param name="entry">An app of the Store.</param>
    internal PendingRemoval? PendingRemovalOf(StoreEntry entry) =>
        _pendingRemoval is { } pending && string.Equals(pending.AppId, entry.AppId, StringComparison.Ordinal) ? pending : null;

    /// <summary>Describes the dependencies of the version an app's page shows (ADR 0248 §2).</summary>
    /// <param name="version">The version shown.</param>
    internal IReadOnlyList<StoreDependency> DependenciesOf(KazoCatalogAppVersion version) =>
        StoreDependencies.Describe(version.Dependencies ?? [], _store.Find, _runningApps);

    /// <summary>Reads the apps of the running OS and their version, built-in ones included; empty when the OS cannot say.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadRunningAppsAsync()
    {
        try
        {
            IReadOnlyList<KazoAppInfo> apps = await KazoApps.GetInstalledAsync();
            return apps.DistinctBy(static app => app.Id, StringComparer.Ordinal).ToDictionary(static app => app.Id, static app => app.Version, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// ADR 0248 §3: before an uninstall, asks the OS which apps need the package. None: it is removed
    /// at once, as before. Some: the Store shows them and waits for <see cref="ConfirmRemoveAsync"/>.
    /// </summary>
    /// <param name="entry">The installed app.</param>
    internal async Task RequestRemoveAsync(StoreEntry entry)
    {
        IReadOnlyList<KazoPackageDependent> dependents;
        try
        {
            dependents = await KazoAppPackages.GetDependentsAsync(entry.AppId);
        }
        catch (NotSupportedException)
        {
            dependents = [];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or KazoPackageException)
        {
            ShowError(L["Error.Generic", exception.Message]);
            return;
        }

        if (dependents.Count == 0)
        {
            await RemoveAsync(entry);
            return;
        }

        _pendingRemoval = new PendingRemoval(entry.AppId, [.. dependents.Select(static app => app.Name)]);
    }

    /// <summary>Removes the package whose warning the user confirmed; its dependents are disabled with it.</summary>
    /// <param name="entry">The installed app.</param>
    internal Task ConfirmRemoveAsync(StoreEntry entry)
    {
        _pendingRemoval = null;
        return RemoveAsync(entry);
    }

    /// <summary>Forgets a removal the user did not confirm.</summary>
    internal void CancelRemove() => _pendingRemoval = null;

    /// <summary>The message after an installation, naming the packages installed or reactivated with the app.</summary>
    private string DescribeInstalled(KazoPackageInstallReport report)
    {
        string installed = L[report.SystemWide ? "Message.InstalledForEveryone" : "Message.InstalledForMe", report.Name, report.Version];
        string[] with = [.. report.Dependencies.Select(static dependency => $"{dependency.Name} {dependency.Version}"), .. report.Reactivated];
        return with.Length == 0 ? installed : $"{installed} {L["Message.InstalledWith", string.Join(", ", with)]}";
    }
}
