namespace Kazo.Os.Infrastructure.Browser.Tests.FileSystem;

/// <summary>
/// The JSON form of a binary content chunk (ADR 0272 §5), run in Node: a chunk read by the JSON
/// adapter becomes <c>dataBase64</c>, and a chunk written with <c>binaryChunk</c> goes back to bytes
/// only when it matches its own hash.
/// </summary>
public sealed class BinaryChunksScriptTests
{
    [NodeTheory]
    [InlineData("round-trip")]
    [InlineData("hash-mismatch")]
    [InlineData("malformed")]
    [InlineData("legacy-without-hash")]
    public void Binary_chunk_scenario_passes(string scenario)
    {
        string root = SolutionDirectory();
        (int code, string output, string error) = NodeRunner.Run(
            [
                Path.Combine(root, "Tests/Kazo.Os.Infrastructure.Browser.Tests/FileSystem/binaryChunks.test.mjs"),
                Path.Combine(root, "Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/binaryChunks.js"),
                scenario
            ],
            TimeSpan.FromSeconds(30));

        Assert.True(code == 0 && output.Trim() == "ok", output + error);
    }

    private static string SolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found above the test output.");
    }
}
