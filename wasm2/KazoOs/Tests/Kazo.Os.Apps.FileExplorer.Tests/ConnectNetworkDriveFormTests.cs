using Xunit;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>
/// The generic form of the connect dialog (ADR 0282 §5) and the host it writes before saving for a
/// field marked as the server address (ADR 0284 §2).
/// </summary>
public sealed class ConnectNetworkDriveFormTests
{
    private static ConnectNetworkDriveDialog.DriveForm Form() => new(
    [
        new("url", "Server", KazoNetworkDriveFieldKind.Url) { IsServerAddress = true },
        new("user", "User name", KazoNetworkDriveFieldKind.Text),
        new("password", "Password", KazoNetworkDriveFieldKind.Secret)
    ]);

    [Fact]
    public void No_host_is_written_while_the_server_address_is_empty()
    {
        ConnectNetworkDriveDialog.DriveForm form = Form();

        Assert.Null(form.ServerHost());
    }

    [Fact]
    public void An_https_address_gives_the_host_the_provider_will_reach()
    {
        ConnectNetworkDriveDialog.DriveForm form = Form();
        form.Set("url", "https://DAV.Example.com/remote.php/dav");
        form.Set("user", "alice");
        form.Set("password", "x");

        Assert.Equal(("dav.example.com", true), form.ServerHost());
        Assert.True(form.IsComplete);
    }

    [Theory]
    [InlineData("http://dav.example.com")]
    [InlineData("https://alice:secret@dav.example.com")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://localhost")]
    [InlineData("not an address")]
    public void An_address_the_OS_cannot_allow_is_refused_and_blocks_the_mount(string address)
    {
        ConnectNetworkDriveDialog.DriveForm form = Form();
        form.Set("url", address);
        form.Set("user", "alice");
        form.Set("password", "x");

        Assert.Equal((string.Empty, false), form.ServerHost());
        Assert.False(form.IsComplete);
    }

    [Fact]
    public void A_form_without_a_server_address_field_writes_no_host()
    {
        ConnectNetworkDriveDialog.DriveForm form = new([new("endpoint", "Endpoint", KazoNetworkDriveFieldKind.Url)]);
        form.Set("endpoint", "http://minio.local");

        Assert.Null(form.ServerHost());
        Assert.True(form.IsComplete);
    }
}
