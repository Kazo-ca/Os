using System.Text.Json;
using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Xunit;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0254: a user event written in a worker, the filter of a read and the events read back cross
/// the worker boundary (ADR 0070) unchanged.
/// </summary>
public sealed class WireCodecUserEventsTests
{
    [Fact]
    public void A_user_event_keeps_its_kind_message_subject_severity_and_properties()
    {
        KazoUserEvent userEvent = new(
            "x86.network.allowed",
            "Network allowed to setup.exe",
            new KazoEventSubject("program", "/home/alice/setup.exe#ab12", "setup.exe"),
            KazoEventSeverity.Warning,
            new Dictionary<string, string> { ["destination"] = "example.org:443" });

        KazoUserEvent copy = RoundTrip(writer => WireCodec.WriteUserEvent(writer, userEvent), WireCodec.ReadUserEvent);

        Assert.Equal((userEvent.Kind, userEvent.Message, userEvent.Subject, userEvent.Severity), (copy.Kind, copy.Message, copy.Subject, copy.Severity));
        Assert.Equal(userEvent.Properties, copy.Properties);
        Assert.Null(RoundTrip(writer => WireCodec.WriteUserEvent(writer, new KazoUserEvent("k", "m")), WireCodec.ReadUserEvent).Subject);
    }

    [Fact]
    public void A_filter_keeps_every_criterion_but_the_app_which_the_os_sets()
    {
        KazoUserEventFilter filter = new()
        {
            Kind = "x86.vm.",
            SubjectType = "vm",
            SubjectId = "4d2c",
            MinimumSeverity = KazoEventSeverity.Warning,
            Since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            Search = "Windows",
            AppId = "org.example.other",
            Limit = 20,
        };

        KazoUserEventFilter copy = RoundTrip(writer => WireCodec.WriteUserEventFilter(writer, filter), WireCodec.ReadUserEventFilter);

        Assert.Equal(filter with { AppId = null }, copy);
    }

    [Fact]
    public void Events_read_back_keep_what_the_os_stamped()
    {
        KazoUserEventEntry entry = new(
            new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero),
            "org.example.vm-manager",
            42,
            "x86.vm.started",
            "Windows 98 started",
            new KazoEventSubject("vm", "4d2c", "Windows 98"),
            KazoEventSeverity.Info,
            new Dictionary<string, string> { ["memory"] = "64 MiB" },
            "org.kazoos.file-explorer");

        IReadOnlyList<KazoUserEventEntry> copy = RoundTrip(
            writer => WireCodec.WriteUserEventEntries(writer, [entry, entry with { ProcessId = null, Subject = null, SenderAppId = null }]),
            WireCodec.ReadUserEventEntries);

        Assert.Equal(2, copy.Count);
        Assert.Equal(entry with { Properties = copy[0].Properties }, copy[0]);
        Assert.Equal(entry.Properties, copy[0].Properties);
        Assert.Equal((null, null, null), (copy[1].ProcessId, copy[1].Subject, copy[1].SenderAppId));
    }

    private static T RoundTrip<T>(Action<Utf8JsonWriter> write, Func<JsonElement, T> read)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return read(document.RootElement);
    }
}
