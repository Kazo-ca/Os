using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Storage;
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
/// Settings ▸ System ▸ Storage (ADR 0250 §3): usage and quota of the origin, its parts, whether it is
/// protected (with the button that asks), and the space each feature declared.
/// </summary>
public sealed class StoragePanelTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly KazoStorageEstimate Estimate = new(5 * GiB, 10 * GiB, IsPersisted: false, IsLowSpace: false)
    {
        IndexedDbBytes = GiB,
        CacheBytes = GiB / 2,
        FileSystemBytes = 3 * GiB,
    };

    [Fact]
    public void The_page_is_in_the_system_group_next_to_memory()
    {
        List<string> ids = [.. SettingsWindow.AllPages.Select(static page => page.Id)];

        Assert.Equal(ids.IndexOf("memory") + 1, ids.IndexOf("storage"));
        Assert.Equal("Nav.Group.System", SettingsWindow.AllPages.Single(static page => page.Id == "storage").GroupKey);
    }

    [Fact]
    public async Task The_page_shows_the_usage_the_parts_and_the_space_each_feature_declared()
    {
        FakeStorage storage = new(Estimate, [new KazoFeatureStorageUsage("org.kazo.machines", "Machines", 3 * GiB, DateTimeOffset.UtcNow)]);
        await using PanelHarness harness = await PanelHarness.RenderAsync(storage);

        string html = harness.Html();
        Assert.Contains("5.0 GiB used of 10.0 GiB (50 %)", html, StringComparison.Ordinal);
        Assert.Contains("Kazo OS files (IndexedDB)", html, StringComparison.Ordinal);
        Assert.Contains("Machines", html, StringComparison.Ordinal);
        Assert.Contains("/dev/feature/org.kazo.machines", html, StringComparison.Ordinal);
        Assert.Contains("3.0 GiB", html, StringComparison.Ordinal);
        Assert.Contains("Protect the storage against erasure", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_feature_that_declared_nothing_shows_unknown_and_a_protected_origin_has_no_button()
    {
        FakeStorage storage = new(Estimate with { IsPersisted = true }, [new KazoFeatureStorageUsage("org.kazo.machines", "Machines", null, null)]);
        await using PanelHarness harness = await PanelHarness.RenderAsync(storage);

        string html = harness.Html();
        Assert.Contains(">unknown<", html, StringComparison.Ordinal);
        Assert.Contains("Protected: the browser will not erase", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Protect the storage against erasure", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_speaks_French_and_warns_when_the_protection_was_lost()
    {
        FakeStorage storage = new(Estimate with { PersistenceLost = true }, []);
        await using PanelHarness harness = await PanelHarness.RenderAsync(storage, "fr_CA.UTF-8");

        string html = harness.Html();
        Assert.Contains("Le navigateur ne protège plus le stockage", html, StringComparison.Ordinal);
        Assert.Contains("Protéger le stockage contre l'effacement", html, StringComparison.Ordinal);
        Assert.Contains("Aucune fonctionnalité ne garde de stockage à elle.", html, StringComparison.Ordinal);
    }

    private sealed class FakeStorage(KazoStorageEstimate estimate, IReadOnlyList<KazoFeatureStorageUsage> features) : IAppStorageGateway
    {
        public bool IsSupported => true;

        public ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(estimate);

        public ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(features);

        public event EventHandler<KazoStorageChangedEventArgs>? Changed
        {
            add
            {
            }

            remove
            {
            }
        }
    }

    private sealed class PanelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;

        private PanelHarness(ServiceProvider services, HtmlRenderer renderer)
        {
            _services = services;
            _renderer = renderer;
        }

        public static async Task<PanelHarness> RenderAsync(IAppStorageGateway storage, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                storage: storage);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance));
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PanelHost.Context)] = context })));
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

    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<StoragePanel>(0);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
