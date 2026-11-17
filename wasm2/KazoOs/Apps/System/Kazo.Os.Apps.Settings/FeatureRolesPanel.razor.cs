using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Users &gt; Roles of the features (ADR 0247 §1): one column per role an installed
/// feature declares, a box per ordinary account an administrator ticks to grant it, and
/// « implicites (administrateur) » for an administrator, who holds every role with no line in
/// <c>/etc/kazoos/roles.config</c>. Hidden when no feature declares a role.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="KazoUserAccounts"/>. Each change needs
/// <c>users.manage</c>, and the OS asks an administrator in its own window, as <c>sudo kazorole</c> does.
/// </remarks>
public partial class FeatureRolesPanel
{
    private IReadOnlyList<KazoFeatureRole> _roles = [];
    private string? _message;
    private bool _failed;
    private bool _busy;

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets the accounts, as <see cref="UsersPanel"/> listed them (with their roles).</summary>
    [Parameter]
    public IReadOnlyList<KazoUserAccount> Accounts { get; set; } = [];

    /// <summary>Gets or sets whether the session user is an administrator, who may tick the boxes.</summary>
    [Parameter]
    public bool CanManage { get; set; }

    /// <summary>Gets or sets what runs after a role changed (the account list is read again).</summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _roles = await KazoUserAccounts.ListRolesAsync(Context, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without roles of features: the panel stays hidden.
            _roles = [];
        }
    }

    /// <summary>Grants or takes back <paramref name="role"/> after its box changed; the list is read again either way.</summary>
    private async Task ToggleAsync(KazoUserAccount account, KazoFeatureRole role, ChangeEventArgs change)
    {
        if (_busy)
        {
            return;
        }

        bool grant = change.Value is true || (change.Value is string text && bool.TryParse(text, out bool parsed) && parsed);
        _busy = true;
        try
        {
            bool changed = grant
                ? await KazoUserAccounts.GrantRoleAsync(Context, account.LoginName, role.Id, Context.CancellationToken)
                : await KazoUserAccounts.RevokeRoleAsync(Context, account.LoginName, role.Id, Context.CancellationToken);
            (_message, _failed) = changed
                ? (L[grant ? "Roles.Granted" : "Roles.Revoked", role.Title, account.LoginName].Value, false)
                : (L["Roles.Unchanged", role.Title, account.LoginName].Value, false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            (_message, _failed) = (L["Roles.Error", exception.Message].Value, true);
        }
        finally
        {
            _busy = false;
        }

        await Changed.InvokeAsync();
    }
}
