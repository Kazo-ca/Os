using Kazo.Os.Ecosystem;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Ecosystem.Tests;

public sealed class HostExceptionReporterTests
{
    [Fact]
    public async Task ReportAsync_records_a_safe_correlated_entry_and_queues_a_syslog_line()
    {
        BoundedDiagnosticSink sink = new(10);
        LogFileWriter writer = new(new ManualSimulationClock(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100)));
        HostExceptionReporter reporter = new(sink, writer, TimeProvider.System);

        Guid correlationId = await reporter.ReportAsync(
            new InvalidOperationException("password=secret"),
            "test-phase");

        DiagnosticEntry entry = Assert.Single(sink.Entries);
        Assert.Equal(correlationId, entry.CorrelationId);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.Properties["exceptionType"]);
        Assert.Equal("test-phase", entry.Properties["phase"]);
        Assert.Equal(correlationId.ToString("D"), entry.Properties["correlationId"]);

        // ADR 0240: the message and the stack are kept, with the secret masked.
        Assert.Equal("password=***redacted***", entry.Properties["message"]);
        Assert.Contains("InvalidOperationException: password=***redacted***", entry.Properties["stack"], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Properties["stack"], StringComparison.Ordinal);

        // No filesystem is attached in this test, so the CRIT line waits in the writer's queue.
        Assert.Equal(1, writer.PendingCount);
    }

    [Fact]
    public async Task Describe_gives_the_correlation_before_the_log_is_written()
    {
        // Arrange
        BoundedDiagnosticSink sink = new(10);
        LogFileWriter writer = new(new ManualSimulationClock(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100)));
        HostExceptionReporter reporter = new(sink, writer, TimeProvider.System);

        // Act: the error boundary describes first (the page renders now), then reports.
        HostFailureDetails failure = reporter.Describe(new InvalidOperationException("boom"), "component-render");
        Assert.Empty(sink.Entries);
        await reporter.ReportAsync(failure);

        // Assert
        DiagnosticEntry entry = Assert.Single(sink.Entries);
        Assert.Equal(failure.CorrelationId, entry.CorrelationId);
        Assert.Equal("boom", entry.Properties["message"]);
    }
}
