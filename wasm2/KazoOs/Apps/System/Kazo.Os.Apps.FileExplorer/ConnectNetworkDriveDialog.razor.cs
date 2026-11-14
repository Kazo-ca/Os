using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The "Connect a network drive" dialog of the explorer (ADR 0267 §11, ADR 0270), on a computer and
/// on a phone. It reads what the OS can mount (<see cref="KazoDriveInfo.GetNetworkDriveTypesAsync"/>)
/// and the user's cloud accounts (<see cref="KazoCloudAccounts"/>), then mounts through
/// <see cref="KazoDriveInfo.MountNetworkDriveAsync"/>.
/// </summary>
/// <remarks>
/// The browser opens a folder dialog or a sign-in window only right after a click. So the dialog
/// loads everything when it opens (<see cref="KazoCloudAccounts.PrepareAsync"/> included), and each
/// button calls the OS first thing in its handler. Without <c>cloud-accounts.manage</c> (a host that
/// denies it), only the folder of the computer is offered.
/// </remarks>
public partial class ConnectNetworkDriveDialog
{
    /// <summary>Type of a folder of the computer (ADR 0181).</summary>
    internal const string HostFolderType = "hostfs";

    /// <summary>Type of an S3 compatible storage (ADR 0267, work item 6).</summary>
    internal const string S3Type = "s3";

    private readonly string _titleId = "cnd-title-" + Guid.NewGuid().ToString("N");
    private readonly string _scopeGroup = "cnd-scope-" + Guid.NewGuid().ToString("N");
    private IReadOnlyList<string> _types = [];
    private IReadOnlyList<KazoCloudProvider> _providers = [];
    private IReadOnlyList<KazoCloudAccount> _accounts = [];
    private bool _canConnect;
    private bool _cloudAllowed;
    private bool _loading = true;
    private bool _busy;
    private string? _error;
    private Step _step = Step.Choose;
    private KazoCloudProvider? _provider;
    private KazoCloudAccessScope _scope;
    private string? _root;
    private S3Form _s3 = new();

    private enum Step
    {
        Choose,
        Provider,
        S3
    }

    /// <summary>The explorer's texts in the language of its process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => AppContext.GetLocalizer<Strings>();

    /// <summary>Gets or sets what runs once a drive is mounted; the dialog is then done.</summary>
    [Parameter]
    public EventCallback<KazoDriveInfo> Mounted { get; set; }

    /// <summary>Gets or sets what runs when the user closes the dialog without mounting.</summary>
    [Parameter]
    public EventCallback Closed { get; set; }

    private bool HasCloudChoice =>
        _cloudAllowed && (_providers.Any(provider => _types.Contains(provider.Id)) || _types.Contains(S3Type));

    private bool HasAnyChoice => _types.Contains(HostFolderType) || HasCloudChoice;

    private string Title => _step switch
    {
        Step.Provider when _provider is not null => L["CloudDrive.ProviderTitle", _provider.DisplayName],
        Step.S3 => L["CloudDrive.S3"],
        _ => L["CloudDrive.Title"]
    };

    /// <summary>The dialog element, focused when it opens so that Escape and Tab stay inside it.</summary>
    private ElementReference _dialog;

    /// <summary>
    /// Set when a failed action re-enables the form: its button was disabled while it ran, so the
    /// browser moved the focus out of the dialog, and Escape would no longer reach it.
    /// </summary>
    private bool _refocus;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _refocus)
        {
            _refocus = false;
            try
            {
                await _dialog.FocusAsync();
            }
            catch (JSException)
            {
                // The dialog closed before its first render finished: nothing to focus.
            }
        }
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _types = await KazoDriveInfo.GetNetworkDriveTypesAsync();
            if (_types.Any(static type => type != HostFolderType))
            {
                await LoadCloudAsync();
            }
        }
        catch (NotSupportedException)
        {
            _types = [];
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Reads the providers and the accounts, and readies the sign-in window for the click.</summary>
    private async Task LoadCloudAsync()
    {
        try
        {
            _canConnect = await KazoCloudAccounts.CanConnectAsync();
            _providers = await KazoCloudAccounts.GetProvidersAsync();
            _accounts = [.. (await KazoCloudAccounts.GetAccountsAsync()).Where(account => _types.Contains(account.ProviderId))];
            await KazoCloudAccounts.PrepareAsync();
            _cloudAllowed = true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException)
        {
            // No cloud-accounts.manage, or a host without cloud accounts: only the folder of the computer.
            (_providers, _accounts, _cloudAllowed) = ([], [], false);
        }
        catch (KazoCloudAccountException exception)
        {
            _error = ErrorText(exception);
        }
    }

    private void ChooseProvider(KazoCloudProvider provider)
    {
        (_provider, _scope, _root, _error) = (provider, provider.Scopes[0], null, null);
        _step = Step.Provider;
    }

    private void ChooseS3()
    {
        (_s3, _error) = (new S3Form(), null);
        _step = Step.S3;
    }

    private void Back()
    {
        (_step, _error) = (Step.Choose, null);
        _s3 = new S3Form();
    }

    private Task MountHostFolderAsync() =>
        RunAsync(() => KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(HostFolderType)));

    private Task MountAccountAsync(KazoCloudAccount account) =>
        RunAsync(() => KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(account.ProviderId, account.Id)));

    /// <summary>Signs in (the window opens inside this click), then mounts the new account's storage.</summary>
    private Task ConnectProviderAsync()
    {
        if (_provider is not { } provider)
        {
            return Task.CompletedTask;
        }

        KazoCloudAccessScope scope = _scope;
        string? root = string.IsNullOrWhiteSpace(_root) ? null : _root.Trim();
        return RunAsync(async () =>
        {
            KazoCloudAccount account = await KazoCloudAccounts.ConnectAsync(provider.Id, scope);
            return await KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(provider.Id, account.Id, root));
        });
    }

    private Task MountS3Async()
    {
        if (!_s3.IsComplete)
        {
            return Task.CompletedTask;
        }

        KazoNetworkDriveRequest request = _s3.ToRequest();
        return RunAsync(async () =>
        {
            KazoDriveInfo? drive = await KazoDriveInfo.MountNetworkDriveAsync(request);
            // The keys are in the OS now: the form forgets them.
            _s3 = new S3Form();
            return drive;
        });
    }

    private async Task RunAsync(Func<Task<KazoDriveInfo?>> mount)
    {
        if (_busy)
        {
            return;
        }

        (_busy, _error) = (true, null);
        try
        {
            if (await mount() is { } drive)
            {
                await Mounted.InvokeAsync(drive);
            }
        }
        catch (Exception exception) when (exception is KazoCloudAccountException or ArgumentException or NotSupportedException
            or InvalidOperationException or IOException or UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            _error = ErrorText(exception);
            _refocus = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CloseAsync()
    {
        if (!_busy)
        {
            await Closed.InvokeAsync();
        }
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            await CloseAsync();
        }
    }

    /// <summary>The user's text for a refused operation; the OS's own message only where nothing better exists.</summary>
    internal string ErrorText(Exception exception) => exception switch
    {
        KazoCloudAccountException { Error: not KazoCloudAccountError.Other } cloud => L[$"CloudDrive.Error.{cloud.Error}"],
        ArgumentException argument => L["CloudDrive.Error.Refused", argument.Message],
        UnauthorizedAccessException or AppGatewayAccessDeniedException => L["CloudDrive.Error.NotAllowed"],
        _ => L["Network.Error", exception.Message]
    };

    private static string ProviderIcon(string providerId) => providerId switch
    {
        S3Type => "database",
        _ => "cloud"
    };

    private string ProviderName(string providerId) =>
        _providers.FirstOrDefault(provider => provider.Id == providerId)?.DisplayName
        ?? (providerId == S3Type ? L["CloudDrive.S3"] : providerId);

    private string AccountDetail(KazoCloudAccount account) =>
        account.ProviderId == S3Type
            ? ProviderName(account.ProviderId)
            : L["CloudDrive.AccountDetail", ProviderName(account.ProviderId), ScopeShort(account.Scope)];

    private string ScopeShort(KazoCloudAccessScope scope) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolderShort"] : L["CloudDrive.Scope.FullShort"];

    private string ScopeTitle(KazoCloudAccessScope scope, KazoCloudProvider provider) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolder"] : L["CloudDrive.Scope.Full", provider.DisplayName];

    private string ScopeDetail(KazoCloudAccessScope scope) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolderDetail"] : L["CloudDrive.Scope.FullDetail"];

    /// <summary>What the S3 form holds; the keys leave it as soon as the OS has them.</summary>
    internal sealed class S3Form
    {
        public string? Endpoint { get; set; }

        public string? Region { get; set; }

        public string? Bucket { get; set; }

        public string? Prefix { get; set; }

        public string? Addressing { get; set; }

        public string? AccessKeyId { get; set; }

        public string? SecretAccessKey { get; set; }

        /// <summary>Gets whether the required fields are filled: address, bucket and both keys.</summary>
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Bucket)
            && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrEmpty(SecretAccessKey);

        /// <summary>The request of ADR 0270: settings in <c>Options</c>, keys in <c>Secrets</c>.</summary>
        public KazoNetworkDriveRequest ToRequest()
        {
            Dictionary<string, string> options = new(StringComparer.Ordinal)
            {
                ["endpoint"] = Endpoint!.Trim(),
                ["bucket"] = Bucket!.Trim()
            };
            Add(options, "region", Region);
            Add(options, "prefix", Prefix);
            Add(options, "addressing", Addressing);
            return new KazoNetworkDriveRequest(S3Type)
            {
                Options = options,
                Secrets = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["access-key-id"] = AccessKeyId!.Trim(),
                    ["secret-access-key"] = SecretAccessKey!
                }
            };
        }

        private static void Add(Dictionary<string, string> options, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                options[key] = value.Trim();
            }
        }
    }
}
