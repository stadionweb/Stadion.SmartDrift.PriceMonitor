using Stadion.SmartDrift.PriceMonitor.Crawler;
using Stadion.SmartDrift.PriceMonitor.Core;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddCrawlerRuntime();

using var host = builder.Build();
await host.StartAsync();
try
{
    var registry = host.Services.GetRequiredService<CrawlerRegistry>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Crawler");

    if (args.Length == 0)
    {
        logger.LogInformation("No crawler selected. No external websites were contacted.");
        return 0;
    }

    if ((args.Length != 2 && args.Length != 3) || args[0] != "--run" || string.IsNullOrWhiteSpace(args[1]) ||
        (args.Length == 3 && args[2] != "--control-target"))
    {
        logger.LogError("Usage: --run <crawler-key> [--control-target]");
        return 2;
    }

    if (args.Length == 3)
    {
        if (!args[1].Equals("platou-sport", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("The control target is only available for platou-sport.");
            return 2;
        }
        var target = new CrawlTarget(Guid.Empty, "ArcTeryx", "Beta SL Jacket M", "X000010552",
            [new CrawlVariant(Guid.Empty, "0623555782018", null, "M", "Black")]);
        var result = await registry.ExecuteAsync(args[1], target, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == CrawlStatus.Succeeded ? 0 : 1;
    }

    if (registry.Find(args[1]) is null)
    {
        logger.LogError("Unknown crawler key: {CrawlerKey}", args[1]);
        return 2;
    }

    logger.LogInformation("Crawler {CrawlerKey} selected, but no monitoring targets are configured.", args[1]);
    return 0;
}
finally
{
    await host.StopAsync();
}
