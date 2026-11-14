using System.Diagnostics;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0271 §4: <c>build/tools/notify-app-repositories.sh</c> tells the app repositories that Kazo OS
/// published a version (repository_dispatch <c>kazoos-sdk-released</c> with the version and the
/// channel). "Release Kazo OS" runs it once a version is released; a wrong payload would move every
/// app to a wrong version, and a failure must never fail a release that is already done.
/// The cases use a stand-in for <c>gh</c> that records each call.
/// </summary>
public sealed class NotifyAppRepositoriesScriptTests : IDisposable
{
    private static readonly string Script = Path.Combine(FindRoot(), "build", "tools", "notify-app-repositories.sh");
    private readonly string _directory = Directory.CreateTempSubdirectory("kazoos-notify-apps-").FullName;

    [Fact]
    public void Each_listed_repository_receives_the_version_and_the_channel()
    {
        ScriptResult? result = Run(
            "# comment\n\nKazo-ca/Os-App-One\n  Kazo-ca/Os-App-Two  # trailing comment\nkazo-ca/os-app-one\n",
            ["--version", "3.4.0-preview.1", "--channel", "beta"]);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        string[] calls = ReadCalls();
        Assert.Equal(2, calls.Length);
        Assert.Contains("api --method POST repos/Kazo-ca/Os-App-One/dispatches --input -", calls[0], StringComparison.Ordinal);
        Assert.Contains("api --method POST repos/Kazo-ca/Os-App-Two/dispatches --input -", calls[1], StringComparison.Ordinal);
        Assert.All(calls, call => Assert.Contains(
            "{\"event_type\":\"kazoos-sdk-released\",\"client_payload\":{\"version\":\"3.4.0-preview.1\",\"channel\":\"beta\"}}",
            call,
            StringComparison.Ordinal));
        Assert.Contains("notified=2/2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_that_refuses_gets_a_warning_and_the_others_are_still_told()
    {
        ScriptResult? result = Run("Kazo-ca/Refuses\nKazo-ca/Os-App-Two\n", ["--version", "3.4.0", "--channel", "stable"]);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("::warning title=App notification::Kazo-ca/Refuses was not told", result.Output, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", result.Output, StringComparison.Ordinal);
        Assert.Contains("notified Kazo-ca/Os-App-Two", result.Output, StringComparison.Ordinal);
        Assert.Contains("notified=1/2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_token_it_warns_and_sends_nothing()
    {
        ScriptResult? result = Run("Kazo-ca/Os-App-One\n", ["--version", "3.4.0", "--channel", "stable"], token: null);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("::warning title=App notification::No token of the Kazo OS automation GitHub App", result.Output, StringComparison.Ordinal);
        Assert.Empty(ReadCalls());
    }

    [Fact]
    public void A_dry_run_prints_the_payload_and_sends_nothing()
    {
        ScriptResult? result = Run("Kazo-ca/Os-App-One\n", ["--version", "3.4.0", "--channel", "stable", "--dry-run"]);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("would notify Kazo-ca/Os-App-One: {\"event_type\":\"kazoos-sdk-released\"", result.Output, StringComparison.Ordinal);
        Assert.Empty(ReadCalls());
    }

    [Theory]
    [InlineData("Kazo-ca/Os-App-One\n", "3.4", "stable", "is not a Kazo OS version")]
    [InlineData("Kazo-ca/Os-App-One\n", "3.4.0\",\"x\":\"1", "stable", "is not a Kazo OS version")]
    [InlineData("Kazo-ca/Os-App-One\n", "3.4.0-preview.1", "stable", "the stable channel takes a version without suffix")]
    [InlineData("Kazo-ca/Os-App-One\n", "3.4.0", "beta", "the beta channel takes a version with a suffix")]
    [InlineData("Kazo-ca/Os-App-One\n", "3.4.0", "nightly", "the channel is stable or beta")]
    [InlineData("Kazo-ca/Os-App-One\nnot a repository\n", "3.4.0", "stable", "line 2: 'notarepository' is not owner/name")]
    public void A_wrong_version_channel_or_list_sends_nothing(string list, string version, string channel, string reason)
    {
        ScriptResult? result = Run(list, ["--version", version, "--channel", channel]);
        if (result is null)
        {
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
        Assert.Empty(ReadCalls());
    }

    [Fact]
    public void The_repository_list_of_the_release_is_valid()
    {
        ScriptResult? result = Run(list: null, ["--version", "3.4.0", "--channel", "stable", "--dry-run"]);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("would notify Kazo-ca/", result.Output, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string CallLog => Path.Combine(_directory, "gh-calls.log");

    private string[] ReadCalls() =>
        File.Exists(CallLog) ? File.ReadAllLines(CallLog).Where(line => line.Length > 0).ToArray() : [];

    /// <summary>
    /// Runs the script with a stand-in gh that writes "arguments | stdin" on one line of the call
    /// log, and fails like GitHub (HTTP 404) for a repository named Refuses. <paramref name="list"/>
    /// null uses the real build/app-repositories.txt. The CI runs this suite on Linux, where bash
    /// always exists; on Windows without bash on the PATH, the case returns null and passes.
    /// </summary>
    private ScriptResult? Run(string? list, string[] arguments, string? token = "test-token")
    {
        string gh = Path.Combine(_directory, "gh");
        File.WriteAllText(gh, $"""
            #!/usr/bin/env bash
            input="$(cat)"
            echo "$* | $input" >> '{CallLog}'
            if [[ "$*" == *Refuses* ]]; then echo 'gh: Not Found (HTTP 404)' >&2; exit 1; fi
            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (list is not null)
        {
            string file = Path.Combine(_directory, "app-repositories.txt");
            File.WriteAllText(file, list);
            start.ArgumentList.Add("--list");
            start.ArgumentList.Add(file);
        }

        start.Environment["GH"] = gh;
        start.Environment.Remove("GH_TOKEN");
        if (token is not null)
        {
            start.Environment["GH_TOKEN"] = token;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start.");
        }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows())
        {
            return null;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new ScriptResult(process.ExitCode, output.Result, error.Result);
        }
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Kazo.Os.sln not found above the test output directory.");
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error);
}
