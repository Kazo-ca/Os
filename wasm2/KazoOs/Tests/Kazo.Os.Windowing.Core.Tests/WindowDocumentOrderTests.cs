using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Windowing.Core.Tests;

/// <summary>Tests for the stable page order of windows (ADR 0244 §1).</summary>
public sealed class WindowDocumentOrderTests
{
    [Fact]
    public void A_window_that_comes_to_the_front_keeps_its_place_in_the_page()
    {
        // Arrange: ids sorted differently from the opening order, so the order is not an id sort.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowDocumentOrder order = new();
        WindowRuntimeState first = CreateState(3);
        WindowRuntimeState second = CreateState(1);
        WindowRuntimeState third = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));
        runtime.Apply(new CreateWindowCommand(third));
        Assert.Equal([first.Id, second.Id, third.Id], Ids(order.Arrange(runtime.Windows)));

        // Act
        runtime.Apply(new FocusWindowCommand(first.Id));
        IReadOnlyList<WindowRuntimeState> arranged = order.Arrange(runtime.Windows);

        // Assert: the runtime puts it last (front), the page keeps it first.
        Assert.Equal(first.Id, runtime.Windows[^1].Id);
        Assert.Equal([first.Id, second.Id, third.Id], Ids(arranged));
        Assert.True(arranged[0].IsFocused);
    }

    [Fact]
    public void Closed_windows_leave_the_order_and_new_ones_are_added_at_the_end()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowDocumentOrder order = new();
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        WindowRuntimeState third = CreateState(3);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));
        order.Arrange(runtime.Windows);

        runtime.Apply(new FocusWindowCommand(first.Id));
        runtime.Apply(new ForceWindowCloseCommand(second.Id));
        order.Arrange(runtime.Windows);
        runtime.Apply(new CreateWindowCommand(third));
        runtime.Apply(new FocusWindowCommand(first.Id));

        Assert.Equal([first.Id, third.Id], Ids(order.Arrange(runtime.Windows)));
    }

    [Fact]
    public void Several_new_windows_in_one_call_keep_the_order_of_that_call()
    {
        WindowDocumentOrder order = new();
        WindowRuntimeState a = CreateState(5);
        WindowRuntimeState b = CreateState(4);

        Assert.Equal([a.Id, b.Id], Ids(order.Arrange([a, b])));
        Assert.Equal([a.Id, b.Id], Ids(order.Arrange([b, a])));
        Assert.Empty(order.Arrange([]));
        Assert.Equal([b.Id, a.Id], Ids(order.Arrange([b, a])));
    }

    [Fact]
    public void Arrange_returns_the_latest_snapshots()
    {
        WindowDocumentOrder order = new();
        WindowRuntimeState before = CreateState(1);
        order.Arrange([before]);
        WindowRuntimeState after = CreateState(1, title: "Renamed");

        Assert.Same(after, Assert.Single(order.Arrange([after])));
    }

    [Fact]
    public void Arrange_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => new WindowDocumentOrder().Arrange(null!));

    private static WindowId[] Ids(IReadOnlyList<WindowRuntimeState> windows) => [.. windows.Select(window => window.Id)];

    private static WindowRuntimeState CreateState(int seed, string? title = null) =>
        new(
            WindowId.FromGuid(Guid.Parse($"50000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"60000000-0000-0000-0000-{seed:D12}")),
            title ?? $"Test {seed}",
            null,
            new WindowBounds(0, 0, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            false);
}
