using System.Text.RegularExpressions;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// The privacy policy says loading Kazo OS calls no third-party service
/// (doc/en/wasm/dev/legal-pages.md): the start page links only its own stylesheets, and the
/// Roboto faces it uses are files of the release.
/// </summary>
public sealed partial class SelfHostedFontTests
{
    [Fact]
    public void The_start_page_links_no_stylesheet_or_script_of_another_origin()
    {
        string index = File.ReadAllText(Path.Combine(SolutionRoot(), "OS", "Kazo.Os.Ecosystem", "wwwroot", "index.html"));

        foreach (Match link in ExternalReference().Matches(index))
        {
            Assert.Fail($"index.html loads {link.Groups["url"].Value} from another origin.");
        }

        Assert.Contains("_content/Kazo.Os.Ecosystem.Host/fonts/roboto/roboto.css", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_roboto_face_points_to_a_file_of_the_release_with_its_license()
    {
        string fonts = Path.Combine(SolutionRoot(), "OS", "Kazo.Os.Ecosystem.Host", "wwwroot", "fonts", "roboto");
        string css = File.ReadAllText(Path.Combine(fonts, "roboto.css"));

        string[] files = FontUrl().Matches(css).Select(static match => match.Groups["file"].Value).ToArray();

        Assert.Equal(8, files.Length);
        Assert.All(files, file => Assert.True(File.Exists(Path.Combine(fonts, file)), file));
        Assert.DoesNotContain("http", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(fonts, "LICENSE.txt")), StringComparison.Ordinal);
    }

    private static string SolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found.");
    }

    /// <summary>A <c>link</c> or <c>script</c> whose address starts with a scheme or <c>//</c>.</summary>
    [GeneratedRegex(@"<(?:link|script)\b[^>]*\b(?:href|src)=""(?<url>(?:https?:)?//[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalReference();

    [GeneratedRegex(@"url\(\./(?<file>[^)]+\.woff2)\)")]
    private static partial Regex FontUrl();
}
