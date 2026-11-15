using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>
/// The C# side of the iframe: what it hands <c>craft-host.js</c> (the contract shared with work
/// item 3) and how it turns the proxy's messages into events.
/// </summary>
public sealed class CraftFrameTests
{
    private static CraftFrameOptions Options => new(CraftFrameOptions.PagePathOf("Kazo.Word"), "WordCraft")
    {
        Language = "fr",
        Parameters = new Dictionary<string, string> { ["author"] = "Neo", ["webgl"] = string.Empty },
        ReservedKeys = [ReservedKeys.NewWindow],
        Settings = new Dictionary<string, string> { ["wordcraft.ui"] = "{}" },
    };

    private static async Task<(CraftFrame Frame, FakeJavaScript Js)> StartedAsync()
    {
        FakeJavaScript js = new();
        CraftFrame frame = new(js);
        await frame.StartAsync(default, Options, CancellationToken.None);
        return (frame, js);
    }

    [Fact]
    public void The_usual_page_is_the_app_s_host_page()
    {
        Assert.Equal("_content/Kazo.GridCraft/host/index.html", CraftFrameOptions.PagePathOf("Kazo.GridCraft"));
        Assert.ThrowsAny<ArgumentException>(() => CraftFrameOptions.PagePathOf(" "));
    }

    [Fact]
    public async Task Start_imports_the_OS_module_and_creates_the_iframe_with_the_options()
    {
        (_, FakeJavaScript js) = await StartedAsync();

        Assert.Equal(("runtime.import", "./_content/Kazo.Os.AppSdk.Craft/craft-host.js"), (js.Calls[0].Identifier, js.Calls[0].Arguments[0]));
        (string identifier, object?[] arguments) = js.Calls[1];
        Assert.Equal("create", identifier);
        Assert.IsType<ElementReference>(arguments[0]);
        Assert.IsType<DotNetObjectReference<CraftFrame>>(arguments[1]);

        // The exact JSON craft-host.js receives.
        using JsonDocument options = JsonDocument.Parse(JsonSerializer.Serialize(arguments[2]));
        JsonElement root = options.RootElement;
        Assert.Equal(
            ["page", "title", "language", "parameters", "reservedKeys", "settings", "exports"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("_content/Kazo.Word/host/index.html", root.GetProperty("page").GetString());
        Assert.Equal("WordCraft", root.GetProperty("title").GetString());
        Assert.Equal("fr", root.GetProperty("language").GetString());
        Assert.Equal("Neo", root.GetProperty("parameters").GetProperty("author").GetString());
        Assert.Equal(string.Empty, root.GetProperty("parameters").GetProperty("webgl").GetString());
        JsonElement key = Assert.Single(root.GetProperty("reservedKeys").EnumerateArray());
        Assert.Equal("app", key.GetProperty("target").GetString());
        Assert.Equal("{}", root.GetProperty("settings").GetProperty("wordcraft.ui").GetString());

        // No Exporting handler: the browser keeps the app's downloads (ADR 0264).
        Assert.False(root.GetProperty("exports").GetBoolean());
    }

    [Fact]
    public async Task A_window_that_handles_exports_asks_the_proxy_for_them()
    {
        FakeJavaScript js = new();
        CraftFrame frame = new(js);
        frame.Exporting += _ => Task.FromResult(true);
        await frame.StartAsync(default, Options, CancellationToken.None);

        using JsonDocument options = JsonDocument.Parse(JsonSerializer.Serialize(js.Calls[1].Arguments[2]));
        Assert.True(options.RootElement.GetProperty("exports").GetBoolean());
    }

    [Fact]
    public async Task An_export_is_never_a_save_and_reports_whether_the_file_was_written()
    {
        CraftFrame frame = new(new FakeJavaScript());
        Assert.False(await frame.OnFrameExport("a.pdf", [1]));

        FrameSave? exported = null;
        bool saved = false;
        frame.Saving += _ =>
        {
            saved = true;
            return Task.FromResult(true);
        };
        frame.Exporting += export =>
        {
            exported = export;
            return Task.FromResult(true);
        };

        Assert.True(await frame.OnFrameExport("a.pdf", [1, 2]));
        Assert.False(saved);
        Assert.Equal("a.pdf", exported!.Name);
        Assert.False(exported.SaveAs);
        Assert.Equal([1, 2], exported.Bytes);
    }

    [Fact]
    public async Task Default_options_send_english_and_no_parameters_keys_or_settings()
    {
        FakeJavaScript js = new();
        await new CraftFrame(js).StartAsync(default, new CraftFrameOptions("page.html", "App"), CancellationToken.None);

        using JsonDocument options = JsonDocument.Parse(JsonSerializer.Serialize(js.Calls[1].Arguments[2]));
        Assert.Equal("en", options.RootElement.GetProperty("language").GetString());
        Assert.Equal(JsonValueKind.Null, options.RootElement.GetProperty("parameters").ValueKind);
        Assert.Empty(options.RootElement.GetProperty("reservedKeys").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, options.RootElement.GetProperty("settings").ValueKind);
    }

    [Fact]
    public async Task Before_start_commands_fail_and_documents_are_not_sent()
    {
        CraftFrame frame = new(new FakeJavaScript());

        FrameCommandResult result = await frame.RequestAsync("ui.inspect");

        Assert.False(result.Ok);
        Assert.Equal("the app is not connected", result.Error);
        Assert.False(await frame.OpenAsync("a.docx", [1]));
        Assert.Equal("dark", await frame.GetColorSchemeAsync());
    }

    [Fact]
    public async Task A_command_answers_with_the_app_s_value()
    {
        (CraftFrame frame, FakeJavaScript js) = await StartedAsync();

        FrameCommandResult result = await frame.RequestAsync("ui.theme", new { theme = "dark" }, TimeSpan.FromSeconds(2.5));

        Assert.True(result.Ok);
        Assert.Equal(1, result.Value.GetProperty("ok").GetInt32());
        (string identifier, object?[] arguments) = js.Calls[^1];
        Assert.Equal("request", identifier);
        Assert.Equal("ui.theme", arguments[0]);
        Assert.Equal(2500, arguments[2]);
    }

    [Fact]
    public async Task A_refused_command_is_a_failed_result_with_the_first_line_of_the_error()
    {
        (CraftFrame frame, FakeJavaScript js) = await StartedAsync();
        js.RequestAnswer = _ => new JSException("unknown command\n    at proxy.js:12");

        FrameCommandResult result = await frame.RequestAsync("nope");

        Assert.False(result.Ok);
        Assert.Equal("unknown command", result.Error);
    }

    [Fact]
    public async Task A_closed_page_is_a_failed_result()
    {
        (CraftFrame frame, FakeJavaScript js) = await StartedAsync();
        js.RequestAnswer = _ => new JSDisconnectedException("gone");

        FrameCommandResult result = await frame.RequestAsync("ui.inspect");

        Assert.Equal(FrameCommandResult.Failure("the window closed"), result);
    }

    [Fact]
    public async Task Messages_become_events()
    {
        CraftFrame frame = new(new FakeJavaScript());
        List<string> seen = [];
        frame.Ready += version => Record($"ready {version}");
        frame.Failed += error => Record($"failed {error}");
        frame.DirtyChanged += dirty => Record($"dirty {dirty}");
        frame.SchemeChanged += scheme => Record($"scheme {scheme}");
        frame.ErrorReported += error => Record($"error {error.Message} {error.Id}");
        frame.SettingChanged += (key, value) => Record($"setting {key}={value ?? "<removed>"}");
        frame.KeyPressed += key => Record($"key {key.Key} {key.Ctrl} {key.Target}");
        frame.PickRequested += pick => Record($"pick {pick.Id} {pick.Accept} {pick.Multiple}");

        await frame.OnFrameMessage(FrameMessages.Ready, "0.18.1");
        await frame.OnFrameMessage(FrameMessages.Failed, "no WebGPU");
        await frame.OnFrameMessage(FrameMessages.Dirty, "true");
        await frame.OnFrameMessage(FrameMessages.Dirty, "false");
        await frame.OnFrameMessage(FrameMessages.Scheme, "light");
        await frame.OnFrameMessage(FrameMessages.Error, """{"error":"disk full","id":"s1"}""");
        await frame.OnFrameMessage(FrameMessages.Error, """{"error":"later","id":7}""");
        await frame.OnFrameMessage(FrameMessages.Error, "plain text");
        await frame.OnFrameMessage(FrameMessages.Setting, """{"key":"wordcraft.ui","value":"{}"}""");
        await frame.OnFrameMessage(FrameMessages.Setting, """{"key":"old","value":null}""");
        await frame.OnFrameMessage(FrameMessages.Key, """{"key":"n","code":"KeyN","ctrl":true}""");
        await frame.OnFrameMessage(FrameMessages.Pick, """{"id":3,"accept":".png","multiple":false}""");
        await frame.OnFrameMessage("unknown", "x");

        Assert.Equal(
            [
                "ready 0.18.1", "failed no WebGPU", "dirty True", "dirty False", "scheme light",
                "error disk full s1", "error later 7", "error plain text ",
                "setting wordcraft.ui={}", "setting old=<removed>", "key n True App", "pick 3 .png False",
            ],
            seen);

        Task Record(string line)
        {
            seen.Add(line);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(FrameMessages.Key, "not json")]
    [InlineData(FrameMessages.Key, "[1]")]
    [InlineData(FrameMessages.Setting, "\"text\"")]
    [InlineData(FrameMessages.Setting, """{"value":"x"}""")]
    [InlineData(FrameMessages.Pick, "{}")]
    [InlineData(FrameMessages.Scheme, null)]
    [InlineData(FrameMessages.Key, null)]
    public async Task A_malformed_message_raises_nothing(string type, string? value)
    {
        CraftFrame frame = new(new FakeJavaScript());
        int raised = 0;
        frame.KeyPressed += _ => Count();
        frame.SettingChanged += (_, _) => Count();
        frame.PickRequested += _ => Count();
        frame.SchemeChanged += _ => Count();

        await frame.OnFrameMessage(type, value);

        Assert.Equal(0, raised);

        Task Count()
        {
            raised++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_save_reports_whether_the_file_was_written()
    {
        CraftFrame frame = new(new FakeJavaScript());
        Assert.False(await frame.OnFrameSave("a.docx", false, [1]));

        FrameSave? received = null;
        frame.Saving += save =>
        {
            received = save;
            return Task.FromResult(true);
        };

        Assert.True(await frame.OnFrameSave("a.docx", true, [1, 2]));
        Assert.Equal("a.docx", received!.Name);
        Assert.True(received.SaveAs);
        Assert.Equal([1, 2], received.Bytes);
    }

    [Fact]
    public async Task Documents_picks_and_focus_reach_the_frame()
    {
        (CraftFrame frame, FakeJavaScript js) = await StartedAsync();

        Assert.True(await frame.OpenAsync("report.docx", [7]));
        await frame.AnswerPickAsync(3, [new PickedFile("a.png", "image/png", [9])]);
        await frame.AnswerPickAsync(4, []);
        await frame.FocusAsync();
        Assert.Equal("light", await frame.GetColorSchemeAsync());
        await frame.DisposeAsync();

        Assert.Equal(["open", "answerPick", "answerPick", "focus", "colorScheme", "dispose"], js.Calls.Skip(2).Select(call => call.Identifier));
        Assert.Null(js.Calls[4].Arguments[1]);
        Assert.False(await frame.OpenAsync("again.docx", [1]));
    }
}
