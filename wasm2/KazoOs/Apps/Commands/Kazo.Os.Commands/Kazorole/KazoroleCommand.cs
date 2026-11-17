using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Kazorole;

/// <summary>
/// Implements <c>kazorole list | grant &lt;user&gt; &lt;role&gt; | revoke &lt;user&gt; &lt;role&gt;</c>
/// (ADR 0247 §1). <c>list</c> shows each role a feature declares with the ordinary accounts that hold
/// it; every administrator holds them all, implicitly. <c>grant</c> and <c>revoke</c> run under
/// <c>sudo</c> only, like <c>usermod</c>, and write <c>/etc/kazoos/roles.config</c> through
/// <see cref="KazoUserAccounts"/>; a grant to an administrator changes nothing and says so (exit 0).
/// </summary>
public sealed class KazoroleCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public KazoroleCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> arguments = context.Arguments;

        try
        {
            switch (arguments)
            {
                case ["-h" or "--help"]:
                    await context.StandardOutput.WriteLineAsync(text["Usage"]);
                    return 0;
                case [] or ["list"]:
                    return await ListAsync(context, text, cancellationToken);
                case ["grant" or "revoke", string login, string role]:
                    return await ChangeAsync(context, text, arguments[0] == "grant", login, role, cancellationToken);
                default:
                    await context.StandardError.WriteLineAsync(text["Usage"]);
                    return 2;
            }
        }
        catch (UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["AuthenticationFailed"]);
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            string message = exception is ArgumentException { ParamName: { } name }
                ? exception.Message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal)
                : exception.Message;
            await context.StandardError.WriteLineAsync($"kazorole: {message}");
            return 1;
        }
    }

    /// <summary>One line per role: its id, its title and feature, then the ordinary accounts that hold it.</summary>
    private static async ValueTask<int> ListAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KazoFeatureRole> roles = await KazoUserAccounts.ListRolesAsync(context.App, cancellationToken);
        if (roles.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NoRoles"]);
            return 0;
        }

        IReadOnlyList<KazoUserAccount> accounts = await KazoUserAccounts.ListAsync(context.App, cancellationToken);
        foreach (KazoFeatureRole role in roles)
        {
            string[] holders = [.. accounts
                .Where(account => !account.IsAdministrator && account.Roles.Contains(role.Id))
                .Select(static account => account.LoginName)];
            string held = holders.Length == 0 ? text["NoHolder"] : string.Join(",", holders);
            await context.StandardOutput.WriteLineAsync($"{role.Id}\t{role.Title} ({role.FeatureName})\t{held}");
        }

        await context.StandardOutput.WriteLineAsync(text["AdministratorsImplicit"]);
        return 0;
    }

    /// <summary><c>grant</c> or <c>revoke</c>: root only; an administrator already holds every role.</summary>
    private static async ValueTask<int> ChangeAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        bool grant,
        string login,
        string role,
        CancellationToken cancellationToken)
    {
        if (!context.App.IsRootElevated)
        {
            await context.StandardError.WriteLineAsync(text["RootRequired"]);
            return 1;
        }

        IReadOnlyList<KazoUserAccount> accounts = await KazoUserAccounts.ListAsync(context.App, cancellationToken);
        KazoUserAccount? account = accounts.FirstOrDefault(
            candidate => string.Equals(candidate.LoginName, login, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            await context.StandardError.WriteLineAsync(text["UnknownUser", login]);
            return 1;
        }

        if (grant && account.IsAdministrator)
        {
            // ADR 0247, answer 233 B: nothing to write; roles.config never lists an administrator.
            await context.StandardOutput.WriteLineAsync(text["AdministratorHoldsAll", account.LoginName, role]);
            return 0;
        }

        bool changed = grant
            ? await KazoUserAccounts.GrantRoleAsync(context.App, account.LoginName, role, cancellationToken)
            : await KazoUserAccounts.RevokeRoleAsync(context.App, account.LoginName, role, cancellationToken);
        string key = (grant, changed) switch
        {
            (true, true) => "Granted",
            (true, false) => "AlreadyHeld",
            (false, true) => "Revoked",
            (false, false) => "NotHeld",
        };
        await context.StandardOutput.WriteLineAsync(text[key, account.LoginName, role]);
        return 0;
    }
}
