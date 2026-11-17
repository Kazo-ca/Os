using System.Diagnostics;
using System.Text.Json;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0282 section 5: the shipped cloud drive connectors (Dropbox, OneDrive, S3) composed as the
/// build chose them, the provider of a connector left out never offered, the runtime list of
/// <c>appsettings.json</c>, and the MSBuild switches of <c>build/Kazo.Os.CloudDrives.targets</c>.
/// </summary>
public sealed class CloudDriveCompositionTests
{
    private static readonly string[] Shipped = ["dropbox", "onedrive", "s3"];

    public static TheoryData<string[]> EverySubset()
    {
        TheoryData<string[]> data = [];
        for (int mask = 0; mask < 8; mask++)
        {
            data.Add([.. Shipped.Where((_, index) => (mask & (1 << index)) != 0)]);
        }

        return data;
    }

    [Fact]
    public void This_build_compiles_the_three_connectors_in()
    {
        Assert.Equal(Shipped, CloudDriveServiceCollectionExtensions.CompiledTypes);
        Assert.Empty(CloudDriveServiceCollectionExtensions.LeftOutTypes);
        Assert.True(CloudDriveFeatures.IsDropboxSupported);
        Assert.True(CloudDriveFeatures.IsOneDriveSupported);
        Assert.True(CloudDriveFeatures.IsS3Supported);
    }

    [Theory]
    [MemberData(nameof(EverySubset))]
    public async Task Only_the_kept_connectors_become_drive_sources(string[] kept)
    {
        ServiceCollection services = Ecosystem();
        services.RemoveAll<INetworkDriveSource>();
        services.AddKazoOsShippedCloudDrives(kept);

        await using ServiceProvider root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = root.CreateAsyncScope();
        NetworkDriveService drives = scope.ServiceProvider.GetRequiredService<NetworkDriveService>();

        Assert.Equal([.. kept.Append("hostfs").Order(StringComparer.Ordinal)], drives.SourceTypes);
        Assert.All(scope.ServiceProvider.GetServices<INetworkDriveSource>(), static source => Assert.IsType<SwitchableDriveSource>(source));
    }

    [Fact]
    public async Task The_default_composition_has_the_three_connectors_behind_their_switch()
    {
        await using ServiceProvider root = Ecosystem().BuildServiceProvider();
        await using AsyncServiceScope scope = root.CreateAsyncScope();

        Assert.Equal(["dropbox", "hostfs", "onedrive", "s3"], scope.ServiceProvider.GetRequiredService<NetworkDriveService>().SourceTypes);
    }

    [Fact]
    public void A_provider_whose_connector_the_build_left_out_is_marked_disabled()
    {
        CloudProviderSettings host = new("full", "folder", "client") { Disabled = ["s3"] };

        CloudProviderSettings composed = CloudDriveServiceCollectionExtensions.WithLeftOutTypes(host);

        // This build keeps the three connectors: only the host's own list remains.
        Assert.Equal(["s3"], composed.Disabled);
        Assert.Equal(["dropbox", "onedrive"], composed.BuildProviders().Select(static provider => provider.Id));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("dropbox, S3", new[] { "dropbox", "s3" })]
    public void KazoOs_CloudDrives_Disabled_reads_as_a_string(string? value, string[] expected)
    {
        Dictionary<string, string?> values = value is null ? [] : new() { ["KazoOs:CloudDrives:Disabled"] = value };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(expected, KazoOsBrowserHostExtensions.ReadDisabledCloudDriveTypes(configuration.GetSection("KazoOs:CloudDrives:Disabled")));
    }

    [Fact]
    public void KazoOs_CloudDrives_Disabled_reads_as_a_json_array()
    {
        using MemoryStream json = new("""{ "KazoOs": { "CloudDrives": { "Disabled": [ "OneDrive", "s3" ] } } }"""u8.ToArray());
        IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(json).Build();

        Assert.Equal(["onedrive", "s3"], KazoOsBrowserHostExtensions.ReadDisabledCloudDriveTypes(configuration.GetSection("KazoOs:CloudDrives:Disabled")));
    }

    [Theory]
    [InlineData("", true, "KAZOOS_DROPBOX_DRIVES;KAZOOS_ONEDRIVE_DRIVES;KAZOOS_S3_DRIVES")]
    [InlineData("-p:KazoOsDropboxDrives=false", true, "KAZOOS_ONEDRIVE_DRIVES;KAZOOS_S3_DRIVES")]
    [InlineData("-p:KazoOsOneDriveDrives=false", true, "KAZOOS_DROPBOX_DRIVES;KAZOOS_S3_DRIVES")]
    [InlineData("-p:KazoOsS3Drives=false", true, "KAZOOS_DROPBOX_DRIVES;KAZOOS_ONEDRIVE_DRIVES")]
    [InlineData("-p:KazoOsDropboxDrives=false -p:KazoOsOneDriveDrives=false -p:KazoOsS3Drives=false", false, "")]
    public async Task The_msbuild_properties_choose_the_constants_the_switches_and_the_reference(string properties, bool referenced, string constants)
    {
        string project = Path.Combine(FindRoot(), "OS", "Kazo.Os.Ecosystem.Host", "Kazo.Os.Ecosystem.Host.csproj");
        List<string> arguments = ["msbuild", project, "-getProperty:DefineConstants", "-getItem:ProjectReference", "-getItem:RuntimeHostConfigurationOption"];
        arguments.AddRange(properties.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        using JsonDocument evaluated = JsonDocument.Parse(await RunDotnetAsync(arguments));

        string defined = evaluated.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!;
        string[] shipped = [.. defined.Split(';').Where(static name => name.StartsWith("KAZOOS_", StringComparison.Ordinal) && name.EndsWith("_DRIVES", StringComparison.Ordinal) && name != "KAZOOS_CLOUD_DRIVES")];
        Assert.Equal(constants, string.Join(';', shipped));
        Assert.Equal(referenced, defined.Split(';').Contains("KAZOOS_CLOUD_DRIVES"));

        JsonElement items = evaluated.RootElement.GetProperty("Items");
        Assert.Equal(referenced, items.GetProperty("ProjectReference").EnumerateArray()
            .Any(static item => item.GetProperty("Identity").GetString()!.Contains("Kazo.Os.Platform.CloudDrives.csproj", StringComparison.Ordinal)));
        Dictionary<string, string> switches = items.GetProperty("RuntimeHostConfigurationOption").EnumerateArray()
            .Where(static item => item.GetProperty("Identity").GetString()!.StartsWith("Kazo.Os.CloudDrives.", StringComparison.Ordinal))
            .ToDictionary(static item => item.GetProperty("Identity").GetString()!, static item => item.GetProperty("Value").GetString()!);
        Assert.Equal(constants.Contains("DROPBOX", StringComparison.Ordinal) ? "true" : "false", switches[CloudDriveFeatures.DropboxSwitchName]);
        Assert.Equal(constants.Contains("ONEDRIVE", StringComparison.Ordinal) ? "true" : "false", switches[CloudDriveFeatures.OneDriveSwitchName]);
        Assert.Equal(constants.Contains("S3", StringComparison.Ordinal) ? "true" : "false", switches[CloudDriveFeatures.S3SwitchName]);
    }

    private static ServiceCollection Ecosystem()
    {
        ServiceCollection services = new();
        services.AddSingleton<NavigationManager, EcosystemServiceCollectionExtensionsTests.TestNavigationManager>();
        services.AddSingleton<IJSRuntime, EcosystemServiceCollectionExtensionsTests.NonInvokedJsRuntime>();
        services.AddKazoOsEcosystem();
        services.RemoveAll<Microsoft.Extensions.Logging.ILoggerProvider>();
        return services;
    }

    private static async Task<string> RunDotnetAsync(IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // An evaluation only: no node is left behind to hold the redirected output (pitfalls.md).
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
        return await output;
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
}
