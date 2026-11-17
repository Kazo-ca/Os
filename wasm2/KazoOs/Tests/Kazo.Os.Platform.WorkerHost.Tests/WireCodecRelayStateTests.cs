using System.Text.Json;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Xunit;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>ADR 0278 §1: an app in a worker reads the relay's state through its gateway, across the worker boundary.</summary>
public sealed class WireCodecRelayStateTests
{
    [Theory]
    [InlineData(KazoRelayStatus.NoServer)]
    [InlineData(KazoRelayStatus.RelayDisabled)]
    [InlineData(KazoRelayStatus.GrantPending)]
    public void A_relay_state_without_ports_crosses_as_is(KazoRelayStatus status)
    {
        KazoRelayState copy = RoundTrip(new KazoRelayState(status));

        Assert.Equal(status, copy.Status);
        Assert.Empty(copy.TcpBlockedPorts);
        Assert.Empty(copy.UdpBlockedPorts);
    }

    [Fact]
    public void A_ready_state_keeps_its_blocked_ports()
    {
        KazoRelayState copy = RoundTrip(new KazoRelayState(KazoRelayStatus.Ready) { TcpBlockedPorts = [23, 25], UdpBlockedPorts = [53] });

        Assert.Equal(KazoRelayStatus.Ready, copy.Status);
        Assert.Equal([23, 25], copy.TcpBlockedPorts);
        Assert.Equal([53], copy.UdpBlockedPorts);
    }

    private static KazoRelayState RoundTrip(KazoRelayState state)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WireCodec.WriteRelayState(writer, state);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return WireCodec.ReadRelayState(document.RootElement);
    }
}
