
namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>A Craft app's settings for one Kazo user, in ~/.config/&lt;appId&gt;/web-storage.json.</summary>
public sealed class WebStorageFileTests
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _folders = [];

    private WebStorageFile Create() => new(
        "/home/neo/",
        "ca.kazo.word",
        (path, _) => _files.TryGetValue(path, out string? text) ? Task.FromResult(text) : Task.FromException<string>(new FileNotFoundException(path)),
        (path, text, _) =>
        {
            _files[path] = text;
            return Task.CompletedTask;
        },
        (path, _) =>
        {
            _folders.Add(path);
            return Task.CompletedTask;
        });

    [Fact]
    public void The_file_is_in_the_app_s_own_config_folder()
    {
        WebStorageFile file = Create();

        Assert.Equal("/home/neo/.config/ca.kazo.word", file.Directory);
        Assert.Equal("/home/neo/.config/ca.kazo.word/web-storage.json", file.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void An_app_id_that_is_not_a_folder_name_is_refused(string appId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new WebStorageFile("/home/neo", appId));
    }

    [Fact]
    public async Task No_file_yet_gives_no_settings()
    {
        Assert.Empty(await Create().LoadAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public async Task A_corrupt_file_gives_no_settings_rather_than_no_WordCraft(string text)
    {
        _files["/home/neo/.config/ca.kazo.word/web-storage.json"] = text;

        Assert.Empty(await Create().LoadAsync());
    }

    [Fact]
    public async Task What_WordCraft_writes_is_saved_and_read_back()
    {
        WebStorageFile file = Create();
        await file.LoadAsync();

        Assert.True(file.Set("wordcraft.ui", """{"theme":"dark"}"""));
        await file.SaveAsync();
        IReadOnlyDictionary<string, string> read = await Create().LoadAsync();

        Assert.Equal("""{"theme":"dark"}""", read["wordcraft.ui"]);
        Assert.Contains("/home/neo/.config/ca.kazo.word", _folders);
    }

    [Fact]
    public async Task A_save_that_starts_while_another_is_writing_waits_and_writes_the_latest_values()
    {
        // The file system can take longer than the window's half second between two saves: the
        // first save's write must not land after the second one and put back older values.
        TaskCompletionSource firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> written = [];
        int writing = 0;
        int overlaps = 0;
        WebStorageFile file = new(
            "/home/neo/",
            "ca.kazo.word",
            (path, _) => Task.FromException<string>(new FileNotFoundException(path)),
            async (path, text, _) =>
            {
                if (Interlocked.Increment(ref writing) > 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                if (written.Count == 0 && !firstWrite.Task.IsCompleted)
                {
                    await firstWrite.Task;
                }

                written.Add(text);
                Interlocked.Decrement(ref writing);
            },
            (_, _) => Task.CompletedTask);

        file.Set("wordcraft.ui", """{"statusCount":"words"}""");
        Task first = file.SaveAsync();
        file.Set("wordcraft.ui", """{"statusCount":"characters"}""");
        Task second = file.SaveAsync();
        firstWrite.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(0, overlaps);
        Assert.Contains("characters", written[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_value_again_is_no_change_and_a_removal_is_one()
    {
        WebStorageFile file = Create();
        await file.LoadAsync();
        file.Set("a", "1");

        Assert.False(file.Set("a", "1"));
        Assert.True(file.Set("a", null));
        Assert.False(file.Set("a", null));
        Assert.Empty(file.Entries);
    }

    [Fact]
    public async Task Values_that_are_not_text_are_left_out()
    {
        _files["/home/neo/.config/ca.kazo.word/web-storage.json"] = """{"a":"1","b":2,"c":null}""";

        IReadOnlyDictionary<string, string> read = await Create().LoadAsync();

        Assert.Equal(["a"], read.Keys);
    }
}
