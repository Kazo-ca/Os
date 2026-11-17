using System.Globalization;
using System.Text;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>One resource of a <c>207 Multi-Status</c> answer to <c>PROPFIND</c> (RFC 4918 §9.1, RFC 4331).</summary>
/// <param name="Path">The resource's path on the server, decoded, without a trailing <c>/</c> (<c>/dav/Photos</c>).</param>
/// <param name="IsCollection">Whether it is a folder (<c>resourcetype</c> holds <c>collection</c>).</param>
/// <param name="Length">The file's <c>getcontentlength</c>, 0 when absent.</param>
/// <param name="LastModified">The file's <c>getlastmodified</c>, or <see langword="null"/>.</param>
/// <param name="ETag">The file's <c>getetag</c>, as the server writes it (quotes included), or <see langword="null"/>.</param>
public sealed record WebDavResource(string Path, bool IsCollection, long Length, DateTimeOffset? LastModified, string? ETag)
{
    /// <summary>Gets the free space of the collection (<c>quota-available-bytes</c>), or <see langword="null"/>.</summary>
    public long? QuotaAvailable { get; init; }

    /// <summary>Gets the space used under the collection (<c>quota-used-bytes</c>), or <see langword="null"/>.</summary>
    public long? QuotaUsed { get; init; }
}

/// <summary>Writes the <c>PROPFIND</c> bodies and reads the <c>207 Multi-Status</c> answers.</summary>
public static class WebDavMultistatus
{
    /// <summary>The <c>DAV:</c> namespace.</summary>
    public const string Dav = "DAV:";

    /// <summary>The body asking for what a <see cref="WebDavResource"/> holds, the quota excepted.</summary>
    public static readonly byte[] EntryProperties = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>");

    /// <summary>The body asking for the quota of a collection (RFC 4331).</summary>
    public static readonly byte[] QuotaProperties = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:quota-available-bytes/><d:quota-used-bytes/></d:prop></d:propfind>");

    private const int MaxResponses = 100_000;

    /// <summary>The largest answer read: 100 000 entries of a few hundred bytes each.</summary>
    private const int MaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>Reads a <c>207 Multi-Status</c> body.</summary>
    /// <param name="body">The XML.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The resources it describes, with their <c>200</c> properties.</returns>
    /// <exception cref="FormatException">The body is not a multistatus document.</exception>
    public static async Task<IReadOnlyList<WebDavResource>> ParseAsync(Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        WebDavXmlElement root;
        try
        {
            // The sample's own reader: the published OS has no XML library (see WebDavXml). It
            // refuses a DOCTYPE, so the untrusted server cannot expand entities (XXE).
            root = WebDavXml.Parse(await ReadTextAsync(body, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException exception)
        {
            throw new FormatException("The server's answer is not a WebDAV multistatus document.", exception);
        }

        if (root.Namespace != Dav || root.LocalName != "multistatus")
        {
            throw new FormatException("The server's answer is not a WebDAV multistatus document.");
        }

        List<WebDavResource> resources = [];
        foreach (WebDavXmlElement response in root.Elements(Dav, "response").Take(MaxResponses))
        {
            if (Read(response) is { } resource)
            {
                resources.Add(resource);
            }
        }

        return resources;
    }

    /// <summary>Decodes the path of an <c>href</c>, which may be absolute (<c>https://host/dav/a%20b</c>) or a path.</summary>
    /// <param name="href">The <c>href</c> text.</param>
    /// <returns>The decoded path without a trailing <c>/</c>; <c>/</c> stays <c>/</c>.</returns>
    public static string DecodePath(string href)
    {
        ArgumentNullException.ThrowIfNull(href);
        string path = Uri.TryCreate(href.Trim(), UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https"
            ? absolute.AbsolutePath
            : href.Trim();
        string decoded = string.Join('/', path.Split('/').Select(Uri.UnescapeDataString));
        return decoded.Length > 1 ? decoded.TrimEnd('/') : decoded;
    }

    private static WebDavResource? Read(WebDavXmlElement response)
    {
        string? href = response.Element(Dav, "href")?.Value;
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        // Only the properties the server found (status 200); the others are absent, not empty.
        WebDavXmlElement[] found = [.. response.Elements(Dav, "propstat")
            .Where(static propstat => propstat.Element(Dav, "status")?.Value.Contains(" 200 ", StringComparison.Ordinal) == true)
            .SelectMany(static propstat => propstat.Elements(Dav, "prop").SelectMany(static prop => prop.Children))];
        WebDavXmlElement? Prop(string name) => found.FirstOrDefault(element => element.Namespace == Dav && element.LocalName == name);

        bool collection = Prop("resourcetype")?.Element(Dav, "collection") is not null;
        return new WebDavResource(
            DecodePath(href),
            collection,
            collection ? 0 : ParseLong(Prop("getcontentlength")?.Value) ?? 0,
            DateTimeOffset.TryParse(Prop("getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset modified)
                ? modified
                : null,
            collection ? null : NullIfEmpty(Prop("getetag")?.Value))
        {
            QuotaAvailable = ParseLong(Prop("quota-available-bytes")?.Value),
            QuotaUsed = ParseLong(Prop("quota-used-bytes")?.Value),
        };
    }

    /// <summary>Reads the answer as UTF-8 text (RFC 4918 §8.2), at most <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string> ReadTextAsync(Stream body, CancellationToken cancellationToken)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaxBodyBytes)
            {
                throw new FormatException("The server's answer is larger than a WebDAV listing should be.");
            }

            copy.Write(buffer, 0, read);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(copy.GetBuffer(), 0, (int)copy.Length).TrimStart('\uFEFF');
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
