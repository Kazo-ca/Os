using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Kazopkg;

/// <summary>
/// <c>kazopkg install | remove | update | rollback | repair | list | info | sources | policy</c> (ADR 0182 §4,
/// ADR 0189 §6, ADR 0214): installs apps from their GitHub repository through <see cref="KazoAppPackages"/>.
/// </summary>
/// <remarks>
/// <para>
/// An app from a trusted source is installed for the session user by default
/// (<c>~/.local/share/kazoos/apps</c>), without an administrator. <c>--system</c> installs it for
/// every user, and an untrusted source or a blocked app always asks an administrator: the OS shows
/// its two-step window (a fresh sign-in, then the app, its trust level and its capabilities), every
/// time, even under <c>sudo</c> (ADR 0189 §4). <c>--allow-untrusted</c> is gone: the window replaces it.
/// </para>
/// <para>
/// <c>sources add|remove</c> and <c>policy block|unblock</c> edit <c>/etc/kazoos/app-sources.config</c>
/// and <c>/etc/kazoos/app-policy.config</c>, which asks an administrator (or nothing under <c>sudo</c>).
/// <c>list</c>, <c>info</c>, <c>sources</c> and <c>policy</c> alone only read.
/// </para>
/// <para>
/// An installed app is available at once (ADR 0214 §1); only a change to an app already loaded in
/// the page waits for a restart of Kazo OS, which the command says. <c>rollback</c> goes back to the
/// version the last update kept, <c>repair</c> finishes or undoes an interrupted install (§3, §4).
/// </para>
/// <para>
/// ADR 0248: <c>install</c> and <c>update</c> print the plan when the app pulls in dependencies
/// (each package, its version, source, size and dangerous permissions) and ask for a confirmation;
/// <c>remove</c> lists the apps that need the package and are disabled with it, and asks too.
/// <c>--yes</c> answers for the user. A missing dependency stops the installation with its id and range.
/// </para>
/// </remarks>
public sealed class KazopkgCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const string UserOption = "--user";
    private const string SystemOption = "--system";
    private const string TrustedOption = "--trusted";
    private const string YesOption = "--yes";

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            await (args.Count == 0 ? context.StandardError : context.StandardOutput).WriteLineAsync(text["Usage"]);
            return args.Count == 0 ? 2 : 0;
        }

        string verb = args[0];
        if (verb is not ("install" or "remove" or "update" or "rollback" or "repair" or "list" or "info" or "sources" or "policy"))
        {
            await context.StandardError.WriteLineAsync(text["UnknownCommand", verb]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        List<string> options = [.. args.Skip(1).Where(static arg => arg.StartsWith('-'))];
        List<string> operands = [.. args.Skip(1).Where(static arg => !arg.StartsWith('-'))];
        string? invalid = options.FirstOrDefault(option => !IsAllowed(verb, operands, option));
        if (invalid is not null || (options.Contains(UserOption) && options.Contains(SystemOption)))
        {
            await context.StandardError.WriteLineAsync(text["InvalidOption", invalid ?? SystemOption]);
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        if (!HasExpectedOperands(verb, operands))
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 2;
        }

        bool yes = options.Contains(YesOption);
        KazoPackageScope scope = options.Contains(UserOption) ? KazoPackageScope.User
            : options.Contains(SystemOption) ? KazoPackageScope.System
            : KazoPackageScope.Auto;
        try
        {
            return verb switch
            {
                "install" => await InstallAsync(context, operands[0], scope, yes, cancellationToken),
                "remove" => await RemoveAsync(context, operands[0], scope, yes, cancellationToken),
                "update" => await UpdateAsync(context, operands.Count == 0 ? null : operands[0], yes, cancellationToken),
                "rollback" => await RollbackAsync(context, operands[0], scope, cancellationToken),
                "repair" => await RepairAsync(context, options.Contains(SystemOption), cancellationToken),
                "list" => await ListAsync(context, cancellationToken),
                "info" => await InfoAsync(context, operands[0], cancellationToken),
                "sources" => await SourcesAsync(context, operands, options.Contains(TrustedOption), cancellationToken),
                _ => await PolicyAsync(context, operands, cancellationToken)
            };
        }
        catch (KazoPackageException exception)
        {
            // ADR 0260 §4: the one refusal with a sentence of its own, in the user's language.
            await context.StandardError.WriteLineAsync(exception.Code == "apps.package.feature-system-only"
                ? text["FeatureSystemOnly"].Value
                : $"kazopkg: {exception.Message}");
            return 1;
        }
        catch (NotSupportedException)
        {
            await context.StandardError.WriteLineAsync(text["Unsupported"]);
            return 1;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            await context.StandardError.WriteLineAsync($"kazopkg: {CommandText.Describe(context, exception)}");
            return 1;
        }
    }

    /// <summary>Whether <paramref name="option"/> belongs to <paramref name="verb"/>.</summary>
    private static bool IsAllowed(string verb, IReadOnlyList<string> operands, string option) => option switch
    {
        SystemOption => verb is "install" or "remove" or "rollback" or "repair",
        UserOption => verb is "install" or "remove" or "rollback",
        TrustedOption => verb == "sources" && operands.Count > 0 && operands[0] == "add",
        YesOption => verb is "install" or "remove" or "update",
        _ => false
    };

    /// <summary>Whether the operands fit the verb: the usage text is printed otherwise.</summary>
    private static bool HasExpectedOperands(string verb, IReadOnlyList<string> operands) => verb switch
    {
        "install" or "remove" or "rollback" or "info" => operands.Count == 1,
        "list" or "repair" => operands.Count == 0,
        "update" => operands.Count <= 1,
        "sources" => operands.Count == 0 || (operands.Count == 2 && operands[0] is "add" or "remove"),
        _ => operands.Count == 0
            || (operands.Count == 3 && operands[0] is "block" or "unblock" && TryParseKind(operands[1], out _))
    };

    private static bool TryParseKind(string value, out KazoPackageBlockKind kind)
    {
        (bool known, kind) = value switch
        {
            "app" => (true, KazoPackageBlockKind.App),
            "category" => (true, KazoPackageBlockKind.Category),
            "capability" => (true, KazoPackageBlockKind.Capability),
            _ => (false, KazoPackageBlockKind.App)
        };
        return known;
    }

    private static async Task<int> InstallAsync(
        TerminalExecutionContext context, string source, KazoPackageScope scope, bool yes, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteLineAsync(text["Downloading", source]);
        KazoPackageInstallReport report;
        try
        {
            // ADR 0248 §1: the plan is shown, and confirmed, before anything is written.
            KazoPackageInstallPlan? plan = await PlanAsync(source, scope, cancellationToken);
            if (plan is { HasDependencies: true })
            {
                await WritePlanAsync(context, plan);
                if (!yes && !await ConfirmAsync(context, cancellationToken))
                {
                    await context.StandardError.WriteLineAsync(text["Cancelled"]);
                    return 1;
                }
            }

            // The version the user confirmed, not a newer one published in between.
            string confirmed = plan?.Packages.LastOrDefault(static package => package.Requested) is { } requested && !source.Contains('@', StringComparison.Ordinal)
                ? $"{source}@{requested.Version}"
                : source;
            report = await KazoAppPackages.InstallAsync(confirmed, scope, cancellationToken);
        }
        catch (Exception exception) when (exception is KazoUnauthorizedAccessException
            || exception is KazoPackageException { Code: "apps.package.dependency-unresolved" or "apps.package.dependency-conflict" })
        {
            // ADR 0248 §1: a refusal or a missing dependency stops everything; nothing was installed.
            await context.StandardError.WriteLineAsync(text["InstallStopped", source, exception.Message]);
            return 1;
        }

        foreach (KazoPackageInstallReport dependency in report.Dependencies)
        {
            await WriteReportAsync(context, dependency);
        }

        await WriteReportAsync(context, report);
        foreach (string reactivated in report.Reactivated.Concat(report.ReactivatedDependents))
        {
            await context.StandardOutput.WriteLineAsync(text["Reactivated", reactivated]);
        }

        return 0;
    }

    /// <summary>The plan of an installation, or <see langword="null"/> on an OS that predates ADR 0248 (it then installs as before).</summary>
    private static async Task<KazoPackageInstallPlan?> PlanAsync(string source, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return await KazoAppPackages.PlanInstallAsync(source, scope, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Prints a plan as <c>apt</c> lists what it installs: one line per package, then its dangerous permissions.</summary>
    private static async Task WritePlanAsync(TerminalExecutionContext context, KazoPackageInstallPlan plan)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteLineAsync(text["PlanHeader"]);
        foreach (KazoPlannedPackage package in plan.Packages)
        {
            string version = package.PreviousVersion is { } from ? $"{from} -> {package.Version}" : package.Version;
            string role = package.IsFeature ? text["PlanFeature"].Value
                : package.Requested ? text["PlanRequested"].Value
                : text["PlanDependency"].Value;
            await context.StandardOutput.WriteLineAsync(text[
                "PlanPackage",
                package.Name,
                version,
                package.Source,
                FormatSize(package.SizeBytes),
                package.SystemWide ? text["ScopeSystem"].Value : text["ScopeUser"].Value,
                role]);
            if (package.DangerousCapabilities.Count > 0)
            {
                await context.StandardOutput.WriteLineAsync(text["PlanDangerous", string.Join(", ", package.DangerousCapabilities)]);
            }
        }

        foreach (string reactivated in plan.Reactivated)
        {
            await context.StandardOutput.WriteLineAsync(text["PlanReactivated", reactivated]);
        }

        if (plan.RequiresAdministrator)
        {
            await context.StandardOutput.WriteLineAsync(text["PlanAdministrator"]);
        }
    }

    /// <summary>Asks « Continuer ? [o/N] » on the terminal; an empty answer or the end of the input is no.</summary>
    private static async Task<bool> ConfirmAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteAsync(text["ConfirmPrompt"]);
        await context.StandardOutput.FlushAsync(cancellationToken);
        string? answer = (await context.StandardInput.ReadLineAsync(cancellationToken))?.Trim();
        return answer is { Length: > 0 }
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || answer.Equals(text["YesKey"], StringComparison.OrdinalIgnoreCase)
                || answer.Equals(text["Yes"], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A size as the plan prints it, in binary units.</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024d:0.#} KiB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.#} MiB"),
    };

    /// <summary>
    /// Gets whether the OS classifies a capability of the report dangerous (ADR 0246): its own list
    /// when it gave one (features' permissions included), the built-in catalog otherwise.
    /// </summary>
    private static bool IsDangerous(KazoPackageInstallReport report, string capability) =>
        report.DangerousCapabilities is { } dangerous
            ? dangerous.Contains(capability, StringComparer.Ordinal)
            : AppCapabilities.IsDangerous(capability);

    private static async Task WriteReportAsync(TerminalExecutionContext context, KazoPackageInstallReport report)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (report.Trust == KazoPackageTrust.Untrusted)
        {
            await context.StandardError.WriteLineAsync(text["Untrusted", report.Source]);
        }

        if (report.BlockedBy is not null)
        {
            await context.StandardError.WriteLineAsync(text["BlockedBy", report.BlockedBy]);
        }

        await context.StandardOutput.WriteLineAsync(report.PreviousVersion is null
            ? text["Installed", report.Name, report.Version, report.Source]
            : text["Updated", report.Name, report.PreviousVersion, report.Version]);
        await context.StandardOutput.WriteLineAsync(report.SystemWide ? text["ForEveryone"] : text["ForYou"]);
        if (report.ReplacedPersonalCopies > 0)
        {
            await context.StandardOutput.WriteLineAsync(text["ReplacedPersonalCopies", report.ReplacedPersonalCopies]);
        }

        if (report.EntryPoints.Count > 0)
        {
            await context.StandardOutput.WriteLineAsync(text["FeatureEntryPoints", string.Join(", ", report.EntryPoints)]);
        }

        if (report.Capabilities.Count > 0)
        {
            await context.StandardOutput.WriteLineAsync(text[
                "Capabilities",
                string.Join(", ", report.Capabilities.Select(capability =>
                    IsDangerous(report, capability) ? $"{capability} (!)" : capability))]);
            if (report.Capabilities.Any(capability => IsDangerous(report, capability)))
            {
                // ADR 0260 §3: the administrator's consent to the installation approved a feature's set.
                await context.StandardOutput.WriteLineAsync(text[report.IsFeature ? "FeatureApproved" : "DangerousNote"]);
            }
        }

        if (report.Signed)
        {
            await context.StandardOutput.WriteLineAsync(text["Signed"]);
        }

        await context.StandardOutput.WriteLineAsync(report.RestartRequired ? text["RestartRequired"] : text["AvailableNow"]);
    }

    private static async Task<int> RemoveAsync(
        TerminalExecutionContext context, string appId, KazoPackageScope scope, bool yes, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<KazoPackageDependent> dependents;
        try
        {
            dependents = await KazoAppPackages.GetDependentsAsync(appId, cancellationToken);
        }
        catch (NotSupportedException)
        {
            dependents = [];
        }

        if (dependents.Count > 0)
        {
            // ADR 0248 §3: they are disabled, not removed; their data stays.
            await context.StandardOutput.WriteLineAsync(text["DependentsWarning", string.Join(", ", dependents.Select(static app => app.Name))]);
            if (!yes && !await ConfirmAsync(context, cancellationToken))
            {
                await context.StandardError.WriteLineAsync(text["Cancelled"]);
                return 1;
            }
        }

        KazoInstalledPackage removed = await KazoAppPackages.RemoveAsync(appId, scope, cancellationToken);
        // Loaded: the removed app stays in the page until the next restart (ADR 0214 §1).
        await context.StandardOutput.WriteLineAsync(text[removed.Loaded ? "RemovedRestart" : "Removed", removed.AppId, removed.Version]);
        foreach (string disabled in removed.DisabledDependents)
        {
            await context.StandardOutput.WriteLineAsync(text["Disabled", disabled]);
        }

        return 0;
    }

    private static async Task<int> RollbackAsync(
        TerminalExecutionContext context, string appId, KazoPackageScope scope, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageInstallReport report = await KazoAppPackages.RollbackAsync(appId, scope, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["RolledBack", report.Name, report.PreviousVersion ?? "-", report.Version]);
        await context.StandardOutput.WriteLineAsync(report.RestartRequired ? text["RestartRequired"] : text["AvailableNow"]);
        return 0;
    }

    private static async Task<int> RepairAsync(TerminalExecutionContext context, bool system, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<string> repaired = await KazoAppPackages.RepairAsync(system, cancellationToken);
        if (repaired.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingToRepair"]);
            return 0;
        }

        foreach (string line in repaired)
        {
            await context.StandardOutput.WriteLineAsync($"kazopkg: {line}");
        }

        return 0;
    }

    private static async Task<int> UpdateAsync(TerminalExecutionContext context, string? appId, bool yes, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<KazoInstalledPackage> installed = await KazoAppPackages.GetInstalledAsync(cancellationToken);
        IReadOnlyList<KazoInstalledPackage> targets = appId is null
            ? installed
            : [.. installed.Where(package => package.AppId == appId)];
        if (appId is not null && targets.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["NotInstalled", appId]);
            return 1;
        }

        if (targets.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingInstalled"]);
            return 0;
        }

        int failures = 0;
        foreach (KazoInstalledPackage package in targets)
        {
            KazoPackageInfo info = await KazoAppPackages.GetInfoAsync(package.Source, cancellationToken);
            if (info.Latest is null || !IsNewer(info.Latest, package.Version))
            {
                await context.StandardOutput.WriteLineAsync(text["UpToDate", package.AppId, package.Version]);
                continue;
            }

            // An update keeps the copy where it is; a source no longer trusted asks an administrator again.
            // ADR 0248 §4: it follows the plan of an installation; a version out of a dependent's range is refused.
            int code = await InstallAsync(
                context,
                $"{package.Source}@{info.Latest}",
                package.SystemWide ? KazoPackageScope.System : KazoPackageScope.User,
                yes,
                cancellationToken);
            if (code != 0)
            {
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static bool IsNewer(string candidate, string current) =>
        SemanticVersion.TryParse(candidate, out SemanticVersion next)
        && (!SemanticVersion.TryParse(current, out SemanticVersion installed) || next.CompareTo(installed) > 0);

    private static async Task<int> ListAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        IReadOnlyList<KazoInstalledPackage> installed = await KazoAppPackages.GetInstalledAsync(cancellationToken);
        if (installed.Count == 0)
        {
            await context.StandardOutput.WriteLineAsync(text["NothingInstalled"]);
            return 0;
        }

        int idWidth = installed.Max(static package => package.AppId.Length);
        int versionWidth = installed.Max(static package => package.Version.Length);
        foreach (KazoInstalledPackage package in installed)
        {
            string pending = package.Loaded ? string.Empty : $" {text["PendingRestart"]}";
            pending += package.PreviousVersion is null ? string.Empty : $" {text["PreviousKept", package.PreviousVersion]}";
            string scope = package.SystemWide ? text["ScopeSystem"] : text["ScopeUser"];
            await context.StandardOutput.WriteLineAsync(
                $"{package.AppId.PadRight(idWidth)}  {package.Version.PadRight(versionWidth)}  {scope}  {package.Source}{pending}");
        }

        return 0;
    }

    private static async Task<int> InfoAsync(TerminalExecutionContext context, string source, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageInfo info = await KazoAppPackages.GetInfoAsync(source, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["InfoSource", info.Source]);
        await context.StandardOutput.WriteLineAsync(text["InfoApp", info.AppId]);
        await context.StandardOutput.WriteLineAsync(text["InfoTrusted", TrustText(text, info.Trust)]);
        await context.StandardOutput.WriteLineAsync(text["InfoSignature", info.SignatureRequired ? text["SignatureRequired"] : text["SignatureNone"]]);
        await context.StandardOutput.WriteLineAsync(text["InfoLatest", info.Latest ?? "-"]);
        await context.StandardOutput.WriteLineAsync(text["InfoVersions", string.Join(", ", info.Versions)]);
        await context.StandardOutput.WriteLineAsync(text[
            "InfoInstalled",
            info.InstalledVersion is null ? "-"
                : $"{info.InstalledVersion} ({(info.InstalledSystemWide == false ? text["ScopeUser"] : text["ScopeSystem"])})"]);
        return 0;
    }

    private static string TrustText(IStringLocalizer<Strings> text, KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => text["TrustVerified"],
        KazoPackageTrust.Trusted => text["TrustTrusted"],
        _ => text["TrustUntrusted"]
    };

    private static async Task<int> SourcesAsync(
        TerminalExecutionContext context, IReadOnlyList<string> operands, bool trusted, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        KazoPackageSources sources = operands.Count == 0
            ? await KazoAppPackages.GetSourcesAsync(cancellationToken)
            : operands[0] == "add"
                ? await KazoAppPackages.AddSourceAsync(operands[1], trusted, cancellationToken)
                : await KazoAppPackages.RemoveSourceAsync(operands[1], cancellationToken);
        if (operands.Count > 0)
        {
            await context.StandardOutput.WriteLineAsync(operands[0] == "add"
                ? text[trusted ? "SourceAddedTrusted" : "SourceAdded", operands[1]]
                : text["SourceRemoved", operands[1]]);
            return 0;
        }

        await context.StandardOutput.WriteLineAsync(text["SourcesHost", sources.GitHubHost]);
        foreach (KazoPackageCatalog catalog in sources.Catalogs)
        {
            string builtIn = catalog.BuiltIn ? $" {text["BuiltIn"]}" : string.Empty;
            await context.StandardOutput.WriteLineAsync(text[
                "SourcesCatalog", catalog.Name, catalog.Address, TrustText(text, catalog.Trust) + builtIn]);
        }

        await context.StandardOutput.WriteLineAsync(text[
            "SourcesTrusted",
            sources.TrustedSources.Count == 0 ? "-" : string.Join(", ", sources.TrustedSources.Select(source =>
                sources.BuiltInSources.Contains(source, StringComparer.OrdinalIgnoreCase) ? $"{source} {text["BuiltIn"]}" : source))]);
        await context.StandardOutput.WriteLineAsync(text[
            "SourcesUntrusted", sources.UntrustedSources.Count == 0 ? "-" : string.Join(", ", sources.UntrustedSources)]);
        await context.StandardOutput.WriteLineAsync(text["SourcesInterval", sources.UpdateIntervalHours]);
        foreach (string warning in sources.Warnings)
        {
            await context.StandardError.WriteLineAsync($"kazopkg: {warning}");
        }

        return 0;
    }

    private static async Task<int> PolicyAsync(
        TerminalExecutionContext context, IReadOnlyList<string> operands, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (operands.Count > 0)
        {
            _ = TryParseKind(operands[1], out KazoPackageBlockKind kind);
            bool blocked = operands[0] == "block";
            await KazoAppPackages.SetPolicyAsync(kind, operands[2], blocked, cancellationToken);
            await context.StandardOutput.WriteLineAsync(text[blocked ? "PolicyBlocked" : "PolicyUnblocked", operands[1], operands[2]]);
            return 0;
        }

        KazoPackagePolicy policy = await KazoAppPackages.GetPolicyAsync(cancellationToken);
        await context.StandardOutput.WriteLineAsync(text["PolicyApps", Join(policy.BlockedApps)]);
        await context.StandardOutput.WriteLineAsync(text["PolicyCategories", Join(policy.BlockedCategories)]);
        await context.StandardOutput.WriteLineAsync(text["PolicyCapabilities", Join(policy.BlockedCapabilities)]);
        foreach (string warning in policy.Warnings)
        {
            await context.StandardError.WriteLineAsync($"kazopkg: {warning}");
        }

        return 0;
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(", ", values);
}
