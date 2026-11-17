namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>The types of network drive a feature provides, in its manifest (ADR 0282 §1, §3, §4).</summary>
public sealed class FeatureDriveProviderManifestTests
{
    /// <summary>A feature that provides one OAuth drive type and one form drive type.</summary>
    public sealed class DriveFeature : FeatureDefinition
    {
        public override string Id => "ca.example.drives";
        public override string Name => "Example drives";
        public override string PublisherId => "ca.example";
        public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];
        public override IReadOnlyList<AssetManifest> Assets => [new("icons/pcloud.svg", AssetKind.Image, new string('a', 64))];

        public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
        [
            new("webdav", "WebDAV", typeof(Provider), AppDriveConnectionManifest.WithForm(
                new AppDriveFormFieldManifest("address", "Address", AppDriveFormFieldKind.Url),
                new AppDriveFormFieldManifest("password", "Password", AppDriveFormFieldKind.Text) { Secret = true }))
            {
                Hosts = ["*.example.org"],
            },
            new("pcloud", "pCloud", typeof(Provider), AppDriveConnectionManifest.WithOAuth(
                new AppDriveOAuthManifest("https://my.pcloud.com/oauth2/authorize", "https://api.pcloud.com/oauth2_token")
                {
                    FullAccess = new("public-app-id"),
                    Identity = new("https://api.pcloud.com/userinfo", "userid") { NamePaths = ["email"] },
                }))
            {
                Icon = "icons/pcloud.svg",
                Hosts = ["eapi.pcloud.com", "api.pcloud.com"],
            },
        ];
    }

    /// <summary>Stands for the provider's class; the generator, not the validator, checks its base class.</summary>
    public sealed class Provider;

    private static AppManifest Feature() => AppDefinitionMapper.ToFeatureManifests(new DriveFeature(), "3.5.0").Feature;

    private static string[] Codes(AppManifest manifest) => [.. AppManifestValidator.Validate(manifest).Errors.Select(static error => error.Code)];

    /// <summary>The feature with its first provider (<c>webdav</c> before sorting, <c>pcloud</c> after a round trip) changed.</summary>
    private static AppManifest WithOAuthProvider(Func<AppDriveProviderManifest, AppDriveProviderManifest> change)
    {
        AppManifest feature = Feature();
        return feature with
        {
            DriveProviders = [.. feature.DriveProviders!.Select(provider => provider.Type == "pcloud" ? change(provider) : provider)],
        };
    }

    [Fact]
    public void The_mapper_writes_each_provider_with_its_class_connection_and_hosts()
    {
        AppManifest feature = Feature();

        Assert.True(AppManifestValidator.Validate(feature).IsValid, string.Join("; ", Codes(feature)));
        AppDriveProviderManifest pcloud = Assert.Single(feature.DriveProviders!, static provider => provider.Type == "pcloud");
        Assert.Equal($"{typeof(Provider).FullName}, {typeof(Provider).Assembly.GetName().Name}", pcloud.Provider);
        Assert.Equal(typeof(Provider).FullName, pcloud.ProviderType);
        Assert.Equal(typeof(Provider).Assembly.GetName().Name, pcloud.ProviderAssembly);
        Assert.Equal(AppDriveConnectionKind.OAuth, pcloud.Connection.Kind);
        Assert.Equal("icons/pcloud.svg", pcloud.Icon);
    }

    [Fact]
    public void The_providers_round_trip_in_canonical_order_and_a_manifest_without_any_keeps_its_bytes()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new DriveFeature(), "3.5.0");
        string json = AppManifestJsonSerializer.SerializeCanonical(manifests.Feature);
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(json);

        Assert.Equal(["pcloud", "webdav"], read.DriveProviders!.Select(static provider => provider.Type));
        Assert.Equal(["api.pcloud.com", "eapi.pcloud.com"], read.DriveProviders![0].Hosts);
        Assert.Equal(["address", "password"], read.DriveProviders![1].Connection.Fields.Select(static field => field.Name));
        Assert.True(read.DriveProviders![1].Connection.Fields[1].Secret);
        Assert.True(read.DriveProviders![1].Connection.Fields[1].Required);
        Assert.Equal("public-app-id", read.DriveProviders![0].Connection.OAuth!.FullAccess!.ClientId);
        Assert.Contains("\"kind\":\"oauth\"", json, StringComparison.Ordinal);
        Assert.Contains("\"oauth\":{", json, StringComparison.Ordinal);
        Assert.Equal(json, AppManifestJsonSerializer.SerializeCanonical(read));
        Assert.True(AppManifestValidator.Validate(read).IsValid);

        AppManifest none = manifests.Feature with { DriveProviders = [] };
        Assert.DoesNotContain("driveProviders", AppManifestJsonSerializer.SerializeCanonical(none), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_property_inside_a_connection_is_refused()
    {
        string json = AppManifestJsonSerializer.SerializeCanonical(Feature())
            .Replace("\"tokenEndpoint\":", "\"clientSecret\":\"x\",\"tokenEndpoint\":", StringComparison.Ordinal);

        System.Text.Json.JsonException error = Assert.Throws<System.Text.Json.JsonException>(() => AppManifestJsonSerializer.DeserializeStrict(json));
        Assert.Contains("clientSecret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_feature_provides_a_drive_type_or_declares_drives_provide()
    {
        AppManifest service = Feature() with { Kind = AppKind.Service };

        Assert.Equal(2, Codes(service).Count(static code => code == "manifest.feature.only"));
    }

    [Fact]
    public void A_feature_that_provides_a_drive_type_declares_the_capability()
    {
        Assert.Contains("manifest.drive-provider.capability", Codes(Feature() with { Capabilities = [] }));
    }

    [Theory]
    [InlineData("PCloud", "manifest.drive-provider.type")]
    [InlineData("p_cloud", "manifest.drive-provider.type")]
    [InlineData("-pcloud", "manifest.drive-provider.type")]
    [InlineData("pcloud-", "manifest.drive-provider.type")]
    [InlineData("p--cloud", "manifest.drive-provider.type")]
    [InlineData("9cloud", "manifest.drive-provider.type")]
    [InlineData("a-very-long-type-name-that-goes-on", "manifest.drive-provider.type")]
    [InlineData("dropbox", "manifest.drive-provider.reserved")]
    [InlineData("hostfs", "manifest.drive-provider.reserved")]
    [InlineData("proc", "manifest.drive-provider.reserved")]
    [InlineData("webdav", "manifest.drive-provider.duplicate")]
    public void A_type_has_the_form_of_a_filesystem_type_is_not_reserved_and_is_declared_once(string type, string code)
    {
        Assert.Contains(code, Codes(WithOAuthProvider(provider => provider with { Type = type })));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("https://api.pcloud.com")]
    [InlineData("api.pcloud.com:443")]
    [InlineData("api.pcloud.com/v1")]
    [InlineData("API.pcloud.com")]
    [InlineData("api.*.com")]
    [InlineData("10.0.0.1")]
    [InlineData("localhost")]
    [InlineData("")]
    public void A_host_is_a_lowercase_name_reached_over_https_never_a_bare_wildcard(string host)
    {
        Assert.Contains("manifest.drive-provider.host", Codes(WithOAuthProvider(provider => provider with { Hosts = [host] })));
    }

    [Theory]
    [InlineData("api.pcloud.com")]
    [InlineData("*.pcloud.com")]
    [InlineData("s3.eu-west-1.example-cloud.io")]
    public void A_name_or_a_subdomain_wildcard_is_a_valid_host(string host)
    {
        Assert.True(AppDriveProviders.IsValidHost(host));
    }

    [Theory]
    [InlineData("http://api.pcloud.com/oauth2_token")]
    [InlineData("api.pcloud.com/oauth2_token")]
    [InlineData("https://user:pass@api.pcloud.com/oauth2_token")]
    public void Every_OAuth_address_is_absolute_https(string address)
    {
        AppManifest feature = WithOAuthProvider(provider => provider with
        {
            Connection = provider.Connection with { OAuth = provider.Connection.OAuth! with { TokenEndpoint = address } },
        });

        Assert.Contains("manifest.drive-provider.address", Codes(feature));
    }

    [Fact]
    public void An_OAuth_connection_offers_at_least_one_access_with_an_app_id()
    {
        AppManifest none = WithOAuthProvider(static provider => provider with
        {
            Connection = provider.Connection with { OAuth = provider.Connection.OAuth! with { FullAccess = null } },
        });
        AppManifest empty = WithOAuthProvider(static provider => provider with
        {
            Connection = provider.Connection with { OAuth = provider.Connection.OAuth! with { FullAccess = new(" ") } },
        });

        Assert.Contains("manifest.drive-provider.oauth", Codes(none));
        Assert.Contains("manifest.drive-provider.oauth", Codes(empty));
    }

    [Fact]
    public void A_connection_is_one_form_or_the_other()
    {
        AppManifest oauthWithFields = WithOAuthProvider(static provider => provider with
        {
            Connection = provider.Connection with { Fields = [new("bucket", "Bucket", AppDriveFormFieldKind.Text)] },
        });
        AppManifest formWithoutFields = WithOAuthProvider(static provider => provider with { Connection = AppDriveConnectionManifest.WithForm() });
        AppManifest oauthWithoutAddresses = WithOAuthProvider(static provider => provider with { Connection = new(AppDriveConnectionKind.OAuth) });

        Assert.Contains("manifest.drive-provider.connection", Codes(oauthWithFields));
        Assert.Contains("manifest.drive-provider.form", Codes(formWithoutFields));
        Assert.Contains("manifest.drive-provider.oauth", Codes(oauthWithoutAddresses));
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void Form_fields_are_named_labelled_unique_and_a_secret_has_no_default(AppDriveFormFieldManifest[] fields)
    {
        AppManifest feature = WithOAuthProvider(provider => provider with { Connection = AppDriveConnectionManifest.WithForm(fields) });

        Assert.Contains("manifest.drive-provider.form", Codes(feature));
    }

    public static TheoryData<AppDriveFormFieldManifest[]> InvalidFields => new()
    {
        { [new("Bucket", "Bucket", AppDriveFormFieldKind.Text)] },
        { [new("bucket", " ", AppDriveFormFieldKind.Text)] },
        { [new("bucket", "Bucket", AppDriveFormFieldKind.Text), new("bucket", "Again", AppDriveFormFieldKind.Text)] },
        { [new("key", "Key", AppDriveFormFieldKind.Text) { Secret = true, Default = "in-clear" }] },
        { [new("mode", "Mode", AppDriveFormFieldKind.Choice)] },
        { [new("mode", "Mode", AppDriveFormFieldKind.Text) { Choices = ["a"] }] },
    };

    [Fact]
    public void A_provider_has_a_name_a_class_written_with_its_assembly_and_a_declared_icon()
    {
        Assert.Contains("manifest.drive-provider.name", Codes(WithOAuthProvider(static provider => provider with { Name = "" })));
        Assert.Contains("manifest.drive-provider.class", Codes(WithOAuthProvider(static provider => provider with { Provider = "Pcloud.Provider" })));
        Assert.Contains("manifest.drive-provider.icon", Codes(WithOAuthProvider(static provider => provider with { Icon = "icons/missing.svg" })));
    }

    [Fact]
    public void The_schema_reserves_the_same_types_as_the_validator()
    {
        using System.Text.Json.JsonDocument schema = System.Text.Json.JsonDocument.Parse(ManifestSchemaResource.LoadCurrentSchemaJson());
        string[] reserved = [.. schema.RootElement
            .GetProperty("properties").GetProperty("driveProviders").GetProperty("items")
            .GetProperty("properties").GetProperty("type").GetProperty("not").GetProperty("enum")
            .EnumerateArray().Select(static item => item.GetString()!)];

        Assert.Equal(AppDriveProviders.ReservedTypes.Order(StringComparer.Ordinal), reserved.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_capability_is_dangerous_and_known()
    {
        Assert.True(AppCapabilities.IsKnown(AppCapabilities.DrivesProvide));
        Assert.True(AppCapabilities.IsDangerous(AppCapabilities.DrivesProvide));
    }
}
