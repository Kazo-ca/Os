using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.DragDrop;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>ADR 0287 §2 and §3: what a <see cref="KazoFileDropZone"/> hands to the app for a drop.</summary>
public sealed class KazoFileDropZoneTests
{
    private const string ExplorerDrag = """
        {"files":[{"sourceAppId":"org.kazoos.system.files","filePath":"/home/user/a.mp3","fileName":"a.mp3","sizeBytes":12,"mimeType":"audio/mpeg"},
                  {"sourceAppId":"org.kazoos.system.files","filePath":"/home/user/list.m3u","fileName":"list.m3u","sizeBytes":4,"mimeType":null},
                  {"sourceAppId":"org.kazoos.system.files","filePath":"/home/user/notes.txt","fileName":"notes.txt","sizeBytes":1,"mimeType":"text/plain"}],
         "folders":[{"sourceAppId":"org.kazoos.system.files","folderPath":"/home/user/Music","folderName":"Music","itemCount":-1}]}
        """;

    [Fact]
    public void A_drag_from_the_file_explorer_gives_its_files_and_folders()
    {
        KazoFilesDroppedEventArgs drop = Assert.IsType<KazoFilesDroppedEventArgs>(
            KazoFileDropZone.CreateDrop(ExplorerDrag, null, null, NoHostFile));

        Assert.Equal(["/home/user/a.mp3", "/home/user/list.m3u", "/home/user/notes.txt"], drop.Files.Select(file => file.FilePath));
        Assert.Equal("/home/user/Music", Assert.Single(drop.Folders).FolderPath);
        Assert.Empty(drop.HostFiles);
    }

    [Fact]
    public void Accept_keeps_the_matching_files_and_every_folder()
    {
        KazoFilesDroppedEventArgs drop = Assert.IsType<KazoFilesDroppedEventArgs>(
            KazoFileDropZone.CreateDrop(ExplorerDrag, null, ["audio/*", ".m3u"], NoHostFile));

        Assert.Equal(["a.mp3", "list.m3u"], drop.Files.Select(file => file.FileName));
        Assert.Single(drop.Folders);
    }

    [Fact]
    public void A_drop_where_nothing_matches_gives_nothing()
    {
        const string json = """{"files":[{"sourceAppId":"","filePath":"/home/user/notes.txt","fileName":"notes.txt","sizeBytes":1,"mimeType":"text/plain"}]}""";

        Assert.Null(KazoFileDropZone.CreateDrop(json, null, ["audio/*"], NoHostFile));
        Assert.Null(KazoFileDropZone.CreateDrop("not json", "not json either", null, NoHostFile));
        Assert.Null(KazoFileDropZone.CreateDrop(null, "[]", null, NoHostFile));
    }

    [Fact]
    public async Task Files_from_the_computer_are_filtered_and_read_through_the_zone()
    {
        List<(int Index, long MaxBytes)> opened = [];
        Task<Stream> Open(int index, long maxBytes, CancellationToken cancellationToken)
        {
            opened.Add((index, maxBytes));
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        }

        const string hostFiles = """[{"name":"song.ogg","size":3,"type":"audio/ogg"},{"name":"photo.png","size":9,"type":"image/png"},{"name":"mix.m3u","size":5,"type":""}]""";

        KazoFilesDroppedEventArgs drop = Assert.IsType<KazoFilesDroppedEventArgs>(
            KazoFileDropZone.CreateDrop(null, hostFiles, ["audio/*", ".M3U"], Open));

        Assert.Equal(["song.ogg", "mix.m3u"], drop.HostFiles.Select(file => file.Name));
        Assert.Equal("audio/ogg", drop.HostFiles[0].ContentType);
        Assert.Null(drop.HostFiles[1].ContentType);
        await using Stream stream = await drop.HostFiles[1].OpenReadStreamAsync(1024, CancellationToken.None);
        Assert.Equal([(2, 1024L)], opened);
    }

    [Theory]
    [InlineData("a.MP3", null, ".mp3", true)]
    [InlineData("a.mp3", "audio/mpeg", "audio/mpeg", true)]
    [InlineData("a.mp3", "audio/mpeg", "audio/*", true)]
    [InlineData("a.mp3", "audio/mpeg", "video/*", false)]
    [InlineData("a.mp3", null, "audio/*", false)]
    [InlineData("a.txt", "text/plain", " ", false)]
    public void Accept_reads_extensions_and_mime_types(string fileName, string? mimeType, string entry, bool matches)
    {
        Assert.Equal(matches, KazoFileAccept.Matches([entry], fileName, mimeType));
        Assert.True(KazoFileAccept.Matches(null, fileName, mimeType));
        Assert.True(KazoFileAccept.Matches([], fileName, mimeType));
    }

    [Fact]
    public void The_arguments_are_empty_only_with_nothing_in_them()
    {
        Assert.True(new KazoFilesDroppedEventArgs([], [], []).IsEmpty);
        Assert.False(new KazoFilesDroppedEventArgs([], [new VirtualFolderDragPayload("", "/tmp", "tmp", 0)], []).IsEmpty);
        Assert.False(new KazoFilesDroppedEventArgs([], [], [new KazoHostFile("a", 0, null, (_, _) => Task.FromResult(Stream.Null))]).IsEmpty);
    }

    private static Task<Stream> NoHostFile(int index, long maxBytes, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("no computer file in this drop");
}
