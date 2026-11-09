using System.Diagnostics;
using System.Reflection;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class RuntimeSmokeTests
{
    [Theory]
    [InlineData(false, 0, "No crawler selected")]
    [InlineData(true, 2, "Unknown crawler key: unknown")]
    public async Task WorkerProcessStartsAndReportsSelection(bool unknown, int expectedExitCode, string expectedMessage)
    {
        var metadata = typeof(RuntimeSmokeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "run", "--no-build", "--no-restore", "--project", metadata["CrawlerProjectPath"]!, "--configuration", metadata["BuildConfiguration"]! })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (unknown)
        {
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--run");
            startInfo.ArgumentList.Add("unknown");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start crawler process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdout;
        var error = await stderr;
        var diagnostic = $"Timed out: {timedOut}; exit code: {process.ExitCode}\nstdout:\n{output}\nstderr:\n{error}";
        Assert.True(!timedOut && process.ExitCode == expectedExitCode, diagnostic);
        Assert.True(output.Contains(expectedMessage, StringComparison.Ordinal) || error.Contains(expectedMessage, StringComparison.Ordinal), diagnostic);
    }

    [Theory]
    [InlineData("platou-sport")]
    [InlineData("brekke-sport")]
    public async Task RegisteredCrawlerSelectionDoesNotContactWebsiteWithoutControlTarget(string crawlerKey)
    {
        var metadata = typeof(RuntimeSmokeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "run", "--no-build", "--no-restore", "--project", metadata["CrawlerProjectPath"]!,
            "--configuration", metadata["BuildConfiguration"]!, "--", "--run", crawlerKey })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"stdout:\n{output}\nstderr:\n{error}");
        Assert.Contains("selected, but no monitoring targets are configured", output);
    }
}
