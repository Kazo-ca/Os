using System.Text.RegularExpressions;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Masks the secrets a free text can carry, such as an exception message or a stack trace, before
/// it is shown on the error page, copied, or written to <c>/var/log/syslog</c> (ADR 0240).
/// </summary>
/// <remarks>
/// The code of Kazo OS is public, so a stack trace reveals nothing by itself; what can be private
/// is the user's own content that ends up inside a message. This class masks the well-known shapes
/// of a secret: the password of an address (<c>wss://user:password@host</c>), a sensitive
/// <c>key=value</c> or <c>key: value</c> pair (<c>password</c>, <c>token</c>, <c>apikey</c>...),
/// an HTTP <c>Bearer</c> value and the IRC <c>PASS</c> and <c>IDENTIFY</c> commands. File paths stay
/// visible on purpose: they explain most failures and only name the user's own files.
/// </remarks>
public static partial class SensitiveTextRedactor
{
    /// <summary>The text that replaces a masked value.</summary>
    public const string Placeholder = "***redacted***";

    /// <summary>Returns <paramref name="text"/> with every known secret shape replaced by <see cref="Placeholder"/>.</summary>
    /// <param name="text">A message, a stack trace or any other free text; <see langword="null"/> gives an empty text.</param>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = UrlCredentials().Replace(text, "${scheme}${user}:" + Placeholder + "@");
        result = SensitivePair().Replace(result, "${key}${sep}" + Placeholder);
        result = BearerValue().Replace(result, "${scheme}" + Placeholder);
        return IrcSecretCommand().Replace(result, "${command}" + Placeholder);
    }

    // scheme://user:password@ : the user name stays, the password goes.
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<user>[^\s/:@]+):[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    // password=..., access_token=..., "apiKey": "...", secret: ... (query strings, JSON, config lines).
    [GeneratedRegex(
        @"(?<key>\b(?:password|passwd|pwd|passphrase|secret|client_secret|token|access_token|refresh_token|id_token|api[_-]?key|authorization|credential|credentials|cookie|sig|signature)""?)(?<sep>\s*[=:]\s*""?)(?!\*\*\*redacted)(?!Bearer\b|Basic\b)[^\s&;,""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePair();

    // Authorization: Bearer abc.def.ghi
    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic)\s+)(?!\*\*\*redacted)[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerValue();

    // IRC: PASS <password>, PRIVMSG NickServ :IDENTIFY [account] <password>.
    [GeneratedRegex(@"(?<command>\b(?:PASS|IDENTIFY)\s+)[^\s]+(?:[ \t]+[^\s]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex IrcSecretCommand();
}
