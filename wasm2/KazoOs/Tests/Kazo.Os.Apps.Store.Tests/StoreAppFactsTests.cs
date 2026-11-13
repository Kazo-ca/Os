using System.Globalization;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>
/// store-ux work item 3 (ADR 0266 §4): an app's page writes its capabilities in plain language,
/// lists the file types it opens and copies a « Try it » link.
/// </summary>
public sealed class StoreAppFactsTests
{
    private static readonly IStringLocalizer French = new KazoStringLocalizer<Strings>(() => CultureInfo.GetCultureInfo("fr"));
    private static readonly IStringLocalizer English = new KazoStringLocalizer<Strings>(() => CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void Sensitive_capabilities_come_first_then_the_groups_in_a_fixed_order()
    {
        IReadOnlyList<StoreCapabilityList> groups = StoreAppFacts.DescribeCapabilities(
            [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.NetworkRealAccess, AppCapabilities.FileSystemPrivateWrite, AppCapabilities.NetworkSimulatedRead],
            English);

        Assert.Equal(
            [StoreCapabilityGroup.Sensitive, StoreCapabilityGroup.Files, StoreCapabilityGroup.Apps, StoreCapabilityGroup.Network],
            groups.Select(group => group.Group));
        StoreCapability real = Assert.Single(groups[0].Capabilities);
        Assert.Equal(AppCapabilities.NetworkRealAccess, real.Id);
        Assert.True(real.IsDangerous);
        Assert.Equal("Access the real network", real.Title);
        Assert.False(string.IsNullOrWhiteSpace(real.Risk));
        Assert.Equal(2, groups[1].Capabilities.Count);
        Assert.All(groups.Skip(1).SelectMany(group => group.Capabilities), capability => Assert.Null(capability.Risk));
    }

    [Fact]
    public void Capabilities_are_written_in_the_stores_language()
    {
        StoreCapability french = StoreAppFacts.DescribeCapabilities([AppCapabilities.AppsLaunch], French).Single().Capabilities.Single();
        StoreCapability english = StoreAppFacts.DescribeCapabilities([AppCapabilities.AppsLaunch], English).Single().Capabilities.Single();

        Assert.Equal("Lancer d'autres applications", french.Title);
        Assert.Equal("Launch other applications", english.Title);
    }

    [Fact]
    public void An_unknown_capability_is_shown_by_its_identifier_in_the_last_group()
    {
        IReadOnlyList<StoreCapabilityList> groups = StoreAppFacts.DescribeCapabilities(["bus.topic.example", AppCapabilities.ClipboardWrite, "bus.topic.example", " "], English);

        Assert.Equal([StoreCapabilityGroup.Apps, StoreCapabilityGroup.Other], groups.Select(group => group.Group));
        StoreCapability unknown = Assert.Single(groups[1].Capabilities);
        Assert.Equal("bus.topic.example", unknown.Title);
        Assert.False(unknown.IsDangerous);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void Every_capability_of_the_sdk_has_a_text_and_every_dangerous_one_a_risk(string language)
    {
        ResourceManager resources = new("Kazo.Os.Apps.Store.Resources.Strings", typeof(StoreApp).Assembly);
        CultureInfo culture = CultureInfo.GetCultureInfo(language);
        List<string> missing = [];
        foreach (string capability in AppCapabilities.All)
        {
            if (resources.GetString($"Capability.{capability}.Title", culture) is null)
            {
                missing.Add(capability + ".Title");
            }

            if (AppCapabilities.IsDangerous(capability) && resources.GetString($"Capability.{capability}.Risk", culture) is null)
            {
                missing.Add(capability + ".Risk");
            }
        }

        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_group_has_a_title_in_both_languages()
    {
        foreach (StoreCapabilityGroup group in Enum.GetValues<StoreCapabilityGroup>())
        {
            Assert.False(French["Detail.CapabilityGroup." + group].ResourceNotFound, group.ToString());
            Assert.False(English["Detail.CapabilityGroup." + group].ResourceNotFound, group.ToString());
        }
    }

    [Fact]
    public void File_types_are_each_extension_once_with_a_dot_or_the_media_type()
    {
        IReadOnlyList<string> types = StoreAppFacts.FileTypes(
        [
            new KazoAppFileHandler("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [".DOCX", "odt"], ["open", "edit"]),
            new KazoAppFileHandler(null, [".docx", ".rtf"], ["open"]),
            new KazoAppFileHandler("text/markdown", [], ["open"]),
            new KazoAppFileHandler(null, [], ["open"]),
        ]);

        Assert.Equal([".docx", ".odt", ".rtf", "text/markdown"], types);
    }

    [Fact]
    public void The_try_link_names_only_the_app_on_this_os_address()
    {
        Assert.Equal(
            "https://os.kazo.ca/?autolaunch=ca.kazo.word",
            StoreAppFacts.TryLink("https://os.kazo.ca/?autolaunch=org.kazoos.store#desktop", "ca.kazo.word"));
        Assert.Equal(
            "http://127.0.0.1:5000/os/?autolaunch=org.example.notes",
            StoreAppFacts.TryLink("http://127.0.0.1:5000/os/", "org.example.notes"));
        Assert.Throws<ArgumentException>(() => StoreAppFacts.TryLink("file:///index.html", "org.example.notes"));
        Assert.Throws<ArgumentException>(() => StoreAppFacts.TryLink("/relative", "org.example.notes"));
    }

    [Theory]
    [InlineData("official", KazoCatalogItemKind.App, true)]
    [InlineData("community", KazoCatalogItemKind.App, false)]
    [InlineData("official", KazoCatalogItemKind.ThemePack, false)]
    public void Only_an_app_of_the_official_catalog_has_a_try_link(string catalog, KazoCatalogItemKind kind, bool expected)
    {
        KazoCatalogApp app = new(
            catalog, "org.example.notes", "Notes", "productivity", [], new Dictionary<string, string>(), "MIT",
            "https://github.com/example/notes", "1.0.0", [new KazoCatalogAppVersion("1.0.0", 10, [])], KazoPackageTrust.Verified)
        {
            Kind = kind,
        };

        Assert.Equal(expected, StoreAppFacts.HasTryLink(app));
        Assert.False(StoreAppFacts.HasTryLink(null));
    }

    [Theory]
    [InlineData("https://github.com/Kazo-ca/Os-App-Word", true, "GitHub")]
    [InlineData("https://gitlab.com/example/notes", true, null)]
    [InlineData("javascript:alert(1)", false, null)]
    [InlineData("not an address", false, null)]
    public void The_source_link_is_a_web_address_named_after_its_site(string repository, bool isWeb, string? host)
    {
        Assert.Equal(isWeb, StoreAppFacts.IsWebAddress(repository));
        Assert.Equal(host, StoreAppFacts.SourceHost(repository));
    }

    [Fact]
    public void Only_a_message_that_is_not_an_error_closes_by_itself()
    {
        Assert.True(StoreWindow.ClosesByItself(isError: false));
        Assert.False(StoreWindow.ClosesByItself(isError: true));
        Assert.InRange(StoreWindow.MessageLifetime, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));
    }
}
