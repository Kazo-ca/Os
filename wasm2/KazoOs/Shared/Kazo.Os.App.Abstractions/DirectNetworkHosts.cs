using System.Globalization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Parses and matches the hosts an app declares for direct web requests (ADR 0282 §4), written in
/// <c>manifest.json</c> as <see cref="AppManifest.DirectNetworkHosts"/>. A request in direct mode
/// leaves through the browser's <c>fetch</c>, without the Kazo server, and the OS refuses any host
/// that no declaration matches. Shared by the manifest validator, the approval window and the
/// network gateway, so the three read the same text the same way.
/// </summary>
/// <remarks>
/// <para>A declaration is either:</para>
/// <list type="bullet">
/// <item><description>an exact host name, such as <c>api.example.com</c>;</description></item>
/// <item><description>
/// a subdomain wildcard, such as <c>*.example.org</c>, which matches <c>a.example.org</c> and
/// <c>a.b.example.org</c> but not <c>example.org</c> itself (declare it too when it is needed).
/// </description></item>
/// </list>
/// <para>
/// Only the host is declared: the scheme is always <c>https</c> (the OS refuses a direct request in
/// <c>http</c>) and every port of the host is allowed. A bare wildcard (<c>*</c>), a wildcard
/// directly above a top-level domain (<c>*.com</c>), an IP literal, <c>localhost</c> and a name
/// without a dot are refused: a declaration names the real services the app joins. Names are
/// compared case-insensitively, and an internationalized name is kept in its ASCII form
/// (<c>xn--…</c>).
/// </para>
/// </remarks>
public static class DirectNetworkHosts
{
    /// <summary>The prefix of a subdomain wildcard (<c>*.</c>).</summary>
    public const string WildcardPrefix = "*.";

    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Parses one declaration and returns its canonical form: lower case, ASCII (an
    /// internationalized name becomes <c>xn--…</c>), no trailing dot.
    /// </summary>
    /// <param name="value">Text such as <c>api.example.com</c> or <c>*.example.org</c>.</param>
    /// <param name="host">The canonical declaration, or an empty string when refused.</param>
    /// <param name="error">Why <paramref name="value"/> is refused, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse(string? value, out string host, out string? error)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A direct-network host cannot be empty; write it as 'api.example.com' or '*.example.com'.";
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal) || value.IndexOfAny(['/', ':', '@', '?', '#', ' ', '[', ']']) >= 0)
        {
            error = $"Direct-network host '{value}' must be a host name only, without scheme, port or path (the scheme is always https).";
            return false;
        }

        bool wildcard = value.StartsWith(WildcardPrefix, StringComparison.Ordinal);
        string name = wildcard ? value[WildcardPrefix.Length..] : value;
        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        if (name.Contains('*', StringComparison.Ordinal))
        {
            error = $"Direct-network host '{value}' may only use a wildcard as its whole first label ('*.example.com'), never alone.";
            return false;
        }

        if (!TryCanonicalName(name, out string ascii))
        {
            error = $"Direct-network host '{value}' is not a valid host name.";
            return false;
        }

        if (ascii.All(static c => char.IsAsciiDigit(c) || c == '.'))
        {
            error = $"Direct-network host '{value}' is an IP address; declare the service's host name.";
            return false;
        }

        int dots = ascii.Count(static c => c == '.');
        if (wildcard && dots == 0)
        {
            error = $"Direct-network host '{value}' is too wide: a wildcard must stand below a registered domain ('*.example.com').";
            return false;
        }

        if (dots == 0 || ascii.EndsWith(".localhost", StringComparison.Ordinal))
        {
            error = $"Direct-network host '{value}' must be a public host name with at least one dot, not localhost.";
            return false;
        }

        host = wildcard ? WildcardPrefix + ascii : ascii;
        error = null;
        return true;
    }

    /// <summary>
    /// Determines whether <paramref name="host"/> (a request's host name) matches one canonical
    /// declaration.
    /// </summary>
    /// <param name="declaration">A declaration, as <see cref="TryParse"/> returns it.</param>
    /// <param name="host">The request's host name, in any case, Unicode or ASCII.</param>
    /// <returns><see langword="true"/> when the declaration allows the host.</returns>
    public static bool Matches(string declaration, string host)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(host);
        string trimmed = host.EndsWith('.') ? host[..^1] : host;
        if (!TryCanonicalName(trimmed, out string ascii))
        {
            return false;
        }

        if (declaration.StartsWith(WildcardPrefix, StringComparison.Ordinal))
        {
            // ".example.org": the request's host must end with it and have at least one more label.
            string domain = declaration[(WildcardPrefix.Length - 1)..];
            return ascii.Length > domain.Length && ascii.EndsWith(domain, StringComparison.Ordinal);
        }

        return string.Equals(declaration, ascii, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether any valid declaration of <paramref name="declarations"/> allows
    /// <paramref name="host"/>; invalid declarations allow nothing.
    /// </summary>
    /// <param name="declarations">The manifest's declarations, or <see langword="null"/> for none.</param>
    /// <param name="host">The request's host name.</param>
    public static bool IsAllowed(IEnumerable<string>? declarations, string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return ParseValid(declarations).Any(declaration => Matches(declaration, host));
    }

    /// <summary>
    /// Parses every declaration and drops the invalid ones, in canonical form and ordinal order,
    /// without duplicates. Used where the manifest was already validated and only the valid entries
    /// matter (approval window, network gateway).
    /// </summary>
    /// <param name="values">Declarations, or <see langword="null"/>.</param>
    public static IReadOnlyList<string> ParseValid(IEnumerable<string>? values) =>
        values is null
            ? []
            : [.. values
                .Select(static value => TryParse(value, out string host, out _) ? host : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Returns the canonical text of the valid declarations, or <see langword="null"/> when there is
    /// none, as <c>manifest.json</c> omits the property then.
    /// </summary>
    /// <param name="values">Declarations, or <see langword="null"/>.</param>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? values)
    {
        IReadOnlyList<string> hosts = ParseValid(values);
        return hosts.Count == 0 ? null : hosts;
    }

    /// <summary>Converts a host name to lower-case ASCII and checks its labels (RFC 1123).</summary>
    private static bool TryCanonicalName(string name, out string ascii)
    {
        ascii = string.Empty;
        if (name.Length == 0)
        {
            return false;
        }

        try
        {
            ascii = Idn.GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ascii.Length > MaxHostLength)
        {
            return false;
        }

        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is 0 or > MaxLabelLength
                || label[0] == '-'
                || label[^1] == '-'
                || !label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
