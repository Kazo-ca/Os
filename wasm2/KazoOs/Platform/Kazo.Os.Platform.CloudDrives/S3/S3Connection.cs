using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kazo.Os.Platform.Core.CloudDrives.S3;

/// <summary>How the bucket appears in the address of a request.</summary>
internal enum S3AddressingStyle
{
    /// <summary>The bucket is the first segment of the path: <c>https://endpoint/bucket/key</c> (MinIO, most self-hosted services).</summary>
    Path,

    /// <summary>The bucket is part of the host name: <c>https://bucket.endpoint/key</c> (AWS).</summary>
    VirtualHost
}

/// <summary>
/// The service and the keys of an S3 account (ADR 0267 §6). Sealed in the account list under a key
/// of the user's keyring, like OAuth tokens (ADR 0269); never written in the drive list.
/// </summary>
/// <param name="Endpoint">Address of the service, e.g. <c>https://s3.us-east-1.amazonaws.com</c> or <c>https://&lt;id&gt;.r2.cloudflarestorage.com</c>.</param>
/// <param name="Region">Region the requests are signed for, e.g. <c>us-east-1</c>; <c>auto</c> for Cloudflare R2.</param>
/// <param name="Addressing">Path or virtual-host addressing.</param>
/// <param name="AccessKeyId">Access key id; not a secret, it names the key.</param>
/// <param name="SecretAccessKey">Secret access key.</param>
internal sealed record S3Connection(
    Uri Endpoint,
    string Region,
    S3AddressingStyle Addressing,
    string AccessKeyId,
    string SecretAccessKey)
{
    /// <summary>The cloud account provider id of S3 accounts, and the filesystem type of their drives.</summary>
    public const string ProviderId = "s3";

    /// <summary>The region used when the user leaves it empty, as AWS's tools do.</summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>Gets the provider's id of the account: the key and the service, never the secret.</summary>
    public string AccountKey => $"{AccessKeyId}@{Endpoint.Authority}";

    /// <summary>Gets what the account list shows: the key and the host of the service.</summary>
    public string DisplayName => $"{AccessKeyId} — {Endpoint.Host}";

    /// <summary>Prints the connection without the secret, which must never reach a log.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Endpoint = ").Append(Endpoint)
            .Append(", Region = ").Append(Region)
            .Append(", Addressing = ").Append(Addressing)
            .Append(", AccessKeyId = ").Append(AccessKeyId)
            .Append(", SecretAccessKey = ***");
        return true;
    }

    /// <summary>
    /// Builds a connection from what the user typed (option names of <c>NetworkDriveRequest</c>).
    /// </summary>
    /// <param name="endpoint">Address of the service; <c>https</c>, or <c>http</c> for <c>localhost</c> only.</param>
    /// <param name="region">Region; empty for <see cref="DefaultRegion"/>.</param>
    /// <param name="addressing"><c>path</c>, <c>virtual</c>, or empty: virtual host for <c>*.amazonaws.com</c>, path otherwise.</param>
    /// <param name="accessKeyId">Access key id.</param>
    /// <param name="secretAccessKey">Secret access key.</param>
    /// <exception cref="ArgumentException">A value is missing or not usable; the message says which.</exception>
    public static S3Connection Parse(string? endpoint, string? region, string? addressing, string? accessKeyId, string? secretAccessKey)
    {
        Uri address = ParseEndpoint(endpoint);
        string resolvedRegion = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        if (resolvedRegion.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException($"s3: '{resolvedRegion}' is not a region name (letters, digits and '-').", nameof(region));
        }

        S3AddressingStyle style = addressing?.Trim().ToLowerInvariant() switch
        {
            null or "" => address.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                ? S3AddressingStyle.VirtualHost
                : S3AddressingStyle.Path,
            "path" => S3AddressingStyle.Path,
            "virtual" or "virtual-host" or "vhost" => S3AddressingStyle.VirtualHost,
            _ => throw new ArgumentException($"s3: addressing must be 'path' or 'virtual', not '{addressing}'.", nameof(addressing))
        };

        if (string.IsNullOrWhiteSpace(accessKeyId) || accessKeyId.Trim().Any(static c => char.IsWhiteSpace(c) || c == '/'))
        {
            throw new ArgumentException("s3: the access key id is missing or contains a space or '/'.", nameof(accessKeyId));
        }

        if (string.IsNullOrEmpty(secretAccessKey))
        {
            throw new ArgumentException("s3: the secret access key is missing.", nameof(secretAccessKey));
        }

        return new S3Connection(address, resolvedRegion, style, accessKeyId.Trim(), secretAccessKey.Trim());
    }

    /// <summary>
    /// Checks a bucket name: 3 to 63 lowercase letters, digits, dots and hyphens, as S3 requires
    /// (older buckets with capitals or underscores are accepted with path addressing only).
    /// </summary>
    /// <exception cref="ArgumentException">The name is not usable.</exception>
    public void CheckBucket(string bucket)
    {
        if (bucket.Length is < 3 or > 63
            || bucket.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException($"s3: '{bucket}' is not a bucket name (3 to 63 letters, digits, '.' and '-').", nameof(bucket));
        }

        if (Addressing == S3AddressingStyle.VirtualHost
            && bucket.Any(static c => char.IsAsciiLetterUpper(c) || c == '_'))
        {
            throw new ArgumentException($"s3: '{bucket}' cannot be part of a host name; choose path addressing.", nameof(bucket));
        }
    }

    /// <summary>Serializes the connection for sealing; the caller clears the bytes.</summary>
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(
        new S3ConnectionPayload(Endpoint.AbsoluteUri, Region, Addressing, AccessKeyId, SecretAccessKey),
        S3JsonContext.Default.S3ConnectionPayload);

    /// <summary>Reads a connection unsealed from the account list; <see langword="null"/> when it is not one.</summary>
    public static S3Connection? Deserialize(byte[] payload)
    {
        try
        {
            S3ConnectionPayload? read = JsonSerializer.Deserialize(payload, S3JsonContext.Default.S3ConnectionPayload);
            return read is { Endpoint.Length: > 0, AccessKeyId.Length: > 0, SecretAccessKey.Length: > 0 }
                && Uri.TryCreate(read.Endpoint, UriKind.Absolute, out Uri? endpoint)
                ? new S3Connection(endpoint, read.Region ?? DefaultRegion, read.Addressing, read.AccessKeyId, read.SecretAccessKey)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("s3: the address of the service is missing.", nameof(endpoint));
        }

        string text = endpoint.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/")
        {
            throw new ArgumentException($"s3: '{endpoint}' is not the address of a service (https://host[:port], with no path).", nameof(endpoint));
        }

        // A page served over https cannot call http (mixed content), except the loopback that
        // browsers trust: a MinIO on the user's own computer.
        if (uri.Scheme == "http" && !uri.IsLoopback)
        {
            throw new ArgumentException($"s3: '{endpoint}' must use https (http is only for localhost).", nameof(endpoint));
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }
}

/// <summary>Plaintext of a sealed S3 connection.</summary>
internal sealed record S3ConnectionPayload(
    string Endpoint,
    string? Region,
    S3AddressingStyle Addressing,
    string AccessKeyId,
    string SecretAccessKey);

/// <summary>Source-generated JSON of the sealed S3 connection.</summary>
[JsonSerializable(typeof(S3ConnectionPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
internal sealed partial class S3JsonContext : JsonSerializerContext;
