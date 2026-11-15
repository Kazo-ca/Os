using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>
/// Minimal <see cref="Renderer"/> that attaches a component to a real <see cref="RenderHandle"/> (so
/// lifecycle methods behave exactly as in production) and exposes the resulting render tree frames — no
/// bUnit in this solution; this mirrors <c>Tests/Kazo.Os.Apps.FileExplorer.FileControl.Tests/TestComponentRenderer.cs</c>,
/// extended to capture frames (rather than discard them) since `INT-013` needs to assert DOM element order,
/// not just C# backing state.
/// </summary>
internal sealed class FrameCapturingRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
{
    private readonly Dictionary<IComponent, int> _rootIds = new(ReferenceEqualityComparer.Instance);

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override void HandleException(Exception exception) =>
        ExceptionDispatchInfo.Capture(exception).Throw();

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    /// <summary>Attaches <paramref name="component"/> as a root component, runs its initial render, and returns its current frames.</summary>
    public Task<ArrayRange<RenderTreeFrame>> RenderAsync<TComponent>(TComponent component) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            int componentId = AssignRootComponentId(component);
            _rootIds[component] = componentId;
            await RenderRootComponentAsync(componentId);
            return GetCurrentRenderTreeFrames(componentId);
        });

    /// <summary>Returns the current frames of a root component attached by <see cref="RenderAsync{TComponent}"/>, after a later render.</summary>
    public ArrayRange<RenderTreeFrame> RootFrames(IComponent component) => GetCurrentRenderTreeFrames(_rootIds[component]);

    /// <summary>Returns the current frames of a child component, found through its parent's component frame.</summary>
    public ArrayRange<RenderTreeFrame> FramesOf(int componentId) => GetCurrentRenderTreeFrames(componentId);
}
