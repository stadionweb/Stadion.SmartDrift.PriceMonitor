using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed class CrawlerRegistry
{
    private readonly Dictionary<string, IWebsiteCrawler> crawlers = new(StringComparer.OrdinalIgnoreCase);

    public CrawlerRegistry(IEnumerable<IWebsiteCrawler> registrations)
    {
        foreach (var crawler in registrations)
        {
            if (string.IsNullOrWhiteSpace(crawler.Key) || crawler.Key != crawler.Key.Trim())
            {
                throw new InvalidOperationException("Crawler keys must be non-empty and have no surrounding whitespace.");
            }

            if (!crawlers.TryAdd(crawler.Key, crawler))
            {
                throw new InvalidOperationException($"Duplicate crawler key: {crawler.Key}");
            }
        }
    }

    public IWebsiteCrawler? Find(string key) => crawlers.GetValueOrDefault(key);

    public Task<CrawlResult> ExecuteAsync(string key, CrawlTarget target, CancellationToken cancellationToken)
    {
        var crawler = Find(key) ?? throw new ArgumentException($"Unknown crawler key: {key}", nameof(key));
        return crawler.CrawlAsync(target, cancellationToken);
    }
}
