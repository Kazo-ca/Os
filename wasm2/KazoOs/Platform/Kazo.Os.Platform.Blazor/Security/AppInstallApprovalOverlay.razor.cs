using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Policy;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Blazor.Security;

/// <summary>
/// The approval window of ADR 0189 §4, step 2: once an administrator signed in, it shows the app to
/// install, update or remove (name, ID, version, size), its source and trust level, and the
/// capabilities it asks for, dangerous ones first; the administrator approves or refuses.
/// </summary>
/// <remarks>
/// The shell renders exactly one instance above every window, like
/// <see cref="AdministratorAuthenticationOverlay"/>. It attaches itself to
/// <see cref="AppInstallApprovalBroker"/>; it decides nothing but the outcome it reports. Escape
/// refuses.
/// </remarks>
public sealed partial class AppInstallApprovalOverlay
{
    private const double Kibibyte = 1024d;

    private AppInstallApprovalBroker.PendingApproval? _current;
    private IDisposable? _attachment;
    private ElementReference _approveButton;
    private bool _focusPending;

    /// <summary>Gets or sets the overlay's texts, in the session language (ADR 0104).</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets the broker queuing approval requests.</summary>
    [Inject]
    public AppInstallApprovalBroker Broker { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _attachment = Broker.Attach();
        Broker.Changed += OnBrokerChanged;
        ShowCurrent();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPending || _current is null)
        {
            return;
        }

        _focusPending = false;
        try
        {
            // Take the keyboard as soon as the window appears, like a secure desktop.
            await _approveButton.FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // Element not attached (prerendering or test renderer): focus is a convenience only.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Broker.Changed -= OnBrokerChanged;
        _attachment?.Dispose();
    }

    private void OnBrokerChanged() => _ = InvokeAsync(() =>
    {
        ShowCurrent();
        StateHasChanged();
    });

    private void ShowCurrent()
    {
        AppInstallApprovalBroker.PendingApproval? next = Broker.Current;
        if (ReferenceEquals(next, _current))
        {
            return;
        }

        _current = next;
        _focusPending = next is not null;
    }

    private void Approve() => _current?.Complete(AppInstallApprovalOutcome.Approved);

    private void Refuse() => _current?.Complete(AppInstallApprovalOutcome.Refused);

    private void HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            Refuse();
        }
    }

    private string Title(AppInstallApprovalRequest request) => request.Operation switch
    {
        AppInstallApprovalOperation.Update => L["InstallApproval.Title.Update"],
        AppInstallApprovalOperation.Remove => L["InstallApproval.Title.Remove"],
        AppInstallApprovalOperation.Rollback => L["InstallApproval.Title.Rollback"],
        _ => L["InstallApproval.Title.Install"]
    };

    private string ApproveText(AppInstallApprovalRequest request) => request.Operation switch
    {
        AppInstallApprovalOperation.Update => L["InstallApproval.Approve.Update"],
        AppInstallApprovalOperation.Remove => L["InstallApproval.Approve.Remove"],
        AppInstallApprovalOperation.Rollback => L["InstallApproval.Approve.Rollback"],
        _ => L["InstallApproval.Approve.Install"]
    };

    private string TrustText(AppSourceTrust trust) => L[$"InstallApproval.Trust.{trust}"];

    private static string TrustClass(AppSourceTrust trust) => $"is-{trust.ToString().ToLowerInvariant()}";

    private string FormatSize(long bytes)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        return bytes < Kibibyte
            ? L["InstallApproval.Bytes", bytes.ToString("N0", culture)]
            : bytes < Kibibyte * Kibibyte
                ? L["InstallApproval.KiB", (bytes / Kibibyte).ToString("0.#", culture)]
                : L["InstallApproval.MiB", (bytes / (Kibibyte * Kibibyte)).ToString("0.#", culture)];
    }

    /// <summary>Gets the catalog of capabilities of the running OS (ADR 0246); absent in a host without one.</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>
    /// The capabilities with their title and risk sentence (ADR 0104), dangerous ones first, as the
    /// catalog of the running OS describes them (ADR 0246): a feature's permission shows its own texts
    /// and the feature that declares it.
    /// </summary>
    private IEnumerable<CapabilityLine> Describe(IReadOnlyList<string> capabilities)
    {
        ICapabilityCatalog catalog = Services?.GetService(typeof(ICapabilityCatalog)) as ICapabilityCatalog ?? BuiltInCapabilityCatalog.Instance;
        return capabilities
            .Select(capability =>
            {
                if (catalog.Find(capability) is not { } found)
                {
                    // Unknown to this OS (a feature not installed yet): show its identifier.
                    return new CapabilityLine(capability, capability, null, false);
                }

                CapabilityDescription description = CapabilityText.Localize(found, CultureInfo.CurrentUICulture);
                string title = description.ProviderId is { } provider ? $"{description.Title} ({provider})" : description.Title;
                return new CapabilityLine(capability, title, description.RiskDescription, description.Risk == CapabilityRisk.Dangerous);
            })
            .OrderByDescending(static line => line.Dangerous)
            .ThenBy(static line => line.Id, StringComparer.Ordinal);
    }

    private sealed record CapabilityLine(string Id, string Title, string? Risk, bool Dangerous);
}
