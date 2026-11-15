using System.Runtime.ExceptionServices;
using Kazo.Os.AppSdk.Icons;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>
/// A <see cref="Renderer"/> that attaches a Store component to a real render handle and lets the tests
/// read the elements it drew, with no browser and no bUnit (the same technique as
/// <c>Tests/Kazo.Os.Apps.FileExplorer.FileControl.Tests/TestComponentRenderer.cs</c>).
/// </summary>
internal sealed class StoreTestRenderer() : Renderer(Services(), NullLoggerFactory.Instance)
{
    private int _rootId = -1;

    /// <inheritdoc />
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <summary>Attaches <paramref name="component"/> as the root and draws it once.</summary>
    public Task<TComponent> RenderAsync<TComponent>(TComponent component) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            _rootId = AssignRootComponentId(component);
            await RenderRootComponentAsync(_rootId);
            return component;
        });

    /// <summary>Runs <paramref name="action"/> on the renderer's dispatcher, as a browser event does.</summary>
    public Task RunAsync(Func<Task> action) => Dispatcher.InvokeAsync(action);

    /// <summary>The elements the root component drew, in order, with their attributes.</summary>
    public IReadOnlyList<RenderedElement> Elements()
    {
        ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(_rootId);
        List<RenderedElement> elements = [];
        for (int i = 0; i < frames.Count; i++)
        {
            RenderTreeFrame frame = frames.Array[i];
            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            Dictionary<string, string?> attributes = new(StringComparer.Ordinal);
            for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                RenderTreeFrame attribute = frames.Array[j];
                attributes[attribute.AttributeName] = attribute.AttributeValue switch
                {
                    string text => text,
                    bool flag => flag ? "true" : null,
                    null => null,
                    _ => "(handler)",
                };
            }

            elements.Add(new RenderedElement(frame.ElementName, attributes));
        }

        return elements;
    }

    /// <inheritdoc />
    protected override void HandleException(Exception exception) =>
        ExceptionDispatchInfo.Capture(exception).Throw();

    /// <inheritdoc />
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    private static ServiceProvider Services() =>
        new ServiceCollection().AddSingleton<IIconCatalog, IconCatalog>().BuildServiceProvider();
}

/// <summary>An element a component drew, as the tests see it.</summary>
/// <param name="Name">The element's tag.</param>
/// <param name="Attributes">Its attributes; a <see langword="false"/> boolean attribute is absent.</param>
internal sealed record RenderedElement(string Name, IReadOnlyDictionary<string, string?> Attributes)
{
    /// <summary>Tells whether the element's <c>class</c> attribute holds <paramref name="className"/>.</summary>
    public bool HasClass(string className) =>
        Attributes.TryGetValue("class", out string? value) && value is not null
        && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(className, StringComparer.Ordinal);

    /// <summary>The value of an attribute, or <see langword="null"/>.</summary>
    public string? this[string name] => Attributes.TryGetValue(name, out string? value) ? value : null;
}
