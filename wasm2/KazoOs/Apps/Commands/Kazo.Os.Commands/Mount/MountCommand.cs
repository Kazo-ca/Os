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
/// <item><c>mount -t dropbox|onedrive [-o account=…,scope=full|app,root=…] [/mnt/name]</c> mounts a cloud
/// storage (ADR 0267 §11, ADR 0270): with the account named, the only account of that service, or a new
/// sign-in whose window the Enter opens;</item>
/// <item><c>mount -t s3 -o endpoint=…,bucket=…[,region=…,prefix=…,addressing=path|virtual] [/mnt/name]</c>
/// asks for the access keys with the echo off (never on the command line, which the history keeps), or
/// reuses the keys of <c>account=…</c>;</item>
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
        return type switch
        {
            HostFolderType when parsed.Count == 0 => await MountHostFolderAsync(context, text, mountPoint, cancellationToken),
            HostFolderType => await UsageAsync(context, text),
            S3Type => await MountS3Async(context, text, parsed, mountPoint, cancellationToken),
            _ => await MountCloudAsync(context, text, type, parsed, mountPoint, cancellationToken)
        };
    }

    /// <summary>The <c>-o</c> option that asks the browser again for a drive's permission.</summary>
    private const string RemountOption = "remount";

    /// <summary>Filesystem type of an S3 compatible storage (ADR 0267, work item 6).</summary>
    internal const string S3Type = "s3";

    /// <summary>The <c>-o</c> keys of an S3 mount that go to the source as they are (ADR 0270).</summary>
    private static readonly string[] S3Options = ["endpoint", "region", "bucket", "prefix", "addressing"];

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
    /// <c>mount -t dropbox|onedrive</c>: picks the account (named, the only one, or a new sign-in) and
    /// mounts its storage. The Enter that ran the command is the gesture the sign-in window needs.
    /// </summary>
    private static async ValueTask<int> MountCloudAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string type,
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
            if (!(await KazoDriveInfo.GetNetworkDriveTypesAsync(cancellationToken)).Contains(type))
            {
                await context.StandardError.WriteLineAsync(text["UnknownType", type]);
                return MountFailure;
            }

            KazoCloudAccount? account = await PickAccountAsync(context, text, type, options.GetValueOrDefault("account"), scope, cancellationToken);
            if (account is null)
            {
                return MountFailure;
            }

            string? root = options.TryGetValue("root", out string? rootText) && rootText.Length > 0 ? rootText : null;
            KazoDriveInfo? drive = await KazoDriveInfo.MountNetworkDriveAsync(
                new KazoNetworkDriveRequest(type, account.Id, root), mountPoint, cancellationToken);
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
        string type,
        string? wanted,
        KazoCloudAccessScope? scope,
        CancellationToken cancellationToken)
    {
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
        KazoCloudProvider? provider = (await KazoCloudAccounts.GetProvidersAsync(cancellationToken)).FirstOrDefault(provider => provider.Id == type);
        if (provider is null)
        {
            await context.StandardError.WriteLineAsync(text["UnknownType", type]);
            return null;
        }

        KazoCloudAccessScope chosen;
        if (scope is { } given)
        {
            chosen = given;
        }
        else if (provider.Scopes.Count == 1)
        {
            chosen = provider.Scopes[0];
        }
        else
        {
            await context.StandardError.WriteLineAsync(text["ScopeRequired", type]);
            return null;
        }

        await context.StandardOutput.WriteLineAsync(text["SigningIn", provider.DisplayName]);
        return await KazoCloudAccounts.ConnectAsync(type, chosen, cancellationToken);
    }

    /// <summary><c>mount -t s3 -o endpoint=…,bucket=…</c>: the keys are asked with the echo off, or reused from <c>account=</c>.</summary>
    private static async ValueTask<int> MountS3Async(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        Dictionary<string, string> options,
        string? mountPoint,
        CancellationToken cancellationToken)
    {
        if (options.Keys.Any(static key => key != "account" && !S3Options.Contains(key))
            || !options.ContainsKey("endpoint") || !options.ContainsKey("bucket"))
        {
            await context.StandardError.WriteLineAsync(text["S3Usage"]);
            return UsageError;
        }

        try
        {
            if (!(await KazoDriveInfo.GetNetworkDriveTypesAsync(cancellationToken)).Contains(S3Type))
            {
                await context.StandardError.WriteLineAsync(text["UnknownType", S3Type]);
                return MountFailure;
            }

            string? account = options.GetValueOrDefault("account");
            Dictionary<string, string>? secrets = null;
            if (account is null)
            {
                string? keyId = await context.ReadSecretLineAsync(text["AccessKeyPrompt"], cancellationToken);
                string? secret = await context.ReadSecretLineAsync(text["SecretKeyPrompt"], cancellationToken);
                if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrEmpty(secret))
                {
                    await context.StandardError.WriteLineAsync(text["KeysRequired"]);
                    return MountFailure;
                }

                secrets = new(StringComparer.Ordinal)
                {
                    ["access-key-id"] = keyId.Trim(),
                    ["secret-access-key"] = secret
                };
            }

            KazoNetworkDriveRequest request = new(S3Type, account)
            {
                Options = options.Where(static pair => S3Options.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal),
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
