using Stadion.SmartDrift.PriceMonitor.Crawler;
using Stadion.SmartDrift.PriceMonitor.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stadion.SmartDrift.PriceMonitor.Infrastructure;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddCrawlerRuntime();

using var host = builder.Build();
await host.StartAsync();
try
{
    var registry = host.Services.GetRequiredService<CrawlerRegistry>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Crawler");
    var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    if (args.Length == 0)
    {
        logger.LogInformation("No crawler selected. No external websites were contacted.");
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--run" && args[1] == "persisted-monitoring-batch")
    {
        if (!PersistedMonitoringBatchCommand.TryParse(args, out var selection, out var fixture))
        {
            logger.LogError("Usage: --run persisted-monitoring-batch [--website-key <key>] [--max-targets <1-100000>] [--offline-fixture <absolute-json-path>]");
            return 2;
        }
        try
        {
            PersistedMonitoringRunner runner;
            if (fixture is not null)
            {
                runner = await OfflineMonitoringFixture.CreateRunnerAsync(fixture, stopping);
                logger.LogWarning("OFFLINE FIXTURE: no SQL or live HTTP; this is not persisted acceptance.");
            }
            else
            {
                var priceMonitor = builder.Configuration.GetConnectionString("PriceMonitor");
                var integration = builder.Configuration.GetConnectionString("Integration");
                if (string.IsNullOrWhiteSpace(priceMonitor) || string.IsNullOrWhiteSpace(integration))
                {
                    logger.LogError("Persisted monitoring batch requires ConnectionStrings:PriceMonitor and ConnectionStrings:Integration.");
                    return 2;
                }
                runner = new(new SqlMonitoringStore(priceMonitor), new SqlIntegrationRunStore(integration), registry);
            }
            var summary = await runner.RunBatchAsync(selection!, stopping);
            Console.WriteLine(JsonSerializer.Serialize(summary));
            return summary.ExitCode;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            logger.LogWarning("Persisted monitoring batch cancelled.");
            return 1;
        }
        catch (Exception)
        {
            logger.LogError("Persisted monitoring batch failed. Check configuration, schema, and the IntegrationRun operational result.");
            return 1;
        }
    }

    if (args.Length >= 2 && args[0] == "--run" && args[1] == "persisted-monitoring")
    {
        if (!PersistedMonitoringCommand.TryParse(args, out var selection, out var fixture))
        {
            logger.LogError("Usage: --run persisted-monitoring --monitoring-target-id <positive-id> [--gtin <gtin>] [--offline-fixture <absolute-json-path>]");
            return 2;
        }
        try
        {
            PersistedMonitoringRunner runner;
            if (fixture is not null)
            {
                runner = await OfflineMonitoringFixture.CreateRunnerAsync(fixture, stopping);
                logger.LogWarning("OFFLINE FIXTURE: no SQL or live HTTP; this is not persisted acceptance.");
            }
            else
            {
                var priceMonitor = builder.Configuration.GetConnectionString("PriceMonitor");
                var integration = builder.Configuration.GetConnectionString("Integration");
                if (string.IsNullOrWhiteSpace(priceMonitor) || string.IsNullOrWhiteSpace(integration))
                {
                    logger.LogError("Persisted monitoring requires ConnectionStrings:PriceMonitor and ConnectionStrings:Integration.");
                    return 2;
                }
                runner = new(new SqlMonitoringStore(priceMonitor), new SqlIntegrationRunStore(integration), registry);
            }
            var summary = await runner.RunAsync(selection!, stopping);
            Console.WriteLine(JsonSerializer.Serialize(summary));
            return summary.ExitCode;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            logger.LogWarning("Persisted monitoring cancelled.");
            return 1;
        }
        catch (Exception)
        {
            logger.LogError("Persisted monitoring failed. Check configuration, schema, and the IntegrationRun operational result.");
            return 1;
        }
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
        var target = new CrawlTarget(0, "ArcTeryx", "Beta SL Jacket M", "X000010552",
            [new CrawlVariant(0, "0623555782018", null, "M", "Black")]);
        var result = await registry.ExecuteAsync(args[1], target, stopping);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
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
