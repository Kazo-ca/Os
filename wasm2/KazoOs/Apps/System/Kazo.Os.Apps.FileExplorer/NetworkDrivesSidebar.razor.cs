using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// Lists the network drives through <see cref="KazoDriveInfo"/>, reconnects and unmounts them
/// (ADR 0181 §11), and opens <see cref="ConnectNetworkDriveDialog"/> to mount a folder of the
/// computer or a cloud storage (ADR 0267 §11, ADR 0270). The browser opens its folder dialog or a
/// sign-in window only after a user gesture, so every action runs straight from the click.
/// </summary>
public partial class NetworkDrivesSidebar
{
    private IReadOnlyList<KazoDriveInfo> _drives = [];
    private bool _canConnect;
    private bool _busy;
    private bool _dialogOpen;

    /// <summary>The explorer's texts in the language of its process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => AppContext.GetLocalizer<Strings>();

    /// <summary>The folder the explorer shows, to mark the drive it is on.</summary>
    [Parameter]
    public string? CurrentPath { get; set; }

    /// <summary>Opens a folder in the explorer.</summary>
    [Parameter]
    public EventCallback<string> Navigate { get; set; }

    /// <summary>Shows an error in the explorer.</summary>
    [Parameter]
    public EventCallback<string> Failed { get; set; }

    /// <summary>Gets or sets whether the drives are chips of the phone's places bar rather than a sidebar section.</summary>
    [Parameter]
    public bool Compact { get; set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            // ADR 0270: a folder of the computer, or a cloud service this OS is set up for.
            _canConnect = (await KazoDriveInfo.GetNetworkDriveTypesAsync()).Count > 0;
        }
        catch (NotSupportedException)
        {
            _canConnect = false;
        }

        await ReloadAsync();
    }

    /// <summary>Re-reads the drives; a drive mounted from the terminal shows up at the next navigation.</summary>
    protected override Task OnParametersSetAsync() => ReloadAsync();

    /// <summary>Re-reads the network drives.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            _drives = [.. (await KazoDriveInfo.GetDrivesAsync()).Where(static drive => drive.DriveType == KazoDriveType.Network)];
        }
        catch (NotSupportedException)
        {
            _drives = [];
        }
    }

    private bool IsCurrent(KazoDriveInfo drive) =>
        CurrentPath is { } path && (path == drive.Name || path.StartsWith(drive.Name + "/", StringComparison.Ordinal));

    private void OpenDialog() => _dialogOpen = true;

    private void CloseDialog() => _dialogOpen = false;

    /// <summary>A drive was mounted from the dialog: list it and open it.</summary>
    private async Task OnMountedAsync(KazoDriveInfo drive)
    {
        _dialogOpen = false;
        await ReloadAsync();
        await Navigate.InvokeAsync(drive.Name);
    }

    /// <summary>The icon of a drive: a cloud for a cloud storage, a disk for a folder of the computer.</summary>
    private static string DriveIcon(KazoDriveInfo drive) =>
        !drive.IsReady ? "unplug" : drive.Account is null ? "hard-drive" : "cloud";

    private Task OpenAsync(KazoDriveInfo drive) => RunAsync(async () =>
    {
        if (!drive.IsReady && !await KazoDriveInfo.ReconnectAsync(drive.Name))
        {
            await Failed.InvokeAsync(L["Network.PermissionRefused", drive.VolumeLabel]);
            return;
        }

        await ReloadAsync();
        await Navigate.InvokeAsync(drive.Name);
    });

    private Task UnmountAsync(KazoDriveInfo drive) => RunAsync(async () =>
    {
        bool inside = IsCurrent(drive);
        await KazoDriveInfo.UnmountAsync(drive.Name);
        await ReloadAsync();
        if (inside)
        {
            await Navigate.InvokeAsync(KazoUser.Describe(AppContext!).HomeDirectory);
        }
    });

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Failed.InvokeAsync(L["Network.Error", exception.Message]);
        }
        finally
        {
            _busy = false;
        }
    }
}
