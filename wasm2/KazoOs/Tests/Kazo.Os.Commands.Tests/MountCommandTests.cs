using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.CloudAccounts;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Df;
using Kazo.Os.Commands.Mount;
using Kazo.Os.Commands.Umount;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Tests;

/// <summary><c>mount</c>, <c>umount</c> and <c>df</c> with network drives (ADR 0181, section 12; ADR 0267 §3 and §4).</summary>
public sealed class MountCommandTests
{
    private const string ProcMounts =
        "hda / idbfs rw,relatime 0 0\n"
        + "proc /proc proc rw,nosuid,nodev,noexec 0 0\n"
        + "host:My\\040Files /mnt/My\\040Files hostfs rw,nosuid,nodev 0 0\n";

    [Fact]
    public async Task Mount_without_arguments_lists_proc_mounts_like_util_linux()
    {
        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            [
                "hda on / type idbfs (rw,relatime)",
                "proc on /proc type proc (rw,nosuid,nodev,noexec)",
                "host:My Files on /mnt/My Files type hostfs (rw,nosuid,nodev)"
            ],
            run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Mount_t_hostfs_mounts_the_chosen_folder_at_a_relative_point()
    {
        FakeAppDriveGateway drives = new() { NextPick = "Projects" };

        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-t", "hostfs", "../../mnt/code");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Projects mounted on /mnt/code", run.Stdout.Trim());
        Assert.Contains(drives.Drives, static drive => drive.Name == "/mnt/code");
    }

    [Fact]
    public async Task A_closed_dialog_or_an_unsupported_browser_fails_with_32()
    {
        Run closed = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway(), "-t", "hostfs");
        Run unsupported = await RunAsync(
            new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway { Supported = false, NextPick = "x" }, "-t", "hostfs");

        Assert.Equal(32, closed.ExitCode);
        Assert.Contains("no folder chosen", closed.Stderr, StringComparison.Ordinal);
        Assert.Equal(32, unsupported.ExitCode);
        Assert.Contains("not supported", unsupported.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "--bogus")]
    [InlineData(1, "-o", "ro", "/mnt/x")]
    [InlineData(32, "-t", "nfs", "/mnt/x")]
    public async Task Mount_refuses_what_it_cannot_do(int exitCode, params string[] arguments)
    {
        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway(), arguments);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.NotEmpty(run.Stderr);
    }

    [Fact]
    public async Task Remount_asks_the_browser_again_and_reports_a_refusal()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work", ready: false);

        Run granted = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-o", "remount", "/mnt/work");
        drives.GrantOnReconnect = false;
        Run refused = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-o", "remount", "/mnt/work");

        Assert.Equal(0, granted.ExitCode);
        Assert.Equal(32, refused.ExitCode);
        Assert.Contains("permission refused", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Umount_removes_the_drive_and_reports_one_that_is_not_mounted()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work");

        Run removed = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives, "/mnt/work");
        Run missing = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives, "/mnt/work");
        Run usage = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives);

        Assert.Equal(0, removed.ExitCode);
        Assert.DoesNotContain(drives.Drives, static drive => drive.Name == "/mnt/work");
        Assert.Equal(32, missing.ExitCode);
        Assert.Contains("not mounted", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, usage.ExitCode);
    }

    [Fact]
    public async Task Df_shows_a_network_drive_without_sizes()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work");

        Run run = await RunAsync(new DfApp(), static manifest => new DfCommand(manifest), drives);

        Assert.Equal(0, run.ExitCode);
        string line = run.Stdout.Split('\n').Single(static line => line.StartsWith("host:work", StringComparison.Ordinal));
        Assert.Equal(["host:work", "-", "-", "-", "-", "/mnt/work"], line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Df_shows_a_cloud_drive_with_its_type_and_the_service_quota()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway()
            .WithNetworkDrive("/mnt/work", "work")
            .WithCloudDrive("/mnt/Dropbox", "dropbox", "alice", 4096 * 1024, 1024 * 1024)
            .WithCloudDrive("/mnt/bucket", "s3", "bucket", 0, 0);

        Run run = await RunAsync(new DfApp(), static manifest => new DfCommand(manifest), drives, "-T");

        Assert.Equal(0, run.ExitCode);
        string[] lines = run.Stdout.Split('\n');
        string[] Row(string source) =>
            lines.Single(line => line.StartsWith(source, StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["dropbox:alice", "dropbox", "4096", "3072", "1024", "75%", "/mnt/Dropbox"], Row("dropbox:alice"));
        Assert.Equal(["s3:bucket", "s3", "-", "-", "-", "-", "/mnt/bucket"], Row("s3:bucket"));
        Assert.Equal(["host:work", "hostfs", "-", "-", "-", "-", "/mnt/work"], Row("host:work"));
    }

    [Fact]
    public async Task Options_are_trimmed_and_a_value_may_hold_an_equals_sign()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "dropbox" } };
        FakeAppCloudAccountsGateway accounts = new FakeAppCloudAccountsGateway()
            .WithProvider("dropbox", "Dropbox")
            .WithAccount("a1", "dropbox", "alice");

        Run run = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox", "-o", " account=a1 , root=/a=b ");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(new KazoNetworkDriveRequest("dropbox", "a1", "/a=b"), drives.LastNetworkRequest);
    }

    [Fact]
    public async Task Mount_t_dropbox_uses_the_only_account_with_its_root()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "dropbox" } };
        FakeAppCloudAccountsGateway accounts = new FakeAppCloudAccountsGateway()
            .WithProvider("dropbox", "Dropbox")
            .WithAccount("a1", "dropbox", "alice@example.com");

        Run run = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox", "-o", "root=/Documents", "/mnt/db");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(new KazoNetworkDriveRequest("dropbox", "a1", "/Documents"), drives.LastNetworkRequest);
        Assert.Contains(drives.Drives, static drive => drive.Name == "/mnt/db" && drive.Account == "a1");
        Assert.Equal(0, accounts.SignIns);
    }

    [Fact]
    public async Task Mount_t_onedrive_signs_in_when_there_is_no_account_and_asks_for_the_scope()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "onedrive" } };
        FakeAppCloudAccountsGateway accounts = new FakeAppCloudAccountsGateway()
            .WithProvider("onedrive", "OneDrive", KazoCloudAccessScope.Full, KazoCloudAccessScope.AppFolder);

        Run noScope = await RunCloudAsync(drives, accounts, string.Empty, "-t", "onedrive");
        Run app = await RunCloudAsync(drives, accounts, string.Empty, "-t", "onedrive", "-o", "scope=app");
        Run bad = await RunCloudAsync(drives, accounts, string.Empty, "-t", "onedrive", "-o", "scope=everything");

        Assert.Equal(32, noScope.ExitCode);
        Assert.Contains("scope=full", noScope.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, app.ExitCode);
        Assert.Equal(1, accounts.SignIns);
        Assert.Equal(KazoCloudAccessScope.AppFolder, accounts.Accounts.Single().Scope);
        Assert.Equal(1, bad.ExitCode);
    }

    [Fact]
    public async Task Mount_t_dropbox_refuses_an_unknown_or_ambiguous_account()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "dropbox" } };
        FakeAppCloudAccountsGateway accounts = new FakeAppCloudAccountsGateway()
            .WithProvider("dropbox", "Dropbox")
            .WithAccount("a1", "dropbox", "alice")
            .WithAccount("a2", "dropbox", "bob");

        Run several = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox");
        Run unknown = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox", "-o", "account=carol");
        Run byName = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox", "-o", "account=BOB");

        Assert.Equal(32, several.ExitCode);
        Assert.Contains("alice, bob", several.Stderr, StringComparison.Ordinal);
        Assert.Equal(32, unknown.ExitCode);
        Assert.Equal(0, byName.ExitCode);
        Assert.Equal("a2", drives.LastNetworkRequest?.Account);
    }

    [Fact]
    public async Task Mount_t_s3_reads_the_keys_from_standard_input_and_never_echoes_them()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "s3" } };

        Run run = await RunCloudAsync(
            drives, new FakeAppCloudAccountsGateway(), "AKIDEXAMPLE\nsecret-value\n",
            "-t", "s3", "-o", "endpoint=https://s3.example.com,bucket=photos,addressing=path");

        Assert.Equal(0, run.ExitCode);
        KazoNetworkDriveRequest request = Assert.IsType<KazoNetworkDriveRequest>(drives.LastNetworkRequest);
        Assert.Equal("https://s3.example.com", request.Options!["endpoint"]);
        Assert.Equal("path", request.Options["addressing"]);
        Assert.Equal("AKIDEXAMPLE", request.Secrets!["access-key-id"]);
        Assert.Equal("secret-value", request.Secrets["secret-access-key"]);
        Assert.DoesNotContain("secret-value", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("/mnt/photos", run.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("endpoint=https://s3.example.com")]
    [InlineData("bucket=photos")]
    [InlineData("endpoint=https://s3.example.com,bucket=photos,password=x")]
    public async Task Mount_t_s3_needs_an_endpoint_and_a_bucket_and_no_unknown_option(string options)
    {
        Run run = await RunCloudAsync(new FakeAppDriveGateway { NetworkTypes = { "s3" } }, new FakeAppCloudAccountsGateway(), string.Empty, "-t", "s3", "-o", options);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("endpoint=", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_t_s3_without_keys_fails_with_32()
    {
        Run run = await RunCloudAsync(
            new FakeAppDriveGateway { NetworkTypes = { "s3" } }, new FakeAppCloudAccountsGateway(), string.Empty,
            "-t", "s3", "-o", "endpoint=https://s3.example.com,bucket=photos");

        Assert.Equal(32, run.ExitCode);
    }

    [Fact]
    public async Task Mount_t_s3_with_an_account_reuses_its_keys()
    {
        FakeAppDriveGateway drives = new() { NetworkTypes = { "s3" } };

        Run run = await RunCloudAsync(
            drives, new FakeAppCloudAccountsGateway(), string.Empty,
            "-t", "s3", "-o", "endpoint=https://s3.example.com,bucket=photos,account=s3-1");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("s3-1", drives.LastNetworkRequest?.Account);
        Assert.Null(drives.LastNetworkRequest?.Secrets);
        Assert.False(drives.LastNetworkRequest!.Options!.ContainsKey("account"));
    }

    [Fact]
    public async Task A_refused_cloud_mount_fails_with_32()
    {
        FakeAppDriveGateway drives = new()
        {
            NetworkTypes = { "dropbox" },
            NextFailure = new KazoCloudAccountException(KazoCloudAccountError.ReconnectRequired, "sign in again")
        };
        FakeAppCloudAccountsGateway accounts = new FakeAppCloudAccountsGateway().WithProvider("dropbox", "Dropbox").WithAccount("a1", "dropbox", "alice");

        Run run = await RunCloudAsync(drives, accounts, string.Empty, "-t", "dropbox");

        Assert.Equal(32, run.ExitCode);
        Assert.Contains("sign in again", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hostfs_with_options_is_a_usage_error()
    {
        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway(), "-t", "hostfs", "-o", "root=/x");

        Assert.Equal(1, run.ExitCode);
    }

    private static Task<Run> RunCloudAsync(FakeAppDriveGateway drives, FakeAppCloudAccountsGateway accounts, string input, params string[] arguments) =>
        RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, arguments, accounts, input);

    private static Task<Run> RunAsync(
        AppDefinition definition,
        Func<AppManifest, TerminalAppBase> command,
        FakeAppDriveGateway drives,
        params string[] arguments) => RunAsync(definition, command, drives, arguments, null, string.Empty);

    private static async Task<Run> RunAsync(
        AppDefinition definition,
        Func<AppManifest, TerminalAppBase> command,
        FakeAppDriveGateway drives,
        string[] arguments,
        FakeAppCloudAccountsGateway? accounts,
        string input)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(definition);
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithFile("/proc/mounts", ProcMounts)
            .WithDirectory("/home/alice");
        MinimalAppExecutionContext app = new(
            manifest, fileSystem: fileSystem, homeDirectory: "/home/alice",
            environment: new FakeAppEnvironmentGateway("/home/alice"), drives: drives, cloudAccounts: accounts);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app, arguments, new StringReader(input), stdout, stderr,
            "/home/alice", new Dictionary<string, string> { ["HOME"] = "/home/alice" });

        using IDisposable scope = KazoExecutionContext.Enter(app);
        int exitCode = await command(manifest).ExecuteAsync(context, CancellationToken.None);
        return new Run(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);
}
