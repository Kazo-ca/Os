using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.FileSystem.DriveProviders;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Platform.Core.Tests.CloudDrives;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>
/// The form of a type a feature declares (ADR 0282 §3, §5): the OS describes it to the dialog and to
/// <c>mount</c>, checks every value against the declaration, and saves the account before the
/// provider ever sees a drive.
/// </summary>
public sealed class FeatureDriveFormTests
{
    private static readonly AppDriveFormFieldManifest[] Fields =
    [
        new("url", "Server", AppDriveFormFieldKind.Url),
        new("user", "User name", AppDriveFormFieldKind.Text),
        new("port", "Port", AppDriveFormFieldKind.Number) { Required = false },
        new("mode", "Mode", AppDriveFormFieldKind.Choice) { Required = false, Choices = ["fast", "safe"] },
        new("password", "Password", AppDriveFormFieldKind.Text) { Secret = true }
    ];

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void A_complete_form_passes()
    {
        FeatureDriveForm.Check(
            "webdav", Fields,
            Values(("url", "https://dav.example.com"), ("user", "alice"), ("port", "443"), ("mode", "safe")),
            Values(("password", "x")));
    }

    [Theory]
    [InlineData("url", "http://dav.example.com")]
    [InlineData("url", "not an address")]
    [InlineData("port", "four")]
    [InlineData("mode", "turbo")]
    [InlineData("user", "line\nbreak")]
    [InlineData("other", "x")]
    public void A_value_the_declaration_does_not_allow_is_refused(string field, string value)
    {
        Dictionary<string, string> options = Values(("url", "https://dav.example.com"), ("user", "alice"));
        options[field] = value;

        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check("webdav", Fields, options, Values(("password", "x"))));
    }

    [Fact]
    public void A_missing_required_field_or_a_value_in_the_wrong_half_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check(
            "webdav", Fields, Values(("url", "https://dav.example.com")), Values(("password", "x"))));
        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check(
            "webdav", Fields, Values(("url", "https://dav.example.com"), ("user", "a")), Values()));
        // A secret typed as a plain value would be written in the clear: refused.
        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check(
            "webdav", Fields, Values(("url", "https://dav.example.com"), ("user", "a"), ("password", "x")), Values()));
        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check(
            "webdav", Fields, Values(("url", "https://dav.example.com")), Values(("user", "a"), ("password", "x"))));
    }

    [Fact]
    public void A_value_longer_than_the_limit_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FeatureDriveForm.Check(
            "webdav", Fields, Values(("url", "https://dav.example.com"), ("user", new string('a', 4096))), Values(("password", "x"))));
    }

    [Fact]
    public void The_account_is_named_after_the_host_of_the_first_address_and_the_next_value()
    {
        Assert.Equal(
            "dav.example.com (alice)",
            FeatureDriveForm.AccountName(Fields, Values(("url", "https://dav.example.com/remote"), ("user", "alice")), "WebDAV"));
    }

    [Fact]
    public void The_type_is_described_with_its_feature_and_its_secret_fields()
    {
        using DriveProviderFixtures.Page page = new(new FakeCloudAccountTokens(), static _ => null, DriveProviderFixtures.Feature());

        KazoNetworkDriveTypeInfo info = ((IDescribedNetworkDriveSource)page.Source).Describe(CultureInfo.InvariantCulture);

        Assert.Equal(DriveProviderFixtures.Type, info.Type);
        Assert.Equal("Memory", info.DisplayName);
        Assert.Equal(KazoNetworkDriveConnection.Form, info.Connection);
        Assert.Equal(DriveProviderFixtures.FeatureId, info.ProviderFeatureId);
        Assert.Equal("Memory drives", info.ProviderFeatureName);
        Assert.True(info.IsThirdParty);
        KazoNetworkDriveField field = Assert.Single(info.Fields);
        Assert.Equal((InMemoryDriveProvider.ServiceOption, "Service", KazoNetworkDriveFieldKind.Text), (field.Name, field.Label, field.Kind));
    }

    [Fact]
    public async Task Mounting_a_form_saves_the_account_first_and_the_drive_carries_only_its_id()
    {
        using DriveProviderFixtures.Page page = new(new FakeCloudAccountTokens(), static _ => null, DriveProviderFixtures.Feature());

        NetworkDriveCreation creation = (await page.Source.CreateAsync(new NetworkDriveRequest()
        {
            Options = Values((InMemoryDriveProvider.ServiceOption, "box-1"))
        }))!;

        (string type, string name, IReadOnlyDictionary<string, string> options, IReadOnlyDictionary<string, string> secrets) = Assert.Single(page.FormAccounts);
        Assert.Equal(DriveProviderFixtures.Type, type);
        Assert.Equal("box-1", options[InMemoryDriveProvider.ServiceOption]);
        Assert.Empty(secrets);
        Assert.Equal("box-1", name);
        Assert.Equal("form-1", creation.Reference.Account);
        Assert.Empty(page.Started);
    }

    [Fact]
    public async Task A_form_that_breaks_the_declaration_saves_no_account()
    {
        using DriveProviderFixtures.Page page = new(new FakeCloudAccountTokens(), static _ => null, DriveProviderFixtures.Feature());

        await Assert.ThrowsAsync<ArgumentException>(async () => await page.Source.CreateAsync(new NetworkDriveRequest()
        {
            Options = Values(("unknown", "x"))
        }));

        Assert.Empty(page.FormAccounts);
    }
}
