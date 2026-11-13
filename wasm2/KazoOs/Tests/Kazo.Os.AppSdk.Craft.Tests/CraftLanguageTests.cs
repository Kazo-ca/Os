using System.Globalization;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>The language WordCraft shows for the Kazo user's culture (WordCraft's list as the example).</summary>
public sealed class CraftLanguageTests
{
    // WordCraft 0.18.1's list (ui.language).
    private static readonly string[] Available =
    [
        "en", "zh-hans", "zh-hant", "ja", "ko", "uk", "es", "pt-br", "sr", "sr-latn", "pl", "cs", "et", "de",
        "ru", "tr", "it", "hu", "fr", "bg", "vi", "sv", "hr", "nl", "da", "ro", "ar", "sl",
    ];

    [Theory]
    [InlineData("fr-CA", "fr")]
    [InlineData("fr", "fr")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "de")]
    [InlineData("zh-CN", "zh-hans")]
    [InlineData("zh-Hans", "zh-hans")]
    [InlineData("zh-TW", "zh-hant")]
    [InlineData("zh-HK", "zh-hant")]
    [InlineData("zh-Hant", "zh-hant")]
    [InlineData("pt-PT", "pt-br")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("sr-Latn-RS", "sr-latn")]
    [InlineData("sr-Cyrl-RS", "sr")]
    public void The_user_s_language_is_used_when_WordCraft_has_it(string culture, string expected)
    {
        Assert.Equal(expected, CraftLanguage.Pick(CultureInfo.GetCultureInfo(culture), Available));
    }

    [Theory]
    [InlineData("fi-FI")]
    [InlineData("he-IL")]
    public void English_when_WordCraft_does_not_have_it(string culture)
    {
        Assert.Equal("en", CraftLanguage.Pick(CultureInfo.GetCultureInfo(culture), Available));
    }

    [Fact]
    public void English_when_WordCraft_lists_nothing()
    {
        Assert.Equal("en", CraftLanguage.Pick(CultureInfo.GetCultureInfo("fr-FR"), []));
    }
}
