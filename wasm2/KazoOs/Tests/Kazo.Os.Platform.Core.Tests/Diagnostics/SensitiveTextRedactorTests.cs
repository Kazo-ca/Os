using Kazo.Os.Platform.Core.Diagnostics;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>ADR 0240: the error page and syslog mask the known shapes of a secret, nothing else.</summary>
public sealed class SensitiveTextRedactorTests
{
    [Theory]
    [InlineData("connect wss://benoit:hunter2@irc.example.org:6697/", "connect wss://benoit:***redacted***@irc.example.org:6697/")]
    [InlineData("GET https://api.example.org/x?access_token=abc123&page=2", "GET https://api.example.org/x?access_token=***redacted***&page=2")]
    [InlineData("password=hunter2; user=benoit", "password=***redacted***; user=benoit")]
    [InlineData("{\"apiKey\": \"sk-123\", \"name\": \"x\"}", "{\"apiKey\": \"***redacted***\", \"name\": \"x\"}")]
    [InlineData("Authorization: Bearer eyJhbGciOi.eyJzdWIi.sig", "Authorization: Bearer ***redacted***")]
    [InlineData("sent PASS hunter2", "sent PASS ***redacted***")]
    [InlineData("PRIVMSG NickServ :IDENTIFY benoit hunter2", "PRIVMSG NickServ :IDENTIFY ***redacted***")]
    public void Masks_the_known_shapes_of_a_secret(string text, string expected)
    {
        // Act
        string redacted = SensitiveTextRedactor.Redact(text);

        // Assert
        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Could not find file '/home/benoit/Documents/notes.txt'.")]
    [InlineData("Failed to fetch dynamically imported module: https://os.kazo.ca/_content/Kazo.Irc/Components/MessagePane.razor.js")]
    [InlineData("   at Kazo.Irc.Components.MessagePane.OnAfterRenderAsync(Boolean firstRender)")]
    [InlineData("The token budget was exceeded.")]
    public void Leaves_paths_addresses_and_stack_frames_readable(string text) =>
        Assert.Equal(text, SensitiveTextRedactor.Redact(text));

    [Fact]
    public void Null_and_empty_give_an_empty_text()
    {
        Assert.Equal(string.Empty, SensitiveTextRedactor.Redact(null));
        Assert.Equal(string.Empty, SensitiveTextRedactor.Redact(string.Empty));
    }
}
