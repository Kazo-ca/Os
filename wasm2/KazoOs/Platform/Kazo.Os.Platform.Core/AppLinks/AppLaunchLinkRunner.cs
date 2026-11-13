using System.Globalization;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.AppLinks;

/// <summary>
/// Installs and launches the app of a direct link <c>?autolaunch=&lt;appId&gt;</c> for the session
/// user (ADR 0246 §3, §5, §6). The host calls it once the guest session is open.
/// </summary>
/// <remarks>
/// <para>
/// Safety (ADR 0246 §1, §3): the app is looked up <b>only</b> in the official catalog
/// (<c>catalog:official/&lt;appId&gt;</c>) and installed for the session user. The install goes through
/// the same management gateway as the Store and <c>kazopkg</c>, so the catalog and author signatures,
/// the block list and the minimum OS version are checked as for any install; the dangerous
/// capabilities the app declares are asked at launch by the usual gate, never granted by the link.
/// </para>
/// <para>
/// Every outcome writes one line to <c>/var/log/apps/org.kazoos.app-link.log</c>; every failure but a
/// cancel ends with an OS notification whose button opens the Kazo OS Store at the app's page.
/// </para>
/// </remarks>
public sealed class AppLaunchLinkRunner : IAppLaunchLinkRunner
{
    /// <summary>Source of the notifications and of the log lines, <c>/var/log/apps/org.kazoos.app-link.log</c>.</summary>
    public const string SourceAppId = "org.kazoos.app-link";

    /// <summary>The Kazo OS Store, opened by the button of a failure notification.</summary>
    public const string StoreAppId = "org.kazoos.store";

    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly Func<AuthenticatedPrincipal, IAppManagementGateway?> _packages;
    private readonly Func<AuthenticatedPrincipal, string, Task<AppLaunchResult>> _launch;
    private readonly IAppLoggingGateway _log;
    private readonly INotificationQueue? _notifications;
    private readonly Func<CultureInfo> _language;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;

    /// <summary>Creates the runner.</summary>
    /// <param name="currentUser">The session user, read when a link runs.</param>
    /// <param name="packages">
    /// The app-management gateway acting for that user (ADR 0182, 0189), as the Store's is;
    /// <see langword="null"/> when this host cannot install apps.
    /// </param>
    /// <param name="launch">Launches an installed app for that user, as the Start menu does.</param>
    /// <param name="log">Writes <c>/var/log/apps/org.kazoos.app-link.log</c>.</param>
    /// <param name="notifications">Receives the failure notifications; <see langword="null"/> posts none.</param>
    /// <param name="language">Language of the session (ADR 0104); English when <see langword="null"/>.</param>
    /// <param name="time">Time of the notifications; the system clock when <see langword="null"/>.</param>
    public AppLaunchLinkRunner(
        Func<AuthenticatedPrincipal?> currentUser,
        Func<AuthenticatedPrincipal, IAppManagementGateway?> packages,
        Func<AuthenticatedPrincipal, string, Task<AppLaunchResult>> launch,
        IAppLoggingGateway log,
        INotificationQueue? notifications = null,
        Func<CultureInfo>? language = null,
        TimeProvider? time = null)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notifications = notifications;
        _language = language ?? (static () => Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised whenever <see cref="Current"/> changes; the progress window re-renders.</summary>
    public event Action? Changed;

    /// <summary>Gets the launch in progress, or <see langword="null"/> when the progress window is closed.</summary>
    public AppLaunchLinkOperation? Current { get; private set; }

    /// <summary>« Annuler » of the progress window: stops the lookup or the install; nothing is notified.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
        }
    }

    /// <inheritdoc />
    /// <remarks>Never throws: every failure is logged and notified. A second link while one runs is ignored.</remarks>
    public async Task RunAsync(AppLaunchLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_cancel is not null)
            {
                Write(DiagnosticSeverity.Warning, "app-link.failed", link.AppId, "busy");
                return;
            }

            cancel = _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        Write(DiagnosticSeverity.Information, "app-link.received", link.AppId);
        try
        {
            await RunCoreAsync(link.AppId, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Write(DiagnosticSeverity.Information, "app-link.failed", link.AppId, "cancelled");
        }
        catch (AppLaunchLinkException failure)
        {
            Fail(link.AppId, failure.Reason, failure.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Last resort: the link never breaks the session it opened.
            Fail(link.AppId, "error", Text["AppLink.Error.Generic", exception.Message]);
        }
        finally
        {
            lock (_gate)
            {
                _cancel = null;
            }

            cancel.Dispose();
            Show(null);
        }
    }

    /// <summary>
    /// ADR 0246 §4 and §6: writes <c>app-link.store-opened</c> once the host opened the Kazo OS Store at
    /// the app's page for a session that is not the guest's. Nothing is installed in that case.
    /// </summary>
    /// <param name="link">The link read at boot.</param>
    public void RecordStoreOpened(AppLaunchLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        Write(DiagnosticSeverity.Information, "app-link.store-opened", link.AppId);
    }

    private IStringLocalizer Text => Strings.For(_language());

    private async Task RunCoreAsync(string appId, CancellationToken cancellationToken)
    {
        Show(new AppLaunchLinkOperation(appId, appId, null, null, AppLaunchLinkStep.Looking));
        AuthenticatedPrincipal user = _currentUser()
            ?? throw new AppLaunchLinkException("no-session", Text["AppLink.Error.NoSession"]);
        IAppManagementGateway packages = _packages(user)
            ?? throw new AppLaunchLinkException("unsupported", Text["AppLink.Error.Unsupported"]);
        string source = OfficialSource(appId);
        if (!await IsInstalledFromOfficialAsync(packages, appId, source, cancellationToken).ConfigureAwait(false))
        {
            KazoCatalogApp app = await FindInOfficialCatalogAsync(packages, appId, cancellationToken).ConfigureAwait(false);
            Show(new AppLaunchLinkOperation(appId, app.Name, PublisherOf(app.Repository), app.LatestVersion, AppLaunchLinkStep.Installing));
            KazoPackageInstallReport report = await InstallAsync(packages, source, cancellationToken).ConfigureAwait(false);
            Write(DiagnosticSeverity.Information, "app-link.installed", appId, version: report.Version);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Show((Current ?? new AppLaunchLinkOperation(appId, appId, null, null, AppLaunchLinkStep.Looking)) with { Step = AppLaunchLinkStep.Launching });
        AppLaunchResult launched = await _launch(user, appId).ConfigureAwait(false);
        if (!launched.IsSuccess)
        {
            string reason = launched.ErrorCode ?? launched.Status.ToString();
            throw new AppLaunchLinkException("launch:" + reason, Text["AppLink.Error.Launch", launched.ErrorMessage ?? reason]);
        }

        Write(DiagnosticSeverity.Information, "app-link.launched", appId);
    }

    /// <summary>ADR 0246 §3: an app the official catalog already installed is launched without reinstalling, even offline.</summary>
    private static async Task<bool> IsInstalledFromOfficialAsync(
        IAppManagementGateway packages, string appId, string source, CancellationToken cancellationToken)
    {
        IReadOnlyList<KazoInstalledPackage> installed = await packages.GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false);
        return installed.Any(package =>
            string.Equals(package.AppId, appId, StringComparison.Ordinal)
            && string.Equals(package.Source, source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ADR 0246 §3.2: only the official catalog is read; another catalog listing the app does not count.</summary>
    private async Task<KazoCatalogApp> FindInOfficialCatalogAsync(
        IAppManagementGateway packages, string appId, CancellationToken cancellationToken)
    {
        IReadOnlyList<KazoAppCatalog> catalogs = await packages.GetPackageCatalogsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        KazoAppCatalog? official = catalogs.FirstOrDefault(static catalog =>
            string.Equals(catalog.Name, AppSourcesConfig.OfficialCatalogName, StringComparison.OrdinalIgnoreCase));
        if (official?.Error is not null)
        {
            throw new AppLaunchLinkException("unreachable", Text["AppLink.Error.Unreachable"]);
        }

        return official?.Apps.FirstOrDefault(app =>
                app.Kind == KazoCatalogItemKind.App && string.Equals(app.AppId, appId, StringComparison.Ordinal))
            ?? throw new AppLaunchLinkException("not-offered", Text["AppLink.Error.NotOffered", appId]);
    }

    /// <summary>ADR 0246 §3.3, Q4: for the session user only, so no administrator window for a trusted catalog.</summary>
    private async Task<KazoPackageInstallReport> InstallAsync(
        IAppManagementGateway packages, string source, CancellationToken cancellationToken)
    {
        try
        {
            return await packages.InstallPackageAsync(source, KazoPackageScope.User, cancellationToken).ConfigureAwait(false);
        }
        catch (KazoPackageException exception)
        {
            throw new AppLaunchLinkException(exception.Code, Describe(exception));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            throw new AppLaunchLinkException("not-approved", Text["AppLink.Error.NotApproved"]);
        }
    }

    /// <summary>The usual messages of the Store for a refused package (ADR 0246 §5).</summary>
    private string Describe(KazoPackageException exception) => exception.Code switch
    {
        "apps.package.not-in-catalog" => Text["AppLink.Error.NotOffered", exception.Message],
        "apps.package.download-failed" => Text["AppLink.Error.Unreachable"],
        "apps.package.blocked" => Text["AppLink.Error.Blocked"],
        "apps.package.sdk-incompatible" or "apps.package.missing-api" => Text["AppLink.Error.Incompatible", exception.Message],
        "apps.package.unsigned" or "apps.package.bad-signature" => Text["AppLink.Error.Signature"],
        _ => Text["AppLink.Error.Generic", exception.Message],
    };

    private void Fail(string appId, string reason, string message)
    {
        Write(DiagnosticSeverity.Warning, "app-link.failed", appId, reason);
        if (_notifications is null || _currentUser() is not { } user)
        {
            return;
        }

        IStringLocalizer text = Text;
        _notifications.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            user.UserId,
            SourceAppId,
            NotificationSeverity.Error,
            text["AppLink.Notification.Title"],
            message,
            [NotificationAction.LaunchApp(StoreAppId, text["AppLink.Notification.OpenStore"], [$"--app={appId}"])],
            _time.GetUtcNow(),
            expiresAtUtc: null));
    }

    private void Write(DiagnosticSeverity severity, string message, string appId, string? reason = null, string? version = null)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal) { ["appId"] = appId };
        if (reason is not null)
        {
            properties["reason"] = reason;
        }

        if (version is not null)
        {
            properties["version"] = version;
        }

        _log.Log(severity, message, properties);
    }

    private void Show(AppLaunchLinkOperation? operation)
    {
        Current = operation;
        Changed?.Invoke();
    }

    /// <summary>The only source a link installs from: the official catalog.</summary>
    internal static string OfficialSource(string appId) => $"catalog:{AppSourcesConfig.OfficialCatalogName}/{appId}";

    /// <summary>The owner of a GitHub repository address, shown as the publisher; <see langword="null"/> otherwise.</summary>
    internal static string? PublisherOf(string? repository)
    {
        if (!Uri.TryCreate(repository, UriKind.Absolute, out Uri? address))
        {
            return null;
        }

        string owner = address.AbsolutePath.Trim('/').Split('/')[0];
        return owner.Length == 0 ? null : owner;
    }

    /// <summary>A failure with the reason written to the log and the message shown in the notification.</summary>
    private sealed class AppLaunchLinkException(string reason, string message) : Exception(message)
    {
        public string Reason { get; } = reason;
    }
}
