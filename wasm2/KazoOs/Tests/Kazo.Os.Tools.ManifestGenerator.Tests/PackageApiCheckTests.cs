namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The packaging check of ADR 0277 §2: a package that calls a trimmed member fails its build.</summary>
public sealed class PackageApiCheckTests(RuntimeSurfaceFixture fixture) : IClassFixture<RuntimeSurfaceFixture>
{
    private static Dictionary<string, byte[]> Package => new()
    {
        ["Kazo.Os.Tools.ManifestGenerator.Tests.dll"] = File.ReadAllBytes(typeof(PackageApiCheckTests).Assembly.Location),
    };

    [Fact]
    public void A_package_calling_a_trimmed_member_fails_with_the_replacement()
    {
        IReadOnlyList<PackageApiDiagnostic> findings = PackageApiCheck.Check(Package, TrimmedSurface(), asError: true);

        PackageApiDiagnostic finding = Assert.Single(findings, static entry => entry.Message.Contains("System.Linq.Enumerable.ToHashSet", StringComparison.Ordinal));
        Assert.Equal("KOSAPP0100", finding.Code);
        Assert.True(finding.IsError);
        Assert.Contains("Kazo OS 9.9.9 (trimmed) does not have", finding.Message, StringComparison.Ordinal);
        Assert.Contains("use new HashSet<T>(source) instead", finding.Message, StringComparison.Ordinal);
        Assert.StartsWith("/app/manifest.json : error KOSAPP0100: Kazo.Os.Tools.ManifestGenerator.Tests.dll calls ", finding.ToMsBuildLine("/app/manifest.json"), StringComparison.Ordinal);
        Assert.Contains("x", ApiSurfaceTests.CallsToHashSet(["x"]));
    }

    [Fact]
    public void With_the_check_turned_to_warnings_the_same_member_only_warns()
    {
        PackageApiDiagnostic finding = Assert.Single(
            PackageApiCheck.Check(Package, TrimmedSurface(), asError: false),
            static entry => entry.Message.Contains("System.Linq.Enumerable.ToHashSet", StringComparison.Ordinal));

        Assert.Equal("KOSAPP0101", finding.Code);
        Assert.False(finding.IsError);
        Assert.Contains("KazoOsApiCheck=false", finding.Message, StringComparison.Ordinal);
        Assert.Contains(" : warning KOSAPP0101: ", finding.ToMsBuildLine("/app/manifest.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_whose_framework_calls_all_exist_passes()
    {
        IReadOnlyList<PackageApiDiagnostic> findings = PackageApiCheck.Check(Package, fixture.Surface, asError: true);

        Assert.DoesNotContain(findings, static entry => entry.Message.Contains(" calls System.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Linq.Enumerable.ToHashSet``1(System.Collections.Generic.IEnumerable`1<!!0>) in System.Linq", "use new HashSet<T>(source) instead")]
    [InlineData("System.String.Create``1(System.Int32,!!0,System.Buffers.SpanAction`2<System.Char,!!0>) in System.Runtime", "build the string with new string(char[]) or a StringBuilder instead")]
    [InlineData("System.String.Split(System.String[],System.StringSplitOptions) in System.Runtime", "split with a char[] separator or Split(string, StringSplitOptions) instead")]
    [InlineData("System.String.Split(System.String,System.StringSplitOptions) in System.Runtime", null)]
    [InlineData("System.Int32.Parse(System.String) in System.Runtime", null)]
    public void A_known_member_gets_its_replacement(string member, string? replacement) =>
        Assert.Equal(replacement, PackageApiCheck.SuggestReplacement(member));

    private ApiSurfaceFile TrimmedSurface()
    {
        ApiSurfaceDocument trimmed = fixture.Surface.Document with
        {
            Assemblies = new SortedDictionary<string, ApiSurfaceAssembly>(
                fixture.Surface.Document.Assemblies.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Key == "System.Linq" ? ApiSurfaceTests.WithoutMethod(pair.Value, "System.Linq.Enumerable", "ToHashSet`") : pair.Value),
                StringComparer.Ordinal),
        };
        return new ApiSurfaceFile(trimmed);
    }
}
