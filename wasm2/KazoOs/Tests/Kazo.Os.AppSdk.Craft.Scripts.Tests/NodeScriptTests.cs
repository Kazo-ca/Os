using System.ComponentModel;
using System.Diagnostics;

namespace Kazo.Os.AppSdk.Craft.Scripts.Tests;

/// <summary>
/// Runs each test file of <c>scripts/</c> with Node's test runner: the OS side
/// (<c>craft-host.js</c>) and the iframe proxy (<c>craft-proxy.js</c> and its modules) of the
/// shared Craft module (ADR 0259), against a fake Craft app. Skipped when <c>node</c> is not on
/// the PATH; the GitHub runners have it.
/// </summary>
public sealed class NodeScriptTests
{
    /// <summary>Gets the test files, by name, found next to this project's sources.</summary>
    public static TheoryData<string> TestFiles()
    {
        TheoryData<string> files = new();
        foreach (string path in Directory.EnumerateFiles(ScriptsDirectory(), "*.test.mjs").Order(StringComparer.Ordinal))
        {
            files.Add(Path.GetFileName(path));
        }

        return files;
    }

    /// <summary>Every test of one file passes.</summary>
    /// <param name="file">The test file's name in <c>scripts/</c>.</param>
    [NodeTheory]
    [MemberData(nameof(TestFiles))]
    public void Script_tests_pass(string file)
    {
        (int code, string output) = RunNode(["--test", Path.Combine(ScriptsDirectory(), file)], TimeSpan.FromSeconds(60));

        Assert.True(code == 0, output);
    }

    /// <summary>The scripts have tests: an empty folder would make the theory pass with no case.</summary>
    [Fact]
    public void Scripts_folder_has_tests() => Assert.NotEmpty(TestFiles());

    internal static bool NodeAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            return RunNode(["--version"], TimeSpan.FromSeconds(10)).ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) RunNode(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ProcessStartInfo start = new("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "node timed out");
        }

        return (process.ExitCode, output.Result + error.Result);
    }

    private static string ScriptsDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found above the test output.");
        return Path.Combine(root, "Tests", "Kazo.Os.AppSdk.Craft.Scripts.Tests", "scripts");
    }
}

/// <summary>A <see cref="TheoryAttribute"/> skipped when <c>node</c> cannot be started.</summary>
public sealed class NodeTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips the theory when Node is not on the PATH.</summary>
    public NodeTheoryAttribute()
    {
        if (!NodeScriptTests.NodeAvailable)
        {
            Skip = "node is not on the PATH";
        }
    }
}
