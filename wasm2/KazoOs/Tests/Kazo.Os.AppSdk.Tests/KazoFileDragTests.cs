using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.DragDrop;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>ADR 0287 §1: the public format of a Kazo OS file drag.</summary>
public sealed class KazoFileDragTests
{
    [Fact]
    public void The_json_the_file_explorer_writes_reads_back()
    {
        const string json = """
            {"files":[{"sourceAppId":"org.kazoos.system.files","filePath":"/home/user/a.mp3","fileName":"a.mp3","sizeBytes":12,"mimeType":"audio/mpeg"}],
             "folders":[{"sourceAppId":"org.kazoos.system.files","folderPath":"/home/user/Music","folderName":"Music","itemCount":-1}]}
            """;

        Assert.True(KazoFileDrag.TryParse(json, out KazoFileDragData data));

        VirtualFileDragPayload file = Assert.Single(data.Files);
        Assert.Equal("/home/user/a.mp3", file.FilePath);
        Assert.Equal(12, file.SizeBytes);
        Assert.Equal("audio/mpeg", file.MimeType);
        Assert.Equal("/home/user/Music", Assert.Single(data.Folders).FolderPath);
    }

    [Fact]
    public void Serialize_writes_camel_case_and_round_trips()
    {
        KazoFileDragData data = new(
            [new VirtualFileDragPayload("org.example.app", "/home/user/a.txt", "a.txt", 3, null)],
            []);

        string json = KazoFileDrag.Serialize(data);

        Assert.Contains("\"filePath\":\"/home/user/a.txt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"folders\":[]", json, StringComparison.Ordinal);
        Assert.True(KazoFileDrag.TryParse(json, out KazoFileDragData copy));
        Assert.Equal(data.Files, copy.Files);
    }

    [Fact]
    public void A_missing_list_reads_as_empty()
    {
        Assert.True(KazoFileDrag.TryParse("""{"folders":[{"sourceAppId":"","folderPath":"/tmp","folderName":"tmp","itemCount":0}]}""", out KazoFileDragData data));

        Assert.Empty(data.Files);
        Assert.Single(data.Folders);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"files":[{"sourceAppId":"","filePath":"relative/a.txt","fileName":"a.txt","sizeBytes":0}]}""")]
    [InlineData("""{"files":[{"sourceAppId":"","filePath":"/home/user/a.txt","fileName":" ","sizeBytes":0}]}""")]
    [InlineData("""{"folders":[null]}""")]
    public void An_invalid_drag_is_refused_without_throwing(string? json)
    {
        Assert.False(KazoFileDrag.TryParse(json, out KazoFileDragData data));
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void A_drag_of_more_than_the_maximum_is_refused()
    {
        VirtualFileDragPayload[] files = [.. Enumerable.Range(0, KazoFileDrag.MaxItems + 1)
            .Select(index => new VirtualFileDragPayload("", $"/home/user/{index}.txt", $"{index}.txt", 0, null))];

        Assert.False(KazoFileDrag.TryParse(KazoFileDrag.Serialize(new KazoFileDragData(files, [])), out _));
    }

    [Fact]
    public async Task CreateAsync_reads_each_path_through_the_app_and_names_it_as_the_source()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/user")
            .WithDirectory("/home/user/Music")
            .WithFile("/home/user/a.txt", "abc");
        MinimalAppExecutionContext app = new(Manifest(), fileSystem);

        KazoFileDragData data;
        using (KazoExecutionContext.Enter(app, "/home/user"))
        {
            data = await KazoFileDrag.CreateAsync(["a.txt", "/home/user/missing.txt", ""], ["/home/user/Music", "/home/user/a.txt", "/"]);
        }

        VirtualFileDragPayload file = Assert.Single(data.Files);
        Assert.Equal(new VirtualFileDragPayload("org.kazoos.test-drag", "/home/user/a.txt", "a.txt", 3, file.MimeType), file);
        VirtualFolderDragPayload folder = Assert.Single(data.Folders);
        Assert.Equal("/home/user/Music", folder.FolderPath);
        Assert.Equal("Music", folder.FolderName);
        Assert.Equal("org.kazoos.test-drag", folder.SourceAppId);
    }

    [Fact]
    public async Task CreateAsync_with_nothing_readable_gives_an_empty_drag()
    {
        MinimalAppExecutionContext app = new(Manifest(), new FakeAppFileSystemGateway().WithDirectory("/home/user"));

        using (KazoExecutionContext.Enter(app, "/home/user"))
        {
            Assert.True((await KazoFileDrag.CreateAsync(["/home/user/none.txt"], null)).IsEmpty);
            Assert.True((await KazoFileDrag.CreateAsync(null, null)).IsEmpty);
        }
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.kazoos.test-drag",
        Name = "Drag App",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "File drag test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Tests", "DragApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.FileSystemUserHomeRead]
    };
}
