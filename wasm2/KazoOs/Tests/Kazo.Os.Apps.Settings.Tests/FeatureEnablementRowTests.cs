using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
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
/// Settings ▸ Features ▸ Installed features, a feature's state and its one action (ADR 0287 §5):
/// "Disable" with a confirmation naming the dependents and the processes, or "Enable".
/// </summary>
public sealed class FeatureEnablementRowTests
{
    private const string FeatureId = "ca.kazo.x86";

    [Fact]
    public async Task An_administrator_disables_an_enabled_feature_after_a_confirmation_naming_what_stops()
    {
        FakeAppManagement management = new();
        await using RowHarness harness = await RowHarness.RenderAsync(management, KazoAppInstallState.Installed, AppAuthority.Administrator);
        Assert.Contains("Enabled", harness.Html(), StringComparison.Ordinal);
        Assert.Contains(">Disable<", harness.Html(), StringComparison.Ordinal);

        await harness.RunAsync(row => row.AskDisableAsync());
        string dialog = harness.Html();
        await harness.RunAsync(row => row.ConfirmDisableAsync());

        Assert.Contains("Disable Os-x86?", dialog, StringComparison.Ordinal);
        Assert.Contains("The feature stays installed", dialog, StringComparison.Ordinal);
        Assert.Contains("VM client", dialog, StringComparison.Ordinal);
        Assert.Contains("vmclient", dialog, StringComparison.Ordinal);
        // The card already lists the entry points: the dialog names only the apps taken along.
        Assert.DoesNotContain("Console", dialog, StringComparison.Ordinal);
        Assert.Equal([$"disable:{FeatureId}"], management.Calls);
        Assert.Equal(1, harness.Changes);
        Assert.Contains("Stopped: vmclient (PID 42).", harness.Html(), StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_enables_a_disabled_feature_again()
    {
        FakeAppManagement management = new();
        await using RowHarness harness = await RowHarness.RenderAsync(management, KazoAppInstallState.Disabled, AppAuthority.Administrator);
        Assert.Contains("Disabled", harness.Html(), StringComparison.Ordinal);
        Assert.Contains(">Enable<", harness.Html(), StringComparison.Ordinal);

        await harness.RunAsync(row => row.EnableAsync());

        Assert.Equal([$"enable:{FeatureId}"], management.Calls);
        Assert.Contains("Os-x86 is enabled again.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_sees_the_state_without_an_action()
    {
        await using RowHarness harness = await RowHarness.RenderAsync(new FakeAppManagement(), KazoAppInstallState.Disabled, AppAuthority.User);

        Assert.Contains("Disabled", harness.Html(), StringComparison.Ordinal);
        Assert.Contains("Only an administrator enables or disables a feature.", harness.Html(), StringComparison.Ordinal);
        Assert.DoesNotContain("<button", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_administrator_authenticated_leaves_the_feature_enabled_with_a_message()
    {
        FakeAppManagement management = new() { RefuseAdministrator = true };
        await using RowHarness harness = await RowHarness.RenderAsync(management, KazoAppInstallState.Installed, AppAuthority.Administrator);

        await harness.RunAsync(row => row.AskDisableAsync());
        await harness.RunAsync(row => row.ConfirmDisableAsync());

        Assert.Equal(0, harness.Changes);
        Assert.Contains("No administrator authenticated: /etc/kazoos/features.config was not changed.", harness.Html(), StringComparison.Ordinal);
    }

    private static KazoAppInfo App(string id, string name, AppKind kind) =>
        new(id, name, name + ".", kind, "system", null, "1.0.0", "ca.kazo", false, true, []);

    /// <summary>The feature, one of its entry points, and an app that needs it.</summary>
    private static IReadOnlyList<KazoAppInstallInfo> Apps(KazoAppInstallState state) =>
    [
        new(App(FeatureId, "Os-x86", AppKind.Feature), state, [FeatureId]),
        new(App("ca.kazo.x86.console", "Console", AppKind.Terminal) with { ParentId = FeatureId }, state, [FeatureId]),
        new(App("org.test.vmclient", "VM client", AppKind.Window), state, ["org.test.vmclient"]),
    ];

    /// <summary>Records the changes asked for; the plan names the dependent and one of its processes.</summary>
    private sealed class FakeAppManagement : IAppManagementGateway
    {
        public List<string> Calls { get; } = [];

        public bool RefuseAdministrator { get; init; }

        public ValueTask<IReadOnlyList<KazoAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Apps(KazoAppInstallState.Installed));

        public ValueTask<KazoUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KazoUninstallPlan(
                ["org.test.vmclient", "ca.kazo.x86.console", FeatureId],
                [new KazoRunningProcessInfo(42, "org.test.vmclient", "vmclient", "alice")]));

        public ValueTask<KazoDisableReport> DisableFeatureAsync(string featureId, CancellationToken cancellationToken = default)
        {
            if (RefuseAdministrator)
            {
                throw new KazoUnauthorizedAccessException("Administrator authentication failed (cancelled).");
            }

            Calls.Add($"disable:{featureId}");
            return ValueTask.FromResult(new KazoDisableReport(
                ["org.test.vmclient", featureId], [new KazoRunningProcessInfo(42, "org.test.vmclient", "vmclient", "alice")]));
        }

        public ValueTask<IReadOnlyList<string>> EnableFeatureAsync(string featureId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"enable:{featureId}");
            return ValueTask.FromResult<IReadOnlyList<string>>([featureId]);
        }

        public ValueTask<IReadOnlyList<string>> UninstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Renders the row inside the Settings window's cascaded context and runs its actions like clicks.</summary>
    private sealed class RowHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly IAppExecutionContext _context;
        private HtmlRootComponent _root;

        private RowHarness(ServiceProvider services, HtmlRenderer renderer, IAppExecutionContext context)
        {
            _services = services;
            _renderer = renderer;
            _context = context;
        }

        public FeatureEnablementRow Row { get; private set; } = default!;

        /// <summary>How many times the row told its card list to read the states again.</summary>
        public int Changes { get; private set; }

        public static async Task<RowHarness> RenderAsync(FakeAppManagement management, KazoAppInstallState state, AppAuthority authority)
        {
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                userAuthority: authority,
                appManagement: management);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            RowHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            IReadOnlyList<KazoAppInstallInfo> apps = Apps(state);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.BeginRenderingComponent<RowHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(RowHost.Context)] = context,
                    [nameof(RowHost.Feature)] = apps[0],
                    [nameof(RowHost.Apps)] = apps,
                    [nameof(RowHost.Changed)] = (Action)(() => harness.Changes++),
                    [nameof(RowHost.Captured)] = (Action<FeatureEnablementRow>)(row => harness.Row = row),
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a row action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public async Task RunAsync(Func<FeatureEnablementRow, Task> action)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (KazoExecutionContext.Enter(_context))
                {
                    await action(Row);
                }
            });
        }

        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class RowHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public KazoAppInstallInfo Feature { get; set; } = default!;

        [Parameter]
        public IReadOnlyList<KazoAppInstallInfo> Apps { get; set; } = [];

        [Parameter]
        public Action Changed { get; set; } = default!;

        [Parameter]
        public Action<FeatureEnablementRow> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<FeatureEnablementRow>(0);
                child.AddComponentParameter(1, nameof(FeatureEnablementRow.Feature), Feature);
                child.AddComponentParameter(2, nameof(FeatureEnablementRow.Apps), Apps);
                child.AddComponentParameter(3, nameof(FeatureEnablementRow.Changed), EventCallback.Factory.Create(this, Changed));
                child.AddComponentReferenceCapture(4, reference => Captured((FeatureEnablementRow)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
