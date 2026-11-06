using Stadion.SmartDrift.PriceMonitor.Crawler;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton<CrawlerRegistry>();

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

    if (args.Length != 2 || args[0] != "--run" || string.IsNullOrWhiteSpace(args[1]))
    {
        logger.LogError("Usage: --run <crawler-key>");
        return 2;
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
