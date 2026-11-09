using System.Net;
using Kazo.Os.Ecosystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>ADR 0240: the error page shows the failure summary, folded details and its actions.</summary>
public sealed class HostFailurePanelTests
{
    [Fact]
    public async Task A_failure_shows_its_summary_folded_details_and_actions()
    {
        // Arrange
        HostFailureDetails failure = new(
            Guid.Parse("92c3aad1-620c-4762-ac57-dcf7199d9d07"),
            new DateTimeOffset(2026, 11, 8, 20, 2, 56, TimeSpan.Zero),
            "component-render",
            "Microsoft.JSInterop.JSException",
            "Failed to fetch dynamically imported module",
            "Kazo.Irc",
            "Kazo.Irc.Components.MessagePane.OnAfterRenderAsync",
            "Microsoft.JSInterop.JSException: Failed to fetch\n   at Kazo.Irc.Components.MessagePane.OnAfterRenderAsync()");

        // Act
        string html = WebUtility.HtmlDecode(await RenderAsync(failure));

        // Assert
        Assert.Contains("92c3aad1-620c-4762-ac57-dcf7199d9d07", html, StringComparison.Ordinal);
        Assert.Contains("Kazo.Irc", html, StringComparison.Ordinal);
        Assert.Contains("Kazo.Irc.Components.MessagePane.OnAfterRenderAsync", html, StringComparison.Ordinal);
        Assert.Contains("Microsoft.JSInterop.JSException", html, StringComparison.Ordinal);
        Assert.Contains("Failed to fetch dynamically imported module", html, StringComparison.Ordinal);
        Assert.Matches("<details class=\"failure-details\"[^>]*>", html);
        Assert.DoesNotContain("<details class=\"failure-details\" open", html, StringComparison.Ordinal);
        Assert.Contains("Copy details", html, StringComparison.Ordinal);
        Assert.Contains("Reload", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_failure_only_the_recorded_sentence_shows()
    {
        string html = WebUtility.HtmlDecode(await RenderAsync(null));

        Assert.Contains("/var/log/syslog", html, StringComparison.Ordinal);
        Assert.DoesNotContain("failure-details", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(HostFailureDetails? failure)
    {
        ServiceCollection services = new();
        services.AddLogging();
        // The host's texts come from its own resource manager, as ProjectInformationTests reads them.
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(EnglishLocalizer<>));
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddSingleton<IJSRuntime, NoJSRuntime>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<HostFailurePanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(HostFailurePanel.Failure)] = failure }));
            return result.ToHtmlString();
        });
    }

    private sealed class EnglishLocalizer<TResource>()
        : KazoStringLocalizer(KazoLocalization.GetResourceManager(typeof(TResource)), () => System.Globalization.CultureInfo.GetCultureInfo("en")), IStringLocalizer<TResource>;

    private sealed class TestNavigation : NavigationManager
    {
        internal TestNavigation() => Initialize("https://os.kazo.ca/", "https://os.kazo.ca/");

        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class NoJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
