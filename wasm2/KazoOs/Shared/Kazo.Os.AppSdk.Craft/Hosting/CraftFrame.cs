using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// The C# side of one Craft app iframe (ADR 0259): it starts <c>craft-host.js</c>, served by the OS
/// at <see cref="ModulePath"/>, in an element of the window and raises the proxy's messages as .NET
/// events. The window decides everything else (bound files, changes, title, close).
/// </summary>
/// <remarks>
/// One <see cref="CraftFrame"/> per iframe: "Retry" disposes it and creates another, so a late
/// message from the old iframe never reaches the window.
/// </remarks>
public sealed class CraftFrame : IAsyncDisposable
{
    /// <summary>Path of the OS side module, served by the OS with this library.</summary>
    public const string ModulePath = "./_content/Kazo.Os.AppSdk.Craft/craft-host.js";

    private readonly IJSRuntime _javaScript;
    private DotNetObjectReference<CraftFrame>? _self;
    private IJSObjectReference? _module;
    private IJSObjectReference? _frame;

    /// <summary>Creates a frame; <see cref="StartAsync"/> puts the iframe in the window.</summary>
    /// <param name="javaScript">The window's JavaScript runtime.</param>
    public CraftFrame(IJSRuntime javaScript)
    {
        ArgumentNullException.ThrowIfNull(javaScript);
        _javaScript = javaScript;
    }

    /// <summary>Raised with the Craft app's version when it is listening.</summary>
    public event Func<string?, Task>? Ready;

    /// <summary>Raised with the error when the Craft app did not start.</summary>
    public event Func<string?, Task>? Failed;

    /// <summary>Raised when the document the Craft app shows gets changes (<see langword="true"/>) or loses them.</summary>
    public event Func<bool, Task>? DirtyChanged;

    /// <summary>
    /// Raised for every file the Craft app saves: its name (the format is in the extension), its
    /// <c>saveAs</c>, and the bytes. The handler answers whether the file reached its destination;
    /// when the app's save carried an <c>id</c>, the proxy then tells the app <c>saved</c>.
    /// </summary>
    public event Func<FrameSave, Task<bool>>? Saving;

    /// <summary>
    /// Raised for every file the Craft app exports through a browser download link (ADR 0264): a
    /// copy made for the user (Export, Print to PDF…), never the document, whatever its extension.
    /// <see cref="FrameSave.SaveAs"/> is <see langword="false"/>. The handler answers whether the
    /// file reached its destination. The page's downloads go to the window only when this event has
    /// a handler before <see cref="StartAsync"/>; otherwise the browser downloads them as before.
    /// </summary>
    public event Func<FrameSave, Task<bool>>? Exporting;

    /// <summary>Raised when the running Craft app reports an error (<see cref="FrameMessages.Error"/>).</summary>
    public event Func<FrameError, Task>? ErrorReported;

    /// <summary>Raised when a reserved chord meant for the window (<see cref="ReservedKeyTarget.App"/>) was typed.</summary>
    public event Func<ReservedKey, Task>? KeyPressed;

    /// <summary>Raised when the Craft app opened its file picker; answer with <see cref="AnswerPickAsync"/>.</summary>
    public event Func<PickRequest, Task>? PickRequested;

    /// <summary>Raised when the Craft app wrote a setting: its key, and its value (<see langword="null"/> when removed).</summary>
    public event Func<string, string?, Task>? SettingChanged;

    /// <summary>Raised with <c>light</c> or <c>dark</c> when the OS theme's color scheme changed.</summary>
    public event Func<string, Task>? SchemeChanged;

    /// <summary>Creates the iframe in <paramref name="container"/>; the Craft app starts loading in it.</summary>
    /// <param name="container">The element of the window that holds the iframe.</param>
    /// <param name="options">The app's page, title, language, parameters, reserved chords and settings.</param>
    /// <param name="cancellationToken">Cancels the start (the window closes).</param>
    /// <returns>A task that completes once the iframe is in the window.</returns>
    public async Task StartAsync(ElementReference container, CraftFrameOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _self = DotNetObjectReference.Create(this);
        _module = await _javaScript.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        _frame = await _module.InvokeAsync<IJSObjectReference>(
            "create",
            cancellationToken,
            container,
            _self,
            options.ToMessage(takesExports: Exporting is not null));
    }

    /// <summary>Gives the keyboard to the Craft app (the window was activated, or the Craft app is ready).</summary>
    /// <returns>A task that completes once the iframe has the focus.</returns>
    public async Task FocusAsync()
    {
        if (_frame is not null)
        {
            try
            {
                await _frame.InvokeVoidAsync("focus");
            }
            catch (JSDisconnectedException)
            {
                // The window is closing.
            }
        }
    }

    /// <summary>
    /// Runs a command of the Craft app's page API (<c>window.&lt;app&gt;.request</c>, the
    /// <c>craft-web-api</c> contract; each app's <c>docs/control-protocol.md</c> lists them), such as
    /// <c>ui.inspect</c> or <c>ui.theme</c>.
    /// </summary>
    /// <param name="method">The method or command id.</param>
    /// <param name="parameters">Its parameters, serialized to JSON; <see langword="null"/> for none.</param>
    /// <param name="timeout">How long the Craft app has to answer; the proxy's default (10 s) when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops waiting (the window closes).</param>
    /// <returns>
    /// The Craft app's answer. A failure is a result, never an exception: the Craft app refused the command,
    /// did not answer in time, is not started yet, or its page reloaded.
    /// </returns>
    public async Task<FrameCommandResult> RequestAsync(
        string method,
        object? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (_frame is null)
        {
            return FrameCommandResult.Failure("the app is not connected");
        }

        int timeoutMs = timeout is { } limit ? (int)Math.Ceiling(limit.TotalMilliseconds) : 0;
        try
        {
            JsonElement value = await _frame.InvokeAsync<JsonElement>(
                "request",
                cancellationToken,
                method,
                parameters ?? new Dictionary<string, object?>(),
                timeoutMs);
            return new FrameCommandResult(true, value, null);
        }
        catch (JSException exception)
        {
            return FrameCommandResult.Failure(FirstLine(exception.Message));
        }
        catch (JSDisconnectedException)
        {
            return FrameCommandResult.Failure("the window closed");
        }
    }

    /// <summary>Sends a document to the Craft app (<c>open</c>).</summary>
    /// <param name="name">The file name the Craft app gives the document (its extension tells the format).</param>
    /// <param name="bytes">The file's content.</param>
    /// <returns><see langword="true"/> when the Craft app received it; <see langword="false"/> when it is not connected.</returns>
    public async Task<bool> OpenAsync(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);
        if (_frame is null)
        {
            return false;
        }

        try
        {
            return await _frame.InvokeAsync<bool>("open", name, bytes);
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }

    /// <summary>Called by <c>craft-host.js</c> for each file the Craft app saves.</summary>
    /// <param name="name">The file name the Craft app gave.</param>
    /// <param name="saveAs">The Craft app's <c>saveAs</c>.</param>
    /// <param name="bytes">The file's content.</param>
    /// <returns>Whether the file reached its destination; <see langword="false"/> when nobody handles saves.</returns>
    [JSInvokable]
    public Task<bool> OnFrameSave(string name, bool saveAs, byte[] bytes) =>
        Saving?.Invoke(new FrameSave(name, saveAs, bytes)) ?? Task.FromResult(false);

    /// <summary>Called by <c>craft-host.js</c> for each file the Craft app exports (ADR 0264).</summary>
    /// <param name="name">The file name the Craft app gave.</param>
    /// <param name="bytes">The file's content.</param>
    /// <returns>Whether the file reached its destination; <see langword="false"/> when nobody handles exports.</returns>
    [JSInvokable]
    public Task<bool> OnFrameExport(string name, byte[] bytes) =>
        Exporting?.Invoke(new FrameSave(name, false, bytes)) ?? Task.FromResult(false);

    /// <summary>Called by <c>craft-host.js</c> for each message of the proxy.</summary>
    /// <param name="type">The message name, one of <see cref="FrameMessages"/>.</param>
    /// <param name="value">Its value, as text.</param>
    /// <returns>A task that completes once the window handled the message.</returns>
    [JSInvokable]
    public Task OnFrameMessage(string type, string? value) => type switch
    {
        FrameMessages.Ready => Ready?.Invoke(value) ?? Task.CompletedTask,
        FrameMessages.Failed => Failed?.Invoke(value) ?? Task.CompletedTask,
        FrameMessages.Error => OnErrorAsync(value),
        FrameMessages.Dirty => DirtyChanged?.Invoke(value == "true") ?? Task.CompletedTask,
        FrameMessages.Key => OnKeyAsync(value),
        FrameMessages.Pick => PickRequest.FromJson(value) is { } pick && PickRequested is { } picked
            ? picked(pick)
            : Task.CompletedTask,
        FrameMessages.Setting => OnSettingAsync(value),
        FrameMessages.Scheme => value is { } scheme ? SchemeChanged?.Invoke(scheme) ?? Task.CompletedTask : Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    /// <summary>Answers the Craft app's file picker (message <c>picked</c>).</summary>
    /// <param name="id">The pick's id (<see cref="PickRequest.Id"/>).</param>
    /// <param name="files">The chosen files; <see langword="null"/> or empty when the user cancelled.</param>
    /// <returns>A task that completes once the answer is sent.</returns>
    public async Task AnswerPickAsync(int id, IReadOnlyList<PickedFile>? files)
    {
        if (_frame is null)
        {
            return;
        }

        try
        {
            object? payload = files is { Count: > 0 }
                ? files.Select(file => new Dictionary<string, object> { ["name"] = file.Name, ["type"] = file.Type, ["bytes"] = file.Bytes }).ToArray()
                : null;
            await _frame.InvokeVoidAsync("answerPick", id, payload);
        }
        catch (JSDisconnectedException)
        {
            // The window is closing: the Craft app goes with it.
        }
    }

    /// <summary>Reads the color scheme of the OS theme around the window.</summary>
    /// <returns><c>light</c> or <c>dark</c>.</returns>
    public async Task<string> GetColorSchemeAsync()
    {
        if (_frame is null)
        {
            return "dark";
        }

        try
        {
            return await _frame.InvokeAsync<string>("colorScheme");
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException)
        {
            return "dark";
        }
    }

    private Task OnKeyAsync(string? json)
    {
        if (KeyPressed is null || string.IsNullOrWhiteSpace(json))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.CompletedTask;
            }

            string? Text(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
            ReservedKey key = new(Text("key"), Text("code"), Flag("ctrl"), Flag("alt"), Flag("shift"), Flag("meta"), ReservedKeyTarget.App);
            return KeyPressed(key);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }
    }

    private Task OnErrorAsync(string? json)
    {
        if (ErrorReported is null || string.IsNullOrWhiteSpace(json))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ErrorReported(new FrameError(json, null));
            }

            string message = root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? string.Empty
                : string.Empty;
            string? id = root.TryGetProperty("id", out JsonElement value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            } : null;
            return ErrorReported(new FrameError(message, id));
        }
        catch (JsonException)
        {
            // Not JSON: the text is the error itself.
            return ErrorReported(new FrameError(json, null));
        }
    }

    private Task OnSettingAsync(string? json)
    {
        if (SettingChanged is null || string.IsNullOrWhiteSpace(json))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("key", out JsonElement key)
                || key.ValueKind != JsonValueKind.String)
            {
                return Task.CompletedTask;
            }

            string? value = root.TryGetProperty("value", out JsonElement text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            return SettingChanged(key.GetString()!, value);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }
    }

    // A JSException message carries the JavaScript stack after the first line.
    private static string FirstLine(string message)
    {
        int end = message.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? message : message[..end]).Trim();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_frame is not null)
            {
                await _frame.InvokeVoidAsync("dispose");
                await _frame.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is gone: so is the iframe.
        }
        finally
        {
            _frame = null;
            _module = null;
            _self?.Dispose();
            _self = null;
        }
    }
}
