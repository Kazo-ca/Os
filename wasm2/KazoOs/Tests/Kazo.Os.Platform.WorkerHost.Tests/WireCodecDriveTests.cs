using System.Text.Json;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Xunit;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>ADR 0181: a network drive crosses the worker boundary (ADR 0070) with its type and readiness.</summary>
public sealed class WireCodecDriveTests
{
    [Fact]
    public void Drives_keep_their_type_and_readiness()
    {
        KazoDriveSnapshot[] drives =
        [
            new("/", "idbfs", "hda", 100, 40),
            new("/mnt/Projects", "hostfs", "Projects", 0, 0) { DriveType = KazoDriveType.Network, IsReady = false }
        ];

        IReadOnlyList<KazoDriveSnapshot> copy = RoundTrip(writer => WireCodec.WriteDrives(writer, drives), WireCodec.ReadDrives);

        Assert.Equal(drives, copy);
    }

    [Fact]
    public void A_closed_dialog_crosses_as_no_drive_and_old_payloads_read_as_fixed_and_ready()
    {
        Assert.Null(RoundTrip(writer => WireCodec.WriteDrive(writer, null), WireCodec.ReadDrive));

        using JsonDocument old = JsonDocument.Parse("{\"d\":{\"n\":\"/\",\"f\":\"idbfs\",\"l\":\"hda\",\"t\":1,\"a\":1}}");
        KazoDriveSnapshot drive = WireCodec.ReadDrive(old.RootElement)!;

        Assert.Equal(KazoDriveType.Fixed, drive.DriveType);
        Assert.True(drive.IsReady);
    }

    [Fact]
    public void A_cloud_drive_keeps_its_account()
    {
        KazoDriveSnapshot[] drives =
        [
            new("/mnt/Dropbox", "dropbox", "alice", 10, 4) { DriveType = KazoDriveType.Network, Account = "a1" }
        ];

        Assert.Equal(drives, RoundTrip(writer => WireCodec.WriteDrives(writer, drives), WireCodec.ReadDrives));
    }

    [Fact]
    public void A_network_drive_request_crosses_with_its_options_and_secrets()
    {
        KazoNetworkDriveRequest request = new("s3", null, "/photos")
        {
            Options = new Dictionary<string, string> { ["endpoint"] = "https://s3.example.com", ["bucket"] = "b" },
            Secrets = new Dictionary<string, string> { ["access-key-id"] = "AK", ["secret-access-key"] = "SK" }
        };

        KazoNetworkDriveRequest copy = RoundTrip(writer => WireCodec.WriteNetworkDriveRequest(writer, request), WireCodec.ReadNetworkDriveRequest);
        KazoNetworkDriveRequest bare = RoundTrip(
            writer => WireCodec.WriteNetworkDriveRequest(writer, new KazoNetworkDriveRequest("dropbox", "a1")), WireCodec.ReadNetworkDriveRequest);

        Assert.Equal(("s3", null, "/photos"), (copy.Type, copy.Account, copy.Root));
        Assert.Equal(request.Options, copy.Options);
        Assert.Equal(request.Secrets, copy.Secrets);
        Assert.Equal(new KazoNetworkDriveRequest("dropbox", "a1"), bare);
    }

    [Fact]
    public void Cloud_accounts_and_providers_cross_with_their_scopes_and_state()
    {
        KazoCloudAccount[] accounts =
        [
            new("a1", "dropbox", KazoCloudAccessScope.AppFolder, "alice", KazoCloudAccountState.NeedsReconnect, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero))
        ];
        KazoCloudProvider[] providers = [new("onedrive", "OneDrive", [KazoCloudAccessScope.Full, KazoCloudAccessScope.AppFolder])];

        IReadOnlyList<KazoCloudAccount> accountsCopy = RoundTrip(writer => WireCodec.WriteCloudAccounts(writer, accounts), WireCodec.ReadCloudAccounts);
        KazoCloudProvider providerCopy = Assert.Single(RoundTrip(writer => WireCodec.WriteCloudProviders(writer, providers), WireCodec.ReadCloudProviders));

        Assert.Equal(accounts, accountsCopy);
        Assert.Equal(("onedrive", "OneDrive"), (providerCopy.Id, providerCopy.DisplayName));
        Assert.Equal(providers[0].Scopes, providerCopy.Scopes);
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
