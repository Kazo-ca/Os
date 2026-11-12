namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The libraries <c>pack</c> reads from the list the <c>KazoOsPack</c> target writes (ADR 0244 §1).</summary>
public sealed class PackagedLibrariesTests
{
    [Fact]
    public void Each_listed_file_is_read_by_its_file_name_and_blank_lines_are_skipped()
    {
        string directory = Directory.CreateTempSubdirectory("kazoos-libraries-").FullName;
        try
        {
            string first = Path.Combine(directory, "Kazo.Sample.Protocol.dll");
            string second = Path.Combine(directory, "Kazo.Sample.Contracts.dll");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            string list = Path.Combine(directory, "libraries.txt");
            File.WriteAllLines(list, [first, string.Empty, "  " + second + "  "]);

            SortedDictionary<string, byte[]> libraries = PackagedLibraries.Read(list);

            Assert.Equal(["Kazo.Sample.Contracts.dll", "Kazo.Sample.Protocol.dll"], libraries.Keys);
            Assert.Equal([1], libraries["Kazo.Sample.Protocol.dll"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_assembly_listed_twice_is_packed_once_from_its_first_path()
    {
        string directory = Directory.CreateTempSubdirectory("kazoos-libraries-").FullName;
        try
        {
            string package = Directory.CreateDirectory(Path.Combine(directory, "package")).FullName;
            string project = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
            File.WriteAllBytes(Path.Combine(package, "Kazo.Sample.Protocol.dll"), [1]);
            File.WriteAllBytes(Path.Combine(project, "Kazo.Sample.Protocol.dll"), [2]);
            string list = Path.Combine(directory, "libraries.txt");
            File.WriteAllLines(list, [Path.Combine(package, "Kazo.Sample.Protocol.dll"), Path.Combine(project, "Kazo.Sample.Protocol.dll")]);

            SortedDictionary<string, byte[]> libraries = PackagedLibraries.Read(list);

            Assert.Equal([1], Assert.Single(libraries).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_listed_file_that_is_missing_fails_the_pack()
    {
        string directory = Directory.CreateTempSubdirectory("kazoos-libraries-").FullName;
        try
        {
            string list = Path.Combine(directory, "libraries.txt");
            File.WriteAllLines(list, [Path.Combine(directory, "Missing.dll")]);

            Assert.ThrowsAny<IOException>(() => PackagedLibraries.Read(list));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
