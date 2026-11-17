using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings ▸ Features ▸ Installed features (ADR 0249 §1): one card per installed feature (ADR 0260)
/// with its entry points and their kind, the permissions (ADR 0246) and roles (ADR 0247) it declares,
/// the apps that depend on it (ADR 0248), and its actions: enable or disable (the install row of
/// <see cref="InstalledAppsPanel"/>, an administrator for every user), details (the app page),
/// open in the Store, and how to remove it.
/// </summary>
/// <remarks>
/// Uses only the public SDK: <see cref="IAppManagementGateway"/> (capability <c>apps.manage</c>) for
/// the list and the dependents, refreshed on <see cref="IAppCatalogGateway.Changed"/>.
/// </remarks>
public partial class InstalledFeaturesPanel : IDisposable
{
    /// <summary>The Kazo OS Store, which installs, updates and removes features.</summary>
    internal const string StoreAppId = "org.kazoos.store";

    private IReadOnlyList<KazoAppInstallInfo>? _apps;
    private IReadOnlyList<KazoAppInstallInfo>? _features;
    private readonly Dictionary<string, IReadOnlyList<KazoPackageDependent>> _dependents = new(StringComparer.Ordinal);
    private IAppCatalogGateway? _catalog;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets a message shown above the list, such as a page the user may not see (ADR 0249 §4).</summary>
    [Parameter]
    public string? Notice { get; set; }

    /// <summary>Gets or sets what "Details" does with a feature's id: Settings shows its app page.</summary>
    [Parameter]
    public EventCallback<string> ShowDetails { get; set; }

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the installed features, sorted by name (tests).</summary>
    internal IReadOnlyList<KazoAppInstallInfo> Features => _features ?? [];

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _catalog = Context.Catalog;
        _catalog.Changed += OnCatalogChanged;
        await ReloadAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_catalog is not null)
        {
            _catalog.Changed -= OnCatalogChanged;
        }
    }

    /// <summary>The entry points of a feature, sorted by name.</summary>
    internal IReadOnlyList<KazoAppInstallInfo> EntryPointsOf(string featureId) =>
        [.. (_apps ?? []).Where(entry => entry.App.ParentId == featureId).OrderBy(static entry => entry.App.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The installed apps that depend on a feature (ADR 0248), as the OS listed them.</summary>
    internal IReadOnlyList<KazoPackageDependent> DependentsOf(string featureId) =>
        _dependents.TryGetValue(featureId, out IReadOnlyList<KazoPackageDependent>? dependents) ? dependents : [];

    private static string TitleId(KazoAppInstallInfo feature) => "feature-" + feature.App.Id.Replace('.', '-');

    /// <summary>The kind of an entry point, as the app details page names it.</summary>
    private string KindLabel(AppKind kind) => kind switch
    {
        AppKind.Window => L["Details.Kind.Window"],
        AppKind.Terminal => L["Details.Kind.Terminal"],
        AppKind.Service => L["Details.Kind.Service"],
        AppKind.Web => L["Details.Kind.Web"],
        AppKind.SettingsPage => L["Details.Kind.SettingsPage"],
        _ => kind.ToString()
    };

    private void OnCatalogChanged(object? sender, EventArgs e) => _ = InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    private async Task ReloadAsync()
    {
        try
        {
            _apps = await Context.AppManagement.GetAllAsync(Context.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException)
        {
            _apps = [];
            _message = L["Apps.Error.NotAllowed"];
        }

        _features = [.. _apps.Where(static entry => entry.App.Kind == AppKind.Feature)
            .OrderBy(static entry => entry.App.Name, StringComparer.CurrentCultureIgnoreCase)];
        _dependents.Clear();
        foreach (KazoAppInstallInfo feature in _features)
        {
            try
            {
                _dependents[feature.App.Id] = await Context.AppManagement.GetPackageDependentsAsync(feature.App.Id, Context.CancellationToken);
            }
            catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException or AppGatewayAccessDeniedException)
            {
                // A host that cannot list dependents shows none; removing still warns (ADR 0248 §3).
                _dependents[feature.App.Id] = [];
            }
        }
    }

    /// <summary>Opens the Kazo OS Store, on the feature's page when one is named (ADR 0249 §1).</summary>
    private async Task OpenInStoreAsync(string? featureId)
    {
        _message = null;
        try
        {
            await KazoApps.LaunchAsync(StoreAppId, featureId is null ? [] : ["--app=" + featureId], Context.CancellationToken);
        }
        catch (Exception exception) when (exception is KazoAppLaunchException or UnauthorizedAccessException)
        {
            _message = L["Features.StoreUnavailable"];
        }
    }
}
