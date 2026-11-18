using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>
/// ADR 0254: each user's event journal, <c>/var/log/users/&lt;id&gt;/events.log</c>, written by an
/// app through its own context and readable by that user only.
/// </summary>
public sealed class UserEventLogTests
{
    private const string VmAppId = "org.example.vm-manager";
    private const string WineAppId = "org.example.wine";

    [Fact]
    public async Task Write_puts_the_event_in_the_journal_of_the_user_with_the_app_of_the_context()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway gateway = harness.Gateway(harness.Alice, VmAppId, processId: 42, sender: "org.kazoos.file-explorer");

        gateway.Write(new KazoUserEvent(
            "x86.vm.started",
            "Windows 98 started",
            new KazoEventSubject("vm", "4d2c", "Windows 98"),
            KazoEventSeverity.Info,
            new Dictionary<string, string> { ["memory"] = "64 MiB" }));
        await harness.Writer.FlushAsync();

        string path = $"/var/log/users/{harness.Alice.UserId}/events.log";
        string line = Assert.Single(await harness.Files.ReadLinesAsync(path));
        Assert.Equal(
            "2026-10-10T03:11:42.123Z INFO  org.example.vm-manager[42]: Windows 98 started {kind=x86.vm.started, memory=64 MiB, sender=org.kazoos.file-explorer, subject.id=4d2c, subject.name=Windows 98, subject.type=vm}",
            line);
        FileSystemEntrySnapshot file = (await harness.Files.StatAsync(path))!;
        Assert.Equal(harness.Alice.UserId.ToString(), file.Metadata.OwnerId);
        Assert.Equal(harness.Alice.PrimaryGroupId.ToString(), file.Metadata.GroupId);
        Assert.Equal(FileSystemPermissions.FromMode(0b110_000_000), file.Metadata.Permissions);
        foreach (string directory in new[] { "/var/log/users", $"/var/log/users/{harness.Alice.UserId}" })
        {
            FileSystemEntrySnapshot entry = (await harness.Files.StatAsync(directory))!;
            Assert.Equal("system", entry.Metadata.OwnerId);
            Assert.Equal(FileSystemPermissions.FromMode(0b111_001_001), entry.Metadata.Permissions);
        }
    }

    [Fact]
    public async Task The_user_reads_their_journal_but_another_user_can_neither_read_it_nor_list_the_journals()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Gateway(harness.Alice, WineAppId).Write(new KazoUserEvent("wine.program.added", "setup.exe added"));
        await harness.Writer.FlushAsync();
        VirtualPath path = LogFileTarget.UserEventsPath(harness.Alice.UserId);

        string own = await harness.Files.ReadTextAsync(path.Value, harness.UserContext(harness.Alice));
        Assert.Contains("setup.exe added", own, StringComparison.Ordinal);

        FileSystemResult<FileSystemContentReadHandle> read = await harness.Files.Service.ReadAsync(
            new FileSystemReadRequest(path), harness.UserContext(harness.Bob));
        Assert.Equal(FileSystemErrorCode.PermissionDenied, read.Error?.Code);
        FileSystemResult<FileSystemDirectorySnapshot> list = await harness.Files.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(LogFileTarget.UserLogDirectory), harness.UserContext(harness.Bob));
        Assert.Equal(FileSystemErrorCode.PermissionDenied, list.Error?.Code);
    }

    [Fact]
    public async Task ReadAsync_returns_only_the_lines_of_the_calling_app_whatever_the_filter_asks()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway vm = harness.Gateway(harness.Alice, VmAppId);
        AppUserEventsGateway wine = harness.Gateway(harness.Alice, WineAppId);
        vm.Write(new KazoUserEvent("x86.vm.started", "started", new KazoEventSubject("vm", "a", "Alpha")));
        wine.Write(new KazoUserEvent("wine.program.added", "added"));
        vm.Write(new KazoUserEvent("x86.vm.stopped", "stopped", new KazoEventSubject("vm", "b", "Beta"), KazoEventSeverity.Warning));

        IReadOnlyList<KazoUserEventEntry> all = await vm.ReadAsync(new KazoUserEventFilter { AppId = WineAppId });
        IReadOnlyList<KazoUserEventEntry> beta = await vm.ReadAsync(new KazoUserEventFilter { SubjectType = "vm", SubjectId = "b" });
        IReadOnlyList<KazoUserEventEntry> last = await vm.ReadAsync(new KazoUserEventFilter { Limit = 1 });

        Assert.Equal(["x86.vm.started", "x86.vm.stopped"], all.Select(entry => entry.Kind));
        Assert.All(all, entry => Assert.Equal(VmAppId, entry.AppId));
        KazoUserEventEntry stopped = Assert.Single(beta);
        Assert.Equal(new KazoEventSubject("vm", "b", "Beta"), stopped.Subject);
        Assert.Equal(KazoEventSeverity.Warning, stopped.Severity);
        Assert.Equal("x86.vm.stopped", Assert.Single(last).Kind);
    }

    [Fact]
    public async Task An_app_without_events_write_can_neither_write_nor_read()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway gateway = harness.Gateway(harness.Alice, VmAppId, granted: []);

        AppGatewayAccessDeniedException write = Assert.Throws<AppGatewayAccessDeniedException>(
            () => gateway.Write(new KazoUserEvent("x86.vm.started", "started")));
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(async () => await gateway.ReadAsync(new KazoUserEventFilter()));

        Assert.Equal(AppCapabilities.EventsWrite, write.Capability);
        await harness.Writer.FlushAsync();
        Assert.Null(await harness.Files.StatAsync($"/var/log/users/{harness.Alice.UserId}"));
    }

    [Fact]
    public async Task KazoEvents_writes_through_the_ambient_app_and_turns_a_refusal_into_an_unauthorized_error()
    {
        Harness harness = await Harness.CreateAsync();
        AppManifest manifest = new()
        {
            Id = VmAppId,
            Name = "VM manager",
            Version = "1.0.0",
            PublisherId = "org.example",
            Description = "User events test application.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Test", "Test.Window"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = [AppCapabilities.EventsWrite],
        };
        MinimalAppExecutionContext allowed = new(manifest, userEvents: harness.Gateway(harness.Alice, VmAppId));
        MinimalAppExecutionContext denied = new(manifest, userEvents: harness.Gateway(harness.Alice, VmAppId, granted: []));

        using (KazoExecutionContext.Enter(allowed))
        {
            KazoEvents.Write(new KazoUserEvent("x86.vm.started", "started"));
            Assert.Equal("x86.vm.started", Assert.Single(await KazoEvents.ReadAsync()).Kind);
        }

        using (KazoExecutionContext.Enter(denied))
        {
            KazoUnauthorizedAccessException error = Assert.Throws<KazoUnauthorizedAccessException>(
                () => KazoEvents.Write(new KazoUserEvent("x86.vm.started", "started")));
            Assert.Equal(AppCapabilities.EventsWrite, error.RequiredCapability);
            await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => KazoEvents.ReadAsync());
        }
    }

    [Theory]
    [InlineData("x86 vm started", "message", "")]
    [InlineData("", "message", "")]
    [InlineData("x86.vm.started", " ", "")]
    [InlineData("x86.vm.started", "message", KazoUserEventProperties.Sender)]
    [InlineData("x86.vm.started", "message", KazoUserEventProperties.Kind)]
    [InlineData("kazoos.permission.granted", "message", "")]
    [InlineData("KAZOOS.role.granted", "message", "")]
    public async Task A_malformed_event_or_a_property_of_the_os_is_refused(string kind, string message, string property)
    {
        Harness harness = await Harness.CreateAsync();
        Dictionary<string, string>? properties = property.Length == 0 ? null : new() { [property] = "forged" };

        Assert.Throws<ArgumentException>(
            () => harness.Gateway(harness.Alice, VmAppId).Write(new KazoUserEvent(kind, message, Properties: properties)));
    }

    [Fact]
    public async Task The_journal_rotates_like_every_other_log_and_the_new_file_stays_the_users()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway gateway = harness.Gateway(harness.Alice, VmAppId);
        gateway.Write(new KazoUserEvent("x86.vm.started", "first"));
        await harness.Writer.FlushAsync();
        string path = LogFileTarget.UserEventsPath(harness.Alice.UserId).Value;
        string first = Assert.Single(await harness.Files.ReadLinesAsync(path));
        await harness.Files.WriteTextAsync(path, first + "\n" + new string('y', (int)LogRotationPolicy.MaxFileBytes) + "\n", 0b110_000_000);

        gateway.Write(new KazoUserEvent("x86.vm.stopped", "second"));
        await harness.Writer.FlushAsync();

        Assert.StartsWith(first, await harness.Files.ReadTextAsync(path + ".1"), StringComparison.Ordinal);
        FileSystemEntrySnapshot active = (await harness.Files.StatAsync(path))!;
        Assert.Equal(harness.Alice.UserId.ToString(), active.Metadata.OwnerId);
        Assert.Equal(FileSystemPermissions.FromMode(0b110_000_000), active.Metadata.Permissions);
        IReadOnlyList<KazoUserEventEntry> both = await gateway.ReadAsync(new KazoUserEventFilter());
        Assert.Equal(["first", "second"], both.Select(entry => entry.Message));
    }

    [Fact]
    public async Task Deleting_the_journal_removes_the_directory_of_the_account_and_its_archives()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Gateway(harness.Alice, VmAppId).Write(new KazoUserEvent("x86.vm.started", "started"));
        harness.Gateway(harness.Bob, VmAppId).Write(new KazoUserEvent("x86.vm.started", "started"));
        await harness.Writer.FlushAsync();
        string path = LogFileTarget.UserEventsPath(harness.Alice.UserId).Value;
        await harness.Files.WriteTextAsync(path + ".1", "archive\n", 0b110_000_000);

        await harness.Log.DeleteAsync(harness.Alice.UserId);

        Assert.Null(await harness.Files.StatAsync($"/var/log/users/{harness.Alice.UserId}"));
        Assert.NotNull(await harness.Files.StatAsync(LogFileTarget.UserEventsPath(harness.Bob.UserId).Value));
    }

    [Fact]
    public async Task An_app_writing_too_fast_is_cut_like_journald_and_the_drop_is_in_syslog()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway gateway = harness.Gateway(harness.Alice, VmAppId);
        for (int index = 0; index < FileUserEventLog.RateLimitBurst + 50; index++)
        {
            gateway.Write(new KazoUserEvent("x86.vm.tick", $"tick {index}"));
        }

        // Another app of the same user keeps its own window.
        harness.Gateway(harness.Alice, WineAppId).Write(new KazoUserEvent("wine.program.added", "added"));
        harness.Files.Clock.Advance(tickCount: 300); // 100 ms ticks: the 30 s window
        gateway.Write(new KazoUserEvent("x86.vm.tick", "after the window"));
        await harness.Writer.FlushAsync();

        IReadOnlyList<KazoUserEventEntry> written = await harness.Log.ReadAsync(harness.Alice.UserId, new KazoUserEventFilter { Limit = 5000 });
        Assert.Equal(FileUserEventLog.RateLimitBurst + 2, written.Count);
        Assert.Equal("after the window", written[^1].Message);
        string syslog = await harness.Files.ReadTextAsync("/var/log/syslog");
        Assert.Contains($"suppressed 50 events of {VmAppId} for one user", syslog, StringComparison.Ordinal);
        Assert.DoesNotContain("tick 2", syslog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_journal_too_often_is_refused_for_a_while()
    {
        Harness harness = await Harness.CreateAsync();
        for (int index = 0; index < FileUserEventLog.ReadLimitBurst; index++)
        {
            await harness.Log.ReadAsync(harness.Alice.UserId, new KazoUserEventFilter());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Log.ReadAsync(harness.Alice.UserId, new KazoUserEventFilter()));
        harness.Files.Clock.Advance(tickCount: 300); // 100 ms ticks: the 30 s window
        Assert.Empty(await harness.Log.ReadAsync(harness.Alice.UserId, new KazoUserEventFilter()));
    }

    [Fact]
    public async Task An_os_event_is_shortened_to_fit_and_a_bad_one_never_throws()
    {
        Harness harness = await Harness.CreateAsync();
        UserEventOwner alice = UserEventOwner.Of(harness.Alice);
        UserEventAuthor os = new("org.kazoos.sync");
        string longPath = string.Join('/', Enumerable.Repeat("very-long-folder-name", 40)) + "/note.txt";

        bool written = harness.Log.TryWriteSystem(alice, os, new KazoUserEvent(
            "kazoos.sync.conflict-resolved", new string('m', 5000), new KazoEventSubject("file", longPath, "note.txt"),
            KazoEventSeverity.Warning, new Dictionary<string, string> { ["path"] = longPath }));
        bool malformed = harness.Log.TryWriteSystem(alice, os, new KazoUserEvent("kazoos.bad kind", "message"));
        await harness.Writer.FlushAsync();

        Assert.True(written);
        Assert.False(malformed);
        KazoUserEventEntry entry = Assert.Single(await harness.Log.ReadAsync(harness.Alice.UserId, new KazoUserEventFilter()));
        Assert.Equal(FileUserEventLog.MaxMessageLength, entry.Message.Length);
        Assert.EndsWith("…", entry.Subject!.Id, StringComparison.Ordinal);
        Assert.Equal(FileUserEventLog.MaxPropertyValueLength, entry.Properties["path"].Length);
        Assert.Contains("could not write the event kazoos.bad kind", await harness.Files.ReadTextAsync("/var/log/syslog"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_written_after_the_account_is_deleted_brings_its_directory_back()
    {
        Harness harness = await Harness.CreateAsync();
        AppUserEventsGateway gateway = harness.Gateway(harness.Alice, VmAppId);
        gateway.Write(new KazoUserEvent("x86.vm.started", "before"));

        await harness.Log.DeleteAsync(harness.Alice.UserId);
        gateway.Write(new KazoUserEvent("x86.vm.stopped", "after"));
        Assert.False(harness.Log.TryWriteSystem(UserEventOwner.Of(harness.Alice), new UserEventAuthor("org.kazoos.accounts"), new KazoUserEvent("kazoos.role.granted", "after")));
        await harness.Writer.FlushAsync();

        Assert.Null(await harness.Files.StatAsync($"/var/log/users/{harness.Alice.UserId}"));
        await harness.Log.DeleteAsync(harness.Alice.UserId);
    }

    [Fact]
    public void An_event_line_reads_back_into_the_same_event()
    {
        string line = "2026-10-10T03:11:42.123Z WARN  org.example.wine[7]: Network refused to setup.exe {destination=example.org:443, kind=wine.network.refused, sender=org.kazoos.terminal, subject.id=/home/alice/setup.exe#ab12, subject.name=setup.exe, subject.type=program}";

        Assert.True(KazoUserEventEntry.TryParse(line, out KazoUserEventEntry? entry));
        Assert.Equal(WineAppId, entry.AppId);
        Assert.Equal(7, entry.ProcessId);
        Assert.Equal("wine.network.refused", entry.Kind);
        Assert.Equal(KazoEventSeverity.Warning, entry.Severity);
        Assert.Equal(new KazoEventSubject("program", "/home/alice/setup.exe#ab12", "setup.exe"), entry.Subject);
        Assert.Equal("org.kazoos.terminal", entry.SenderAppId);
        Assert.Equal(new Dictionary<string, string> { ["destination"] = "example.org:443" }, entry.Properties);
        Assert.False(KazoUserEventEntry.TryParse("2026-10-10T03:11:42.123Z INFO  kernel: no kind here", out _));
    }

    /// <summary>A seeded filesystem, the OS's log writer and two users, alice and bob.</summary>
    private sealed class Harness
    {
        private Harness(LogTestFileSystem files)
        {
            Files = files;
            Writer = new LogFileWriter(files.Clock);
            Log = new FileUserEventLog(Writer, files.Clock);
        }

        public LogTestFileSystem Files { get; }

        public LogFileWriter Writer { get; }

        public FileUserEventLog Log { get; }

        public AuthenticatedPrincipal Alice { get; } = Principal("alice");

        public AuthenticatedPrincipal Bob { get; } = Principal("bob");

        public static async Task<Harness> CreateAsync()
        {
            Harness harness = new(await LogTestFileSystem.CreateAsync());
            await harness.Writer.AttachAsync(harness.Files.Service);
            return harness;
        }

        public AppUserEventsGateway Gateway(
            AuthenticatedPrincipal user, string appId, long? processId = null, string? sender = null, string[]? granted = null) =>
            new(Log, new FixedCapabilities(granted ?? [AppCapabilities.EventsWrite]), UserEventOwner.Of(user), appId, processId, () => sender);

        /// <summary>An app of <paramref name="user"/> holding every capability: only the file's mode decides.</summary>
        public FileSystemAuthorizationContext UserContext(AuthenticatedPrincipal user) => new(
            new AppOperationContext
            {
                AppId = "org.kazoos.error-log-viewer",
                UserId = user.UserId.ToString(),
                UserName = user.LoginName.Value,
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = false
            },
            [user.PrimaryGroupId.ToString()],
            Files.Clock.UtcNow);

        private static AuthenticatedPrincipal Principal(string login)
        {
            LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
            return new AuthenticatedPrincipal(
                SessionId.FromGuid(Guid.NewGuid()),
                LocalUserId.FromGuid(Guid.NewGuid()),
                LocalLoginName.Parse(login),
                login,
                AppAuthority.User,
                group,
                [group],
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                DateTimeOffset.UnixEpoch);
        }
    }

    /// <summary>Grants exactly the listed capabilities (the gateway only calls <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
