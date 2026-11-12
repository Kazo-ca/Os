using System.Text;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>The libraries of an app in its package (ADR 0244 §1, issue #419).</summary>
public sealed class AppPackageLibraryTests
{
    private const string Protocol = "Kazo.Sample.Protocol";
    private const string Contracts = "Kazo.Sample.Contracts";

    private static byte[] Write(IReadOnlyDictionary<string, byte[]> libraries, IReadOnlyDictionary<string, byte[]>? satellites = null) =>
        AppPackageWriter.Write(
            Encoding.UTF8.GetBytes(AppPackageTests.ManifestJson()),
            _ => TestLibraries.Build("Kazo.Sample", "1.0.0"),
            _ => throw new InvalidOperationException("no asset"),
            satellites,
            null,
            libraries);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_assembly_says_its_name_its_version_and_whether_its_author_marked_it_shared(bool shared)
    {
        byte[] bytes = TestLibraries.Build(Contracts, "1.2.3", shared);

        AppPackageLibrary library = AppPackageLibrary.Describe("Kazo.Sample.Contracts.dll", bytes);

        Assert.Equal(Contracts, library.Name);
        Assert.Equal(new Version(1, 2, 3, 0), library.Version);
        Assert.Equal(AppPackage.Sha256(bytes), library.Sha256);
        Assert.Equal(shared, library.Shared);
        Assert.Equal("Kazo.Sample.Contracts.dll", library.FileName);
    }

    [Fact]
    public void A_file_that_is_no_assembly_is_refused()
    {
        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageLibrary.Describe("x.dll", [1, 2, 3]));

        Assert.Equal("apps.package.bad-assembly", refused.Code);
    }

    [Fact]
    public void The_writer_packs_the_libraries_in_lib_and_lists_them_with_the_shared_mark()
    {
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");
        byte[] contracts = TestLibraries.Build(Contracts, "1.2.0", shared: true);

        AppPackage package = AppPackage.Read(Write(new Dictionary<string, byte[]>
        {
            ["Kazo.Sample.Protocol.dll"] = protocol,
            ["Kazo.Sample.Contracts.dll"] = contracts,
        }));

        Assert.Equal(
            [(Contracts, new Version(1, 2, 0, 0), true), (Protocol, new Version(1, 0, 0, 0), false)],
            package.Libraries.Select(static library => (library.Name, library.Version, library.Shared)));
        Assert.Equal(AppPackage.Sha256(contracts), package.Libraries[0].Sha256);
        // The reference check reads every assembly of lib/, the libraries included.
        Assert.Equal(["Kazo.Sample.Contracts.dll", "Kazo.Sample.Protocol.dll", "Kazo.Sample.dll"], package.Assemblies.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_package_without_library_lists_none()
    {
        AppPackage package = AppPackage.Read(Write(new Dictionary<string, byte[]>()));

        Assert.Empty(package.Libraries);
        Assert.DoesNotContain("libraries", Encoding.UTF8.GetString(package.Files[AppPackage.DescriptionFileName]), StringComparison.Ordinal);
    }

    [Fact]
    public void An_assembly_the_manifest_declares_is_not_packed_as_a_library()
    {
        AppPackage package = AppPackage.Read(Write(new Dictionary<string, byte[]> { ["Kazo.Sample.dll"] = TestLibraries.Build("Kazo.Sample", "9.9.9") }));

        Assert.Empty(package.Libraries);
        Assert.Equal(["Kazo.Sample.dll"], package.Assemblies.Keys);
    }

    [Fact]
    public void A_library_is_packed_under_the_name_of_its_assembly()
    {
        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => Write(new Dictionary<string, byte[]> { ["Renamed.dll"] = TestLibraries.Build(Protocol, "1.0.0") }));

        Assert.Equal("apps.package.bad-assembly", refused.Code);
        Assert.Contains(Protocol, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_satellite_of_a_library_goes_with_it()
    {
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");

        AppPackage package = AppPackage.Read(Write(
            new Dictionary<string, byte[]> { ["Kazo.Sample.Protocol.dll"] = protocol },
            new Dictionary<string, byte[]> { ["fr/Kazo.Sample.Protocol.resources.dll"] = [7, 7] }));

        Assert.Equal(["fr/Kazo.Sample.Protocol.resources.dll"], package.SatelliteAssemblies.Keys);
    }

    private static Dictionary<string, byte[]> FilesWith(byte[] library, string fileName = "Kazo.Sample.Protocol.dll")
    {
        Dictionary<string, byte[]> files = AppPackageTests.SampleFiles();
        files["lib/" + fileName] = library;
        return files;
    }

    [Fact]
    public void A_listed_library_missing_from_lib_is_refused()
    {
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");

        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => AppPackage.Read(AppPackageTests.Build(AppPackageTests.SampleFiles(), libraries: [TestLibraries.Entry(protocol)])));

        Assert.Equal("apps.package.missing-assembly", refused.Code);
    }

    [Fact]
    public void A_library_listed_with_another_version_than_its_assembly_is_refused()
    {
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");
        object lie = new { name = Protocol, version = "2.0.0.0", sha256 = AppPackage.Sha256(protocol), shared = false };

        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => AppPackage.Read(AppPackageTests.Build(FilesWith(protocol), libraries: [lie])));

        Assert.Equal("apps.package.bad-description", refused.Code);
        Assert.Contains("2.0.0.0", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_cannot_mark_shared_a_library_its_author_did_not_mark()
    {
        // ADR 0244 §2: the mark is the author's, in the assembly; the package only copies it.
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");
        object lie = new { name = Protocol, version = "1.0.0.0", sha256 = AppPackage.Sha256(protocol), shared = true };

        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => AppPackage.Read(AppPackageTests.Build(FilesWith(protocol), libraries: [lie])));

        Assert.Equal("apps.package.bad-description", refused.Code);
    }

    [Fact]
    public void A_library_whose_hash_differs_from_its_entry_is_refused()
    {
        byte[] protocol = TestLibraries.Build(Protocol, "1.0.0");
        object lie = new { name = Protocol, version = "1.0.0.0", sha256 = new string('a', 64), shared = false };

        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => AppPackage.Read(AppPackageTests.Build(FilesWith(protocol), libraries: [lie])));

        Assert.Equal("apps.package.hash-mismatch", refused.Code);
    }

    [Fact]
    public void An_assembly_of_the_app_cannot_be_listed_as_a_library()
    {
        byte[] sample = TestLibraries.Build("Kazo.Sample", "1.0.0");
        Dictionary<string, byte[]> files = AppPackageTests.SampleFiles();
        files["lib/Kazo.Sample.dll"] = sample;

        AppPackageException refused = Assert.Throws<AppPackageException>(
            () => AppPackage.Read(AppPackageTests.Build(files, libraries: [TestLibraries.Entry(sample)])));

        Assert.Equal("apps.package.bad-description", refused.Code);
    }

    [Theory]
    [InlineData("""{"libraries":"x"}""")]
    [InlineData("""{"libraries":[{"name":"A"}]}""")]
    [InlineData("""{"libraries":[{"name":"../A","version":"1.0.0.0","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"libraries":[{"name":"A","version":"one","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    [InlineData("""{"libraries":[{"name":"A","version":"1.0.0.0","sha256":"abc"}]}""")]
    [InlineData("""{"libraries":[{"name":"A","version":"1.0.0.0","sha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"name":"a","version":"1.0.0.0","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""")]
    public void A_malformed_list_of_libraries_is_refused(string json)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageLibrary.ReadList(document.RootElement));

        Assert.Equal("apps.package.bad-description", refused.Code);
    }

    [Fact]
    public void A_description_from_before_the_list_has_no_library()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse("""{"appId":"x","version":"1.0.0","files":{}}""");

        Assert.Empty(AppPackageLibrary.ReadList(document.RootElement));
    }
}
