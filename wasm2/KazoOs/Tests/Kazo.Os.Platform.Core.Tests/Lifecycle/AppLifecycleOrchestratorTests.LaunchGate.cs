using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Lifecycle;

/// <summary>ADR 0239 §2: the host's launch gate keeps apps from starting on an outdated copy of the OS.</summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    [Fact]
    public async Task A_window_app_the_gate_refuses_does_not_start()
    {
        RefusingGate gate = new();
        Fixture fixture = new(new GateServices(gate), SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult result = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.notes", principal, []));

        Assert.Equal(AppLaunchStatus.UpdateRequired, result.Status);
        Assert.Equal(AppLaunchRefusal.UpdateRequiredCode, result.ErrorCode);
        Assert.Equal("restart first", result.ErrorMessage);
        Assert.Null(result.Process);
        Assert.Equal(["org.kazoos.notes"], gate.Asked);
    }

    [Fact]
    public async Task A_running_singleton_is_focused_without_asking_the_gate()
    {
        RefusingGate gate = new() { Refuse = false };
        Fixture fixture = new(new GateServices(gate), SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult first = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.notes", principal, []));
        gate.Refuse = true;

        AppLaunchResult second = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.notes", principal, []));

        Assert.Equal(AppLaunchStatus.Launched, first.Status);
        Assert.Equal(AppLaunchStatus.FocusedExisting, second.Status);
        Assert.Single(gate.Asked);
    }

    [Fact]
    public async Task Commands_are_not_gated()
    {
        RefusingGate gate = new();
        Fixture fixture = new(new GateServices(gate), EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult command = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.echo", principal, ["hi"]));

        Assert.Equal(AppLaunchStatus.Launched, command.Status);
        Assert.Empty(gate.Asked);
    }

    [Fact]
    public async Task Non_interactive_launches_are_not_gated()
    {
        RefusingGate gate = new();
        Fixture fixture = new(new GateServices(gate), SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult background = await fixture.Orchestrator.LaunchAsync(
            new AppLaunchRequest("org.kazoos.notes", principal, [], Interactive: false));

        Assert.Equal(AppLaunchStatus.Launched, background.Status);
        Assert.Empty(gate.Asked);
    }

    private sealed class RefusingGate : IAppLaunchGate
    {
        public bool Refuse { get; set; } = true;

        public List<string> Asked { get; } = [];

        public ValueTask<AppLaunchRefusal?> CheckAsync(AppManifest manifest, CancellationToken cancellationToken)
        {
            Asked.Add(manifest.Id);
            return ValueTask.FromResult(Refuse ? new AppLaunchRefusal(AppLaunchRefusal.UpdateRequiredCode, "restart first") : null);
        }
    }

    private sealed class GateServices(IAppLaunchGate gate) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IAppLaunchGate) ? gate : null;
    }
}
