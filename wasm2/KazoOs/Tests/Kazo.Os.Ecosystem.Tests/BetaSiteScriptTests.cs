using System.Diagnostics;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0271 §3: the scripts of "Deploy Kazo OS beta site". <c>brand-beta-site.sh</c> names the PWA
/// and the tab « Kazo OS Beta » before the publish; <c>check-static-site-limits.sh</c> stops a site
/// that Cloudflare Pages would refuse (20,000 files, 25 MiB per file) with the reason. The real
/// <c>wwwroot</c> is used, so a rename of the product cannot silently skip the branding.
/// </summary>
public sealed class BetaSiteScriptTests : IDisposable
{
    private static readonly string Root = FindRoot();
    private static readonly string BrandScript = Path.Combine(Root, "build", "tools", "brand-beta-site.sh");
    private static readonly string LimitsScript = Path.Combine(Root, "build", "tools", "check-static-site-limits.sh");
    private readonly string _directory = Directory.CreateTempSubdirectory("kazoos-beta-site-").FullName;

    [Fact]
    public void The_branding_renames_the_pwa_and_the_tab_of_the_real_wwwroot()
    {
        string source = Path.Combine(Root, "OS", "Kazo.Os.Ecosystem", "wwwroot");
        File.Copy(Path.Combine(source, "manifest.webmanifest"), Path.Combine(_directory, "manifest.webmanifest"));
        File.Copy(Path.Combine(source, "index.html"), Path.Combine(_directory, "index.html"));

        ScriptResult? result = Run(BrandScript, _directory);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        string manifest = File.ReadAllText(Path.Combine(_directory, "manifest.webmanifest"));
        Assert.Contains("\"name\": \"Kazo OS Beta\",", manifest, StringComparison.Ordinal);
        Assert.Contains("\"short_name\": \"Kazo OS Beta\",", manifest, StringComparison.Ordinal);
        Assert.Contains("<title>Kazo OS Beta</title>", File.ReadAllText(Path.Combine(_directory, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_branding_refuses_files_without_the_expected_name()
    {
        File.WriteAllText(Path.Combine(_directory, "manifest.webmanifest"), "{ \"name\": \"Other\", \"short_name\": \"Other\" }");
        File.WriteAllText(Path.Combine(_directory, "index.html"), "<title>Kazo OS</title>");

        ScriptResult? result = Run(BrandScript, _directory);
        if (result is null)
        {
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("has no \"name\": \"Kazo OS\"", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_site_within_the_limits_passes_and_reports_its_largest_file()
    {
        File.WriteAllBytes(Path.Combine(_directory, "small.js"), new byte[10]);
        Directory.CreateDirectory(Path.Combine(_directory, "_framework"));
        File.WriteAllBytes(Path.Combine(_directory, "_framework", "big.wasm"), new byte[100]);

        ScriptResult? result = Run(LimitsScript, _directory, "5", "1000");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("files=2", result.Output, StringComparison.Ordinal);
        Assert.Contains("largest=100", result.Output, StringComparison.Ordinal);
        Assert.Contains("largest_path=_framework/big.wasm", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", "1000", "2 files, the host accepts at most 1")]
    [InlineData("5", "50", "big.wasm is 100 bytes, the host accepts at most 50 per file")]
    public void A_site_over_a_limit_is_refused_with_the_reason(string maxFiles, string maxBytes, string reason)
    {
        File.WriteAllBytes(Path.Combine(_directory, "small.js"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_directory, "big.wasm"), new byte[100]);

        ScriptResult? result = Run(LimitsScript, _directory, maxFiles, maxBytes);
        if (result is null)
        {
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Runs a script with bash. The CI runs this suite on Linux, where bash always exists; on
    /// Windows without bash on the PATH, the case returns null and passes.
    /// </summary>
    private static ScriptResult? Run(string script, params string[] arguments)
    {
        ProcessStartInfo start = new("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
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
