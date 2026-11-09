using Kazo.Os.Platform.Blazor.Tests.Localization;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0238: an app's failure stays in its window instead of stopping the whole OS.</summary>
public sealed class AppErrorBoundaryTests
{
    [Fact]
    public async Task A_working_app_is_rendered_as_it_is()
    {
        string html = await RenderAsync(typeof(WorkingApp));

        Assert.Contains("app is fine", html, StringComparison.Ordinal);
        Assert.DoesNotContain("app-failure", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_app_shows_the_problem_in_its_window_with_the_error()
    {
        string html = System.Net.WebUtility.HtmlDecode(await RenderAsync(typeof(FailingApp)));

        Assert.DoesNotContain("app is fine", html, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"app-failure\"", html, StringComparison.Ordinal);
        Assert.Contains("\"IRC\" ran into a problem.", html, StringComparison.Ordinal);
        Assert.Contains("The rest of Kazo OS keeps running.", html, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: Failed to fetch dynamically imported module", html, StringComparison.Ordinal);
        Assert.Contains(">Try again</button>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_process_to_stop_only_retry_is_offered()
    {
        string html = await RenderAsync(typeof(FailingApp));

        Assert.DoesNotContain("app-failure-close", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_speaks_French()
    {
        string html = System.Net.WebUtility.HtmlDecode(
            await RenderAsync(typeof(FailingApp), TestLocalization.CreateSessionLocale("fr_CA.UTF-8")));

        Assert.Contains("« IRC » a rencontré un problème.", html, StringComparison.Ordinal);
        Assert.Contains(">Réessayer</button>", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(Type app, SessionLocaleService? locale = null)
    {
        ServiceCollection services = new();
        if (locale is null)
        {
            services.AddEnglishLocalizer();
        }
        else
        {
            services.AddSessionLocalizer(locale);
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent(0, app);
                builder.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<AppErrorBoundary>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AppErrorBoundary.AppName)] = "IRC",
                [nameof(AppErrorBoundary.ChildContent)] = content,
            }));
            return root.ToHtmlString();
        });
    }

    private sealed class WorkingApp : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "app is fine");
    }

    private sealed class FailingApp : ComponentBase
    {
        protected override void OnInitialized() =>
            throw new InvalidOperationException("Failed to fetch dynamically imported module: /_content/Kazo.Irc/Components/MessagePane.razor.js");

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "app is fine");
    }
}
