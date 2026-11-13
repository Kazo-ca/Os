using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>
/// ADR 0244 §3: on mobile, every window stays in the page in opening order, the ones off screen
/// hidden in their slot, so switching apps never removes or moves a window (an iframe would reload).
/// </summary>
public sealed class SingleSurfaceAreaStableOrderTests
{
    [Fact]
    public async Task Switching_apps_keeps_every_window_in_the_page_in_opening_order()
    {
        // Arrange: A opened first, B second and focused.
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IJSRuntime, NoOpJsRuntime>()
            .BuildServiceProvider();
        FrameCapturingRenderer renderer = new(services);
        WindowRuntimeState a = CreateState(2, zOrder: 1, isFocused: false);
        WindowRuntimeState b = CreateState(1, zOrder: 2, isFocused: true);
        SingleSurfaceArea area = new() { Windows = [a, b] };
        ArrayRange<RenderTreeFrame> frames = await renderer.RenderAsync(area);
        Assert.Equal([(a.Id, false), (b.Id, true)], Slots(frames));

        // Act: A comes back on screen; the runtime now lists it last (front).
        WindowRuntimeState aFront = CreateState(2, zOrder: 3, isFocused: true);
        WindowRuntimeState bBehind = CreateState(1, zOrder: 2, isFocused: false, visualState: WindowVisualState.Minimized);
        frames = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await area.SetParametersAsync(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(SingleSurfaceArea.Windows)] = new[] { bBehind, aFront } }));
            return renderer.RootFrames(area);
        });

        // Assert: same page order, only the hidden flags changed.
        Assert.Equal([(a.Id, true), (b.Id, false)], Slots(frames));
    }

    /// <summary>The window slots of the surface, in page order, with whether each one is shown.</summary>
    private static List<(WindowId Id, bool Shown)> Slots(ArrayRange<RenderTreeFrame> frames)
    {
        List<(WindowId, bool)> slots = [];
        for (int i = 0; i < frames.Count; i++)
        {
            RenderTreeFrame frame = frames.Array[i];
            if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "div" || frame.ElementKey is not WindowId id)
            {
                continue;
            }

            bool hidden = false;
            for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                hidden |= frames.Array[j].AttributeName == "hidden";
            }

            slots.Add((id, !hidden));
        }

        return slots;
    }

    private static WindowRuntimeState CreateState(
        int seed,
        int zOrder,
        bool isFocused,
        WindowVisualState visualState = WindowVisualState.Maximized) =>
        new(
            WindowId.FromGuid(Guid.Parse($"70000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"80000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(0, 0, 640, 480),
            null,
            zOrder,
            visualState,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            isFocused);

    /// <summary>A browser that accepts every call and does nothing: the window hosts import their script.</summary>
    private sealed class NoOpJsRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
