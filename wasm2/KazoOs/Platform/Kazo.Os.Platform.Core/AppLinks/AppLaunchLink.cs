using System.Text;

namespace Kazo.Os.Platform.Core.AppLinks;

/// <summary>
/// A direct app launch link of ADR 0246: <c>https://&lt;OS address&gt;/?autolaunch=&lt;appId&gt;</c>.
/// The host reads it once at boot, keeps it in memory only, and removes it from the address.
/// </summary>
/// <remarks>
/// A link is written by anyone, so it only ever carries an app id: never an address, a source, a
/// catalog or a version. What the id leads to (the official signed catalog only, an install for the
/// guest, the Store page for an account) is decided by the OS, not by the link.
/// </remarks>
public sealed record AppLaunchLink
{
    /// <summary>The query parameter that names the app (ADR 0246 question Q1: <c>autolaunch</c>).</summary>
    public const string ParameterName = "autolaunch";

    /// <summary>The longest app id a link may carry.</summary>
    public const int MaxAppIdLength = 128;

    private AppLaunchLink(string appId) => AppId = appId;

    /// <summary>Gets the app id, in reverse-domain notation (for example <c>ca.kazo.word</c>).</summary>
    public string AppId { get; }

    /// <summary>
    /// Reads the link from a page address. Only the first <c>autolaunch</c> parameter counts; a
    /// missing, empty or malformed id gives no link and the OS boots as usual.
    /// </summary>
    /// <param name="address">The page address, absolute or relative (only its query is read).</param>
    /// <param name="link">The link read, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the address carries a valid link.</returns>
    public static bool TryParse(string? address, out AppLaunchLink? link)
    {
        link = null;
        foreach ((string name, string value) in ReadQuery(address))
        {
            if (!StringComparer.Ordinal.Equals(name, ParameterName))
            {
                continue;
            }

            // ADR 0246 §1: if the parameter is repeated, only the first one counts.
            if (!IsValidAppId(value))
            {
                return false;
            }

            link = new AppLaunchLink(value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the address without any <c>autolaunch</c> parameter, every other parameter and the
    /// fragment kept as written, so reloading the page never runs the link again (ADR 0246 §2).
    /// </summary>
    /// <param name="address">The page address.</param>
    /// <returns>The address to show instead; the same string when it carries no such parameter.</returns>
    public static string RemoveFromAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int queryStart = address.IndexOf('?', StringComparison.Ordinal);
        int fragmentStart = address.IndexOf('#', StringComparison.Ordinal);
        if (queryStart < 0 || (fragmentStart >= 0 && fragmentStart < queryStart))
        {
            return address;
        }

        int queryEnd = fragmentStart >= 0 ? fragmentStart : address.Length;
        string[] parts = address[(queryStart + 1)..queryEnd].Split('&');
        string[] kept = [.. parts.Where(static part => !StringComparer.Ordinal.Equals(DecodeName(part), ParameterName))];
        if (kept.Length == parts.Length)
        {
            return address;
        }

        StringBuilder builder = new(address, 0, queryStart, address.Length);
        string[] nonEmpty = [.. kept.Where(static part => part.Length > 0)];
        if (nonEmpty.Length > 0)
        {
            builder.Append('?').AppendJoin('&', nonEmpty);
        }

        builder.Append(address, queryEnd, address.Length - queryEnd);
        return builder.ToString();
    }

    /// <summary>
    /// Checks an app id against ADR 0246 §1: reverse-domain notation (<c>a.b[.c…]</c>, no empty
    /// segment), at most <see cref="MaxAppIdLength"/> characters, only <c>a-z</c>, <c>0-9</c>,
    /// <c>.</c> and <c>-</c>.
    /// </summary>
    /// <param name="appId">The candidate id.</param>
    /// <returns><see langword="true"/> when a link may carry it.</returns>
    public static bool IsValidAppId(string? appId)
    {
        if (string.IsNullOrEmpty(appId) || appId.Length > MaxAppIdLength)
        {
            return false;
        }

        foreach (char character in appId)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
            {
                return false;
            }
        }

        string[] segments = appId.Split('.');
        return segments.Length >= 2 && segments.All(static segment => segment.Length > 0);
    }

    /// <inheritdoc />
    public override string ToString() => $"{ParameterName}={AppId}";

    private static IEnumerable<(string Name, string Value)> ReadQuery(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            yield break;
        }

        int queryStart = address.IndexOf('?', StringComparison.Ordinal);
        int fragmentStart = address.IndexOf('#', StringComparison.Ordinal);
        if (queryStart < 0 || (fragmentStart >= 0 && fragmentStart < queryStart))
        {
            yield break;
        }

        int queryEnd = fragmentStart >= 0 ? fragmentStart : address.Length;
        foreach (string part in address[(queryStart + 1)..queryEnd].Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string value = equals < 0 ? string.Empty : Decode(part[(equals + 1)..]);
            yield return (DecodeName(part), value);
        }
    }

    private static string DecodeName(string part)
    {
        int equals = part.IndexOf('=', StringComparison.Ordinal);
        return Decode(equals < 0 ? part : part[..equals]);
    }

    // A malformed escape (for example "%zz") is kept as written: the id check then refuses it.
    private static string Decode(string text)
    {
        string spaced = text.Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(spaced);
        }
        catch (UriFormatException)
        {
            return spaced;
        }
    }
}
