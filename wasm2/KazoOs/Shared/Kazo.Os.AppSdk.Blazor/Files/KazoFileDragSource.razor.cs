using Kazo.Os.AppSdk.DragDrop;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// An element of an app that drags Kazo OS files and folders (ADR 0287 §5) to the File Explorer, the
/// desktop or another app's <see cref="KazoFileDropZone"/>. The drop is always a copy: an app does not
/// empty a user's folder through a drag.
/// </summary>
/// <remarks>
/// The browser asks for the data during <c>dragstart</c>, with no wait possible, so the component
/// prepares the drag (<see cref="KazoFileDrag.CreateAsync"/>) whenever <see cref="Files"/> or
/// <see cref="Folders"/> change. The sizes, names and MIME types come from the app's file gateway, with
/// its capabilities: a path the app cannot read is left out of the drag.
/// </remarks>
public partial class KazoFileDragSource : IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.AppSdk.Blazor/Files/KazoFileDragSource.razor.js";

    private ElementReference _element;
    private IJSObjectReference? _module;
    private string[] _preparedFiles = [];
    private string[] _preparedFolders = [];
    private int _version;
    private string? _json;
    private string? _sentJson;
    private bool _disposed;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>Gets or sets the content of the element.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Gets or sets the Kazo OS paths of the files the element drags.</summary>
    [Parameter]
    public IReadOnlyList<string>? Files { get; set; }

    /// <summary>Gets or sets the Kazo OS paths of the folders the element drags.</summary>
    [Parameter]
    public IReadOnlyList<string>? Folders { get; set; }

    /// <summary>Gets or sets more CSS classes for the element.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Gets or sets other attributes of the element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Gets the drag the element gives, once prepared (<see cref="KazoFileDragData.Empty"/> before).</summary>
    public KazoFileDragData Drag { get; private set; } = KazoFileDragData.Empty;

    /// <summary>Gets whether the element has something to drag.</summary>
    private bool CanDrag => _json is not null;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        string[] files = [.. Files ?? []];
        string[] folders = [.. Folders ?? []];
        if (files.SequenceEqual(_preparedFiles, StringComparer.Ordinal)
            && folders.SequenceEqual(_preparedFolders, StringComparer.Ordinal))
        {
            return;
        }

        _preparedFiles = files;
        _preparedFolders = folders;
        int version = ++_version;
        KazoFileDragData drag = await KazoFileDrag.CreateAsync(files, folders);
        if (version != _version)
        {
            // The paths changed while this drag was being read: the newer one wins.
            return;
        }

        Drag = drag;
        _json = drag.IsEmpty ? null : KazoFileDrag.Serialize(drag);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            if (_disposed)
            {
                return;
            }

            await _module.InvokeVoidAsync("attach", _element, KazoFileDrag.MimeType);
            await _module.InvokeVoidAsync("setDrag", _element, _json);
            _sentJson = _json;
            return;
        }

        if (_module is not null && !string.Equals(_sentJson, _json, StringComparison.Ordinal))
        {
            await _module.InvokeVoidAsync("setDrag", _element, _json);
            _sentJson = _json;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detach", _element);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone, and the listener with it.
            }
        }

        GC.SuppressFinalize(this);
    }
}
