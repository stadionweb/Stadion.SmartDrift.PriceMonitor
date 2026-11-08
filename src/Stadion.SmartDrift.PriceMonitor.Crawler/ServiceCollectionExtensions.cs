using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCrawlerRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IWebsiteCrawler>(provider =>
        {
            var configuration = provider.GetService<IConfiguration>();
            var maxPages = configuration?.GetValue<int?>("PlatouSport:MaxProductPages") ?? 500;
            var maxSitemaps = configuration?.GetValue<int?>("PlatouSport:MaxSitemaps") ?? 30;
            if (maxPages is < 1 or > 100_000 || maxSitemaps is < 1 or > 100)
                throw new InvalidOperationException("Platou Sport discovery limits are out of range.");
            return new PlatouSportCrawler(new HttpClient(
                new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) },
                new PlatouSportOptions(MaxSitemaps: maxSitemaps, MaxProductPages: maxPages));
        });
        services.AddSingleton<CrawlerRegistry>();
        return services;
    }
}
