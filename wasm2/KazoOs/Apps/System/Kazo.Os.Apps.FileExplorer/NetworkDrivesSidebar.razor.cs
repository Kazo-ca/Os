using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Icons;
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
    private IReadOnlyList<KazoDriveInfo> _missing = [];
    private IReadOnlyList<KazoNetworkDriveTypeInfo> _types = [];
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
            // ADR 0270, ADR 0282 §5: a folder of the computer, or a cloud service this OS is set up
            // for, with the name and icon its provider gives; the explorer names no service itself.
            _types = await KazoDriveInfo.DescribeNetworkDriveTypesAsync();
        }
        catch (NotSupportedException)
        {
            _types = [];
        }

        _canConnect = _types.Count > 0;

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

        try
        {
            _missing = await KazoDriveInfo.GetProviderMissingDrivesAsync();
        }
        catch (NotSupportedException)
        {
            _missing = [];
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

    /// <summary>
    /// The icon of a drive: the mark of its service (Dropbox, OneDrive, S3), a disk for a folder of the
    /// computer, a plug when it is disconnected.
    /// </summary>
    private (IconLibrary Library, string Name) DriveIcon(KazoDriveInfo drive) =>
        drive.ProviderMissing != KazoDriveProviderMissingReason.None ? (IconLibrary.Lucide, "cloud-off")
        : !drive.IsReady ? (IconLibrary.Lucide, "unplug")
        : drive.Account is null ? (IconLibrary.Lucide, "hard-drive")
        : NetworkDriveIcons.For(drive.DriveFormat, _types);

    /// <summary>
    /// The second line of a cloud drive: its service and, when the drive starts in a folder of the
    /// storage, that folder. Two accounts often carry the same e-mail address: this line tells them apart.
    /// </summary>
    private string? Detail(KazoDriveInfo drive)
    {
        string? service = drive.Account is null ? null : NetworkDriveIcons.ServiceName(drive.DriveFormat, _types);
        string? root = string.IsNullOrWhiteSpace(drive.Root) ? null : "/" + drive.Root.Trim('/');
        return (service, root) switch
        {
            (null, null) => null,
            ({ } name, null) => name,
            (null, { } folder) => folder,
            ({ } name, { } folder) => $"{name} · {folder}"
        };
    }

    /// <summary>The name a screen reader reads: the label and its detail.</summary>
    private string AccessibleName(KazoDriveInfo drive) =>
        Detail(drive) is { } detail ? $"{drive.VolumeLabel}, {detail}" : drive.VolumeLabel;

    /// <summary>The tooltip: the mount point, or how to reconnect a disconnected drive.</summary>
    private string Tooltip(KazoDriveInfo drive) =>
        drive.IsReady ? drive.Name : L["Network.Reconnect", drive.VolumeLabel];

    /// <summary>
    /// Why a drive kept without its provider cannot open (ADR 0282 §5): the Feature is not installed,
    /// it is disabled, or the administrator turned off a service shipped with the OS.
    /// </summary>
    private string MissingReason(KazoDriveInfo drive) => drive.ProviderMissing switch
    {
        KazoDriveProviderMissingReason.DisabledByAdministrator => L["Drives.ProviderMissing.Administrator"],
        KazoDriveProviderMissingReason.Disabled => L["Drives.ProviderMissing.Disabled"],
        _ => L["Drives.ProviderMissing.Removed"]
    };

    /// <summary>Gets whether the Store can bring the provider back: a Feature that is not installed now.</summary>
    private static bool CanInstall(KazoDriveInfo drive) =>
        drive.ProviderMissing == KazoDriveProviderMissingReason.Removed && !string.IsNullOrEmpty(drive.ProviderFeatureId);

    /// <summary>The name a screen reader reads for a drive without its provider: the label, the reason and the action.</summary>
    private string MissingName(KazoDriveInfo drive)
    {
        string name = L["Drives.ProviderMissing.Label", drive.VolumeLabel, MissingReason(drive)];
        return CanInstall(drive) ? L["Drives.ProviderMissing.Label", name, L["Drives.ProviderMissing.Install"]] : name;
    }

    /// <summary>
    /// Opens the Store on the Feature that provided the drive (ADR 0282 §5); the drive comes back by
    /// itself once the Feature is installed. A drive whose provider is disabled does nothing: the
    /// reason is its tooltip.
    /// </summary>
    private Task InstallProviderAsync(KazoDriveInfo drive) => !CanInstall(drive)
        ? Task.CompletedTask
        : RunAsync(() => KazoApps.LaunchAsync(KazoApps.StoreAppId, ["--app=" + drive.ProviderFeatureId]));

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
