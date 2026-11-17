using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor.Settings;
using Kazo.Os.Apps.Settings;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.Settings.Tests;

/// <summary>
/// Settings ▸ Features (ADR 0249): the group of its own, the pages features add after "Installed
/// features", the <c>--page=&lt;id&gt;</c> argument, the list of installed features, and the view
/// that draws a feature's page from its own process.
/// </summary>
public sealed class FeaturesSectionTests
{
    private static readonly FeatureSettingsPageInfo AdminPage = new("ca.kazo.x86.admin", "ca.kazo.x86", "Os-x86", "Virtual machines", 1);

    [Fact]
    public void The_features_group_comes_after_applications_and_before_network()
    {
        List<string> groups = [.. SettingsWindow.AllPages.Select(static page => page.GroupKey).Distinct()];

        int features = groups.IndexOf(SettingsWindow.FeaturesGroupKey);
        Assert.Equal(groups.IndexOf("Nav.Group.Applications") + 1, features);
        Assert.Equal(groups.IndexOf("Nav.Group.Network") - 1, features);
    }

    [Fact]
    public void The_pages_of_the_features_follow_installed_features_with_the_feature_name()
    {
        FeatureSettingsPageInfo users = new("ca.kazo.x86.users", "ca.kazo.x86", "Os-x86", "Users", 2) { IconUrl = "/icon.svg" };

        IReadOnlyList<SettingsPage> pages = SettingsWindow.PagesFor(backupSupported: false, hasAppPage: false, [AdminPage, users]);

        int installed = pages.ToList().FindIndex(static page => page.Id == SettingsWindow.FeaturesPageId);
        Assert.Equal("ca.kazo.x86.admin", pages[installed + 1].Id);
        Assert.Equal("Os-x86 ▸ Virtual machines", pages[installed + 1].Label);
        Assert.True(pages[installed + 1].IsFeaturePage);
        Assert.Equal(SettingsWindow.FeaturesGroupKey, pages[installed + 2].GroupKey);
        Assert.Equal("/icon.svg", pages[installed + 2].IconUrl);
        Assert.Equal("Nav.Group.Network", pages[installed + 3].GroupKey);
    }

    [Fact]
    public void Without_a_feature_the_group_shows_installed_features_only()
    {
        SettingsPage page = Assert.Single(
            SettingsWindow.PagesFor(backupSupported: false, hasAppPage: false),
            static page => page.GroupKey == SettingsWindow.FeaturesGroupKey);

        Assert.Equal(SettingsWindow.FeaturesPageId, page.Id);
        Assert.False(page.IsFeaturePage);
    }

    [Fact]
    public void The_page_argument_names_a_feature_page_by_its_entry_point()
    {
        Assert.Equal("ca.kazo.x86.admin", SettingsWindow.ReadFeaturePageArgument(["--page=ca.kazo.x86.admin"]));
        Assert.Equal("ca.kazo.x86.users", SettingsWindow.ReadFeaturePageArgument(["--page=ca.kazo.x86.admin", "--page=ca.kazo.x86.users"]));
        Assert.Null(SettingsWindow.ReadFeaturePageArgument(["--page=desktop"]));
        Assert.Null(SettingsWindow.ReadFeaturePageArgument(["--app=ca.kazo.x86"]));
        Assert.Null(SettingsWindow.ReadPageArgument(["--page=ca.kazo.x86.admin"]));
    }

    [Fact]
    public async Task A_feature_page_opens_in_its_own_process_and_stops_when_settings_leaves_it()
    {
        FakeFeatureSettingsPages pages = new();
        await using (Harness harness = await Harness.RenderAsync<FeatureSettingsPageView>(
            Context(), pages, new() { [nameof(FeatureSettingsPageView.Page)] = AdminPage }))
        {
            Assert.Equal(("org.kazoos.settings", "ca.kazo.x86.admin"), Assert.Single(pages.Opened));
            Assert.Contains("content of the page", harness.Html(), StringComparison.Ordinal);
            Assert.Contains("Virtual machines", harness.Html(), StringComparison.Ordinal);
            Assert.Equal(0, pages.Stopped);
        }

        Assert.Equal(1, pages.Stopped);
    }

    [Fact]
    public async Task A_page_the_user_may_not_see_shows_why_instead()
    {
        FakeFeatureSettingsPages pages = new() { Denied = true };
        await using Harness harness = await Harness.RenderAsync<FeatureSettingsPageView>(
            Context(), pages, new() { [nameof(FeatureSettingsPageView.Page)] = AdminPage });

        Assert.Contains("This page is not available to you", harness.Html(), StringComparison.Ordinal);
        Assert.DoesNotContain("content of the page", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installed_features_lists_each_feature_with_its_entry_points_permissions_roles_and_dependents()
    {
        FakeAppManagement management = new(
            Install(App("ca.kazo.x86", "Os-x86", AppKind.Feature) with
            {
                Roles = [new KazoFeatureRole("x86:vm-admin", "VM administrator", "ca.kazo.x86", "Os-x86")],
                ProvidedCapabilities = ["x86.vm.use"],
            }),
            Install(App("ca.kazo.x86.admin", "Virtual machines", AppKind.SettingsPage) with { ParentId = "ca.kazo.x86", RequiresRole = "x86:vm-admin" }),
            Install(App("ca.kazo.x86.cli", "x86", AppKind.Terminal) with { ParentId = "ca.kazo.x86" }),
            Install(App("org.test.notes", "Notes", AppKind.Window)))
        {
            Dependents = [new KazoPackageDependent("org.test.vmclient", "VM client")],
        };
        await using Harness harness = await Harness.RenderAsync<InstalledFeaturesPanel>(Context(management), new FakeFeatureSettingsPages(), []);

        string html = harness.Html();
        Assert.Contains("Os-x86", html, StringComparison.Ordinal);
        Assert.Contains("Virtual machines", html, StringComparison.Ordinal);
        Assert.Contains("Settings page", html, StringComparison.Ordinal);
        Assert.Contains("role x86:vm-admin", html, StringComparison.Ordinal);
        Assert.Contains("x86.vm.use", html, StringComparison.Ordinal);
        Assert.Contains("VM administrator", html, StringComparison.Ordinal);
        Assert.Contains("VM client", html, StringComparison.Ordinal);
        Assert.Contains("sudo kazopkg remove --system ca.kazo.x86", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Notes", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_feature_installed_features_points_to_the_store()
    {
        await using Harness harness = await Harness.RenderAsync<InstalledFeaturesPanel>(
            Context(new FakeAppManagement(Install(App("org.test.notes", "Notes", AppKind.Window)))), new FakeFeatureSettingsPages(), []);

        Assert.Contains("No feature. The Kazo OS Store offers some.", harness.Html(), StringComparison.Ordinal);
    }

    private static MinimalAppExecutionContext Context(IAppManagementGateway? management = null) => new(
        AppDefinitionMapper.ToManifest(new SettingsApp()),
        userId: "alice",
        appManagement: management);

    private static KazoAppInfo App(string id, string name, AppKind kind) =>
        new(id, name, name + ".", kind, "system", null, "1.0.0", "ca.kazo", false, true, []);

    private static KazoAppInstallInfo Install(KazoAppInfo app) => new(app, KazoAppInstallState.Installed, [app.Id]);

    /// <summary>The OS side of the pages, as Settings sees it.</summary>
    private sealed class FakeFeatureSettingsPages : IFeatureSettingsPages
    {
        public bool Denied { get; init; }

        public List<(string Host, string PageId)> Opened { get; } = [];

        public int Stopped { get; set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<FeatureSettingsPageInfo> GetPages(IAppExecutionContext host) => [AdminPage];

        public Task<FeatureSettingsPageStart> OpenAsync(IAppExecutionContext host, string pageId, CancellationToken cancellationToken = default)
        {
            Opened.Add((host.Manifest.Id, pageId));
            return Task.FromResult(Denied
                ? FeatureSettingsPageStart.Denied("role")
                : FeatureSettingsPageStart.Started(new Session(this, host)));
        }

        private sealed class Session(FakeFeatureSettingsPages owner, IAppExecutionContext context) : IFeatureSettingsPageSession
        {
            public FeatureSettingsPageInfo Page => AdminPage;

            public RenderFragment Content { get; } = builder => builder.AddContent(0, "content of the page");

            public IAppExecutionContext Context => context;

            public bool HasExited { get; private set; }

            public event Action? Exited
            {
                add { }
                remove { }
            }

            public Task StopAsync()
            {
                if (!HasExited)
                {
                    HasExited = true;
                    owner.Stopped++;
                }

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => new(StopAsync());
        }
    }

    /// <summary>The installed apps and the dependents of a feature (ADR 0248).</summary>
    private sealed class FakeAppManagement(params KazoAppInstallInfo[] apps) : IAppManagementGateway
    {
        public IReadOnlyList<KazoPackageDependent> Dependents { get; init; } = [];

        public ValueTask<IReadOnlyList<KazoAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoAppInstallInfo>>(apps);

        public ValueTask<IReadOnlyList<KazoPackageDependent>> GetPackageDependentsAsync(string appId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Dependents);

        public ValueTask<IReadOnlyList<string>> UninstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Renders one Settings component inside the window's cascaded context.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;

        private Harness(ServiceProvider services, HtmlRenderer renderer)
        {
            _services = services;
            _renderer = renderer;
        }

        public static async Task<Harness> RenderAsync<TComponent>(
            IAppExecutionContext context, IFeatureSettingsPages pages, Dictionary<string, object?> parameters)
            where TComponent : IComponent
        {
            ServiceProvider services = new ServiceCollection().AddSingleton(pages).BuildServiceProvider();
            Harness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance));
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<Host>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(Host.Context)] = context,
                    [nameof(Host.Component)] = typeof(TComponent),
                    [nameof(Host.Parameters)] = parameters,
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public Type Component { get; set; } = default!;

        [Parameter]
        public Dictionary<string, object?> Parameters { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent(0, Component);
                int sequence = 1;
                foreach ((string name, object? value) in Parameters)
                {
                    child.AddComponentParameter(sequence++, name, value);
                }

                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
