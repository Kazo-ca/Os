using System.Text.RegularExpressions;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// The share preview of os.kazo.ca (doc/en/wasm/dev/github-pages-deployment.md): Facebook and the
/// other link previews read the Open Graph tags of index.html without running JavaScript, and fetch
/// the image the tags name. These tests keep the tags, the image file and the service worker in step.
/// </summary>
public sealed partial class SocialPreviewTests
{
    private const string ImageAddress = "https://os.kazo.ca/social-preview.png";

    [Fact]
    public void Index_declares_the_open_graph_and_twitter_tags()
    {
        string index = File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));

        Assert.Equal("website", Meta(index, "property", "og:type"));
        Assert.Equal("Kazo OS", Meta(index, "property", "og:title"));
        Assert.False(string.IsNullOrWhiteSpace(Meta(index, "property", "og:description")));
        Assert.Equal(ImageAddress, Meta(index, "property", "og:image"));
        Assert.Equal("1280", Meta(index, "property", "og:image:width"));
        Assert.Equal("640", Meta(index, "property", "og:image:height"));
        Assert.Equal("summary_large_image", Meta(index, "name", "twitter:card"));
        Assert.Equal(ImageAddress, Meta(index, "name", "twitter:image"));
    }

    [Fact]
    public void Index_names_no_address_of_its_own_so_the_beta_keeps_its_own()
    {
        string index = File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));

        Assert.Null(Meta(index, "property", "og:url"));
        Assert.DoesNotContain("rel=\"canonical\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_is_a_png_of_the_declared_size()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Wwwroot(), "social-preview.png"));

        // PNG signature, then the IHDR chunk: width and height as big-endian 32-bit integers.
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        Assert.Equal(1280, BigEndian(bytes, 16));
        Assert.Equal(640, BigEndian(bytes, 20));
        // Facebook refuses an og:image over 8 MB; previews stay fast well under 1 MB.
        Assert.True(bytes.Length < 1024 * 1024, $"social-preview.png is {bytes.Length} bytes.");
    }

    [Fact]
    public void The_service_worker_does_not_cache_the_image_offline()
    {
        string worker = File.ReadAllText(Path.Combine(Wwwroot(), "service-worker.published.js"));
        Match exclude = Regex.Match(worker, @"const offlineAssetsExclude = \[(?<list>[^\]]*)\];");

        Assert.True(exclude.Success, "offlineAssetsExclude not found in service-worker.published.js.");
        Assert.Contains(@"/^social-preview\.png$/", exclude.Groups["list"].Value, StringComparison.Ordinal);
    }

    private static string? Meta(string html, string attribute, string key)
    {
        foreach (Match tag in MetaTag().Matches(html))
        {
            if (tag.Groups["attr"].Value == attribute && tag.Groups["key"].Value == key)
            {
                return tag.Groups["content"].Value;
            }
        }

        return null;
    }

    private static int BigEndian(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static string Wwwroot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found.");
        return Path.Combine(root, "OS", "Kazo.Os.Ecosystem", "wwwroot");
    }

    [GeneratedRegex("<meta (?<attr>property|name)=\"(?<key>[^\"]+)\" content=\"(?<content>[^\"]*)\"")]
    private static partial Regex MetaTag();
}
