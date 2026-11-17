using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The "Connect a network drive" dialog of the explorer (ADR 0267 §11, ADR 0270, ADR 0282 §5), on a
/// computer and on a phone. It reads the types the OS can mount, each with its name, icon and
/// connection (<see cref="KazoDriveInfo.DescribeNetworkDriveTypesAsync"/>), and the user's cloud
/// accounts (<see cref="KazoCloudAccounts"/>), then mounts through <see cref="KazoDriveInfo.MountNetworkDriveAsync"/>.
/// It names no service itself: Dropbox, OneDrive, S3 and a type a feature provides go through the
/// same three paths (folder dialog, sign-in, form).
/// </summary>
/// <remarks>
/// The browser opens a folder dialog or a sign-in window only right after a click. So the dialog
/// loads everything when it opens (<see cref="KazoCloudAccounts.PrepareAsync"/> included), and each
/// button calls the OS first thing in its handler. Without <c>cloud-accounts.manage</c> (a host that
/// denies it), only the folder of the computer is offered. Next to a type a feature provides, the
/// dialog always names that feature (ADR 0282, Security: a feature cannot pass for another service).
/// </remarks>
public partial class ConnectNetworkDriveDialog
{
    /// <summary>Type of a folder of the computer (ADR 0181).</summary>
    internal const string HostFolderType = "hostfs";

    private readonly string _titleId = "cnd-title-" + Guid.NewGuid().ToString("N");
    private readonly string _scopeGroup = "cnd-scope-" + Guid.NewGuid().ToString("N");
    private IReadOnlyList<KazoNetworkDriveTypeInfo> _types = [];
    private IReadOnlyList<KazoCloudAccount> _accounts = [];
    private bool _canConnect;
    private bool _cloudAllowed;
    private bool _loading = true;
    private bool _busy;

    /// <summary>
    /// Set while the provider's sign-in window is open. The OS cannot see that window close (it is
    /// in another browsing context group, ADR 0131), and a provider that shows its own error page
    /// never comes back: Back, Close and Escape stay usable and cancel the wait.
    /// </summary>
    private CancellationTokenSource? _signIn;
    private string? _error;
    private Step _step = Step.Choose;
    private KazoNetworkDriveTypeInfo? _type;
    private KazoCloudAccessScope _scope;
    private string? _root;
    private DriveForm _form = new([]);

    private enum Step
    {
        Choose,
        Provider,
        Form
    }

    /// <summary>The explorer's texts in the language of its process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => AppContext.GetLocalizer<Strings>();

    /// <summary>Gets or sets what runs once a drive is mounted; the dialog is then done.</summary>
    [Parameter]
    public EventCallback<KazoDriveInfo> Mounted { get; set; }

    /// <summary>Gets or sets what runs when the user closes the dialog without mounting.</summary>
    [Parameter]
    public EventCallback Closed { get; set; }

    /// <summary>The types offered as a new drive: a cloud type only when cloud accounts are allowed here.</summary>
    /// <summary>
    /// The types offered, in a stable order: the folder of the computer first, then the OS's own
    /// services, then the ones Features provide, each group by name.
    /// </summary>
    private IEnumerable<KazoNetworkDriveTypeInfo> OfferedTypes =>
        _types.Where(type => type.Connection == KazoNetworkDriveConnection.HostFolder || _cloudAllowed)
            .OrderBy(static type => type.Connection != KazoNetworkDriveConnection.HostFolder)
            .ThenBy(static type => type.IsThirdParty)
            .ThenBy(static type => type.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    private bool HasCloudChoice => _cloudAllowed && _types.Any(static type => type.Connection != KazoNetworkDriveConnection.HostFolder);

    /// <summary>Gets whether Back and Close work: always, except while a mount (not a sign-in) runs.</summary>
    private bool CanLeave => !_busy || _signIn is not null;

    private bool HasAnyChoice => OfferedTypes.Any();

    private string Title => _step switch
    {
        Step.Provider when _type is not null => L["CloudDrive.ProviderTitle", _type.DisplayName],
        Step.Form when _type is not null => _type.DisplayName,
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
            _types = await KazoDriveInfo.DescribeNetworkDriveTypesAsync();
            if (_types.Any(static type => type.Connection != KazoNetworkDriveConnection.HostFolder))
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

    /// <summary>Reads the accounts, and readies the sign-in window for the click.</summary>
    private async Task LoadCloudAsync()
    {
        try
        {
            _canConnect = await KazoCloudAccounts.CanConnectAsync();
            _accounts = [.. (await KazoCloudAccounts.GetAccountsAsync()).Where(account => TypeOf(account.ProviderId) is not null)];
            await KazoCloudAccounts.PrepareAsync();
            _cloudAllowed = true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException)
        {
            // No cloud-accounts.manage, or a host without cloud accounts: only the folder of the computer.
            (_accounts, _cloudAllowed) = ([], false);
        }
        catch (KazoCloudAccountException exception)
        {
            _error = ErrorText(exception);
        }
    }

    /// <summary>A type was chosen: the folder dialog opens at once (it needs this click), the others show their step.</summary>
    private Task ChooseAsync(KazoNetworkDriveTypeInfo type)
    {
        _error = null;
        switch (type.Connection)
        {
            case KazoNetworkDriveConnection.HostFolder:
                return RunAsync(() => KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(type.Type)));
            case KazoNetworkDriveConnection.OAuth when type.Scopes.Count > 0:
                (_type, _scope, _root) = (type, type.Scopes[0], null);
                _step = Step.Provider;
                break;
            case KazoNetworkDriveConnection.Form:
                (_type, _form) = (type, new DriveForm(type.Fields));
                _step = Step.Form;
                break;
        }

        return Task.CompletedTask;
    }

    private void Back()
    {
        if (!CanLeave)
        {
            return;
        }

        CancelSignIn();
        (_step, _error, _type) = (Step.Choose, null, null);
        _form = new DriveForm([]);
    }

    private Task MountAccountAsync(KazoCloudAccount account) =>
        RunAsync(() => KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(account.ProviderId, account.Id)));

    /// <summary>Signs in (the window opens inside this click), then mounts the new account's storage.</summary>
    private Task ConnectProviderAsync()
    {
        if (_type is not { } type)
        {
            return Task.CompletedTask;
        }

        KazoCloudAccessScope scope = _scope;
        string? root = string.IsNullOrWhiteSpace(_root) ? null : _root.Trim();
        return RunAsync(async () =>
        {
            CancellationTokenSource signIn = new();
            _signIn = signIn;
            KazoCloudAccount account;
            try
            {
                account = await KazoCloudAccounts.ConnectAsync(type.Type, scope, signIn.Token);
            }
            finally
            {
                if (ReferenceEquals(_signIn, signIn))
                {
                    _signIn = null;
                }

                signIn.Dispose();
            }

            return await KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest(type.Type, account.Id, root));
        });
    }

    private bool IsFormComplete => _form.IsComplete;

    /// <summary>Mounts with the form's values: the plain ones in <c>Options</c>, the secret ones in <c>Secrets</c>.</summary>
    private Task MountFormAsync()
    {
        if (_type is not { } type || !_form.IsComplete)
        {
            return Task.CompletedTask;
        }

        KazoNetworkDriveRequest request = _form.ToRequest(type.Type);
        return RunAsync(async () =>
        {
            KazoDriveInfo? drive = await KazoDriveInfo.MountNetworkDriveAsync(request);
            // The secrets are in the OS now: the form forgets them.
            _form = new DriveForm(type.Fields);
            return drive;
        });
    }

    private async Task RunAsync(Func<Task<KazoDriveInfo?>> mount)
    {
        if (_busy)
        {
            return;
        }

        (_busy, _error, _left) = (true, null, false);
        try
        {
            if (await mount() is { } drive && !_left)
            {
                await Mounted.InvokeAsync(drive);
            }
        }
        catch (OperationCanceledException) when (_left)
        {
            // The user left the step (Back, Close, Escape) while the sign-in window was open.
        }
        catch (KazoCloudAccountException exception) when (_left && exception.Error == KazoCloudAccountError.Cancelled)
        {
            // Same: the cancelled sign-in is the user's own choice, not an error to show.
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
        if (CanLeave)
        {
            CancelSignIn();
            await Closed.InvokeAsync();
        }
    }

    /// <summary>Set when the user left the step during a sign-in: its late result is dropped.</summary>
    private bool _left;

    /// <summary>Stops waiting for the sign-in window, if one is open.</summary>
    private void CancelSignIn()
    {
        if (_signIn is { } signIn)
        {
            _left = true;
            _signIn = null;
            signIn.Cancel();
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

    /// <summary>The description of a type the OS offers now; <see langword="null"/> otherwise.</summary>
    private KazoNetworkDriveTypeInfo? TypeOf(string type) => NetworkDriveIcons.Find(type, _types);

    /// <summary>
    /// "Provided by &lt;feature&gt; (&lt;id&gt;)" next to a type a feature provides (ADR 0282, Security);
    /// <see langword="null"/> for the OS's own types.
    /// </summary>
    internal string? ProvidedBy(KazoNetworkDriveTypeInfo type)
    {
        if (type.ProviderFeatureId is not { } id)
        {
            return null;
        }

        string text = L["CloudDrive.ProvidedBy", type.ProviderFeatureName ?? id, id];
        return text;
    }

    /// <summary>The second line of a type: what choosing it does.</summary>
    private string TypeDetail(KazoNetworkDriveTypeInfo type) => type.Connection switch
    {
        KazoNetworkDriveConnection.HostFolder => L["CloudDrive.HostFolderDetail"],
        KazoNetworkDriveConnection.OAuth => L["CloudDrive.ProviderDetail", type.DisplayName],
        _ => L["CloudDrive.FormDetail"]
    };

    /// <summary>
    /// Where the reserved folder is in the user's storage (<c>/Apps/Kazo OS Folder/</c>), shown before the
    /// start folder when that scope is chosen; <see langword="null"/> otherwise or when the OS does not know it.
    /// </summary>
    private string? AppFolderPrefix =>
        _scope == KazoCloudAccessScope.AppFolder && !string.IsNullOrWhiteSpace(_type?.AppFolderPath)
            ? "/" + _type.AppFolderPath.Trim('/') + "/"
            : null;

    private string ServiceName(string providerId) => TypeOf(providerId)?.DisplayName ?? providerId;

    private string AccountDetail(KazoCloudAccount account) =>
        TypeOf(account.ProviderId) is { Connection: KazoNetworkDriveConnection.OAuth }
            ? L["CloudDrive.AccountDetail", ServiceName(account.ProviderId), ScopeShort(account.Scope)]
            : ServiceName(account.ProviderId);

    private string ScopeShort(KazoCloudAccessScope scope) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolderShort"] : L["CloudDrive.Scope.FullShort"];

    private string ScopeTitle(KazoCloudAccessScope scope, KazoNetworkDriveTypeInfo type) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolder"] : L["CloudDrive.Scope.Full", type.DisplayName];

    private string ScopeDetail(KazoCloudAccessScope scope) =>
        scope == KazoCloudAccessScope.AppFolder ? L["CloudDrive.Scope.AppFolderDetail"] : L["CloudDrive.Scope.FullDetail"];

    /// <summary>The label of a field, with "(optional)" when the form can go without it.</summary>
    private string FieldLabel(KazoNetworkDriveField field) =>
        field.Required || field.Kind == KazoNetworkDriveFieldKind.Choice || field.Label.Contains('(', StringComparison.Ordinal)
            ? field.Label
            : L["CloudDrive.Optional", field.Label];

    /// <summary>The input of a field: a masked one for a secret.</summary>
    internal static string InputType(KazoNetworkDriveField field) => field.Kind switch
    {
        KazoNetworkDriveFieldKind.Secret => "password",
        KazoNetworkDriveFieldKind.Url => "url",
        KazoNetworkDriveFieldKind.Number => "number",
        _ => "text"
    };

    /// <summary>Addresses and secrets take the whole row; short values share it.</summary>
    private static bool IsWide(KazoNetworkDriveField field) =>
        field.Kind is KazoNetworkDriveFieldKind.Url or KazoNetworkDriveFieldKind.Secret;

    /// <summary>What a form holds; the secrets leave it as soon as the OS has them.</summary>
    /// <param name="fields">The type's fields.</param>
    internal sealed class DriveForm(IReadOnlyList<KazoNetworkDriveField> fields)
    {
        private readonly Dictionary<string, string> _values = fields
            .Where(static field => field.Default is not null)
            .ToDictionary(static field => field.Name, static field => field.Default!, StringComparer.Ordinal);

        /// <summary>Gets the value of a field, empty when none.</summary>
        public string Get(string name) => _values.GetValueOrDefault(name) ?? string.Empty;

        /// <summary>Sets the value of a field.</summary>
        public void Set(string name, string? value) => _values[name] = value ?? string.Empty;

        /// <summary>Gets whether every required field is filled.</summary>
        public bool IsComplete => fields.All(entry => !entry.Required || !string.IsNullOrWhiteSpace(Get(entry.Name)));

        /// <summary>The request of ADR 0270: plain values in <c>Options</c>, secrets in <c>Secrets</c>, empty ones left out.</summary>
        public KazoNetworkDriveRequest ToRequest(string type)
        {
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            Dictionary<string, string> secrets = new(StringComparer.Ordinal);
            foreach (KazoNetworkDriveField field in fields)
            {
                string value = Get(field.Name);
                if (field.IsSecret ? value.Length == 0 : string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (field.IsSecret)
                {
                    secrets[field.Name] = value;
                }
                else
                {
                    options[field.Name] = value.Trim();
                }
            }

            return new KazoNetworkDriveRequest(type)
            {
                Options = options,
                Secrets = secrets.Count == 0 ? null : secrets
            };
        }
    }
}
