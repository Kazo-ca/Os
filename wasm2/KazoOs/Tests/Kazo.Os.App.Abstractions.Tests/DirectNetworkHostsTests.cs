using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

/// <summary>ADR 0282 §4: the hosts an app declares for direct web requests, and how they match.</summary>
public sealed class DirectNetworkHostsTests
{
    [Theory]
    [InlineData("api.pcloud.com", "api.pcloud.com")]
    [InlineData("API.PCloud.com", "api.pcloud.com")]
    [InlineData("api.pcloud.com.", "api.pcloud.com")]
    [InlineData("*.example.org", "*.example.org")]
    [InlineData("*.s3.example.co.uk", "*.s3.example.co.uk")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void TryParse_accepts_a_host_name_or_a_subdomain_wildcard_in_canonical_form(string value, string expected)
    {
        Assert.True(DirectNetworkHosts.TryParse(value, out string host, out string? error), error);
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("**.example.org")]
    [InlineData("api.*.example.org")]
    [InlineData("https://api.example.com")]
    [InlineData("api.example.com:443")]
    [InlineData("api.example.com/v1")]
    [InlineData("localhost")]
    [InlineData("app.localhost")]
    [InlineData("intranet")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("-bad.example.com")]
    [InlineData("user@example.com")]
    public void TryParse_refuses_anything_that_is_not_a_public_host_name(string value)
    {
        Assert.False(DirectNetworkHosts.TryParse(value, out string host, out string? error));
        Assert.Equal(string.Empty, host);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "API.Example.com", true)]
    [InlineData("api.example.com", "api.example.com.", true)]
    [InlineData("api.example.com", "www.example.com", false)]
    [InlineData("api.example.com", "api.example.com.evil.org", false)]
    [InlineData("*.example.org", "a.example.org", true)]
    [InlineData("*.example.org", "a.b.example.org", true)]
    [InlineData("*.example.org", "example.org", false)]
    [InlineData("*.example.org", "badexample.org", false)]
    [InlineData("xn--bcher-kva.example", "bücher.example", true)]
    public void Matches_compares_the_request_host_with_one_declaration(string declaration, string host, bool expected)
    {
        Assert.Equal(expected, DirectNetworkHosts.Matches(declaration, host));
    }

    [Fact]
    public void IsAllowed_ignores_invalid_declarations_and_allows_nothing_without_any()
    {
        Assert.True(DirectNetworkHosts.IsAllowed(["*", "api.example.com"], "api.example.com"));
        Assert.False(DirectNetworkHosts.IsAllowed(["*"], "api.example.com"));
        Assert.False(DirectNetworkHosts.IsAllowed(null, "api.example.com"));
        Assert.False(DirectNetworkHosts.IsAllowed([], "api.example.com"));
    }

    [Fact]
    public void ParseValid_drops_bad_entries_and_duplicates_in_ordinal_order()
    {
        Assert.Equal(
            ["*.example.org", "api.example.com"],
            DirectNetworkHosts.ParseValid(["api.example.com", "bad", "*.example.org", "API.example.com"]));
        Assert.Null(DirectNetworkHosts.Normalize(["*"]));
    }

    [Fact]
    public void Validate_accepts_declared_hosts_with_network_direct_access()
    {
        AppManifest manifest = AppManifestValidatorTests.CreateValidManifest() with
        {
            Capabilities = [AppCapabilities.NetworkDirectAccess],
            DirectNetworkHosts = ["api.example.com", "*.example.org"]
        };

        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData("manifest.directNetworkHost.invalid", "api.example.com", "*")]
    [InlineData("manifest.directNetworkHost.duplicate", "api.example.com", "API.example.com")]
    public void Validate_refuses_an_invalid_or_repeated_host(string code, string first, string second)
    {
        AppManifest manifest = AppManifestValidatorTests.CreateValidManifest() with
        {
            Capabilities = [AppCapabilities.NetworkDirectAccess],
            DirectNetworkHosts = [first, second]
        };

        Assert.Contains(AppManifestValidator.Validate(manifest).Errors, error => error.Code == code && error.Path == "directNetworkHosts");
    }

    [Fact]
    public void Validate_refuses_declared_hosts_without_network_direct_access_and_an_empty_list()
    {
        AppManifest withoutCapability = AppManifestValidatorTests.CreateValidManifest() with { DirectNetworkHosts = ["api.example.com"] };
        AppManifest empty = AppManifestValidatorTests.CreateValidManifest() with
        {
            Capabilities = [AppCapabilities.NetworkDirectAccess],
            DirectNetworkHosts = []
        };

        Assert.Contains(AppManifestValidator.Validate(withoutCapability).Errors, error => error.Code == "manifest.directNetworkHosts.capability");
        Assert.Contains(AppManifestValidator.Validate(empty).Errors, error => error.Code == "manifest.directNetworkHosts.empty");
    }

    [Fact]
    public void DirectNetworkHosts_are_omitted_when_undeclared_and_round_trip_sorted_when_declared()
    {
        AppManifest plain = AppManifestValidatorTests.CreateValidManifest();
        Assert.DoesNotContain("directNetworkHosts", AppManifestJsonSerializer.SerializeCanonical(plain), StringComparison.Ordinal);

        string json = AppManifestJsonSerializer.SerializeCanonical(plain with
        {
            Capabilities = [AppCapabilities.NetworkDirectAccess],
            DirectNetworkHosts = ["api.example.com", "*.example.org"]
        });

        Assert.Contains("\"directNetworkHosts\":[\"*.example.org\",\"api.example.com\"]", json, StringComparison.Ordinal);
        Assert.Equal(["*.example.org", "api.example.com"], AppManifestJsonSerializer.DeserializeStrict(json).DirectNetworkHosts);
    }

    [Fact]
    public void Network_direct_access_is_a_dangerous_catalog_capability()
    {
        Assert.True(AppCapabilities.IsKnown(AppCapabilities.NetworkDirectAccess));
        Assert.True(AppCapabilities.IsDangerous(AppCapabilities.NetworkDirectAccess));
    }
}
