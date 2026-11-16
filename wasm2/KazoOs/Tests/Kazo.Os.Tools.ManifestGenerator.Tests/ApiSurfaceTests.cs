using System.Text;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>
/// Reads the surface of the .NET runtime these tests run on once: the same files the OS ships before
/// trimming, so every lookup must agree with <see cref="LoadedRuntimeApiSurface"/>.
/// </summary>
public sealed class RuntimeSurfaceFixture
{
    public RuntimeSurfaceFixture()
    {
        RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Surface = ApiSurfaceGenerator.Generate(RuntimeDirectory, "9.9.9");
    }

    public string RuntimeDirectory { get; }

    internal ApiSurfaceFile Surface { get; }
}

/// <summary>The API surface of a publish and its file (ADR 0277 §1).</summary>
public sealed class ApiSurfaceTests(RuntimeSurfaceFixture fixture) : IClassFixture<RuntimeSurfaceFixture>
{
    public static TheoryData<string, string, string, int, string[]> Methods => new()
    {
        // ADR 0242: the overload IRC needed, next to one of the same length.
        { "System.Runtime", "System.Int32", "Parse", 0, ["System.ReadOnlySpan`1<System.Char>", "System.IFormatProvider"] },
        { "System.Runtime", "System.Int32", "Parse", 0, ["System.String", "System.IFormatProvider"] },
        // Issue #346: a generic extension method of System.Linq.
        { "System.Linq", "System.Linq.Enumerable", "ToHashSet", 1, ["System.Collections.Generic.IEnumerable`1<!!0>"] },
        // A constructor and a method inherited from a base class.
        { "System.Collections", "System.Collections.Generic.List`1", ".ctor", 0, ["System.Int32"] },
        { "System.Runtime", "System.ArgumentNullException", "GetType", 0, [] },
        // An interface sees its base interfaces and System.Object.
        { "System.Runtime", "System.Collections.Generic.IList`1", "get_Count", 0, [] },
        { "System.Runtime", "System.Collections.Generic.IList`1", "ToString", 0, [] },
        // A struct always has its parameterless constructor.
        { "System.Runtime", "System.DateTime", ".ctor", 0, [] },
        // Absent members.
        { "System.Runtime", "System.Int32", "NoSuchMethod", 0, [] },
        { "System.Runtime", "System.Int32", "Parse", 0, ["System.Double"] },
    };

    [Theory]
    [MemberData(nameof(Methods))]
    public void A_method_lookup_agrees_with_the_loaded_runtime(string assembly, string type, string method, int arity, string[] parameters)
    {
        bool expected = LoadedRuntimeApiSurface.Instance.HasMethod(assembly, type, method, arity, parameters);

        Assert.Equal(expected, fixture.Surface.HasMethod(assembly, type, method, arity, parameters));
        Assert.Equal(
            LoadedRuntimeApiSurface.Instance.HasMethod(assembly, type, method, arity, parameters.Length),
            fixture.Surface.HasMethod(assembly, type, method, arity, parameters.Length));
    }

    [Theory]
    [InlineData("System.Runtime", "System.String")]
    [InlineData("System.Runtime", "System.Collections.Generic.Dictionary`2+Enumerator")]
    [InlineData("System.Private.CoreLib", "System.String")]
    [InlineData("System.Runtime", "System.NoSuchType")]
    [InlineData("No.Such.Assembly", "System.String")]
    public void A_type_lookup_follows_forwarders_like_the_loaded_runtime(string assembly, string type)
    {
        Assert.Equal(LoadedRuntimeApiSurface.Instance.HasType(assembly, type), fixture.Surface.HasType(assembly, type));
        Assert.Equal(LoadedRuntimeApiSurface.Instance.HasAssembly(assembly), fixture.Surface.HasAssembly(assembly));
    }

    [Theory]
    [InlineData("System.Runtime", "System.String", "Empty")]
    [InlineData("System.Runtime", "System.Int32", "MaxValue")]
    [InlineData("System.Runtime", "System.Int32", "NoSuchField")]
    public void A_field_lookup_agrees_with_the_loaded_runtime(string assembly, string type, string field) =>
        Assert.Equal(LoadedRuntimeApiSurface.Instance.HasField(assembly, type, field), fixture.Surface.HasField(assembly, type, field));

    [Fact]
    public void The_check_of_a_package_gives_the_same_framework_verdict_as_at_install_time()
    {
        Dictionary<string, byte[]> package = new()
        {
            ["Kazo.Os.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackageApiCheck).Assembly.Location),
            ["Kazo.Os.Tools.ManifestGenerator.Tests.dll"] = File.ReadAllBytes(typeof(ApiSurfaceTests).Assembly.Location),
        };

        Assert.Equal(
            FrameworkOnly(AppPackageApiCheck.FindMissing(package, LoadedRuntimeApiSurface.Instance)),
            FrameworkOnly(AppPackageApiCheck.FindMissing(package, fixture.Surface)));
    }

    [Fact]
    public void A_member_the_publish_removed_is_reported_by_the_check()
    {
        // The trimmed publish of the issue #346 report: ToHashSet removed from System.Linq.
        ApiSurfaceDocument trimmed = fixture.Surface.Document with
        {
            Assemblies = new SortedDictionary<string, ApiSurfaceAssembly>(
                fixture.Surface.Document.Assemblies.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Key == "System.Linq" ? WithoutMethod(pair.Value, "System.Linq.Enumerable", "ToHashSet`") : pair.Value),
                StringComparer.Ordinal),
        };
        Dictionary<string, byte[]> package = new()
        {
            ["Kazo.Os.Tools.ManifestGenerator.Tests.dll"] = File.ReadAllBytes(typeof(ApiSurfaceTests).Assembly.Location),
        };

        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(package, new ApiSurfaceFile(trimmed));

        Assert.Contains(missing, static entry => entry.Member.StartsWith("System.Linq.Enumerable.ToHashSet", StringComparison.Ordinal) && entry.Member.EndsWith("in System.Linq", StringComparison.Ordinal));
        Assert.Contains("x", CallsToHashSet(["x"]));
    }

    [Fact]
    public void A_surface_written_to_a_file_reads_back_the_same()
    {
        using MemoryStream stream = new();
        fixture.Surface.Write(stream);
        stream.Position = 0;

        ApiSurfaceFile read = ApiSurfaceFile.Read(stream);

        Assert.Equal("9.9.9", read.OsVersion);
        Assert.Equal(fixture.Surface.Document.Assemblies.Keys.Order(StringComparer.Ordinal), read.Document.Assemblies.Keys.Order(StringComparer.Ordinal));
        Assert.True(read.HasMethod("System.Linq", "System.Linq.Enumerable", "ToHashSet", 1, ["System.Collections.Generic.IEnumerable`1<!!0>"]));
    }

    [Fact]
    public void A_plain_json_surface_is_read_too()
    {
        const string json = """
            {"format":1,"osVersion":"1.2.3","assemblies":{
              "A":{"types":{"A.T":{"m":["Go`0(System.Int32,System.Collections.Generic.Dictionary`2<System.String,System.Int32>)"],"f":["Value"],"v":true}},
                   "forwards":{"B.U":"B"}},
              "B":{"types":{"B.U":{}}}}}
            """;

        ApiSurfaceFile surface = ApiSurfaceFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.True(surface.HasMethod("A", "A.T", "Go", 0, 2));
        Assert.False(surface.HasMethod("A", "A.T", "Go", 0, 3));
        Assert.True(surface.HasMethod("A", "A.T", ".ctor", 0, []));
        Assert.True(surface.HasField("A", "A.T", "Value"));
        Assert.True(surface.HasType("A", "B.U"));
        Assert.False(surface.HasType("A", "B.Missing"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":2,"osVersion":"1.0.0","assemblies":{}}""")]
    public void A_file_that_is_not_a_surface_is_refused(string content) =>
        Assert.Throws<InvalidDataException>(() => ApiSurfaceFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(content))));

    [Fact]
    public void A_folder_without_the_core_library_is_refused()
    {
        string folder = Directory.CreateTempSubdirectory("api-surface-").FullName;
        try
        {
            Assert.Throws<InvalidDataException>(() => ApiSurfaceGenerator.Generate(folder, "1.0.0"));
            Assert.Throws<DirectoryNotFoundException>(() => ApiSurfaceGenerator.Generate(Path.Combine(folder, "missing"), "1.0.0"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Calls the member the trimmed test surface removes, so this assembly references it.</summary>
    internal static HashSet<string> CallsToHashSet(IEnumerable<string> values) => values.ToHashSet();

    internal static ApiSurfaceAssembly WithoutMethod(ApiSurfaceAssembly assembly, string typeName, string methodPrefix)
    {
        SortedDictionary<string, ApiSurfaceType> types = new(assembly.Types, StringComparer.Ordinal);
        ApiSurfaceType type = types[typeName];
        types[typeName] = type with { Methods = [.. type.Methods!.Where(key => !key.StartsWith(methodPrefix, StringComparison.Ordinal))] };
        return assembly with { Types = types };
    }

    /// <summary>Keeps what the surface answers for: references into the .NET framework.</summary>
    private static string[] FrameworkOnly(IReadOnlyList<MissingRuntimeApi> missing) =>
        [.. missing
            .Select(static entry => entry.Member)
            .Where(static member => member.Contains(" in System", StringComparison.Ordinal) || member.StartsWith("System", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
}
