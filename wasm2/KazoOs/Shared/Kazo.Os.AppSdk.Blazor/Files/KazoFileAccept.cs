namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// The <see cref="KazoFileDropZone.Accept"/> filter (ADR 0287 §2): MIME types (<c>audio/mpeg</c>,
/// <c>audio/*</c>) or extensions (<c>.m3u</c>), like the <c>accept</c> attribute of an HTML file input.
/// </summary>
internal static class KazoFileAccept
{
    /// <summary>Whether a file of <paramref name="fileName"/> and <paramref name="mimeType"/> matches <paramref name="accept"/>.</summary>
    /// <param name="accept">The filter; <see langword="null"/> or empty accepts everything.</param>
    /// <param name="fileName">The file name, for the extensions.</param>
    /// <param name="mimeType">The file's MIME type, if known, for the MIME types.</param>
    public static bool Matches(IReadOnlyList<string>? accept, string fileName, string? mimeType)
    {
        if (accept is null || accept.Count == 0)
        {
            return true;
        }

        foreach (string raw in accept)
        {
            string entry = raw?.Trim() ?? string.Empty;
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry[0] == '.')
            {
                if (fileName.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.IsNullOrEmpty(mimeType))
            {
                // A MIME entry cannot match a file whose type nobody recorded.
            }
            else if (entry.EndsWith("/*", StringComparison.Ordinal))
            {
                if (mimeType.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(entry, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
