using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class PersistedMonitoringSmokeTests
{
    [Theory]
    [InlineData("discovery", 0, "Found", 1, 0)]
    [InlineData("polling", 0, "Found", 0, 1)]
    [InlineData("broken", 1, "RequiresRequalification", 0, 0)]
    [InlineData("unknown", 1, "UnknownWebsiteAdapter", 0, 0)]
    public async Task RealWorkerRunsBoundedCommandWithOfflineHttpAndPersistence(
        string scenario, int exitCode, string code, int created, int reused)
    {
        var url = new Uri("https://www.platousport.com/produkt/arcteryx-beta-sl-jacket-mens-black-2/");
        var directory = Path.Combine(Path.GetTempPath(), "price-monitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var work = new MonitoringWorkItem(2, 2, 1, scenario == "unknown" ? "unknown" : "platou-sport",
                new(35148, "ArcTeryx", "Beta SL Jacket M", "X000010552",
                    [new(54618, "0623555782018", null, "M", null), new(54620, "0623555782032", null, "XL", null)]));
            var fixture = new OfflineMonitoringFixture(work, new()
            {
                ["https://www.platousport.com/robots.txt"] = "User-agent: *\nDisallow:",
                ["https://www.platousport.com/sitemap_index.xml"] =
                    """<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><sitemap><loc>https://www.platousport.com/product-sitemap.xml</loc></sitemap></sitemapindex>""",
                ["https://www.platousport.com/product-sitemap.xml"] =
                    $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>{url}</loc></url></urlset>""",
                [url.AbsoluteUri] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                    "Fixtures", "PlatouSport", "captured-product-excerpt.html")).Replace("5999", "6123")
            }, scenario is "polling" or "broken"
                ? new(7, scenario == "broken" ? "stale-product" : "392833", url, [new(54618, "392835", "0623555782018")]) : null);
            var path = Path.Combine(directory, "fixture.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture));
            var (actualExit, output, error) = await ExecuteAsync(
                ["--run", "persisted-monitoring", "--monitoring-target-id", "2", "--gtin", "0623555782018", "--offline-fixture", path]);
            Assert.True(actualExit == exitCode, $"exit: {actualExit}\nstdout:\n{output}\nstderr:\n{error}");
            Assert.Contains("OFFLINE FIXTURE", output);
            var json = output.Split('\n').Single(line => line.StartsWith("{\"RunId\"", StringComparison.Ordinal));
            using var summary = JsonDocument.Parse(json);
            Assert.Equal(1, summary.RootElement.GetProperty("Requested").GetInt32());
            Assert.Equal(code, summary.RootElement.GetProperty("Outcomes")[0].GetProperty("Code").GetString());
            Assert.Equal(created, summary.RootElement.GetProperty("ProductMatchesCreated").GetInt32());
            Assert.Equal(reused, summary.RootElement.GetProperty("ProductMatchesReused").GetInt32());
            Assert.Equal(exitCode == 0 ? 1 : 0, summary.RootElement.GetProperty("ObservationsWritten").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("--run persisted-monitoring", "Usage:")]
    [InlineData("--run persisted-monitoring --monitoring-target-id 0", "Usage:")]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2", "requires ConnectionStrings")]
    public async Task InvalidOrUnconfiguredCommandFailsSafely(string args, string message)
    {
        var (exitCode, output, error) = await ExecuteAsync(args.Split(' '));
        Assert.True(exitCode == 2, $"exit: {exitCode}\nstdout:\n{output}\nstderr:\n{error}");
        Assert.Contains(message, output + error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string[] args)
    {
        var metadata = typeof(PersistedMonitoringSmokeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in new[] { "run", "--no-build", "--no-restore", "--project", metadata["CrawlerProjectPath"]!,
            "--configuration", metadata["BuildConfiguration"]!, "--" }.Concat(args))
            start.ArgumentList.Add(arg);
        start.Environment.Remove("ConnectionStrings__PriceMonitor");
        start.Environment.Remove("ConnectionStrings__Integration");
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker failed to start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Worker timed out.\nstdout:\n{await output}\nstderr:\n{await error}");
        }
        return (process.ExitCode, await output, await error);
    }
}
