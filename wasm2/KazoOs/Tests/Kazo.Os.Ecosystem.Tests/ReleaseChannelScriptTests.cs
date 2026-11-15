using System.Diagnostics;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0271 §1: <c>build/tools/check-release-channel.sh</c> keeps each release channel on its own kind
/// of version. <c>main</c> publishes the stable version (no suffix), <c>beta</c> publishes prereleases
/// (a suffix), any other branch has no channel. "Release Kazo OS" runs it before publishing anything,
/// so a mistake here would push a prerelease as the stable version, or the reverse, to nuget.org.
/// </summary>
public sealed class ReleaseChannelScriptTests : IDisposable
{
    private static readonly string Script = Path.Combine(FindRoot(), "build", "tools", "check-release-channel.sh");
    private readonly string _directory = Directory.CreateTempSubdirectory("kazoos-release-channel-").FullName;

    [Theory]
    [InlineData("main", "3.3.4", "stable", "false")]
    [InlineData("beta", "3.4.0-preview.1", "beta", "true")]
    [InlineData("release-channels/base", "3.3.4", "none", "false")]
    [InlineData("release-channels/base", "3.4.0-preview.2", "none", "true")]
    public void A_version_that_fits_the_branch_passes(string branch, string version, string channel, string prerelease)
    {
        ScriptResult? result = Run(branch, version);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"version={version}", result.Output, StringComparison.Ordinal);
        Assert.Contains($"channel={channel}", result.Output, StringComparison.Ordinal);
        Assert.Contains($"prerelease={prerelease}", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main", "3.4.0-preview.1", "main publishes the stable version")]
    [InlineData("beta", "3.4.0", "beta publishes prereleases")]
    [InlineData("main", "three", "no valid KazoOsVersion")]
    [InlineData("beta", "3.4", "no valid KazoOsVersion")]
    public void A_version_that_does_not_fit_the_branch_is_refused(string branch, string version, string reason)
    {
        ScriptResult? result = Run(branch, version);
        if (result is null)
        {
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("version=", result.Output, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Runs the script against a temporary Versions.props. The CI runs this suite on Linux, where
    /// bash always exists; on Windows without bash on the PATH, the case returns null and passes.
    /// </summary>
    private ScriptResult? Run(string branch, string version)
    {
        string props = Path.Combine(_directory, "Versions.props");
        File.WriteAllText(props, $"<Project><PropertyGroup><KazoOsVersion>{version}</KazoOsVersion></PropertyGroup></Project>");

        ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Script);
        start.ArgumentList.Add(branch);
        start.ArgumentList.Add(props);

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
