using Kazo.Os.Ecosystem;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>ADR 0240: what the error page knows about a failure, and the text it copies.</summary>
public sealed class HostFailureDetailsTests
{
    private const string IrcStack = """
        Microsoft.JSInterop.JSException: Failed to fetch dynamically imported module: https://os.kazo.ca/_content/Kazo.Irc/Components/MessagePane.razor.js
           at Microsoft.JSInterop.JSRuntime.<InvokeAsync>d__23`1[[Microsoft.JSInterop.IJSObjectReference, Microsoft.JSInterop, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60]].MoveNext()
           at Kazo.Irc.Components.MessagePane.<OnAfterRenderAsync>d__12.MoveNext()
           at Microsoft.AspNetCore.Components.RenderTree.Renderer.GetErrorHandledTask(Task , ComponentState )
        """;

    private static readonly DateTimeOffset At = new(2026, 11, 8, 20, 2, 56, TimeSpan.Zero);

    [Fact]
    public void Create_keeps_type_message_and_details_and_masks_secrets()
    {
        // Arrange
        InvalidOperationException exception = new("login failed for wss://benoit:hunter2@irc.example.org/");

        // Act
        HostFailureDetails failure = HostFailureDetails.Create(exception, "component-render", At, []);

        // Assert
        Assert.NotEqual(Guid.Empty, failure.CorrelationId);
        Assert.Equal(At, failure.OccurredAtUtc);
        Assert.Equal("component-render", failure.Phase);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.ExceptionType);
        Assert.Equal("login failed for wss://benoit:***redacted***@irc.example.org/", failure.Message);
        Assert.Contains("InvalidOperationException", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_names_the_thrown_methods_module_from_the_loaded_assemblies()
    {
        // Arrange: a real throw, so the stack trace has a frame of this test assembly.
        Exception exception = Throw();

        // Act
        HostFailureDetails failure = HostFailureDetails.Create(
            exception, "component-render", At, ["Kazo.Os", "Kazo.Os.Ecosystem", "Kazo.Os.Ecosystem.Tests"]);

        // Assert: Kazo.Os.* frames are a fallback, and the longest matching assembly name wins.
        Assert.Equal("Kazo.Os.Ecosystem.Tests.HostFailureDetailsTests.Throw", failure.SourceMethod);
        Assert.Equal("Kazo.Os.Ecosystem.Tests", failure.SourceModule);
    }

    [Fact]
    public void The_source_is_the_first_frame_outside_the_framework_with_async_frames_unwrapped()
    {
        Assert.Equal("Kazo.Irc.Components.MessagePane.OnAfterRenderAsync", HostFailureDetails.FindSourceMethod(IrcStack));
        Assert.Equal("Kazo.Irc", HostFailureDetails.FindModule("Kazo.Irc.Components.MessagePane.OnAfterRenderAsync", ["Kazo.Irc", "Kazo.Os.Ecosystem"]));
        Assert.Null(HostFailureDetails.FindSourceMethod("System.Exception: x\n   at System.Threading.Tasks.Task.Wait()"));
    }

    [Fact]
    public void The_report_text_starts_with_a_summary_then_the_details()
    {
        // Arrange
        HostFailureDetails failure = new(
            Guid.Parse("92c3aad1-620c-4762-ac57-dcf7199d9d07"), At, "component-render",
            "Microsoft.JSInterop.JSException", "Failed to fetch", "Kazo.Irc",
            "Kazo.Irc.Components.MessagePane.OnAfterRenderAsync", IrcStack);

        // Act
        string report = failure.ToReportText("3.2.1 (build 1321)");

        // Assert
        Assert.StartsWith(
            "Kazo OS: 3.2.1 (build 1321)" + Environment.NewLine
            + "Correlation: 92c3aad1-620c-4762-ac57-dcf7199d9d07" + Environment.NewLine
            + "Time (UTC): 2026-11-08 20:02:56Z" + Environment.NewLine
            + "Phase: component-render" + Environment.NewLine
            + "Module: Kazo.Irc" + Environment.NewLine
            + "Source: Kazo.Irc.Components.MessagePane.OnAfterRenderAsync" + Environment.NewLine
            + "Exception: Microsoft.JSInterop.JSException" + Environment.NewLine
            + "Message: Failed to fetch" + Environment.NewLine,
            report,
            StringComparison.Ordinal);
        Assert.EndsWith(IrcStack, report, StringComparison.Ordinal);
    }

    private static Exception Throw()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
