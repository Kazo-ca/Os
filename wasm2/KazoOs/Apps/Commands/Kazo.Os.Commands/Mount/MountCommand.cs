using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Mount;

/// <summary>
/// Implements <c>mount</c> (ADR 0181, section 12), after util-linux:
/// <list type="bullet">
/// <item><c>mount</c> or <c>mount -l</c> lists <c>/proc/mounts</c> as <c>source on point type type (options)</c>;</item>
/// <item><c>mount -t hostfs [/mnt/name]</c> opens the browser's folder dialog and mounts the chosen
/// folder of the computer as a network drive (the Enter that ran the command is the user gesture);</item>
/// <item><c>mount -t TYPE [-o account=…,scope=full|app,root=…] [/mnt/name]</c> mounts the storage of a
/// type that signs in (Dropbox, OneDrive, or one a Feature declares, ADR 0267 §11, ADR 0270, ADR 0282 §5):
/// with the account named, the only account of that service, or a new sign-in whose window the Enter opens;</item>
/// <item><c>mount -t TYPE -o field=value,… [/mnt/name]</c> mounts a type of a form (S3 or one a Feature
/// declares): <c>-o</c> carries the declared fields that are not secret, and the secret ones are asked with
/// the echo off (never on the command line, which the history keeps), or reused from <c>account=…</c>;</item>
/// <item><c>mount -o remount /mnt/name</c> asks the browser for its permission again after a reload.</item>
/// </list>
/// Exit codes follow util-linux: 0 on success, 1 for a usage error, 32 when the mount fails.
/// </summary>
public sealed class MountCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const int UsageError = 1;
    private const int MountFailure = 32;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string? type = null;
        string? options = null;
        List<string> operands = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "-l" or "--show-labels":
                    break;
                case "-t" or "--types" when index + 1 < args.Count:
                    type = args[++index];
                    break;
                case "-o" or "--options" when index + 1 < args.Count:
                    options = args[++index];
                    break;
                case { } arg when arg.StartsWith('-'):
                    await context.StandardError.WriteLineAsync(text["InvalidOption", arg]);
                    await context.StandardError.WriteLineAsync(text["Usage"]);
                    return UsageError;
                case { } operand:
                    operands.Add(operand);
                    break;
            }
        }

        if (type is null && options is null && operands.Count == 0)
        {
            return await ListAsync(context, text, cancellationToken);
        }

        Dictionary<string, string> parsed = ParseOptions(options);
        if (parsed.ContainsKey(RemountOption))
        {
            if (type is not null || parsed.Count != 1 || operands.Count != 1)
            {
                await context.StandardError.WriteLineAsync(text["Usage"]);
                return UsageError;
            }

            return await RemountAsync(context, text, Absolute(context, operands[0]), cancellationToken);
        }

        if (type is null || operands.Count > 1)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return UsageError;
        }

        string? mountPoint = operands.Count == 1 ? Absolute(context, operands[0]) : null;
        if (type == HostFolderType)
        {
            return parsed.Count == 0
                ? await MountHostFolderAsync(context, text, mountPoint, cancellationToken)
                : await UsageAsync(context, text);
        }

        // ADR 0282 §5: every type the OS offers now describes how it connects; the command names none.
        KazoNetworkDriveTypeInfo? info;
        try
        {
            info = (await KazoDriveInfo.DescribeNetworkDriveTypesAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.Type, type, StringComparison.Ordinal));
        }
        catch (Exception exception) when (IsMountFailure(exception))
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }

        return info?.Connection switch
        {
            KazoNetworkDriveConnection.OAuth => await MountCloudAsync(context, text, info, parsed, mountPoint, cancellationToken),
            KazoNetworkDriveConnection.Form => await MountFormAsync(context, text, info, parsed, mountPoint, cancellationToken),
            _ => await UnknownTypeAsync(context, text, type)
        };
    }

    private static async ValueTask<int> UnknownTypeAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, string type)
    {
        await context.StandardError.WriteLineAsync(text["UnknownType", type]);
        return MountFailure;
    }

    /// <summary>The <c>-o</c> option that asks the browser again for a drive's permission.</summary>
    private const string RemountOption = "remount";

    /// <summary><c>-o a=1,b,c=3</c> as keys and values; a key without a value has an empty one.</summary>
    internal static Dictionary<string, string> ParseOptions(string? options)
    {
        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        foreach (string option in (options ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = option.IndexOf('=', StringComparison.Ordinal);
            parsed[equals < 0 ? option : option[..equals]] = equals < 0 ? string.Empty : option[(equals + 1)..];
        }

        return parsed;
    }

    private static async ValueTask<int> UsageAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text)
    {
        await context.StandardError.WriteLineAsync(text["Usage"]);
        return UsageError;
    }

    /// <summary>
    /// <c>mount -t TYPE</c> of a type that signs in: picks the account (named, the only one, or a new
    /// sign-in) and mounts its storage. The Enter that ran the command is the gesture the sign-in window needs.
    /// </summary>
    private static async ValueTask<int> MountCloudAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        KazoNetworkDriveTypeInfo info,
        Dictionary<string, string> options,
        string? mountPoint,
        CancellationToken cancellationToken)
    {
        if (options.Keys.Any(static key => key is not ("account" or "scope" or "root")))
        {
            return await UsageAsync(context, text);
        }

        KazoCloudAccessScope? scope = options.TryGetValue("scope", out string? scopeText)
            ? scopeText switch
            {
                "full" => KazoCloudAccessScope.Full,
                "app" => KazoCloudAccessScope.AppFolder,
                _ => null
            }
            : null;
        if (scopeText is not null && scope is null)
        {
            await context.StandardError.WriteLineAsync(text["BadScope", scopeText]);
            return UsageError;
        }

        try
        {
            KazoCloudAccount? account = await PickAccountAsync(context, text, info, options.GetValueOrDefault("account"), scope, cancellationToken);
            if (account is null)
            {
                return MountFailure;
            }

            string? root = options.TryGetValue("root", out string? rootText) && rootText.Length > 0 ? rootText : null;
            KazoDriveInfo? drive = await KazoDriveInfo.MountNetworkDriveAsync(
                new KazoNetworkDriveRequest(info.Type, account.Id, root), mountPoint, cancellationToken);
            return await ReportMountedAsync(context, text, drive);
        }
        catch (Exception exception) when (IsMountFailure(exception))
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    /// <summary>The account to mount: <c>account=</c> (id or name), the only one of the service, or a new sign-in.</summary>
    private static async ValueTask<KazoCloudAccount?> PickAccountAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        KazoNetworkDriveTypeInfo info,
        string? wanted,
        KazoCloudAccessScope? scope,
        CancellationToken cancellationToken)
    {
        string type = info.Type;
        KazoCloudAccount[] accounts =
        [
            .. (await KazoCloudAccounts.GetAccountsAsync(cancellationToken))
                .Where(account => account.ProviderId == type && (scope is null || account.Scope == scope))
        ];
        if (wanted is not null)
        {
            KazoCloudAccount? named = accounts.FirstOrDefault(account => account.Id == wanted)
                ?? accounts.FirstOrDefault(account => string.Equals(account.DisplayName, wanted, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                await context.StandardError.WriteLineAsync(text["NoSuchAccount", wanted, type]);
            }

            return named;
        }

        if (accounts.Length == 1)
        {
            return accounts[0];
        }

        if (accounts.Length > 1)
        {
            await context.StandardError.WriteLineAsync(text["SeveralAccounts", type, string.Join(", ", accounts.Select(static account => account.DisplayName))]);
            return null;
        }

        // No account yet: sign in. The scope is the user's choice (ADR 0267 §8), asked when the service offers two.
        KazoCloudAccessScope chosen;
        if (scope is { } given)
        {
            chosen = given;
        }
        else if (info.Scopes.Count == 1)
        {
            chosen = info.Scopes[0];
        }
        else
        {
            await context.StandardError.WriteLineAsync(text["ScopeRequired", type]);
            return null;
        }

        await context.StandardOutput.WriteLineAsync(text["SigningIn", info.DisplayName]);
        return await KazoCloudAccounts.ConnectAsync(type, chosen, cancellationToken);
    }

    /// <summary>
    /// <c>mount -t TYPE -o field=value,…</c> of a type of a form (ADR 0270, ADR 0282 §5): <c>-o</c>
    /// carries the declared fields that are not secret, and the secret ones are asked with the echo off,
    /// one prompt per field. A secret is never read from the command line, which the history keeps; with
    /// <c>account=</c> the account's values are reused and nothing is asked.
    /// </summary>
    private static async ValueTask<int> MountFormAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        KazoNetworkDriveTypeInfo info,
        Dictionary<string, string> options,
        string? mountPoint,
        CancellationToken cancellationToken)
    {
        string? account = options.GetValueOrDefault("account");
        if (options.Keys.FirstOrDefault(key => info.Fields.Any(field => field.IsSecret && field.Name == key)) is { } secretKey)
        {
            await context.StandardError.WriteLineAsync(text["SecretOnCommandLine", secretKey]);
            return UsageError;
        }

        KazoNetworkDriveField[] plain = [.. info.Fields.Where(static field => !field.IsSecret)];
        if (options.Keys.Any(key => key != "account" && !plain.Any(field => field.Name == key))
            || (account is null && plain.Any(field => field.Required && field.Default is null && !options.ContainsKey(field.Name))))
        {
            await context.StandardError.WriteLineAsync(text["FormUsage", info.Type, FormSyntax(plain)]);
            return UsageError;
        }

        try
        {
            Dictionary<string, string>? secrets = null;
            if (account is null)
            {
                secrets = new(StringComparer.Ordinal);
                foreach (KazoNetworkDriveField field in info.Fields.Where(static field => field.IsSecret))
                {
                    string? value = await context.ReadSecretLineAsync(text["SecretPrompt", field.Label], cancellationToken);
                    if (string.IsNullOrEmpty(value))
                    {
                        if (field.Required)
                        {
                            await context.StandardError.WriteLineAsync(text["SecretRequired", field.Label]);
                            return MountFailure;
                        }

                        continue;
                    }

                    secrets[field.Name] = value;
                }
            }

            KazoNetworkDriveRequest request = new(info.Type, account)
            {
                Options = options.Where(static pair => pair.Key != "account").ToDictionary(StringComparer.Ordinal),
                Secrets = secrets
            };
            KazoDriveInfo? drive = await KazoDriveInfo.MountNetworkDriveAsync(request, mountPoint, cancellationToken);
            return await ReportMountedAsync(context, text, drive);
        }
        catch (Exception exception) when (IsMountFailure(exception))
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    /// <summary>The <c>-o</c> syntax of a form type: its required fields, then the optional ones in brackets.</summary>
    internal static string FormSyntax(IReadOnlyList<KazoNetworkDriveField> plain)
    {
        static string Field(KazoNetworkDriveField field) => field.Choices.Count > 0
            ? $"{field.Name}={string.Join('|', field.Choices.Select(static choice => choice.Value).Where(static value => value.Length > 0))}"
            : $"{field.Name}=…";

        string required = string.Join(',', plain.Where(static field => field.Required && field.Default is null).Select(Field));
        string optional = string.Join(',', plain.Where(static field => !field.Required || field.Default is not null).Select(Field).Append("account=ID"));
        return required.Length == 0 ? $"[{optional}]" : $"{required}[,{optional}]";
    }

    private static async ValueTask<int> ReportMountedAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, KazoDriveInfo? drive)
    {
        if (drive is null)
        {
            await context.StandardError.WriteLineAsync(text["Cancelled"]);
            return MountFailure;
        }

        await context.StandardOutput.WriteLineAsync(text["Mounted", drive.VolumeLabel, drive.Name]);
        return 0;
    }

    /// <summary>The failures a mount reports with exit code 32 rather than crashing the command.</summary>
    private static bool IsMountFailure(Exception exception) =>
        exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
            or Kazo.Os.Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException;

    /// <summary>Filesystem type of a folder of the computer.</summary>
    internal const string HostFolderType = "hostfs";

    private static async ValueTask<int> MountHostFolderAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string? mountPoint,
        CancellationToken cancellationToken)
    {
        try
        {
            KazoDriveInfo? drive = await KazoDriveInfo.MountHostFolderAsync(mountPoint, cancellationToken);
            if (drive is null)
            {
                await context.StandardError.WriteLineAsync(text["Cancelled"]);
                return MountFailure;
            }

            await context.StandardOutput.WriteLineAsync(text["Mounted", drive.VolumeLabel, drive.Name]);
            return 0;
        }
        catch (Exception exception) when (IsMountFailure(exception))
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    private static async ValueTask<int> RemountAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string mountPoint,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await KazoDriveInfo.ReconnectAsync(mountPoint, cancellationToken))
            {
                return 0;
            }

            await context.StandardError.WriteLineAsync(text["PermissionRefused", mountPoint]);
            return MountFailure;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    /// <summary>Prints <c>/proc/mounts</c> the way <c>mount</c> does: <c>source on point type type (options)</c>.</summary>
    private static async ValueTask<int> ListAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await KazoFile.ReadAllLinesAsync("/proc/mounts", cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }

        foreach (string line in lines)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4)
            {
                await context.StandardOutput.WriteLineAsync(
                    $"{Unescape(fields[0])} on {Unescape(fields[1])} type {fields[2]} ({fields[3]})");
            }
        }

        return 0;
    }

    private static string Unescape(string field) => field.Replace("\\040", " ", StringComparison.Ordinal);

    /// <summary>The absolute mount point, with <c>.</c> and <c>..</c> resolved like the shell's <c>cd</c>.</summary>
    internal static string Absolute(TerminalExecutionContext context, string path)
    {
        Stack<string> segments = new();
        foreach (string segment in Common.CommandIO.ResolvePath(context.WorkingDirectory, path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                segments.TryPop(out _);
            }
            else if (segment != ".")
            {
                segments.Push(segment);
            }
        }

        return "/" + string.Join('/', segments.Reverse());
    }
}
