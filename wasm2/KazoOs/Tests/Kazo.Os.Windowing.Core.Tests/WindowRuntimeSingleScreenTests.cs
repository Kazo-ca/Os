using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Windowing.Core.Tests;

/// <summary>Windows that must stay on their screen, such as Craft apps in an iframe (ADR 0263).</summary>
public sealed class WindowRuntimeSingleScreenTests
{
    private static readonly WindowBounds PrimaryArea = new(0, 0, 1280, 720);
    private static readonly WindowBounds SecondArea = new(0, 0, 1920, 1040);
    private static readonly DisplayId Second = DisplayId.FromNumber(2);
    private static readonly WindowFrame SingleScreen = WindowFrame.Default with { CanChangeScreen = false };

    [Fact]
    public void A_window_that_cannot_change_screen_is_not_moved_to_another_screen()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1) with { Frame = SingleScreen };
        runtime.Apply(new CreateWindowCommand(window));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second));

        Assert.Empty(events);
        Assert.False(runtime.CanChangeScreen(window.Id));
        Assert.Equal(DisplayId.Primary, Find(runtime, window.Id).DisplayId);
    }

    [Fact]
    public void A_window_that_cannot_change_screen_opens_on_screen_one()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1) with { Frame = SingleScreen, DisplayId = Second };

        runtime.Apply(new CreateWindowCommand(window));

        Assert.Equal(DisplayId.Primary, Find(runtime, window.Id).DisplayId);
    }

    [Fact]
    public void A_modal_family_stays_together_when_one_of_its_windows_cannot_change_screen()
    {
        // The modal dialog of a Craft window would drag its owner, and the iframe, to the other screen.
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState owner = CreateState(1) with { Frame = SingleScreen };
        WindowRuntimeState modal = CreateState(2, WindowModality.OwnerModal, owner.Id);
        runtime.Apply(new CreateWindowCommand(owner));
        runtime.Apply(new CreateWindowCommand(modal));

        Assert.False(runtime.CanChangeScreen(modal.Id));
        Assert.Empty(runtime.Apply(new MoveWindowToDisplayCommand(modal.Id, Second)));
        Assert.All(runtime.Windows, window => Assert.Equal(DisplayId.Primary, window.DisplayId));
    }

    [Fact]
    public void Other_windows_still_change_screen()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState pinned = CreateState(1) with { Frame = SingleScreen };
        WindowRuntimeState free = CreateState(2);
        runtime.Apply(new CreateWindowCommand(pinned));
        runtime.Apply(new CreateWindowCommand(free));

        Assert.True(runtime.CanChangeScreen(free.Id));
        Assert.NotEmpty(runtime.Apply(new MoveWindowToDisplayCommand(free.Id, Second)));
        Assert.Equal(Second, Find(runtime, free.Id).DisplayId);
    }

    [Fact]
    public void Turning_the_option_off_on_a_secondary_screen_leaves_the_window_there_and_closing_the_screen_brings_it_back()
    {
        // Bringing it back at once would reload its iframe too; closing the screen is the OS's own move.
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1) with { DisplayId = Second };
        runtime.Apply(new CreateWindowCommand(window));

        runtime.Apply(new SetWindowFrameCommand(window.Id, SingleScreen));
        Assert.Equal(Second, Find(runtime, window.Id).DisplayId);

        runtime.Apply(new RemoveDisplayCommand(Second));
        Assert.Equal(DisplayId.Primary, Find(runtime, window.Id).DisplayId);
    }

    private static WindowRuntime RuntimeWithSecondScreen()
    {
        WindowRuntime runtime = new(PrimaryArea);
        runtime.Apply(new AddDisplayCommand(Second, SecondArea));
        return runtime;
    }

    private static WindowRuntimeState Find(WindowRuntime runtime, WindowId id) =>
        runtime.Windows.Single(window => window.Id == id);

    private static WindowRuntimeState CreateState(
        int seed,
        WindowModality modality = WindowModality.Modeless,
        WindowId? ownerId = null) =>
        new(
            WindowId.FromGuid(Guid.Parse($"30000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"40000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 2000, 1200),
            modality,
            ownerId);
}
