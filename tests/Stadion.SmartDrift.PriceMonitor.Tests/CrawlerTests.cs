using Microsoft.Extensions.DependencyInjection;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class CrawlerTests
{
    private static readonly CrawlTarget Target = new(Guid.NewGuid(), "Brand", "Product", "ABC", []);

    [Fact]
    public async Task RegisteredCrawlerReceivesTargetAndCancellationToken()
    {
        var crawler = new TestCrawler("example");
        var services = new ServiceCollection();
        services.AddSingleton<IWebsiteCrawler>(crawler);
        services.AddCrawlerRuntime();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CrawlerRegistry>();
        using var cancellation = new CancellationTokenSource();

        var result = await registry.ExecuteAsync("EXAMPLE", Target, cancellation.Token);

        Assert.Same(crawler, registry.Find("example"));
        Assert.Same(Target, crawler.Target);
        Assert.Equal(cancellation.Token, crawler.Token);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Empty(result.ProductMatches);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public async Task EmptyRegistrationResolvesAndUnknownKeyFails()
    {
        using var provider = new ServiceCollection().AddCrawlerRuntime().BuildServiceProvider();
        var registry = provider.GetRequiredService<CrawlerRegistry>();

        Assert.Null(registry.Find("missing"));
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => registry.ExecuteAsync("missing", Target, CancellationToken.None));
        Assert.Contains("Unknown crawler key: missing", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" example")]
    public void InvalidKeysAreRejected(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new CrawlerRegistry([new TestCrawler(key)]));
    }

    [Fact]
    public void DuplicateKeysAreRejectedCaseInsensitively()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CrawlerRegistry([new TestCrawler("example"), new TestCrawler("EXAMPLE")]));
    }

    [Fact]
    public void ObservationRetainsNeutralPriceAndStockFacts()
    {
        var observedAt = DateTimeOffset.Parse("2026-01-01T12:00:00+00:00");
        var url = new Uri("https://example.com/product");
        var observation = new PriceObservationCandidate(100m, 80m, 90m, 75m, "NOK", false, "Sold out", "Sale", url, observedAt);

        Assert.Equal(100m, observation.RegularPrice);
        Assert.Equal(80m, observation.CurrentPrice);
        Assert.Equal(90m, observation.PreviousPrice);
        Assert.Equal(75m, observation.MemberPrice);
        Assert.Equal("NOK", observation.CurrencyCode);
        Assert.False(observation.InStock);
        Assert.Equal("Sold out", observation.StockText);
        Assert.Equal("Sale", observation.PromotionText);
        Assert.Equal(url, observation.SourceUrl);
        Assert.Equal(observedAt, observation.ObservedAt);
    }

    private sealed class TestCrawler(string key) : IWebsiteCrawler
    {
        public string Key { get; } = key;
        public CrawlTarget? Target { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken)
        {
            Target = target;
            Token = cancellationToken;
            return Task.FromResult(new CrawlResult(CrawlStatus.Succeeded, [], []));
        }
    }
}
