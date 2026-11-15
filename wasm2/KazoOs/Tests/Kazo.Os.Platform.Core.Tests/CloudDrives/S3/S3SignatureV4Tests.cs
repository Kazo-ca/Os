using Kazo.Os.Platform.Core.CloudDrives.S3;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.S3;

/// <summary>
/// SigV4 (ADR 0267 §6) against the worked examples of the public AWS S3 documentation
/// ("Signature Calculations for the Authorization Header: Transferring Payload in a Single Chunk"):
/// the same keys, time and requests must give the same signatures, byte for byte.
/// </summary>
public sealed class S3SignatureV4Tests
{
    private const string AccessKeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Host = "examplebucket.s3.amazonaws.com";
    private static readonly DateTimeOffset Time = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GET", "/test.txt", "range", "bytes=0-9", S3SignatureV4.EmptyPayloadHash,
        "host;range;x-amz-content-sha256;x-amz-date", "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41")]
    [InlineData("GET", "/?lifecycle", null, null, S3SignatureV4.EmptyPayloadHash,
        "host;x-amz-content-sha256;x-amz-date", "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
    [InlineData("GET", "/?max-keys=2&prefix=J", null, null, S3SignatureV4.EmptyPayloadHash,
        "host;x-amz-content-sha256;x-amz-date", "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7")]
    public void The_documented_examples_sign_the_same(
        string method, string pathAndQuery, string? extraName, string? extraValue, string payloadHash, string signedHeaders, string signature)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Host"] = Host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = "20130524T000000Z"
        };
        if (extraName is not null)
        {
            headers[extraName] = extraValue!;
        }

        string authorization = S3SignatureV4.Authorize(
            method, new Uri($"https://{Host}{pathAndQuery}"), headers, payloadHash, "us-east-1", AccessKeyId, Secret, Time);

        Assert.Equal(
            $"AWS4-HMAC-SHA256 Credential={AccessKeyId}/20130524/us-east-1/s3/aws4_request, SignedHeaders={signedHeaders}, Signature={signature}",
            authorization);
    }

    [Fact]
    public void The_documented_put_example_signs_the_same()
    {
        byte[] body = "Welcome to Amazon S3."u8.ToArray();
        string payloadHash = S3SignatureV4.PayloadHash(body);
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["Host"] = Host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = "20130524T000000Z",
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY"
        };

        string authorization = S3SignatureV4.Authorize(
            "PUT", new Uri($"https://{Host}/{S3SignatureV4.Encode("test$file.text", keepSlash: true)}"), headers, payloadHash, "us-east-1", AccessKeyId, Secret, Time);

        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);
        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a b/c", true, "a%20b/c")]
    [InlineData("a b/c", false, "a%20b%2Fc")]
    [InlineData("été+~_.-", true, "%C3%A9t%C3%A9%2B~_.-")]
    [InlineData("50%", false, "50%25")]
    public void Values_are_encoded_as_sigv4_wants(string value, bool keepSlash, string expected) =>
        Assert.Equal(expected, S3SignatureV4.Encode(value, keepSlash));

    [Fact]
    public void Addresses_use_path_or_virtual_host_addressing_with_encoded_keys()
    {
        S3Connection path = S3Connection.Parse("https://minio.example.test:9000", "", "", "key", "secret");
        S3Connection aws = S3Connection.Parse("s3.eu-west-3.amazonaws.com", "eu-west-3", null, "key", "secret");

        Assert.Equal(S3AddressingStyle.Path, path.Addressing);
        Assert.Equal(S3AddressingStyle.VirtualHost, aws.Addressing);
        Assert.Equal(S3Connection.DefaultRegion, path.Region);
        Assert.Equal(
            "https://minio.example.test:9000/photos/2026/%C3%A9t%C3%A9%20%2B%20mer.jpg",
            S3Client.BuildUri(path, "photos", "2026/été + mer.jpg", []).AbsoluteUri);
        Assert.Equal(
            "https://photos.s3.eu-west-3.amazonaws.com/?list-type=2&prefix=a%2Fb%2F",
            S3Client.BuildUri(aws, "photos", null, [("list-type", "2"), ("prefix", "a/b/")]).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://s3.example.test", "must use https")]
    [InlineData("https://s3.example.test/bucket", "no path")]
    [InlineData("ftp://s3.example.test", "not the address")]
    [InlineData("", "address of the service is missing")]
    public void Unusable_addresses_are_refused_with_a_reason(string endpoint, string reason)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => S3Connection.Parse(endpoint, null, null, "key", "secret"));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Http_is_accepted_for_a_service_on_this_computer_and_the_secret_never_prints()
    {
        S3Connection local = S3Connection.Parse("http://localhost:9000", "us-east-1", "path", "minioadmin", "very-secret");

        Assert.Equal("http://localhost:9000/", local.Endpoint.AbsoluteUri);
        Assert.DoesNotContain("very-secret", local.ToString(), StringComparison.Ordinal);
        Assert.Equal("minioadmin@localhost:9000", local.AccountKey);
    }
}
