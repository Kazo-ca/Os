using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>References checked against the running OS before an install (ADR 0182 §5.3, Q1).</summary>
public sealed class AppPackageApiCheckTests
{
    private static Dictionary<string, byte[]> RealAssemblies() => new(StringComparer.Ordinal)
    {
        ["Kazo.Os.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location),
        ["Kazo.Os.Platform.Core.Tests.dll"] = File.ReadAllBytes(typeof(AppPackageApiCheckTests).Assembly.Location),
    };

    [Fact]
    public void Real_assemblies_find_every_reference_in_an_untrimmed_runtime()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(RealAssemblies(), LoadedRuntimeApiSurface.Instance);

        Assert.Empty(missing);
    }

    [Fact]
    public void A_member_the_runtime_lost_is_reported_once_with_its_assembly()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(
            new Dictionary<string, byte[]> { ["Kazo.Os.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location) },
            new TrimmedSurface(type: "System.IO.Compression.ZipArchive", member: "get_Entries"));

        MissingRuntimeApi entry = Assert.Single(missing);
        Assert.Equal("Kazo.Os.Platform.Core.dll", entry.Assembly);
        Assert.Equal("System.IO.Compression.ZipArchive.get_Entries() in System.IO.Compression", entry.Member);
    }

    [Fact]
    public void An_overload_the_runtime_lost_is_reported_even_when_one_of_the_same_length_remains()
    {
        // ADR 0242: the trimmed OS kept Int32.Parse(string, IFormatProvider) and removed the
        // ReadOnlySpan<char> overload; IRC passed the old count-only check and failed at run time.
        Assert.Equal(42, ParseSpan("42"));
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(
            new Dictionary<string, byte[]> { ["Kazo.Os.Platform.Core.Tests.dll"] = File.ReadAllBytes(typeof(AppPackageApiCheckTests).Assembly.Location) },
            new TrimmedSurface(type: "System.Int32", member: "Parse", parameterTypes: ["System.ReadOnlySpan`1<System.Char>", "System.IFormatProvider"]));

        MissingRuntimeApi entry = Assert.Single(missing);
        Assert.Equal("System.Int32.Parse(System.ReadOnlySpan`1<System.Char>,System.IFormatProvider) in System.Runtime", entry.Member);
    }

    [Theory]
    [InlineData(typeof(int), "System.Int32")]
    [InlineData(typeof(ReadOnlySpan<char>), "System.ReadOnlySpan`1<System.Char>")]
    [InlineData(typeof(int[]), "System.Int32[]")]
    [InlineData(typeof(int[,]), "System.Int32[,]")]
    [InlineData(typeof(Dictionary<string, List<int>>.Enumerator), "System.Collections.Generic.Dictionary`2+Enumerator<System.String,System.Collections.Generic.List`1<System.Int32>>")]
    public void A_loaded_type_has_the_text_form_of_the_metadata_decoder(Type type, string expected)
    {
        Assert.Equal(expected, RuntimeTypeNames.Of(type));
    }

    [Fact]
    public void Generic_parameters_of_a_type_and_of_a_method_are_told_apart()
    {
        System.Reflection.MethodInfo convertAll = typeof(List<>).GetMethod(nameof(List<int>.ConvertAll))!;

        Assert.Equal("System.Converter`2<!0,!!0>", RuntimeTypeNames.Of(convertAll.GetParameters()[0].ParameterType));
    }

    [Fact]
    public void A_missing_type_and_a_missing_assembly_are_reported()
    {
        IReadOnlyList<MissingRuntimeApi> missing = AppPackageApiCheck.FindMissing(
            new Dictionary<string, byte[]> { ["Kazo.Os.Platform.Core.dll"] = File.ReadAllBytes(typeof(AppPackage).Assembly.Location) },
            new TrimmedSurface(type: "System.IO.Compression.ZipArchive", member: null, assembly: "Microsoft.Extensions.Logging.Abstractions"));

        Assert.Contains(missing, item => item.Member == "System.IO.Compression.ZipArchive in System.IO.Compression");
        Assert.Contains(missing, item => item.Member == "assembly Microsoft.Extensions.Logging.Abstractions");
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_refused()
    {
        Assert.Equal(
            "apps.package.bad-assembly",
            Assert.Throws<AppPackageException>(() => AppPackageApiCheck.FindMissing(new Dictionary<string, byte[]> { ["x.dll"] = [1, 2, 3] }, LoadedRuntimeApiSurface.Instance)).Code);
    }

    private static int ParseSpan(string text) => int.Parse(text.AsSpan(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The real runtime, minus one member (every overload, or only the one with
    /// <paramref name="parameterTypes"/>), or one whole type, and optionally one assembly.
    /// </summary>
    private sealed class TrimmedSurface(string type, string? member, string? assembly = null, string[]? parameterTypes = null) : IRuntimeApiSurface
    {
        public bool HasAssembly(string assemblyName) =>
            assemblyName != assembly && LoadedRuntimeApiSurface.Instance.HasAssembly(assemblyName);

        public bool HasType(string assemblyName, string typeName) =>
            !(member is null && typeName == type) && LoadedRuntimeApiSurface.Instance.HasType(assemblyName, typeName);

        public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount) =>
            !(typeName == type && methodName == member)
            && LoadedRuntimeApiSurface.Instance.HasMethod(assemblyName, typeName, methodName, genericArity, parameterCount);

        public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, IReadOnlyList<string> parameters) =>
            !(typeName == type && methodName == member && (parameterTypes is null || parameters.SequenceEqual(parameterTypes)))
            && LoadedRuntimeApiSurface.Instance.HasMethod(assemblyName, typeName, methodName, genericArity, parameters);

        public bool HasField(string assemblyName, string typeName, string fieldName) =>
            LoadedRuntimeApiSurface.Instance.HasField(assemblyName, typeName, fieldName);
    }
}
