using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Ecosystem;

/// <summary>Converts unhandled Blazor component failures into correlated host diagnostics.</summary>
public sealed class HostErrorBoundary : ErrorBoundary
{
    /// <summary>Gets the correlation ID assigned to the most recent unhandled failure.</summary>
    public Guid? CorrelationId => Failure?.CorrelationId;

    /// <summary>
    /// Gets what the error page shows about the most recent unhandled failure (ADR 0240): known
    /// before the error content renders, while the log line is still being written.
    /// </summary>
    public HostFailureDetails? Failure { get; private set; }

    [Inject]
    private HostExceptionReporter Reporter { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnErrorAsync(Exception exception)
    {
        // ErrorBoundaryBase calls this method, then renders the error content without waiting for
        // it: the details are set before the first await so the page never shows an empty
        // correlation (ADR 0240).
        HostFailureDetails failure = Reporter.Describe(exception, "component-render");
        Failure = failure;
        Console.Error.WriteLine($"[HostErrorBoundary] Unhandled failure (CorrelationId: {failure.CorrelationId}):\n{exception}");
        await Reporter.ReportAsync(failure);
    }
}
