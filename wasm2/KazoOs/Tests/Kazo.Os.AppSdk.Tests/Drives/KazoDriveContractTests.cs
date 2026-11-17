using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.AppSdk.Tests.Drives;

/// <summary>The public contract of a drive provider (ADR 0282 §2): paths, defaults of a folder, errors.</summary>
public sealed class KazoDriveContractTests
{
    [Fact]
    public void A_path_parses_names_between_slashes_and_writes_them_back()
    {
        KazoDrivePath path = KazoDrivePath.Parse("/Photos/2026/");

        Assert.Equal(["Photos", "2026"], path.Segments);
        Assert.Equal("2026", path.Name);
        Assert.Equal("/Photos/2026", path.ToString());
        Assert.Equal(KazoDrivePath.Parse("Photos"), path.Parent);
        Assert.Equal(KazoDrivePath.Root, path.Parent!.Parent);
        Assert.Null(KazoDrivePath.Root.Parent);
        Assert.True(KazoDrivePath.Parse("/").IsRoot);
        Assert.True(KazoDrivePath.Parse(string.Empty).IsRoot);
        Assert.Equal("/", KazoDrivePath.Root.ToString());
        Assert.Equal(path, KazoDrivePath.Root.Append("Photos").Append("2026"));
        Assert.Equal(path.GetHashCode(), KazoDrivePath.FromSegments(["Photos", "2026"]).GetHashCode());
        Assert.NotEqual(path, KazoDrivePath.Parse("/photos/2026"));
    }

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/a//b")]
    [InlineData("/a\\b")]
    [InlineData("/a\u0000b")]
    [InlineData("/a\nb")]
    public void A_path_never_escapes_the_root_of_the_drive(string text)
    {
        Assert.Throws<ArgumentException>(() => KazoDrivePath.Parse(text));
        Assert.False(KazoDrivePath.TryParse(text, out KazoDrivePath? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void A_name_is_checked_when_a_path_grows()
    {
        Assert.Throws<ArgumentException>(() => KazoDrivePath.Root.Append(".."));
        Assert.Throws<ArgumentException>(() => KazoDrivePath.Root.Append(""));
        Assert.Throws<ArgumentException>(() => KazoDrivePath.Root.Append(new string('a', KazoDrivePath.MaxNameLength + 1)));
        Assert.Throws<ArgumentException>(() => KazoDrivePath.FromSegments(["a", "b/c"]));
        Assert.True(KazoDrivePath.IsValidName("Été 2026.pdf"));
        Assert.False(KazoDrivePath.TryParse(null, out _));
    }

    [Fact]
    public async Task A_folder_creates_a_file_by_stat_then_write_and_refuses_a_taken_name()
    {
        MemoryFolder folder = new();
        KazoDrivePath path = KazoDrivePath.Parse("/new.txt");

        await folder.CreateFileAsync(path);
        KazoDriveException taken = await Assert.ThrowsAsync<KazoDriveException>(async () => await folder.CreateFileAsync(path));

        Assert.Equal(KazoDriveErrorKind.Exists, taken.Kind);
        Assert.Empty(await folder.ReadAsync(path));
        Assert.Equal(1, folder.Writes);
    }

    [Fact]
    public async Task A_folder_moves_nothing_and_reports_no_quota_unless_it_says_so()
    {
        MemoryFolder folder = new();

        Assert.False(await folder.TryMoveAsync(KazoDrivePath.Parse("/a"), KazoDrivePath.Parse("/b")));
        Assert.Null(await folder.GetQuotaAsync());
        await folder.DisposeAsync();
    }

    [Fact]
    public void An_error_keeps_its_kind_and_the_service_failure()
    {
        InvalidOperationException inner = new("503");
        KazoDriveException error = new(KazoDriveErrorKind.Unavailable, "The service did not answer.", inner);

        Assert.Equal(KazoDriveErrorKind.Unavailable, error.Kind);
        Assert.Same(inner, error.InnerException);
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(128L * 1024 * 1024, KazoDriveLimits.MaxFileBytes);
    }

    [Fact]
    public async Task A_provider_opens_a_drive_from_the_request_without_reaching_the_service()
    {
        FakeContext context = new();
        KazoDriveOpenRequest request = new("Work", context) { Root = "/Work", Options = new Dictionary<string, string> { ["region"] = "eu" } };

        await using KazoDriveFolder folder = await new MemoryProvider().OpenAsync(request);

        Assert.Equal(KazoDriveState.Ready, await folder.GetStateAsync());
        Assert.Equal(0, context.TokenRequests);
    }

    /// <summary>A provider whose drives live in memory.</summary>
    private sealed class MemoryProvider : KazoDriveProvider
    {
        public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<KazoDriveFolder>(new MemoryFolder());
    }

    /// <summary>A drive in memory that only overrides what the contract requires.</summary>
    private sealed class MemoryFolder : KazoDriveFolder
    {
        private readonly Dictionary<KazoDrivePath, byte[]> files = [];

        public int Writes { get; private set; }

        public override ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(KazoDriveState.Ready);

        public override ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            files.TryGetValue(path, out byte[]? content)
                ? ValueTask.FromResult(new KazoDriveEntry(path.Name, KazoDriveEntryKind.File, content.Length, null))
                : throw new KazoDriveException(KazoDriveErrorKind.NotFound, $"{path} is missing.");

        public override ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoDriveEntry>>([]);

        public override ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(files[path]);

        public override ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default)
        {
            Writes++;
            files[path] = content.ToArray();
            return ValueTask.CompletedTask;
        }

        public override ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeContext : IKazoDriveProviderContext
    {
        public int TokenRequests { get; private set; }

        public string Type => "memory";

        public KazoDriveAccount? Account => null;

        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            TokenRequests++;
            throw new KazoDriveException(KazoDriveErrorKind.SignInRequired, "A form connection has no token.");
        }

        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

        public void NotifyAvailabilityChanged()
        {
        }
    }
}
