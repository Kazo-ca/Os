using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Windowing.Core;

/// <summary>
/// Remembers the order in which windows first appeared, so a window area renders them in a stable
/// order in the page (ADR 0244 §1, doc/en/wasm/dev/adr/0244-stable-window-order-in-the-page.md).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WindowRuntime.Windows"/> is sorted by <see cref="WindowRuntimeState.ZOrder"/>. Rendering
/// that list as is makes Blazor move a window's element every time the window comes to the front,
/// and a browser reloads an <c>&lt;iframe&gt;</c> moved in the document. The stacking is drawn with
/// <c>z-index</c> instead; the page order only needs to never change for a window that stays open.
/// </para>
/// <para>
/// One instance belongs to one window area. A window seen for the first time is added at the end;
/// a window that is no longer in the list is forgotten. Several new windows in one call keep their
/// order in that call.
/// </para>
/// </remarks>
public sealed class WindowDocumentOrder
{
    private readonly List<WindowId> _order = [];

    /// <summary>
    /// Returns <paramref name="windows"/> in the order the windows first appeared, and records the
    /// windows that appear or disappear in this call.
    /// </summary>
    /// <param name="windows">The windows of the area, in any order (usually back to front).</param>
    /// <returns>The same snapshots, in opening order.</returns>
    public IReadOnlyList<WindowRuntimeState> Arrange(IEnumerable<WindowRuntimeState> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        Dictionary<WindowId, WindowRuntimeState> current = [];
        List<WindowId> appeared = [];
        foreach (WindowRuntimeState window in windows)
        {
            if (current.TryAdd(window.Id, window) && !_order.Contains(window.Id))
            {
                appeared.Add(window.Id);
            }
        }

        _order.RemoveAll(id => !current.ContainsKey(id));
        _order.AddRange(appeared);
        return [.. _order.Select(id => current[id])];
    }
}
