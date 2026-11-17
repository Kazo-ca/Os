using System.Text;
using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.AppSdk.Testing.Drives;

/// <summary>
/// The public conformance suite of a network drive provider (ADR 0282 §2): the cases every
/// <see cref="KazoDriveProvider"/> must pass before it reaches the Store. It depends on no test
/// framework: derive a class, open a drive of the provider in <see cref="OpenDriveAsync"/>, and
/// run each case from the framework you use.
/// </summary>
/// <remarks>
/// <para>
/// Each case opens a fresh drive, works in its own folder under the drive's root
/// (<c>kazo-conformance-…</c>), then deletes it, so the suite can also run against a test account
/// of the real service. A case that fails throws <see cref="KazoDriveConformanceException"/>; a
/// case the class cannot simulate returns <see cref="KazoDriveConformanceOutcome.Skipped"/>.
/// </para>
/// <para>With xUnit:</para>
/// <code>
/// public sealed class MyProviderConformance : KazoDriveProviderTests
/// {
///     public static TheoryData&lt;string&gt; Cases => new(CaseNames);
///
///     [Theory, MemberData(nameof(Cases))]
///     public Task Conforms(string name) => RunAsync(name);
///
///     protected override ValueTask&lt;KazoDriveFolder&gt; OpenDriveAsync(CancellationToken cancellationToken) => ...;
/// }
/// </code>
/// </remarks>
public abstract partial class KazoDriveProviderTests
{
    /// <summary>The prefix of the folder each case works in, under the drive's root.</summary>
    public const string WorkingFolderPrefix = "kazo-conformance-";

    private static readonly KeyValuePair<string, Func<KazoDriveProviderTests, CaseScope, Task<string?>>>[] s_cases =
    [
        new(KazoDriveConformanceCases.RootIsAFolder, static (suite, scope) => suite.RootIsAFolderAsync(scope)),
        new(KazoDriveConformanceCases.WriteThenRead, static (suite, scope) => suite.WriteThenReadAsync(scope)),
        new(KazoDriveConformanceCases.EmptyFile, static (suite, scope) => suite.EmptyFileAsync(scope)),
        new(KazoDriveConformanceCases.Overwrite, static (suite, scope) => suite.OverwriteAsync(scope)),
        new(KazoDriveConformanceCases.CreateFolderAndList, static (suite, scope) => suite.CreateFolderAndListAsync(scope)),
        new(KazoDriveConformanceCases.CreateExisting, static (suite, scope) => suite.CreateExistingAsync(scope)),
        new(KazoDriveConformanceCases.Missing, static (suite, scope) => suite.MissingAsync(scope)),
        new(KazoDriveConformanceCases.WrongKind, static (suite, scope) => suite.WrongKindAsync(scope)),
        new(KazoDriveConformanceCases.Delete, static (suite, scope) => suite.DeleteAsync(scope)),
        new(KazoDriveConformanceCases.RevisionConflict, static (suite, scope) => suite.RevisionConflictAsync(scope)),
        new(KazoDriveConformanceCases.Move, static (suite, scope) => suite.MoveAsync(scope)),
        new(KazoDriveConformanceCases.Quota, static (suite, scope) => suite.QuotaAsync(scope)),
        new(KazoDriveConformanceCases.Names, static (suite, scope) => suite.NamesAsync(scope)),
        new(KazoDriveConformanceCases.Offline, static (suite, scope) => suite.OfflineAsync(scope)),
        new(KazoDriveConformanceCases.SignInExpired, static (suite, scope) => suite.SignInExpiredAsync(scope)),
        new(KazoDriveConformanceCases.FileTooLarge, static (suite, scope) => suite.FileTooLargeAsync(scope)),
    ];

    /// <summary>Gets the names of every case, in the order <see cref="RunAllAsync"/> runs them.</summary>
    public static IReadOnlyList<string> CaseNames { get; } = [.. s_cases.Select(static pair => pair.Key)];

    /// <summary>Gets how long one case may take before it fails. 60 seconds by default.</summary>
    protected virtual TimeSpan CaseTimeout => TimeSpan.FromSeconds(60);

    /// <summary>
    /// Opens a new drive of the provider under test, as the OS would: build a
    /// <see cref="KazoDriveOpenRequest"/> (a <see cref="KazoDriveTestContext"/> is enough) and call
    /// <see cref="KazoDriveProvider.OpenAsync"/>. Called at least once per case; the suite disposes
    /// each drive.
    /// </summary>
    /// <param name="cancellationToken">Cancels the opening.</param>
    /// <returns>The drive.</returns>
    protected abstract ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Makes the service unreachable or reachable again (the network goes down, then comes back).
    /// By default the class cannot, and the <c>offline</c> case is skipped.
    /// </summary>
    /// <param name="reachable">Whether the service answers.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when the class simulated it.</returns>
    protected virtual ValueTask<bool> SetServiceReachableAsync(bool reachable, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Makes the service refuse the account's sign-in (an expired or revoked token, a changed
    /// password) until <see cref="ResetAsync"/>. By default the class cannot, and the
    /// <c>sign-in-expired</c> case is skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when the class simulated it.</returns>
    protected virtual ValueTask<bool> ExpireSignInAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Places a file of <paramref name="length"/> bytes on the service, as another device would,
    /// without the provider. By default the class cannot, and the <c>file-too-large</c> case is skipped.
    /// </summary>
    /// <param name="path">The file, relative to the drive's root.</param>
    /// <param name="length">Its size, larger than <see cref="KazoDriveLimits.MaxFileBytes"/>.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when the class placed it.</returns>
    protected virtual ValueTask<bool> PlaceLargeFileAsync(KazoDrivePath path, long length, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    /// <summary>Undoes what <see cref="SetServiceReachableAsync"/> and <see cref="ExpireSignInAsync"/> changed; called after each case.</summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>A task that completes when the service is back to normal.</returns>
    protected virtual ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Runs one case.</summary>
    /// <param name="caseName">One of <see cref="CaseNames"/>.</param>
    /// <param name="cancellationToken">Cancels the case.</param>
    /// <returns>Passed or skipped, with a detail.</returns>
    /// <exception cref="KazoDriveConformanceException">The provider broke the contract.</exception>
    /// <exception cref="ArgumentException"><paramref name="caseName"/> is not a case.</exception>
    public async Task<KazoDriveConformanceResult> RunAsync(string caseName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseName);
        Func<KazoDriveProviderTests, CaseScope, Task<string?>> run = Array.Find(s_cases, pair => pair.Key == caseName).Value
            ?? throw new ArgumentException($"'{caseName}' is not a case of the drive provider suite.", nameof(caseName));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaseTimeout);
        await using CaseScope scope = new(this, caseName, timeout.Token);
        try
        {
            await scope.StartAsync().ConfigureAwait(false);
            string? detail = await run(this, scope).ConfigureAwait(false);
            return scope.SkipReason is { } reason
                ? new KazoDriveConformanceResult(caseName, KazoDriveConformanceOutcome.Skipped, reason)
                : new KazoDriveConformanceResult(caseName, KazoDriveConformanceOutcome.Passed, detail);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new KazoDriveConformanceException(caseName, $"The case took more than {CaseTimeout.TotalSeconds:0} s.", exception);
        }
        catch (KazoDriveConformanceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new KazoDriveConformanceException(caseName, $"The provider threw {exception.GetType().Name}: {exception.Message}", exception);
        }
    }

    /// <summary>Runs every case, in order, and stops at the first failure.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>One result per case.</returns>
    /// <exception cref="KazoDriveConformanceException">A case found the provider breaking the contract.</exception>
    public async Task<IReadOnlyList<KazoDriveConformanceResult>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        List<KazoDriveConformanceResult> results = [];
        foreach (string name in CaseNames)
        {
            results.Add(await RunAsync(name, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>What one case holds: its name, its folder, the drives it opened, its cancellation.</summary>
    private sealed class CaseScope(KazoDriveProviderTests suite, string name, CancellationToken cancellationToken) : IAsyncDisposable
    {
        private readonly List<KazoDriveFolder> _drives = [];

        public string Name { get; } = name;

        public CancellationToken Token { get; } = cancellationToken;

        public KazoDrivePath Folder { get; } = KazoDrivePath.Root.Append(WorkingFolderPrefix + Guid.NewGuid().ToString("N")[..12]);

        public KazoDriveFolder Drive { get; private set; } = null!;

        public string? SkipReason { get; private set; }

        public async Task StartAsync()
        {
            Drive = await OpenAsync().ConfigureAwait(false);
            await Drive.CreateFolderAsync(Folder, Token).ConfigureAwait(false);
        }

        public async Task<KazoDriveFolder> OpenAsync()
        {
            KazoDriveFolder drive = await suite.OpenDriveAsync(Token).ConfigureAwait(false)
                ?? throw new KazoDriveConformanceException(Name, "OpenDriveAsync returned null.");
            _drives.Add(drive);
            return drive;
        }

        public KazoDrivePath In(params string[] names) => KazoDrivePath.FromSegments([.. Folder.Segments, .. names]);

        public string? Skip(string reason)
        {
            SkipReason = reason;
            return null;
        }

        public void Expect(bool condition, string message)
        {
            if (!condition)
            {
                throw new KazoDriveConformanceException(Name, message);
            }
        }

        /// <summary>Runs <paramref name="operation"/> and checks that it throws <see cref="KazoDriveException"/> of <paramref name="kind"/>.</summary>
        public async Task ExpectErrorAsync(KazoDriveErrorKind kind, string what, Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (KazoDriveException exception) when (exception.Kind == kind)
            {
                return;
            }
            catch (KazoDriveException exception)
            {
                throw new KazoDriveConformanceException(Name, $"{what} must throw KazoDriveException {kind}, it threw {exception.Kind}.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !Token.IsCancellationRequested)
            {
                throw new KazoDriveConformanceException(
                    Name, $"{what} must throw KazoDriveException {kind}, it threw {exception.GetType().Name}: {exception.Message}", exception);
            }

            throw new KazoDriveConformanceException(Name, $"{what} must throw KazoDriveException {kind}, it succeeded.");
        }

        public async ValueTask DisposeAsync()
        {
            // Back to normal before cleaning up, whatever the case did; a failure here never hides the case's own result.
            try
            {
                await suite.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                if (Drive is not null)
                {
                    using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(30));
                    await Drive.DeleteAsync(Folder, recursive: true, cleanup.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is KazoDriveException or OperationCanceledException or IOException)
            {
                // The folder may be gone already, or the service still down: the next run uses a new folder.
            }

            foreach (KazoDriveFolder drive in _drives)
            {
                await drive.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
