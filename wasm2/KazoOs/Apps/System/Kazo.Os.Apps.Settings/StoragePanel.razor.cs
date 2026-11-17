using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings ▸ System ▸ Storage (ADR 0250 §3): the origin's usage and quota, the part of Kazo OS's
/// files, its caches and the features' disks when the browser details them, whether the browser
/// protects the storage from eviction (with the button that asks it, a gesture of the user), and the
/// space each feature declared.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): the app's storage gateway (<see cref="KazoStorage"/>). Settings
/// is a system app, so it may ask for persistence; the OS asks once for the whole origin.
/// </remarks>
public partial class StoragePanel
{
    /// <summary>
    /// Gets or sets what a feature's button does (ADR 0250 §3, ADR 0249): Settings opens the feature's
    /// own page, where the user frees space. Without it, no button is shown.
    /// </summary>
    [Parameter]
    public EventCallback<string> OpenFeature { get; set; }

    private const double Mebibyte = 1024d * 1024d;
    private const double Gibibyte = Mebibyte * 1024d;

    private KazoStorageEstimate? _estimate;
    private IReadOnlyList<KazoFeatureStorageUsage> _features = [];
    private bool _requesting;
    private bool _refused;
    private string? _error;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale, ADR 0104).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    private int UsedPercent => _estimate is { QuotaBytes: > 0 } estimate
        ? (int)Math.Clamp(Math.Round(estimate.UsageBytes * 100d / estimate.QuotaBytes), 0, 100)
        : 0;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            IAppStorageGateway storage = Context.Storage;
            _estimate = await storage.GetEstimateAsync(Context.CancellationToken);
            _features = await storage.GetFeatureUsageAsync(Context.CancellationToken);
            _error = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _estimate ??= KazoStorageEstimate.Unknown;
            _error = L["Storage.Error", exception.Message];
        }
    }

    /// <summary>The button's click is the gesture the browser waits for before it grants persistence.</summary>
    private async Task ProtectAsync()
    {
        if (_requesting)
        {
            return;
        }

        _requesting = true;
        _refused = false;
        try
        {
            _refused = !await Context.Storage.RequestPersistenceAsync(Context.CancellationToken);
            await LoadAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            _error = L["Storage.Error", exception.Message];
        }
        finally
        {
            _requesting = false;
        }
    }

    private string FormatOptional(long? bytes) => bytes is { } value ? FormatSize(value) : L["Storage.NotDetailed"];

    private string FormatSize(long bytes) => bytes >= Gibibyte
        ? L["Storage.GiB", (bytes / Gibibyte).ToString("0.0", Formats)]
        : L["Storage.MiB", (bytes / Mebibyte).ToString("0.#", Formats)];
}
