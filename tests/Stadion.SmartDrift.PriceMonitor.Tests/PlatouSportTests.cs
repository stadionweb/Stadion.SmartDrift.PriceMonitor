using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class PlatouSportTests
{
    private static readonly Guid VariantId = Guid.Parse("dfef8022-1c76-404b-919e-71a9363bb83f");
    private static readonly CrawlTarget Target = new(Guid.NewGuid(), "ArcTeryx", "Beta SL Jacket M", "X000010552",
        [new CrawlVariant(VariantId, "0623555782018", null, "M", "Black")]);
    private static readonly Uri ProductUrl = new("https://www.platousport.com/produkt/item-123/");
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatouSport", name));

    [Fact]
    public void ParserUsesExactVariantOfferRatherThanAnotherVariantOrHistoricalPrice()
    {
        var now = DateTimeOffset.UtcNow;
        var result = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), ProductUrl, Target, now);
        var match = Assert.Single(result.ProductMatches);
        var observation = Assert.Single(result.PriceObservations);
        Assert.Equal("gtin", match.MatchBasis);
        Assert.Equal("0623555782018", match.Gtin);
        Assert.Equal(VariantId, match.VariantId);
        Assert.Equal("M", match.Size);
        Assert.Equal("Black", match.Color);
        Assert.Equal(4321.50m, observation.CurrentPrice);
        Assert.Null(observation.RegularPrice);
        Assert.Equal("NOK", observation.CurrencyCode);
        Assert.True(observation.InStock);
        Assert.Equal(ProductUrl, observation.SourceUrl);
        Assert.Equal(now, observation.ObservedAt);
        Assert.Equal(VariantId, observation.VariantId);
    }

    [Fact]
    public void ParserNeverAssociatesProductLevelOfferWithUnmatchedVariant()
    {
        var html = Fixture("beta-sl-jacket.html").Replace("0623555782018", "0000000000000");
        var result = PlatouSportProductParser.Parse(html, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal("article-number", Assert.Single(result.ProductMatches).MatchBasis);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public void ConflictingVariantAttributesDoNotProduceAnExactObservation()
    {
        var html = Fixture("beta-sl-jacket.html").Replace("\"size\": \"M\"", "\"size\": \"S\"");
        var result = PlatouSportProductParser.Parse(html, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal("article-number", Assert.Single(result.ProductMatches).MatchBasis);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public void MissingOrAmbiguousOffersDoNotInventPrices()
    {
        var html = Fixture("beta-sl-jacket.html").Replace("\"price\": \"4321.50\"", "\"price\": \"not a price\"");
        var result = PlatouSportProductParser.Parse(html, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal("gtin", Assert.Single(result.ProductMatches).MatchBasis);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public void UnsupportedPageIsNotReportedAsAnExhaustiveNonMatch()
    {
        var result = PlatouSportProductParser.Parse("<html></html>", ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Empty(result.ProductMatches);
    }

    [Fact]
    public void SitemapRejectsForeignOriginsAndDtds()
    {
        var index = PlatouSportCrawler.ParseSitemap(Fixture("sitemap-index.xml"), "sitemapindex", "sitemap");
        Assert.Equal(2, index.Count);
        Assert.Throws<System.Xml.XmlException>(() => PlatouSportCrawler.ParseSitemap(
            "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><urlset xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'>&x;</urlset>",
            "urlset", "url"));
    }

    [Fact]
    public async Task DiscoveryFetchesOnlyAllowedProductSitemapsAndPages()
    {
        var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var crawler = new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero));
        var result = await crawler.CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(4321.50m, Assert.Single(result.PriceObservations).CurrentPrice);
        Assert.Equal(new[] { "/robots.txt", "/sitemap_index.xml", "/product-sitemap26.xml",
            "/produkt/unrelated/", "/produkt/item-123/" }, handler.Paths);
        Assert.All(handler.Agents, agent => Assert.Equal("StadionSmartDriftPriceMonitor/1.0", agent));
    }

    [Fact]
    public async Task DiscoveryReportsLimitInsteadOfFalseNotFound()
    {
        using var client = new HttpClient(new FixtureHandler());
        var result = await new PlatouSportCrawler(client, new(MaxProductPages: 1, RequestInterval: TimeSpan.Zero))
            .CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public async Task DiscoveryRespectsSpecificRobotsRules()
    {
        var handler = new FixtureHandler { Robots = "User-agent: *\nDisallow:\n\nUser-agent: StadionSmartDriftPriceMonitor\nDisallow: /produkt/" };
        using var client = new HttpClient(handler);
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.DoesNotContain(handler.Paths, p => p.StartsWith("/produkt/"));
    }

    [Fact]
    public async Task DiscoveryUsesLongestRobotsRuleAndIgnoresOtherBots()
    {
        var handler = new FixtureHandler { Robots = "User-agent: *\nDisallow: /produkt/\nAllow: /produkt/item-123/\n\nUser-agent: meta-externalagent\nDisallow: /" };
        using var client = new HttpClient(handler);
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.DoesNotContain("/produkt/unrelated/", handler.Paths);
        Assert.Contains("/produkt/item-123/", handler.Paths);
    }

    [Fact]
    public async Task MissingMatchIsReportedAfterCompleteSearch()
    {
        using var client = new HttpClient(new FixtureHandler());
        var target = Target with { ArticleNumber = "other", ProductName = "unrelated name",
            Variants = [new CrawlVariant(VariantId, "0000000000000", null, null, null)] };
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(target, CancellationToken.None);
        Assert.Equal(CrawlStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task RobotsFailureIsNotTreatedAsPermission()
    {
        using var client = new HttpClient(new FixtureHandler { Robots = null });
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Failed, result.Status);
    }

    [Fact]
    public async Task RegistryIncludesPlatouSport()
    {
        using var provider = new ServiceCollection().AddCrawlerRuntime().BuildServiceProvider();
        Assert.IsType<PlatouSportCrawler>(provider.GetRequiredService<CrawlerRegistry>().Find("PLATOU-SPORT"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<CrawlerRegistry>().ExecuteAsync("platou-sport", Target, cancellation.Token));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string? Robots { get; init; } = Fixture("robots.txt");
        public List<string> Paths { get; } = [];
        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Agents.Add(request.Headers.UserAgent.ToString());
            var body = path switch
            {
                "/robots.txt" => Robots,
                "/sitemap_index.xml" => Fixture("sitemap-index.xml"),
                "/product-sitemap26.xml" => Fixture("product-sitemap.xml"),
                "/produkt/unrelated/" => """<script type="application/ld+json">{"@type":"Product","name":"Other","gtin13":"1234567890123"}</script>""",
                "/produkt/item-123/" => Fixture("beta-sl-jacket.html"),
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
