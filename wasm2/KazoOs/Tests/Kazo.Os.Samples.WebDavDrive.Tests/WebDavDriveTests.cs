using System.Text;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Testing.Drives;

namespace Kazo.Os.Samples.WebDavDrive.Tests;

/// <summary>The WebDAV sample passes the public conformance suite against a local server (ADR 0282, work item 7).</summary>
public sealed class WebDavConformanceTests : KazoDriveProviderTests
{
    private readonly LocalWebDavServer _server = new();

    public static TheoryData<string> Cases => new(CaseNames);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_webdav_sample_conforms(string name)
    {
        KazoDriveConformanceResult result = await RunAsync(name);

        Assert.Equal(KazoDriveConformanceOutcome.Passed, result.Outcome);
    }

    protected override ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken) =>
        WebDavDriveTests.Open(_server, cancellationToken: cancellationToken);

    protected override ValueTask<bool> SetServiceReachableAsync(bool reachable, CancellationToken cancellationToken)
    {
        _server.Online = reachable;
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> ExpireSignInAsync(CancellationToken cancellationToken)
    {
        _server.Password = "changed-on-the-server";
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> PlaceLargeFileAsync(KazoDrivePath path, long length, CancellationToken cancellationToken)
    {
        _server.QuotaBytes = null;
        _server.PlaceFile(LocalWebDavServer.BasePath + path, length);
        return ValueTask.FromResult(true);
    }

    protected override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        _server.Online = true;
        _server.Password = "app-password-1";
        _server.QuotaBytes = 50_000_000;
        return ValueTask.CompletedTask;
    }
}

/// <summary>What the sample does that the suite does not see: the form's values, the requests it sends.</summary>
public sealed class WebDavDriveTests
{
    internal static ValueTask<KazoDriveFolder> Open(
        LocalWebDavServer server,
        string address = LocalWebDavServer.Origin + LocalWebDavServer.BasePath + "/",
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        KazoDriveTestContext context = new("webdav") { Secrets = { [WebDavFields.Password] = "app-password-1" } };
        return new WebDavDriveProvider(server).OpenAsync(
            new KazoDriveOpenRequest("Nextcloud", context)
            {
                Root = root,
                Options = new Dictionary<string, string>
                {
                    [WebDavFields.Server] = address,
                    [WebDavFields.UserName] = LocalWebDavServer.UserName,
                },
            },
            cancellationToken);
    }

    [Fact]
    public async Task Opening_a_drive_sends_nothing_to_the_server()
    {
        LocalWebDavServer server = new() { Online = false };

        await using KazoDriveFolder drive = await Open(server);

        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData("http://dav.test.example/remote.php/dav/files/ben/")]
    [InlineData("https://ben:secret@dav.test.example/")]
    [InlineData("dav.test.example")]
    [InlineData("")]
    public async Task An_address_that_is_not_plain_https_is_refused(string address)
    {
        KazoDriveException refused = await Assert.ThrowsAsync<KazoDriveException>(() => Open(new LocalWebDavServer(), address).AsTask());

        Assert.Equal(KazoDriveErrorKind.Other, refused.Kind);
    }

    [Fact]
    public async Task A_root_folder_scopes_the_drive_to_it()
    {
        LocalWebDavServer server = new();
        await using (KazoDriveFolder whole = await Open(server))
        {
            await whole.CreateFolderAsync(KazoDrivePath.Parse("/Work"));
            await whole.WriteAsync(KazoDrivePath.Parse("/Work/plan.txt"), Encoding.UTF8.GetBytes("plan"), null);
            await whole.WriteAsync(KazoDrivePath.Parse("/private.txt"), Encoding.UTF8.GetBytes("no"), null);
        }

        await using KazoDriveFolder work = await Open(server, root: "/Work");

        KazoDriveEntry only = Assert.Single(await work.ListAsync(KazoDrivePath.Root));
        Assert.Equal("plan.txt", only.Name);
        Assert.Equal("plan", Encoding.UTF8.GetString(await work.ReadAsync(KazoDrivePath.Parse("/plan.txt"))));
    }

    [Fact]
    public async Task A_server_address_without_a_trailing_slash_still_lists_its_folder()
    {
        LocalWebDavServer server = new();
        await using KazoDriveFolder drive = await Open(server, LocalWebDavServer.Origin + LocalWebDavServer.BasePath);
        await drive.WriteAsync(KazoDrivePath.Parse("/a.txt"), Encoding.UTF8.GetBytes("a"), null);

        Assert.Equal("a.txt", Assert.Single(await drive.ListAsync(KazoDrivePath.Root)).Name);
    }

    [Fact]
    public async Task A_create_is_one_conditional_put_and_never_replaces_a_file()
    {
        LocalWebDavServer server = new();
        await using KazoDriveFolder drive = await Open(server);
        await drive.WriteAsync(KazoDrivePath.Parse("/taken.txt"), Encoding.UTF8.GetBytes("kept"), null);
        server.Methods.Clear();

        KazoDriveException exists = await Assert.ThrowsAsync<KazoDriveException>(() => drive.CreateFileAsync(KazoDrivePath.Parse("/taken.txt")).AsTask());

        Assert.Equal(KazoDriveErrorKind.Exists, exists.Kind);
        Assert.Equal(["PUT"], server.Methods);
        Assert.Equal("kept", Encoding.UTF8.GetString(await drive.ReadAsync(KazoDrivePath.Parse("/taken.txt"))));
    }

    [Fact]
    public async Task The_quota_comes_from_the_rfc_4331_properties()
    {
        LocalWebDavServer server = new() { QuotaBytes = 1_000 };
        await using KazoDriveFolder drive = await Open(server);
        await drive.WriteAsync(KazoDrivePath.Parse("/a.bin"), new byte[400], null);

        KazoDriveQuota? quota = await drive.GetQuotaAsync();

        Assert.Equal(new KazoDriveQuota(1_000, 600), quota);
    }

    [Fact]
    public async Task A_full_server_is_quota_exceeded_and_a_wrong_password_is_sign_in_required()
    {
        LocalWebDavServer server = new() { QuotaBytes = 10 };
        await using KazoDriveFolder drive = await Open(server);

        KazoDriveException full = await Assert.ThrowsAsync<KazoDriveException>(
            () => drive.WriteAsync(KazoDrivePath.Parse("/big.bin"), new byte[11], null).AsTask());
        server.Password = "other";
        KazoDriveException refused = await Assert.ThrowsAsync<KazoDriveException>(() => drive.StatAsync(KazoDrivePath.Root).AsTask());

        Assert.Equal(KazoDriveErrorKind.QuotaExceeded, full.Kind);
        Assert.Equal(KazoDriveErrorKind.SignInRequired, refused.Kind);
    }

    [Fact]
    public async Task A_hostile_multistatus_with_an_external_entity_is_refused()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><d:multistatus xmlns:d=\"DAV:\">&x;</d:multistatus>");

        await Assert.ThrowsAsync<FormatException>(() => WebDavMultistatus.ParseAsync(new MemoryStream(body), CancellationToken.None));
    }

    [Theory]
    [InlineData("/remote.php/dav/files/ben/a%20b%23c.txt", "/remote.php/dav/files/ben/a b#c.txt")]
    [InlineData("https://dav.test.example/dav/Photos/", "/dav/Photos")]
    [InlineData("/", "/")]
    public void An_href_is_decoded_to_the_server_path(string href, string expected)
    {
        Assert.Equal(expected, WebDavMultistatus.DecodePath(href));
    }
}
