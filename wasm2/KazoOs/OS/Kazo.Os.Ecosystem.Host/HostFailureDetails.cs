using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kazo.Os.Platform.Core.Diagnostics;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// What the error page shows about one unhandled failure (ADR 0240): its correlation ID, when it
/// happened, the module and method it came from, the exception type, its message and the full
/// details (stack traces and inner exceptions). Message and details are already masked by
/// <see cref="SensitiveTextRedactor"/>.
/// </summary>
/// <param name="CorrelationId">The ID written with the <c>CRIT</c> line of <c>/var/log/syslog</c>.</param>
/// <param name="OccurredAtUtc">When the failure was caught.</param>
/// <param name="Phase">What the host was doing, for example <c>component-render</c>.</param>
/// <param name="ExceptionType">The full name of the outermost exception type.</param>
/// <param name="Message">The outermost exception message, masked.</param>
/// <param name="SourceModule">The loaded assembly of <see cref="SourceMethod"/> (an app's assembly, such as <c>Kazo.Irc</c>), when known.</param>
/// <param name="SourceMethod">The first stack frame outside the .NET and Blazor framework, when one exists.</param>
/// <param name="Details">The whole <see cref="Exception.ToString"/> text, masked.</param>
public sealed partial record HostFailureDetails(
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string Phase,
    string ExceptionType,
    string Message,
    string? SourceModule,
    string? SourceMethod,
    string Details)
{
    private static readonly string[] FrameworkPrefixes = ["System.", "Microsoft.", "Mono.", "Interop.", "MudBlazor."];

    /// <summary>
    /// Describes <paramref name="exception"/> synchronously, so the error page can show the
    /// correlation ID on its first render (the log write happens after).
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="phase">What the host was doing.</param>
    /// <param name="occurredAtUtc">When it was caught.</param>
    /// <param name="loadedAssemblyNames">The names of the loaded assemblies; the current domain's when omitted.</param>
    public static HostFailureDetails Create(
        Exception exception,
        string phase,
        DateTimeOffset occurredAtUtc,
        IEnumerable<string>? loadedAssemblyNames = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        string details = exception.ToString();
        string? sourceMethod = FindSourceMethod(details);
        string? sourceModule = sourceMethod is null
            ? null
            : FindModule(sourceMethod, loadedAssemblyNames ?? LoadedAssemblyNames());

        return new HostFailureDetails(
            Guid.NewGuid(),
            occurredAtUtc,
            phase,
            exception.GetType().FullName ?? exception.GetType().Name,
            SensitiveTextRedactor.Redact(exception.Message),
            sourceModule,
            sourceMethod,
            SensitiveTextRedactor.Redact(details));
    }

    /// <summary>
    /// The text « Copier les détails » puts on the clipboard: a short header the user can paste in
    /// a GitHub issue, then the full details.
    /// </summary>
    /// <param name="osVersion">The running version of Kazo OS, with its build number when known.</param>
    public string ToReportText(string? osVersion)
    {
        StringBuilder builder = new();
        builder.Append("Kazo OS: ").AppendLine(string.IsNullOrWhiteSpace(osVersion) ? "?" : osVersion);
        builder.Append("Correlation: ").AppendLine(CorrelationId.ToString("D"));
        builder.Append("Time (UTC): ").AppendLine(OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        builder.Append("Phase: ").AppendLine(Phase);
        if (SourceModule is not null)
        {
            builder.Append("Module: ").AppendLine(SourceModule);
        }

        if (SourceMethod is not null)
        {
            builder.Append("Source: ").AppendLine(SourceMethod);
        }

        builder.Append("Exception: ").AppendLine(ExceptionType);
        builder.Append("Message: ").AppendLine(Message);
        builder.AppendLine();
        builder.Append(Details);
        return builder.ToString();
    }

    /// <summary>
    /// Finds the first frame of the stack trace text that is not the .NET or Blazor framework,
    /// turning an async state machine frame (<c>Type.&lt;Method&gt;d__5.MoveNext</c>) back into
    /// <c>Type.Method</c>. Parsing the text avoids <c>StackFrame.GetMethod</c>, which trimming forbids.
    /// </summary>
    internal static string? FindSourceMethod(string details)
    {
        string? osFrame = null;
        foreach (Match match in StackFrameLine().Matches(details))
        {
            string name = AsyncStateMachine().Replace(match.Groups["name"].Value, ".${method}");
            if (FrameworkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            // An app's own frame is the most useful; Kazo OS frames are kept as a fallback.
            if (!name.StartsWith("Kazo.Os.", StringComparison.Ordinal))
            {
                return name;
            }

            osFrame ??= name;
        }

        return osFrame;
    }

    /// <summary>Returns the longest assembly name that prefixes <paramref name="method"/>'s namespace.</summary>
    internal static string? FindModule(string method, IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(name => method.StartsWith(name + ".", StringComparison.Ordinal))
            .OrderByDescending(name => name.Length)
            .FirstOrDefault();

    private static IEnumerable<string> LoadedAssemblyNames() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .OfType<string>();

    [GeneratedRegex(@"^\s*at (?<name>[^\s(]+)\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameLine();

    [GeneratedRegex(@"\.<(?<method>[^>]+)>d__\d+\.MoveNext$", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncStateMachine();
}
