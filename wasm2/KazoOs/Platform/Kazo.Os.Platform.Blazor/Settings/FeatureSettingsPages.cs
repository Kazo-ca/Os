using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Settings;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.Platform.Blazor.Settings;

/// <summary>
/// The pages features add to Settings ▸ Features (ADR 0249): read from the running catalog, filtered
/// by enablement, role (ADR 0247 §2) and platform, and started as child processes of Settings that
/// run in their own execution context (§2, answer 1 A), never in Settings'.
/// </summary>
/// <remarks>
/// Like the renderer of a web site (ADR 0084 §3), a page is an auxiliary process: its own id, its own
/// subset of the feature's capabilities, visible in <c>ps</c> under Settings. What Settings draws is
/// the page's component inside a cascade of the page's context and the OS error barrier (ADR 0238);
/// Settings' window gateway is cascaded as <see langword="null"/>, so the page cannot reach it.
/// </remarks>
public sealed class FeatureSettingsPages : IFeatureSettingsPages, IDisposable
{
    /// <summary>The <c>argv</c> flag of a page process, as <c>chrome --type=renderer</c> for a site.</summary>
    public const string PageArgument = "--type=settings-page";

    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly ISessionService _sessions;
    private readonly Func<FeatureRoleService?> _roles;
    private readonly Func<CultureInfo> _culture;
    private readonly Func<bool> _isMobile;
    private readonly Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> _descriptors;
    private readonly StartPage _start;
    private readonly Func<ProcessId, ProcessExitReason, Task<bool>> _stop;
    private FeatureRoleService? _subscribedRoles;

    /// <summary>Starts the auxiliary process of a page (<c>AppLifecycleOrchestrator.StartAuxiliaryAsync</c>).</summary>
    /// <param name="appId">The page's entry point.</param>
    /// <param name="principal">Settings' user.</param>
    /// <param name="parentPid">Settings' process.</param>
    /// <param name="arguments"><c>argv</c> after the page's id.</param>
    /// <param name="cancellationToken">Cancels the load of the feature's assembly.</param>
    /// <returns>The launch.</returns>
    public delegate Task<AppLaunchResult> StartPage(
        string appId, AuthenticatedPrincipal principal, ProcessId parentPid, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>Creates the service over the OS catalog and lifecycle.</summary>
    /// <param name="catalog">The running catalog, swapped in place when a feature is installed (ADR 0214 §1).</param>
    /// <param name="enablement">Installed state: a disabled feature's pages are not listed.</param>
    /// <param name="sessions">The session whose user sees the pages.</param>
    /// <param name="roles">The roles of the features, or <see langword="null"/> (only administrators see a page that requires a role).</param>
    /// <param name="culture">The session's language, for the names of the pages.</param>
    /// <param name="isMobile">Whether the phone presentation is active (ADR 0171).</param>
    /// <param name="descriptors">Gets an entry point's descriptor, lazy-loading its assembly.</param>
    /// <param name="start">Starts a page's process.</param>
    /// <param name="stop">Stops a process.</param>
    public FeatureSettingsPages(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        ISessionService sessions,
        Func<FeatureRoleService?> roles,
        Func<CultureInfo> culture,
        Func<bool> isMobile,
        Func<string, CancellationToken, Task<(AppDescriptor? Descriptor, string? Error)>> descriptors,
        StartPage start,
        Func<ProcessId, ProcessExitReason, Task<bool>> stop)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _culture = culture ?? throw new ArgumentNullException(nameof(culture));
        _isMobile = isMobile ?? throw new ArgumentNullException(nameof(isMobile));
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));

        // The enablement registry already re-raises the catalog's swaps (a feature installed or removed).
        _enablement.Changed += OnSourceChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<FeatureSettingsPageInfo> GetPages(IAppExecutionContext host)
    {
        ArgumentNullException.ThrowIfNull(host);
        SubscribeToRoles();
        if (!IsSettings(host) || PrincipalOf(host) is not { } principal)
        {
            return [];
        }

        CultureInfo culture = _culture();
        bool mobile = _isMobile();
        FeatureRoleService? roles = _roles();
        return [.. _catalog.Manifests.Values
            .Where(manifest => IsOpenTo(manifest, principal, roles) && (!mobile || SettingsPageOptions.Of(manifest).MobileSupported))
            .Select(manifest => Describe(manifest, culture))
            .OrderBy(static page => page.FeatureName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static page => page.Order)
            .ThenBy(static page => page.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <inheritdoc />
    public async Task<FeatureSettingsPageStart> OpenAsync(IAppExecutionContext host, string pageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (!IsSettings(host))
        {
            return FeatureSettingsPageStart.Denied($"Only Settings draws the page '{pageId}'.");
        }

        if (PrincipalOf(host) is not { } principal)
        {
            return FeatureSettingsPageStart.Denied("No user session is open for this Settings window.");
        }

        if (!_catalog.Manifests.TryGetValue(pageId, out AppManifest? manifest) || !IsOpenTo(manifest, principal, _roles()))
        {
            return FeatureSettingsPageStart.Denied($"The page '{pageId}' is not available to this user.");
        }

        try
        {
            return await StartAsync(host, manifest, principal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A feature's page never takes Settings down (ADR 0238): its load or start failure is shown.
            return FeatureSettingsPageStart.Failed(exception.Message);
        }
    }

    private async Task<FeatureSettingsPageStart> StartAsync(
        IAppExecutionContext host, AppManifest manifest, AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        string pageId = manifest.Id;
        (AppDescriptor? descriptor, string? error) = await _descriptors(pageId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return FeatureSettingsPageStart.Failed(error ?? $"The page '{pageId}' could not be loaded.");
        }

        Type component = descriptor.EntryPointType;
        if (component.IsAbstract || !typeof(KazoSettingsPageBase).IsAssignableFrom(component))
        {
            return FeatureSettingsPageStart.Failed($"The page '{pageId}' declares '{component.FullName}', which is not a KazoSettingsPageBase.");
        }

        AppLaunchResult launch = await _start(pageId, principal, host.ProcessId, [PageArgument], cancellationToken).ConfigureAwait(false);
        if (launch.Status == AppLaunchStatus.RoleRequired)
        {
            return FeatureSettingsPageStart.Denied($"The page '{pageId}' requires a role.");
        }

        if (launch.Status != AppLaunchStatus.Launched || launch.Context is null)
        {
            return FeatureSettingsPageStart.Failed(launch.ErrorMessage ?? launch.ErrorCode ?? $"The page '{pageId}' did not start ({launch.Status}).");
        }

        return FeatureSettingsPageStart.Started(new PageSession(Describe(manifest, _culture()), component, launch.Context, _stop));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _enablement.Changed -= OnSourceChanged;
        if (_subscribedRoles is { } roles)
        {
            roles.Changed -= OnSourceChanged;
        }
    }

    /// <summary>The session's user, when it is the user of the Settings window asking.</summary>
    private AuthenticatedPrincipal? PrincipalOf(IAppExecutionContext host) =>
        _sessions.CurrentPrincipal is { } principal && string.Equals(principal.UserId.ToString(), host.UserId, StringComparison.Ordinal)
            ? principal
            : null;

    /// <summary>Only Settings hosts the pages (ADR 0249 §2): no other app may draw another's page.</summary>
    private static bool IsSettings(IAppExecutionContext host) =>
        string.Equals(host.Manifest.Id, AppIntentDispatcher.SettingsAppId, StringComparison.Ordinal);

    /// <summary>
    /// A page is open to the user when it is a settings page of a feature, both are enabled, and the
    /// user holds the role it requires (ADR 0247 §2; every administrator holds them all, 233 B).
    /// </summary>
    private bool IsOpenTo(AppManifest manifest, AuthenticatedPrincipal principal, FeatureRoleService? roles) =>
        manifest.Kind == AppKind.SettingsPage
        && manifest.Parent is { } featureId
        && _catalog.Manifests.ContainsKey(featureId)
        && _enablement.IsEnabled(featureId)
        && _enablement.IsEnabled(manifest.Id)
        && FeatureRoleService.OpensTo(roles, manifest, principal);

    private FeatureSettingsPageInfo Describe(AppManifest manifest, CultureInfo culture)
    {
        AppManifest page = _catalog.Localize(manifest, culture);
        string featureName = _catalog.Manifests.TryGetValue(manifest.Parent!, out AppManifest? feature)
            ? _catalog.Localize(feature, culture).Name
            : manifest.Parent!;
        return new FeatureSettingsPageInfo(manifest.Id, manifest.Parent!, featureName, page.Name, SettingsPageOptions.Of(manifest).Order)
        {
            IconUrl = KazoAppInfo.FromManifest(manifest, isSystemApp: false).IconUrl
        };
    }

    /// <summary>The roles service is created after this one at boot; it is subscribed to once it exists.</summary>
    private void SubscribeToRoles()
    {
        FeatureRoleService? roles = _roles();
        if (ReferenceEquals(roles, _subscribedRoles))
        {
            return;
        }

        if (_subscribedRoles is { } previous)
        {
            previous.Changed -= OnSourceChanged;
        }

        _subscribedRoles = roles;
        if (roles is not null)
        {
            roles.Changed += OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The running page handed to Settings: ends when Settings leaves it, or when the OS stops it.</summary>
    private sealed class PageSession : IFeatureSettingsPageSession
    {
        private readonly Func<ProcessId, ProcessExitReason, Task<bool>> _stop;
        private readonly CancellationTokenRegistration _exit;
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
        private readonly Type _component;
        private int _exited;

        public PageSession(
            FeatureSettingsPageInfo page,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type component,
            IAppExecutionContext context,
            Func<ProcessId, ProcessExitReason, Task<bool>> stop)
        {
            Page = page;
            Context = context;
            _component = component;
            _stop = stop;
            Content = Render;
            // The OS ended the process (task manager, kill, logout): the context's token is cancelled.
            _exit = context.CancellationToken.Register(OnExited);
        }

        public FeatureSettingsPageInfo Page { get; }

        public RenderFragment Content { get; }

        public IAppExecutionContext Context { get; }

        public bool HasExited => Volatile.Read(ref _exited) == 1;

        public event Action? Exited;

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 1)
            {
                return;
            }

            _exit.Dispose();
            await _stop(Context.ProcessId, ProcessExitReason.CloseRequested).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private void OnExited()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 1)
            {
                return;
            }

            Exited?.Invoke();
        }

        // The page's context replaces Settings' in cascade; Settings' window gateway is hidden.
        private void Render(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)RenderWithoutGateway);
            builder.CloseComponent();
        }

        private void RenderWithoutGateway(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppWindowGateway?>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppWindowGateway?>.Value), null);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppWindowGateway?>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<IAppWindowGateway?>.ChildContent), (RenderFragment)RenderWithoutMenus);
            builder.CloseComponent();
        }

        // Settings' context menus are not lent either: the page receives nothing from Settings.
        private void RenderWithoutMenus(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IKazoMenuHost?>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IKazoMenuHost?>.Value), null);
            builder.AddComponentParameter(2, nameof(CascadingValue<IKazoMenuHost?>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<IKazoMenuHost?>.ChildContent), (RenderFragment)RenderInBoundary);
            builder.CloseComponent();
        }

        // ADR 0238: an exception of the page stays in the page's area; Settings goes on.
        private void RenderInBoundary(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AppErrorBoundary>(0);
            builder.AddComponentParameter(1, nameof(AppErrorBoundary.AppName), Page.FeatureName);
            builder.AddComponentParameter(2, nameof(AppErrorBoundary.AppContext), Context);
            builder.AddComponentParameter(3, nameof(AppErrorBoundary.AllowClose), false);
            builder.AddComponentParameter(4, nameof(AppErrorBoundary.ChildContent), (RenderFragment)RenderPage);
            builder.CloseComponent();
        }

        private void RenderPage(RenderTreeBuilder builder)
        {
            builder.OpenComponent(0, _component);
            builder.CloseComponent();
        }
    }
}
