using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class BrekkeSportTests
{
    private const long VariantId = 12;
    private const long LargeVariantId = 13;
    private static readonly Uri ProductUrl = new("https://www.brekkesport.no/test-jacket");
    private static readonly CrawlTarget Target = new(1, "Brekke", "Brekke Test Jacket", null,
        [new(VariantId, "7040000000012", null, "M", "Blue")]);
    private static string Product => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrekkeSport", "product.html"));

    [Fact]
    public void ParserMatchesExactVariantAndReadsItsOffer()
    {
        var observedAt = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var result = BrekkeSportProductParser.Parse(Product, ProductUrl, Target, observedAt);

        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        var product = Assert.Single(result.ProductMatches);
        Assert.Equal(("BS-100", "Brekke Test Jacket", ProductUrl), (product.ExternalProductId, product.Name, product.SourceUrl));
        var variant = Assert.Single(result.VariantMatches);
        Assert.Equal((VariantId, "7040000000012", "M", "Blue", "BS-100-M", VariantMatchMethod.ExactGtin),
            (variant.VariantId, variant.Gtin, variant.Size, variant.Color, variant.ExternalVariationId, variant.MatchMethod));
        var observation = Assert.Single(result.PriceObservations);
        Assert.Equal((899m, "NOK", true, VariantId, ProductUrl, observedAt),
            (observation.CurrentPrice, observation.CurrencyCode, observation.InStock, observation.VariantId, observation.SourceUrl, observation.ObservedAt));
    }

    [Fact]
    public void ParserSupportsMultipleVariantsAndDoesNotInventMissingPrices()
    {
        var target = Target with { Variants = [Target.Variants[0], new(LargeVariantId, "7040000000029", null, "L", "Blue")] };
        var result = BrekkeSportProductParser.Parse(Product, ProductUrl, target, DateTimeOffset.UtcNow);

        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(2, result.VariantMatches.Count);
        Assert.Equal(2, result.PriceObservations.Count);
        Assert.False(Assert.Single(result.PriceObservations, o => o.VariantId == LargeVariantId).InStock);

        var withoutPrice = Product.Replace("\"price\": \"899.00\"", "\"price\": \"unknown\"");
        result = BrekkeSportProductParser.Parse(withoutPrice, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Single(result.VariantMatches);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public void ParserRejectsDuplicateGtinsAndReportsNoMatchConservatively()
    {
        var duplicate = Product.Replace("7040000000029", "7040000000012");
        var result = BrekkeSportProductParser.Parse(duplicate, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Ambiguous, result.Status);
        Assert.Empty(result.PriceObservations);

        var other = Target with { Variants = [new(VariantId, "7040000000098", null, "M", "Blue")] };
        result = BrekkeSportProductParser.Parse(Product, ProductUrl, other, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.NotFound, result.Status);
        Assert.Empty(result.VariantMatches);
        Assert.Empty(result.PriceObservations);

        result = BrekkeSportProductParser.Parse("<html></html>", ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
    }

    [Fact]
    public void SitemapParsingRejectsForeignOriginsAndDtds()
    {
        var urls = BrekkeSportCrawler.ParseSitemap(
            """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://www.brekkesport.no/jacket</loc></url><url><loc>https://other.example/jacket</loc></url></urlset>""",
            "urlset", "url");
        Assert.Equal([new Uri("https://www.brekkesport.no/jacket")], urls);
        Assert.Throws<System.Xml.XmlException>(() => BrekkeSportCrawler.ParseSitemap(
            "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><urlset xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'>&x;</urlset>",
            "urlset", "url"));
        Assert.Throws<InvalidDataException>(() => BrekkeSportCrawler.ParseSitemap("<html></html>", "sitemapindex", "sitemap"));
    }

    [Fact]
    public async Task DiscoveryUsesRobotsSitemapAndProductPage()
    {
        var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var result = await new BrekkeSportCrawler(client, new(RequestInterval: TimeSpan.Zero))
            .CrawlAsync(Target, CancellationToken.None);

        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(899m, Assert.Single(result.PriceObservations).CurrentPrice);
        Assert.Equal(new[] { "/robots.txt", "/sitemap.xml", "/products.xml", ProductUrl.AbsolutePath }, handler.Paths);
        Assert.All(handler.Agents, agent => Assert.Equal("StadionSmartDriftPriceMonitor/1.0", agent));
    }

    [Fact]
    public async Task DiscoveryDoesNotFetchRobotsDisallowedSitemap()
    {
        var handler = new FixtureHandler { Robots = "User-agent: *\nDisallow: /" };
        using var client = new HttpClient(handler);
        var result = await new BrekkeSportCrawler(client, new(RequestInterval: TimeSpan.Zero))
            .CrawlAsync(Target, CancellationToken.None);

        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Equal(["/robots.txt"], handler.Paths);
    }

    [Fact]
    public async Task PollingVerifiesKnownProductWithoutSitemapDiscovery()
    {
        var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var match = new VerifiedProductMatch(7, "BS-100", ProductUrl, [new(VariantId, "BS-100-M", "7040000000012")]);
        var result = await new BrekkeSportCrawler(client, new(RequestInterval: TimeSpan.Zero))
            .PollAsync(Target, match, CancellationToken.None);

        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "/robots.txt", ProductUrl.AbsolutePath }, handler.Paths);
    }

    [Fact]
    public async Task PollingRequiresRequalificationForChangedIdentityOrUnsafeUrl()
    {
        var handler = new FixtureHandler { Product = Product.Replace("BS-100", "BS-OLD") };
        using var client = new HttpClient(handler);
        var match = new VerifiedProductMatch(7, "BS-100", ProductUrl, [new(VariantId, "BS-100-M", "7040000000012")]);
        var result = await new BrekkeSportCrawler(client, new(RequestInterval: TimeSpan.Zero))
            .PollAsync(Target, match, CancellationToken.None);
        Assert.Equal(CrawlStatus.Failed, result.Status);
        Assert.Contains("RequiresRequalification", result.Diagnostic);

        var unsafeMatch = match with { ProductUrl = new Uri("https://www.brekkesport.no/test-jacket?redirect=example") };
        result = await new BrekkeSportCrawler(client, new(RequestInterval: TimeSpan.Zero))
            .PollAsync(Target, unsafeMatch, CancellationToken.None);
        Assert.Equal(CrawlStatus.Failed, result.Status);
        Assert.Equal(new[] { "/robots.txt", ProductUrl.AbsolutePath }, handler.Paths);
    }

    [Fact]
    public async Task RuntimeRegistersBrekkeSportCrawler()
    {
        using var provider = new ServiceCollection().AddCrawlerRuntime().BuildServiceProvider();
        var crawler = Assert.IsType<BrekkeSportCrawler>(provider.GetRequiredService<CrawlerRegistry>().Find("BREKKE-SPORT"));
        Assert.Equal("brekke-sport", crawler.Key);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Agents { get; } = [];
        public string Robots { get; init; } = "User-agent: *\nAllow: /\nSitemap: https://www.brekkesport.no/sitemap.xml";
        public string Product { get; init; } = BrekkeSportTests.Product;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Agents.Add(request.Headers.UserAgent.ToString());
            var body = path switch
            {
                "/robots.txt" => Robots,
                "/sitemap.xml" => """<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><sitemap><loc>https://www.brekkesport.no/products.xml</loc></sitemap></sitemapindex>""",
                "/products.xml" => $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>{ProductUrl}</loc></url></urlset>""",
                _ when path == ProductUrl.AbsolutePath => Product,
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
