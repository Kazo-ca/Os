using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kazo.Os.Platform.Core.CloudDrives.S3;

/// <summary>
/// AWS Signature Version 4 for S3 (ADR 0267 §6): every request to an S3 compatible service is
/// signed in the OS with the user's keys, so no server ever sees them. Written from the public
/// specification ("Authenticating Requests: Using the Authorization Header", AWS S3 API reference).
/// </summary>
/// <remarks>
/// The signed headers are <c>host</c>, <c>x-amz-content-sha256</c>, <c>x-amz-date</c> and every
/// other <c>x-amz-*</c> header (S3 requires them signed), plus any header the caller asks for. The
/// browser adds its own headers (<c>origin</c>, <c>user-agent</c>) to the request; they are not signed,
/// which S3 accepts. SHA-256 and HMAC-SHA-256 are managed in .NET on WebAssembly, so the signature
/// is the same in the browser and in tests.
/// </remarks>
internal static class S3SignatureV4
{
    /// <summary>The algorithm name of the <c>Authorization</c> header.</summary>
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The SHA-256 of an empty body, in lowercase hexadecimal.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Service = "s3";

    /// <summary>Builds the <c>Authorization</c> header value of a request.</summary>
    /// <param name="method">HTTP method, e.g. <c>GET</c>.</param>
    /// <param name="uri">The full request address; its path and query must already be URI-encoded with <see cref="Encode"/>.</param>
    /// <param name="headers">Headers to sign (names in any case); must contain <c>host</c>, <c>x-amz-date</c> and <c>x-amz-content-sha256</c>.</param>
    /// <param name="payloadHash">Lowercase hex SHA-256 of the body.</param>
    /// <param name="region">Region of the service, e.g. <c>us-east-1</c> (R2: <c>auto</c>).</param>
    /// <param name="accessKeyId">Access key id.</param>
    /// <param name="secretAccessKey">Secret access key.</param>
    /// <param name="timestamp">The time also written in <c>x-amz-date</c>.</param>
    /// <returns>The header value.</returns>
    public static string Authorize(
        string method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        string region,
        string accessKeyId,
        string secretAccessKey,
        DateTimeOffset timestamp)
    {
        SortedDictionary<string, string> canonicalHeaders = new(StringComparer.Ordinal);
        foreach ((string name, string value) in headers)
        {
            canonicalHeaders[name.ToLowerInvariant()] = CollapseSpaces(value.Trim());
        }

        string signedHeaders = string.Join(';', canonicalHeaders.Keys);
        StringBuilder canonical = new();
        canonical.Append(method).Append('\n')
            .Append(string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath).Append('\n')
            .Append(CanonicalQuery(uri.Query)).Append('\n');
        foreach ((string name, string value) in canonicalHeaders)
        {
            canonical.Append(name).Append(':').Append(value).Append('\n');
        }

        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        string date = timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string scope = $"{date}/{region}/{Service}/aws4_request";
        string stringToSign = string.Join(
            '\n',
            Algorithm,
            AmzDate(timestamp),
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));

        byte[] key = SigningKey(secretAccessKey, date, region);
        try
        {
            string signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
            return $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Formats <paramref name="timestamp"/> as <c>x-amz-date</c> wants it: <c>yyyyMMddTHHmmssZ</c>.</summary>
    public static string AmzDate(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The lowercase hex SHA-256 of a body.</summary>
    public static string PayloadHash(ReadOnlySpan<byte> body) => body.IsEmpty ? EmptyPayloadHash : Hex(SHA256.HashData(body));

    /// <summary>
    /// URI-encodes a value the way SigV4 wants: every byte except the unreserved characters
    /// <c>A-Z a-z 0-9 - _ . ~</c> becomes <c>%XX</c> (uppercase); <c>/</c> is kept when
    /// <paramref name="keepSlash"/> (an object key in a path).
    /// </summary>
    public static string Encode(string value, bool keepSlash = false)
    {
        StringBuilder builder = new(value.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~'
                || (keepSlash && c == '/'))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The query sorted by name then value; names and values are already encoded by the caller.</summary>
    private static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        List<(string Name, string Value)> pairs = [];
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            pairs.Add(equals < 0 ? (part, string.Empty) : (part[..equals], part[(equals + 1)..]));
        }

        return string.Join('&', pairs
            .OrderBy(static pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Value, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Name}={pair.Value}"));
    }

    private static byte[] SigningKey(string secretAccessKey, string date, string region)
    {
        byte[] secret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        byte[] dateKey = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(date));
        byte[] regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        byte[] serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(Service));
        byte[] signingKey = HMACSHA256.HashData(serviceKey, "aws4_request"u8);
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(dateKey);
        CryptographicOperations.ZeroMemory(regionKey);
        CryptographicOperations.ZeroMemory(serviceKey);
        return signingKey;
    }

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  ", StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        bool space = false;
        foreach (char c in value)
        {
            if (c != ' ' || !space)
            {
                builder.Append(c);
            }

            space = c == ' ';
        }

        return builder.ToString();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
