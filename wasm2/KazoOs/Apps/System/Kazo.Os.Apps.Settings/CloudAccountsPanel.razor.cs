using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Network &gt; Cloud accounts (ADR 0267 §11, ADR 0270): lists the session user's cloud
/// accounts with their scope and drives, reconnects an account whose sign-in expired, and removes one
/// (its drives are unmounted, its sign-in revoked where the service allows it).
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="KazoCloudAccounts"/>, which needs the dangerous
/// capability <c>cloud-accounts.manage</c>, and <see cref="KazoDriveInfo"/> for the drives. A host
/// without cloud accounts shows a note instead of the list.
/// </remarks>
public partial class CloudAccountsPanel
{
    private const string S3Type = "s3";

    private IReadOnlyList<KazoCloudAccount> _accounts = [];
    private IReadOnlyList<KazoCloudProvider> _providers = [];
    private IReadOnlyList<KazoDriveInfo> _drives = [];
    private bool _loading = true;
    private bool _unsupported;
    private bool _canConnect;
    private bool _busy;
    private bool _failed;
    private string? _message;
    private string? _confirmRemove;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.CurrentCulture;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private async Task LoadAsync()
    {
        try
        {
            _canConnect = await KazoCloudAccounts.CanConnectAsync();
            _providers = await KazoCloudAccounts.GetProvidersAsync();
            _accounts = await KazoCloudAccounts.GetAccountsAsync();
            _drives = [.. (await KazoDriveInfo.GetDrivesAsync()).Where(static drive => drive.Account is not null)];
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            _unsupported = true;
        }
        catch (KazoCloudAccountException exception)
        {
            (_message, _failed) = (ErrorText(exception), true);
        }
    }

    private IReadOnlyList<KazoDriveInfo> DrivesOf(KazoCloudAccount account) =>
        [.. _drives.Where(drive => drive.Account == account.Id)];

    private string ProviderName(string providerId) =>
        _providers.FirstOrDefault(provider => provider.Id == providerId)?.DisplayName
        ?? providerId switch
        {
            "dropbox" => "Dropbox",
            "onedrive" => "OneDrive",
            S3Type => "S3",
            _ => providerId
        };

    private void AskRemove(KazoCloudAccount account) => (_confirmRemove, _message) = (account.Id, null);

    /// <summary>Signs in again; the window opens inside this click, so nothing waits before the call.</summary>
    private Task ReconnectAsync(KazoCloudAccount account) => RunAsync(async () =>
    {
        KazoCloudAccount reconnected = await KazoCloudAccounts.ReconnectAsync(account.Id);
        // The drives of the account come back now that it signs in again (ADR 0267 §9).
        foreach (KazoDriveInfo drive in DrivesOf(account).Where(static drive => !drive.IsReady))
        {
            await KazoDriveInfo.ReconnectAsync(drive.Name);
        }

        return L["CloudAccounts.Reconnected", reconnected.DisplayName];
    });

    private Task RemoveAsync(KazoCloudAccount account) => RunAsync(async () =>
    {
        await KazoCloudAccounts.RemoveAsync(account.Id);
        _confirmRemove = null;
        return L["CloudAccounts.Removed", account.DisplayName];
    });

    private async Task RunAsync(Func<Task<string>> action)
    {
        if (_busy)
        {
            return;
        }

        (_busy, _message, _failed) = (true, null, false);
        try
        {
            _message = await action();
        }
        catch (Exception exception) when (exception is KazoCloudAccountException or InvalidOperationException
            or IOException or UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException)
        {
            (_message, _failed) = (ErrorText(exception), true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    private string ErrorText(Exception exception) => exception switch
    {
        KazoCloudAccountException { Error: not KazoCloudAccountError.Other } cloud => L[$"CloudAccounts.Error.{cloud.Error}"],
        UnauthorizedAccessException or AppGatewayAccessDeniedException => L["CloudAccounts.Error.NotAllowed"],
        _ => L["CloudAccounts.Error.Failed", exception.Message]
    };
}
