namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCrawlerRuntime(this IServiceCollection services)
    {
        services.AddSingleton<CrawlerRegistry>();
        return services;
    }
}
