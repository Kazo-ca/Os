using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.DragDrop;
using Kazo.Os.AppSdk.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>
/// A zone of an app that receives dropped files (ADR 0287 §2 and §3): Kazo OS files and folders dragged
/// from the File Explorer, the desktop or another app, and files dragged from the real computer.
/// </summary>
/// <remarks>
/// <para>
/// A drop into an app is always a copy: the source keeps its files. The app receives paths, never
/// rights, and reads them with its own capabilities (ADR 0287 §4).
/// </para>
/// <para>
/// Blazor gives no <c>DataTransfer.getData</c> in C#, so the collocated
/// <c>KazoFileDropZone.razor.js</c> reads the drop and calls <see cref="OnDropAsync"/>.
/// </para>
/// </remarks>
public partial class KazoFileDropZone : IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.AppSdk.Blazor/Files/KazoFileDropZone.razor.js";

    private ElementReference _element;
    private IJSObjectReference? _module;
    private DotNetObjectReference<KazoFileDropZone>? _self;
    private bool _disposed;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>Gets or sets the content of the zone.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Gets or sets the files the zone takes: MIME types (<c>audio/*</c>, <c>audio/mpeg</c>) or extensions
    /// (<c>.m3u</c>). The files that match none are removed from the drop; a folder always matches.
    /// <see langword="null"/> or empty takes every file.
    /// </summary>
    [Parameter]
    public IReadOnlyList<string>? Accept { get; set; }

    /// <summary>
    /// Gets or sets the callback receiving each drop that keeps something once <see cref="Accept"/> is
    /// applied. The computer's files are released when it returns.
    /// </summary>
    [Parameter]
    public EventCallback<KazoFilesDroppedEventArgs> FilesDropped { get; set; }

    /// <summary>Gets or sets more CSS classes for the zone's element.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Gets or sets other attributes of the zone's element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Called by <c>KazoFileDropZone.razor.js</c> for a drop. Not for apps: it is public only for the
    /// JavaScript interop.
    /// </summary>
    /// <param name="json">The text of the drag under <see cref="KazoFileDrag.MimeType"/>, or <see langword="null"/>.</param>
    /// <param name="token">The token under which the script keeps the computer's files (0 for none).</param>
    /// <param name="hostFilesJson">The computer's files, as <c>[{ "name", "size", "type" }]</c>.</param>
    /// <returns>Completes once the app's <see cref="FilesDropped"/> handler returns.</returns>
    [JSInvokable]
    public async Task OnDropAsync(string? json, int token, string? hostFilesJson)
    {
        // Called from JavaScript, outside any frame: enter the app's context first (ADR 0161 §1).
        using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
        KazoFilesDroppedEventArgs? drop = CreateDrop(json, hostFilesJson, Accept,
            (index, maxBytes, cancellationToken) => OpenHostFileAsync(token, index, maxBytes, cancellationToken));
        if (drop is not null && FilesDropped.HasDelegate)
        {
            await FilesDropped.InvokeAsync(drop);
        }
    }

    /// <summary>
    /// Builds the arguments of a drop: reads the Kazo OS drag (an invalid one gives no Kazo OS item),
    /// applies <paramref name="accept"/> and wraps the computer's files.
    /// </summary>
    /// <returns>The drop, or <see langword="null"/> when nothing is left.</returns>
    internal static KazoFilesDroppedEventArgs? CreateDrop(
        string? json,
        string? hostFilesJson,
        IReadOnlyList<string>? accept,
        Func<int, long, CancellationToken, Task<Stream>> openHostFile)
    {
        KazoFileDrag.TryParse(json, out KazoFileDragData data);
        List<VirtualFileDragPayload> files = [.. data.Files.Where(file => KazoFileAccept.Matches(accept, file.FileName, file.MimeType))];

        List<KazoHostFile> hostFiles = [];
        IReadOnlyList<HostFileMeta> metas = ReadHostFiles(hostFilesJson);
        for (int index = 0; index < metas.Count; index++)
        {
            HostFileMeta meta = metas[index];
            string? type = string.IsNullOrEmpty(meta.Type) ? null : meta.Type;
            if (string.IsNullOrWhiteSpace(meta.Name) || !KazoFileAccept.Matches(accept, meta.Name, type))
            {
                continue;
            }

            int fileIndex = index;
            hostFiles.Add(new KazoHostFile(meta.Name, Math.Max(0, meta.Size), type,
                (maxBytes, cancellationToken) => openHostFile(fileIndex, maxBytes, cancellationToken)));
        }

        KazoFilesDroppedEventArgs drop = new(files, data.Folders, hostFiles);
        return drop.IsEmpty ? null : drop;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
        _self = DotNetObjectReference.Create(this);
        if (!_disposed)
        {
            await _module.InvokeVoidAsync("attach", _element, _self, KazoFileDrag.MimeType);
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
                // The page is gone, and the listeners with it.
            }
        }

        _self?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<HostFileMeta> ReadHostFiles(string? hostFilesJson)
    {
        if (string.IsNullOrWhiteSpace(hostFilesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(hostFilesJson, KazoFileDropZoneJsonContext.Default.HostFileMetaArray) is { } metas
                ? [.. metas.Where(static meta => meta is not null)]
                : [];
        }
        catch (JsonException)
        {
            // Written by our own script: an unreadable list means no computer file, not a crash.
            return [];
        }
    }

    private async Task<Stream> OpenHostFileAsync(int token, int index, long maxBytes, CancellationToken cancellationToken)
    {
        IJSObjectReference module = _module ?? throw new InvalidOperationException("The dropped file is no longer available.");
        IJSStreamReference reference = await module.InvokeAsync<IJSStreamReference>("openFile", cancellationToken, token, index);
        return await reference.OpenReadStreamAsync(maxBytes, cancellationToken);
    }

    /// <summary>One computer file of a drop, as <c>KazoFileDropZone.razor.js</c> describes it.</summary>
    internal sealed record HostFileMeta(string Name, long Size, string? Type);
}

/// <summary>Source-generated (trim-safe) reader of the computer files a drop lists.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KazoFileDropZone.HostFileMeta[]))]
internal sealed partial class KazoFileDropZoneJsonContext : JsonSerializerContext;
