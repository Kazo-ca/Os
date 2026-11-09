using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Records host failures with a correlation ID and masked structured metadata (ADR 0240), in the
/// in-memory buffer of recent entries and as a <c>CRIT</c> line in <c>/var/log/syslog</c> (ADR 0045).
/// </summary>
public sealed class HostExceptionReporter
{
    private const string Category = "ecosystem-host";

    private readonly IDiagnosticSink _sink;
    private readonly LogFileWriter _writer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the reporter over the memory buffer and the platform log writer.</summary>
    public HostExceptionReporter(
        IDiagnosticSink sink,
        LogFileWriter writer,
        TimeProvider timeProvider)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Describes and records one failure (ADR 0240): see <see cref="Describe"/> and
    /// <see cref="ReportAsync(HostFailureDetails, CancellationToken)"/>.
    /// </summary>
    public async ValueTask<Guid> ReportAsync(
        Exception exception,
        string phase,
        CancellationToken cancellationToken = default)
    {
        HostFailureDetails failure = Describe(exception, phase);
        await ReportAsync(failure, cancellationToken).ConfigureAwait(false);
        return failure.CorrelationId;
    }

    /// <summary>
    /// Describes <paramref name="exception"/> without writing anything, so the caller has the
    /// correlation ID before the error page renders (ADR 0240). Message and stack are masked by
    /// <see cref="SensitiveTextRedactor"/>.
    /// </summary>
    public HostFailureDetails Describe(Exception exception, string phase) =>
        HostFailureDetails.Create(exception, phase, _timeProvider.GetUtcNow());

    /// <summary>
    /// Records one described failure, its masked message and stack included (ADR 0240), then asks
    /// the writer to flush so the line survives a tab that is about to crash.
    /// </summary>
    public async ValueTask ReportAsync(
        HostFailureDetails failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);

        Guid correlationId = failure.CorrelationId;
        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["correlationId"] = correlationId.ToString("D"),
            ["exceptionType"] = failure.ExceptionType,
            ["phase"] = failure.Phase,
            ["message"] = failure.Message,
            ["stack"] = failure.Details
        };
        if (failure.SourceModule is not null)
        {
            properties["module"] = failure.SourceModule;
        }

        if (failure.SourceMethod is not null)
        {
            properties["source"] = failure.SourceMethod;
        }

        DiagnosticEntry entry = new(
            failure.OccurredAtUtc,
            DiagnosticSeverity.Critical,
            Category,
            "An unexpected host failure interrupted the current surface.",
            correlationId,
            properties);

        _sink.Record(entry);
        _writer.Enqueue(LogFileTarget.Syslog, LogLineFormat.Format(entry));
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception flushException) when (flushException is not OperationCanceledException)
        {
            // The writer already notes per-file failures in memory; this only guards the caller.
            _sink.Record(new DiagnosticEntry(
                _timeProvider.GetUtcNow(),
                DiagnosticSeverity.Warning,
                Category,
                "The host failure could not be written to the system log.",
                correlationId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = flushException.GetType().FullName ?? flushException.GetType().Name,
                    ["phase"] = "diagnostic-persistence"
                }));
        }
    }
}
