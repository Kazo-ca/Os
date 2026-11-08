using System.Globalization;
using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Ecosystem;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>Checks the actual rendered product information, including exact public-host matching.</summary>
public sealed class ProjectInformationTests
{
    [Theory]
    [InlineData("https://os.kazo.ca/", true, true)]
    [InlineData("https://OS.KAZO.CA/login", true, true)]
    [InlineData("https://os.kazo.ca/", false, false)]
    [InlineData("https://os.kazo.ca.example.com/", true, false)]
    [InlineData("https://hackos.kazo.ca/", true, false)]
    [InlineData("http://localhost:7240/?host=os.kazo.ca", true, false)]
    [InlineData("https://kazo-ca.github.io/HackerOs/", true, false)]
    public async Task Migration_notice_is_only_shown_on_the_public_authentication_pages(string url, bool enabled, bool visible)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation(url));
        services.AddLocalization();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectInformation>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ProjectInformation.ShowLegacyNotice)] = enabled }));
            return result.ToHtmlString();
        });
        Assert.Equal(visible, html.Contains("https://os.kazo.ca/legacy/", StringComparison.Ordinal));
        Assert.Contains("https://github.com/Kazo-ca/Os", html);
        Assert.Contains($"Kazo OS {KazoOsSdk.Version}", html);
    }

    [Theory]
    [InlineData("en", "Author:", "GitHub project:")]
    [InlineData("fr", "Auteur :", "Projet GitHub :")]
    public async Task Credits_link_the_author_and_the_repository_with_localized_labels(string culture, string author, string project)
    {
        string html = await RenderAsync("https://kazo-ca.github.io/HackerOs/", CultureInfo.GetCultureInfo(culture));

        // Scoped-CSS attributes (b-…) follow the authored ones, so match each link up to its closing bracket.
        Assert.Matches($@"{Regex.Escape(author)} <a class=""project-author"" href=""https://kazo\.ca"" target=""_blank"" rel=""noopener noreferrer""[^>]*>Benoit Robin</a>", html);
        Assert.Contains(project, html);
        Assert.Matches(@"<a class=""project-repository"" href=""https://github\.com/Kazo-ca/Os"" target=""_blank"" rel=""noopener noreferrer""[^>]*>", html);
        Assert.Contains("Kazo-ca/Os", html);
        Assert.Contains("aria-hidden=\"true\"", html);
        // The "GitHub project" line replaces the raw URL as link text (ADR 0187, section 7).
        Assert.DoesNotContain(">https://github.com/Kazo-ca/Os<", html);
    }

    [Fact]
    public async Task Without_a_release_monitor_the_footer_shows_a_local_build_and_no_status()
    {
        string html = await RenderAsync("https://os.kazo.ca/", CultureInfo.GetCultureInfo("en"));

        Assert.Contains("build dev", html);
        Assert.DoesNotContain("project-release-status", html);
    }

    [Theory]
    [InlineData("en", null, "Checking…", "is-checking")]
    [InlineData("en", """{ "version": "@running", "build": 12 }""", "Up to date", "is-up-to-date")]
    [InlineData("fr", """{ "version": "@running", "build": 12 }""", "À jour", "is-up-to-date")]
    [InlineData("en", """{ "version": "@running", "build": 15 }""", "Outdated: build 15 available", "is-outdated")]
    [InlineData("fr", """{ "version": "99.0.0", "build": 20 }""", "Obsolète : 99.0.0 (build 20) disponible", "is-outdated")]
    [InlineData("en", """{ "version": "99.0.0" }""", "Outdated: 99.0.0 available", "is-outdated")]
    [InlineData("fr", "", "État inconnu (hors ligne)", "is-unreachable")]
    public async Task The_status_next_to_the_version_says_whether_it_is_the_served_one(string culture, string? served, string text, string cssClass)
    {
        var monitor = new HostReleaseMonitor(new FixedReleaseProbe(served?.Replace("@running", KazoOsSdk.Version, StringComparison.Ordinal)), KazoOsSdk.Version, runningBuild: 12);
        if (served is not null)
        {
            await monitor.CheckAsync();
        }

        // The renderer encodes « é » and « … » as entities.
        string html = System.Net.WebUtility.HtmlDecode(await RenderAsync("https://os.kazo.ca/", CultureInfo.GetCultureInfo(culture), monitor));

        Assert.Contains($"Kazo OS {KazoOsSdk.Version}", html);
        Assert.Contains("build 12", html);
        Assert.Matches($@"<span class=""project-release-status {cssClass}"" role=""status""[^>]*>{Regex.Escape(text)}</span>", html);
    }

    private static async Task<string> RenderAsync(string url, CultureInfo culture, HostReleaseMonitor monitor)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation(url));
        services.AddSingleton(new TestCulture(culture));
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(TestCultureLocalizer<>));
        services.AddSingleton(monitor);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectInformation>(ParameterView.Empty);
            return result.ToHtmlString();
        });
    }

    private sealed class FixedReleaseProbe(string? document) : IHostReleaseProbe
    {
        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(document);
    }

    private static async Task<string> RenderAsync(string url, CultureInfo culture)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation(url));
        // The host binds its texts to the system language before sign-in, as this fixed culture does.
        services.AddSingleton(new TestCulture(culture));
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(TestCultureLocalizer<>));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectInformation>(ParameterView.Empty);
            return result.ToHtmlString();
        });
    }

    private sealed record TestCulture(CultureInfo Value);

    private sealed class TestCultureLocalizer<TResource>(TestCulture culture)
        : KazoStringLocalizer(KazoLocalization.GetResourceManager(typeof(TResource)), () => culture.Value), IStringLocalizer<TResource>;

    private sealed class TestNavigation : NavigationManager
    {
        internal TestNavigation(string url) => Initialize(new Uri(url).GetLeftPart(UriPartial.Authority) + "/", new Uri(url).AbsoluteUri);
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

}
