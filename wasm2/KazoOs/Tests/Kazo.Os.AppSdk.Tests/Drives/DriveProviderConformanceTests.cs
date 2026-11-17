using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Testing.Drives;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Drives;

/// <summary>
/// The public conformance suite (ADR 0282 §2, work item 7) run on the fake provider of work item 4,
/// with every simulation (network, sign-in, large file), so no case is skipped.
/// </summary>
public sealed class InMemoryDriveConformanceTests : KazoDriveProviderTests, IDisposable
{
    private const string Token = "token-1";
    private readonly InMemoryDriveService _service = new(totalBytes: 10_000_000);
    private string _acceptedToken = Token;

    public InMemoryDriveConformanceTests() => _service.AcceptedToken = () => _acceptedToken;

    public static TheoryData<string> Cases => new(CaseNames);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_fake_provider_conforms(string name)
    {
        KazoDriveConformanceResult result = await RunAsync(name);

        Assert.Equal(KazoDriveConformanceOutcome.Passed, result.Outcome);
    }

    [Fact]
    public async Task Run_all_returns_one_result_per_case_and_leaves_no_working_folder()
    {
        IReadOnlyList<KazoDriveConformanceResult> results = await RunAllAsync();

        Assert.Equal(CaseNames, results.Select(static r => r.Case));
        await using KazoDriveFolder drive = await OpenDriveAsync(CancellationToken.None);
        Assert.Empty(await drive.ListAsync(KazoDrivePath.Root));
    }

    [Fact]
    public async Task An_unknown_case_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync("no-such-case"));
    }

    public void Dispose() => _service.Dispose();

    protected override ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken) =>
        new InMemoryDriveProvider().OpenAsync(
            new KazoDriveOpenRequest("Test", new KazoDriveTestContext("memory") { AccessToken = Token })
            {
                Options = new Dictionary<string, string> { [InMemoryDriveProvider.ServiceOption] = _service.Key }
            },
            cancellationToken);

    protected override ValueTask<bool> SetServiceReachableAsync(bool reachable, CancellationToken cancellationToken)
    {
        _service.Online = reachable;
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> ExpireSignInAsync(CancellationToken cancellationToken)
    {
        _acceptedToken = "token-2";
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> PlaceLargeFileAsync(KazoDrivePath path, long length, CancellationToken cancellationToken)
    {
        _service.SeedLargeFile(path.ToString(), length);
        return ValueTask.FromResult(true);
    }

    protected override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        _service.Online = true;
        _acceptedToken = Token;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The suite catches a provider that breaks the contract, and skips what a test class cannot simulate.</summary>
public sealed class DriveProviderConformanceSuiteTests : IDisposable
{
    private readonly InMemoryDriveService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task A_provider_that_reports_missing_files_as_other_errors_fails_the_missing_case()
    {
        Suite suite = new(_service, breakNotFound: true);

        KazoDriveConformanceException failure = await Assert.ThrowsAsync<KazoDriveConformanceException>(
            () => suite.RunAsync(KazoDriveConformanceCases.Missing));

        Assert.Equal(KazoDriveConformanceCases.Missing, failure.Case);
        Assert.Contains("NotFound", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(KazoDriveConformanceCases.Offline)]
    [InlineData(KazoDriveConformanceCases.SignInExpired)]
    [InlineData(KazoDriveConformanceCases.FileTooLarge)]
    public async Task A_case_the_class_cannot_simulate_is_skipped_with_a_reason(string name)
    {
        KazoDriveConformanceResult result = await new Suite(_service, breakNotFound: false).RunAsync(name);

        Assert.Equal(KazoDriveConformanceOutcome.Skipped, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task The_test_context_gives_secrets_and_refuses_a_token_it_does_not_have()
    {
        KazoDriveTestContext context = new("webdav") { Secrets = { ["password"] = "s3cret" } };

        Assert.Equal("s3cret", await context.GetSecretAsync("password"));
        Assert.Null(await context.GetSecretAsync("other"));
        KazoDriveException refused = await Assert.ThrowsAsync<KazoDriveException>(() => context.GetAccessTokenAsync().AsTask());
        Assert.Equal(KazoDriveErrorKind.SignInRequired, refused.Kind);
        context.NotifyAvailabilityChanged();
        Assert.Equal(1, context.AvailabilityNotifications);
    }

    private sealed class Suite(InMemoryDriveService service, bool breakNotFound) : KazoDriveProviderTests
    {
        protected override async ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken)
        {
            KazoDriveFolder folder = await new InMemoryDriveProvider().OpenAsync(
                new KazoDriveOpenRequest("Test", new KazoDriveTestContext("memory"))
                {
                    Options = new Dictionary<string, string> { [InMemoryDriveProvider.ServiceOption] = service.Key }
                },
                cancellationToken);
            return breakNotFound ? new NotFoundAsOther(folder) : folder;
        }
    }

    /// <summary>A provider bug: every NotFound of the service escapes as Other.</summary>
    private sealed class NotFoundAsOther(KazoDriveFolder inner) : KazoDriveFolder
    {
        public override ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default) => inner.GetStateAsync(cancellationToken);

        public override ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            Wrap(() => inner.StatAsync(path, cancellationToken));

        public override ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            Wrap(() => inner.ListAsync(path, cancellationToken));

        public override ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            Wrap(() => inner.ReadAsync(path, cancellationToken));

        public override ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(path, content, ifRevision, cancellationToken);

        public override ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default) =>
            inner.CreateFolderAsync(path, cancellationToken);

        public override ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(path, recursive, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        private static async ValueTask<T> Wrap<T>(Func<ValueTask<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.NotFound)
            {
                throw new KazoDriveException(KazoDriveErrorKind.Other, exception.Message);
            }
        }
    }
}
