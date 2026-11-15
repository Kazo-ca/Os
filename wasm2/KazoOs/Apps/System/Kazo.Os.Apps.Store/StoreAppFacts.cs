using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Store;

/// <summary>Where a capability is listed on an app's page (ADR 0266 §4).</summary>
internal enum StoreCapabilityGroup
{
    /// <summary>Dangerous capabilities (ADR 0049): always first, whatever they touch.</summary>
    Sensitive,

    /// <summary>The app's own data, the user's files, the file dialogs and associations.</summary>
    Files,

    /// <summary>Other apps, windows, processes, the clipboard, notifications, screens.</summary>
    Apps,

    /// <summary>The simulated network, the real network and the server.</summary>
    Network,

    /// <summary>The machine, the accounts, the permissions and the OS components an app provides.</summary>
    System,

    /// <summary>An identifier this Store does not know (a bus topic, a newer SDK's capability).</summary>
    Other,
}

/// <summary>One capability as the app's page writes it.</summary>
/// <param name="Id">The technical identifier, kept for « See the technical identifiers ».</param>
/// <param name="Title">The plain-language text, or the identifier when the Store has none for it.</param>
/// <param name="Risk">Why it is sensitive, for a dangerous capability whose risk the Store can say.</param>
/// <param name="IsDangerous">Whether an administrator must approve it (ADR 0049 §2).</param>
internal sealed record StoreCapability(string Id, string Title, string? Risk, bool IsDangerous);

/// <summary>The capabilities of one group, in the order the page lists them.</summary>
/// <param name="Group">The group.</param>
/// <param name="Capabilities">Its capabilities.</param>
internal sealed record StoreCapabilityList(StoreCapabilityGroup Group, IReadOnlyList<StoreCapability> Capabilities);

/// <summary>
/// The facts of an app's page that the catalog gives in technical form (ADR 0266 §4): the
/// capabilities in plain language, the file types it opens and the « Try it » link. Pure functions,
/// so the tests read them without a browser.
/// </summary>
internal static class StoreAppFacts
{
    /// <summary>The catalog whose apps a « Try it » link can install (ADR 0258 §3: the official signed catalog only).</summary>
    internal const string OfficialCatalog = "official";

    /// <summary>The query parameter of a direct launch link (ADR 0258 §1).</summary>
    internal const string LaunchLinkParameter = "autolaunch";

    /// <summary>
    /// Writes <paramref name="capabilities"/> in plain language and groups them: the sensitive ones
    /// first, then files, apps, network, system and the identifiers the Store does not know. A
    /// capability without a text in the Store's language keeps its identifier as its title.
    /// </summary>
    /// <param name="capabilities">The identifiers the catalog lists.</param>
    /// <param name="text">The Store's texts (<c>Capability.&lt;id&gt;.Title</c> and <c>.Risk</c>).</param>
    internal static IReadOnlyList<StoreCapabilityList> DescribeCapabilities(IEnumerable<string> capabilities, IStringLocalizer text)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(text);
        return [.. capabilities
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => Describe(id, text))
            .GroupBy(GroupOf)
            .OrderBy(static group => group.Key)
            .Select(static group => new StoreCapabilityList(
                group.Key,
                [.. group.OrderBy(static capability => capability.Title, StringComparer.CurrentCultureIgnoreCase)]))];
    }

    /// <summary>Gets the group a capability is listed in.</summary>
    /// <param name="capability">The capability.</param>
    internal static StoreCapabilityGroup GroupOf(StoreCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.IsDangerous)
        {
            return StoreCapabilityGroup.Sensitive;
        }

        string id = capability.Id;
        if (!AppCapabilities.IsKnown(id) && !id.StartsWith("kazoos.", StringComparison.Ordinal))
        {
            return StoreCapabilityGroup.Other;
        }

        return Prefix(id) switch
        {
            "filesystem" or "dialogs" or "file-associations" => StoreCapabilityGroup.Files,
            "apps" or "process" or "windows" or "services" or "shell" or "notifications" or "clipboard" or "screens" => StoreCapabilityGroup.Apps,
            "network" or "server" => StoreCapabilityGroup.Network,
            _ => StoreCapabilityGroup.System,
        };
    }

    /// <summary>
    /// The file types an app opens, as the page shows them: each extension once, in lower case with
    /// its dot, in the catalog's order; a handler with no extension shows its media type.
    /// </summary>
    /// <param name="handlers">The <c>fileHandlers</c> of the version shown.</param>
    internal static IReadOnlyList<string> FileTypes(IEnumerable<KazoAppFileHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        List<string> types = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (KazoAppFileHandler handler in handlers)
        {
            IEnumerable<string> names = handler.Extensions.Count > 0
                ? handler.Extensions.Select(static extension => extension.StartsWith('.') ? extension : "." + extension)
                : handler.MediaType is { Length: > 0 } mediaType ? [mediaType] : [];
            foreach (string name in names)
            {
                string shown = name.Trim().ToLowerInvariant();
                if (shown.Length > 1 && seen.Add(shown))
                {
                    types.Add(shown);
                }
            }
        }

        return types;
    }

    /// <summary>Whether a « Try it » link can install this app: an app (not a theme pack) of the official catalog.</summary>
    /// <param name="app">The catalog's entry.</param>
    internal static bool HasTryLink(KazoCatalogApp? app) =>
        app is { Kind: KazoCatalogItemKind.App } && string.Equals(app.Catalog, OfficialCatalog, StringComparison.Ordinal);

    /// <summary>
    /// The « Try it » link of ADR 0258 for the OS the Store runs in: <c>&lt;page address&gt;?autolaunch=&lt;appId&gt;</c>.
    /// The page's own query and fragment are dropped: the link carries only the app id.
    /// </summary>
    /// <param name="pageAddress">The address the OS is served from, as the browser shows it.</param>
    /// <param name="appId">The app.</param>
    /// <exception cref="ArgumentException">The address is not an absolute web address.</exception>
    internal static string TryLink(string pageAddress, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!IsWebAddress(pageAddress))
        {
            throw new ArgumentException($"'{pageAddress}' is not an absolute web address.", nameof(pageAddress));
        }

        string root = new Uri(pageAddress, UriKind.Absolute).GetLeftPart(UriPartial.Path);
        return $"{root}?{LaunchLinkParameter}={Uri.EscapeDataString(appId)}";
    }

    /// <summary>The site that hosts an app's code (<c>GitHub</c> for github.com), or <see langword="null"/> for any other address.</summary>
    /// <param name="repository">The repository address the catalog gives.</param>
    internal static string? SourceHost(string? repository) =>
        Uri.TryCreate(repository, UriKind.Absolute, out Uri? address)
        && address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "GitHub"
            : null;

    /// <summary>Whether an address can be shown as a link: only http and https, never <c>javascript:</c> or a file.</summary>
    /// <param name="address">The address the catalog gives.</param>
    internal static bool IsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static StoreCapability Describe(string id, IStringLocalizer text)
    {
        LocalizedString title = text[$"Capability.{id}.Title"];
        bool dangerous = AppCapabilities.IsDangerous(id);
        LocalizedString risk = text[$"Capability.{id}.Risk"];
        return new StoreCapability(
            id,
            title.ResourceNotFound ? id : title.Value,
            dangerous && !risk.ResourceNotFound ? risk.Value : null,
            dangerous);
    }

    private static string Prefix(string id)
    {
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? id : id[..dot];
    }
}
