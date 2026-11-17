using System.Globalization;
using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Settings;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Blazor.Settings;
using Kazo.Os.Platform.Blazor.Tests.Localization;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>
/// The pages features add to Settings ▸ Features (ADR 0249): listed by role, enablement and platform,
/// drawn in the page's own context inside the OS error barrier, never with Settings' gateways.
/// </summary>
public sealed class FeatureSettingsPagesTests
{
    private const string SettingsId = "org.kazoos.settings";
    private const string AdminPageId = "org.test.vm.admin";
    private const string MinePageId = "org.test.vm.mine";

    [Fact]
    public void An_administrator_sees_every_page_of_an_enabled_feature_by_order()
    {
        Harness harness = new(AppAuthority.Administrator);

        IReadOnlyList<FeatureSettingsPageInfo> pages = harness.Pages.GetPages(harness.Settings);

        Assert.Equal([MinePageId, AdminPageId], pages.Select(static page => page.Id));
        Assert.All(pages, static page => Assert.Equal("VM feature", page.FeatureName));
        Assert.Equal("Virtual machines", pages[1].Title);
    }

    [Fact]
    public void A_page_that_requires_a_role_is_hidden_from_a_user_without_it()
    {
        Harness harness = new(AppAuthority.User);

        Assert.Equal([MinePageId], harness.Pages.GetPages(harness.Settings).Select(static page => page.Id));
    }

    [Fact]
    public void A_desktop_only_page_is_hidden_on_a_phone()
    {
        Harness harness = new(AppAuthority.Administrator, mobile: true);

        Assert.Equal([AdminPageId], harness.Pages.GetPages(harness.Settings).Select(static page => page.Id));
    }

    [Fact]
    public void Disabling_the_feature_removes_its_pages_at_once()
    {
        Harness harness = new(AppAuthority.Administrator);
        int changed = 0;
        harness.Pages.Changed += (_, _) => changed++;

        harness.Enablement.MarkDisabled(["org.test.vm"]);

        Assert.Empty(harness.Pages.GetPages(harness.Settings));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Another_app_gets_no_page_and_cannot_open_one()
    {
        Harness harness = new(AppAuthority.Administrator);
        StubContext other = new(harness.Catalog.Manifests["org.test.other"], ProcessId.FromInt64(9));

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(other, MinePageId);

        Assert.Empty(harness.Pages.GetPages(other));
        Assert.True(start.IsDenied);
        Assert.Empty(harness.Starts);
    }

    [Fact]
    public async Task A_user_without_the_role_cannot_open_the_page_by_its_id()
    {
        Harness harness = new(AppAuthority.User);

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(harness.Settings, AdminPageId);

        Assert.True(start.IsDenied);
        Assert.Empty(harness.Starts);
    }

    [Fact]
    public async Task A_page_runs_as_a_child_of_settings_in_its_own_context_and_stops_with_it()
    {
        Harness harness = new(AppAuthority.Administrator);

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(harness.Settings, MinePageId);
        string html = await RenderAsync(start.Session!.Content, gateway: true);
        await start.Session.StopAsync();
        await start.Session.StopAsync();

        Assert.True(start.IsStarted, start.Error);
        (string appId, ProcessId parent, IReadOnlyList<string> arguments) = Assert.Single(harness.Starts);
        Assert.Equal(MinePageId, appId);
        Assert.Equal(harness.Settings.ProcessId, parent);
        Assert.Equal([FeatureSettingsPages.PageArgument], arguments);
        Assert.Contains("page of " + MinePageId, html, StringComparison.Ordinal);
        Assert.Contains("no window gateway", html, StringComparison.Ordinal);
        Assert.Equal((start.Session.Context.ProcessId, ProcessExitReason.CloseRequested), Assert.Single(harness.Stops));
        Assert.True(start.Session.HasExited);
    }

    [Fact]
    public async Task A_page_that_throws_stays_in_its_area_and_only_offers_to_retry()
    {
        Harness harness = new(AppAuthority.Administrator, failingPage: true);

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(harness.Settings, MinePageId);
        string html = await RenderAsync(start.Session!.Content, gateway: false);

        Assert.Contains("data-testid=\"app-failure\"", html, StringComparison.Ordinal);
        Assert.Contains("VM feature", html, StringComparison.Ordinal);
        Assert.Contains("app-failure-retry", html, StringComparison.Ordinal);
        Assert.DoesNotContain("app-failure-close", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_whose_start_throws_fails_with_a_message_instead_of_taking_settings_down()
    {
        Harness harness = new(AppAuthority.Administrator, startThrows: true);

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(harness.Settings, MinePageId);

        Assert.False(start.IsStarted);
        Assert.False(start.IsDenied);
        Assert.Contains("process limit", start.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settings_window_of_another_user_gets_no_page()
    {
        Harness harness = new(AppAuthority.Administrator);
        StubContext otherUser = new(harness.Catalog.Manifests[SettingsId], ProcessId.FromInt64(8), userId: "someone-else");

        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(otherUser, MinePageId);

        Assert.Empty(harness.Pages.GetPages(otherUser));
        Assert.True(start.IsDenied);
        Assert.Empty(harness.Starts);
    }

    [Fact]
    public async Task The_page_reports_when_the_os_ends_its_process()
    {
        Harness harness = new(AppAuthority.Administrator);
        FeatureSettingsPageStart start = await harness.Pages.OpenAsync(harness.Settings, MinePageId);
        int exited = 0;
        start.Session!.Exited += () => exited++;

        harness.EndPages.Cancel();
        await start.Session.StopAsync();

        Assert.Equal(1, exited);
        Assert.Empty(harness.Stops);
    }

    /// <summary>Renders a page's content under a Settings-like cascade (optionally with Settings' window gateway).</summary>
    private static async Task<string> RenderAsync(RenderFragment content, bool gateway)
    {
        ServiceCollection services = new();
        services.AddEnglishLocalizer();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment root = gateway
                ? builder =>
                {
                    builder.OpenComponent<CascadingValue<IAppWindowGateway>>(0);
                    builder.AddComponentParameter(1, nameof(CascadingValue<IAppWindowGateway>.Value), DispatchProxy.Create<IAppWindowGateway, NullProxy>());
                    builder.AddComponentParameter(2, nameof(CascadingValue<IAppWindowGateway>.ChildContent), content);
                    builder.CloseComponent();
                }
                : content;
            HtmlRootComponent rendered = await renderer.RenderComponentAsync<FragmentHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(FragmentHost.Content)] = root }));
            return rendered.ToHtmlString();
        });
    }

    /// <summary>A feature with two settings pages (one requires its role, one is desktop only) and a command.</summary>
    public sealed class VmFeature(bool failingPage) : FeatureDefinition
    {
        public override string Id => "org.test.vm";
        public override string Name => "VM feature";
        public override string Version => "1.0.0";
        public override string PublisherId => "org.test";
        public override string? CapabilityPrefix => "vm";
        public override IReadOnlyList<FeatureRole> Roles => [new("vm:manager", "VM manager")];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("admin", "Virtual machines", AppKind.SettingsPage, typeof(TestPage))
            {
                RequiresRole = "vm:manager",
                SettingsPage = new SettingsPageOptions { Order = 2 },
            },
            new("mine", "My machines", AppKind.SettingsPage, failingPage ? typeof(ThrowingPage) : typeof(TestPage))
            {
                SettingsPage = new SettingsPageOptions { Order = 1, MobileSupported = false },
            },
        ];
    }

    /// <summary>A page that shows its own app id and whether it received Settings' window gateway.</summary>
    public sealed class TestPage : KazoSettingsPageBase
    {
        [CascadingParameter]
        private IAppWindowGateway? WindowGateway { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, $"page of {AppContext?.Manifest.Id}; {(WindowGateway is null ? "no window gateway" : "window gateway")}");
    }

    /// <summary>A page that throws while it renders.</summary>
    public sealed class ThrowingPage : KazoSettingsPageBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("The page failed.");
    }

    /// <summary>Renders a fragment, standing in for the Settings window.</summary>
    public sealed class FragmentHost : ComponentBase
    {
        [Parameter]
        public RenderFragment? Content { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Content);
    }

    /// <summary>A window gateway that does nothing: only its presence in cascade matters.</summary>
    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    private sealed class Harness
    {
        public Harness(AppAuthority authority, bool mobile = false, bool failingPage = false, bool startThrows = false)
        {
            FeatureManifests feature = AppDefinitionMapper.ToFeatureManifests(new VmFeature(failingPage), "3.2.0");
            AppCatalogBuildResult built = AppCatalog.Build([.. feature.All, WindowManifest(SettingsId), WindowManifest("org.test.other")], []);
            Assert.True(built.Catalog is not null, string.Join("; ", built.Errors.Select(static error => error.ToString())));
            Catalog = built.Catalog;
            Enablement = new AppEnablementRegistry(Catalog);
            AuthenticatedPrincipal principal = CreatePrincipal(authority);
            Settings = new StubContext(Catalog.Manifests[SettingsId], ProcessId.FromInt64(7), userId: principal.UserId.ToString());
            long nextPid = 100;
            Pages = new FeatureSettingsPages(
                Catalog,
                Enablement,
                new StubSessionService(principal),
                static () => null,
                static () => CultureInfo.InvariantCulture,
                () => mobile,
                (appId, _) => Task.FromResult<(AppDescriptor?, string?)>(
                    (new AppDescriptor(Catalog.Manifests[appId], failingPage && appId == MinePageId ? typeof(ThrowingPage) : typeof(TestPage), typeof(TestPage).Assembly), null)),
                (appId, _, parent, arguments, _) =>
                {
                    if (startThrows)
                    {
                        throw new InvalidOperationException("The user's process limit is reached.");
                    }

                    Starts.Add((appId, parent, arguments));
                    StubContext context = new(Catalog.Manifests[appId], ProcessId.FromInt64(nextPid++), EndPages.Token, principal.UserId.ToString());
                    return Task.FromResult(new AppLaunchResult(AppLaunchStatus.Launched, Context: context));
                },
                (pid, reason) =>
                {
                    Stops.Add((pid, reason));
                    return Task.FromResult(true);
                });
        }

        public AppCatalog Catalog { get; }

        public AppEnablementRegistry Enablement { get; }

        public StubContext Settings { get; }

        public FeatureSettingsPages Pages { get; }

        public CancellationTokenSource EndPages { get; } = new();

        public List<(string AppId, ProcessId Parent, IReadOnlyList<string> Arguments)> Starts { get; } = [];

        public List<(ProcessId Pid, ProcessExitReason Reason)> Stops { get; } = [];
    }

    private static AppManifest WindowManifest(string id) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A window app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test.dll", typeof(TestPage).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
    };

    private static AuthenticatedPrincipal CreatePrincipal(AppAuthority authority)
    {
        LocalGroupId primaryGroup = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("alice"),
            "Alice",
            authority,
            primaryGroup,
            [primaryGroup],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UtcNow);
    }

    private sealed class StubSessionService(AuthenticatedPrincipal principal) : ISessionService
    {
        public SessionState State => SessionState.Active;

        public AuthenticatedPrincipal? CurrentPrincipal => principal;

        public Task<AuthenticatedPrincipal> LoginAsync(LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public CancellationTokenSource CreateLinkedCancellationSource() => new();
    }

    private sealed class StubContext(AppManifest manifest, ProcessId pid, CancellationToken token = default, string userId = "user") : IAppExecutionContext
    {
        public AppManifest Manifest => manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => userId;
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId => pid;
        public CancellationToken CancellationToken => token;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
        public IAppNetworkGateway Network => throw new NotImplementedException();
    }
}
