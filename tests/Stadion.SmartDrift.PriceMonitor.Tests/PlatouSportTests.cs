using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class PlatouSportTests
{
    private static readonly Guid VariantId = Guid.Parse("dfef8022-1c76-404b-919e-71a9363bb83f");
    private static readonly Guid XlVariantId = Guid.Parse("9f1429c4-420d-4655-962c-29a15e5119ce");
    private static readonly CrawlTarget Target = new(Guid.NewGuid(), "ArcTeryx", "Beta SL Jacket M", "X000010552",
        [new CrawlVariant(VariantId, "0623555782018", null, "M", "Black")]);
    private static readonly Uri ProductUrl = new("https://www.platousport.com/produkt/item-123/");
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatouSport", name));

    [Fact]
    public void ParserUsesExactVariantOfferRatherThanAnotherVariantOrHistoricalPrice()
    {
        var now = DateTimeOffset.UtcNow;
        var result = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), ProductUrl, Target, now);
        var product = Assert.Single(result.ProductMatches);
        var match = Assert.Single(result.VariantMatches);
        var observation = Assert.Single(result.PriceObservations);
        Assert.Equal("gtin", product.MatchBasis);
        Assert.Null(product.Gtin);
        Assert.Equal(VariantMatchMethod.ExactGtin, match.MatchMethod);
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
    public void ParserReturnsOneProductWithSeparateMAndXlVariantMatches()
    {
        var url = new Uri("https://www.platousport.com/produkt/arcteryx-beta-sl-jacket-mens-black-2/");
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "0623555782032", null, "XL", "Black")] };
        var now = DateTimeOffset.UtcNow;
        var result = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), url, target, now);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        var product = Assert.Single(result.ProductMatches);
        Assert.Equal("Arc'teryx Beta SL Jacket Mens Black", product.Name);
        Assert.Equal(url, product.SourceUrl);
        Assert.Equal(2, result.VariantMatches.Count);
        var m = Assert.Single(result.VariantMatches, v => v.VariantId == VariantId);
        var xl = Assert.Single(result.VariantMatches, v => v.VariantId == XlVariantId);
        Assert.Equal(("0623555782018", "M", "Black", VariantMatchMethod.ExactGtin, url),
            (m.Gtin, m.Size, m.Color, m.MatchMethod, m.ProductUrl));
        Assert.Equal(("0623555782032", "XL", "Black", VariantMatchMethod.ExactGtin, url),
            (xl.Gtin, xl.Size, xl.Color, xl.MatchMethod, xl.ProductUrl));
        var xlObservation = Assert.Single(result.PriceObservations, o => o.VariantId == XlVariantId);
        Assert.Equal(4450.75m, xlObservation.CurrentPrice);
        Assert.Equal("NOK", xlObservation.CurrencyCode);
        Assert.True(xlObservation.InStock);
        Assert.Equal(url, xlObservation.SourceUrl);
        Assert.Equal(now, xlObservation.ObservedAt);
    }

    [Fact]
    public void SeparateSizeSelectionsResolveToSameProduct()
    {
        var xlTarget = Target with { Variants = [new CrawlVariant(XlVariantId, "0623555782032", null, "XL", "Black")] };
        var m = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), ProductUrl, Target, DateTimeOffset.UtcNow);
        var xl = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), ProductUrl, xlTarget, DateTimeOffset.UtcNow);
        Assert.Equal(Assert.Single(m.ProductMatches), Assert.Single(xl.ProductMatches));
        Assert.NotEqual(Assert.Single(m.VariantMatches).Gtin, Assert.Single(xl.VariantMatches).Gtin);
        Assert.Equal(4450.75m, Assert.Single(xl.PriceObservations).CurrentPrice);
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
    public void AmbiguousRepeatedGtinDoesNotProduceVariantPrice()
    {
        var html = Fixture("beta-sl-jacket.html").Replace("0623555782025", "0623555782018");
        var result = PlatouSportProductParser.Parse(html, ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Empty(result.VariantMatches);
        Assert.Empty(result.PriceObservations);
    }

    [Fact]
    public void DuplicateTargetGtinCannotBeAssignedArbitrarily()
    {
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "0623555782018", null, "M", "Black")] };
        var result = PlatouSportProductParser.Parse(Fixture("beta-sl-jacket.html"), ProductUrl, target, DateTimeOffset.UtcNow);
        Assert.Empty(result.VariantMatches);
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
        result = PlatouSportProductParser.Parse(
            """<script type="application/ld+json">{"@type":"Product","name":"Other"}</script>""",
            ProductUrl, Target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task MultipleVariantsAreDiscoveredOnOneProduct()
    {
        using var client = new HttpClient(new FixtureHandler());
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "0623555782032", null, "XL", "Black")] };
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Single(result.ProductMatches);
        Assert.Equal(2, result.VariantMatches.Count);
        Assert.Equal(2, result.PriceObservations.Count);
    }

    [Fact]
    public async Task UnmatchedVariantYieldsPartialResult()
    {
        using var client = new HttpClient(new FixtureHandler());
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "123", null, null, null)] };
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Single(result.ProductMatches);
        Assert.Single(result.VariantMatches);
        Assert.Single(result.PriceObservations);
    }

    [Fact]
    public async Task DiscoveryContinuesForVariantsOnDifferentPages()
    {
        var fixture = Fixture("beta-sl-jacket.html");
        var handler = new FixtureHandler
        {
            Unrelated = fixture.Replace("0623555782032", "0000000000000"),
            Product = fixture.Replace("0623555782018", "0000000000001")
        };
        using var client = new HttpClient(handler);
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "0623555782032", null, "XL", "Black")] };
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(2, result.ProductMatches.Count);
        Assert.Equal("/produkt/unrelated/", Assert.Single(result.VariantMatches, v => v.VariantId == VariantId).ProductUrl.AbsolutePath);
        Assert.Equal("/produkt/item-123/", Assert.Single(result.VariantMatches, v => v.VariantId == XlVariantId).ProductUrl.AbsolutePath);
        Assert.Equal(2, result.PriceObservations.Count);
    }

    [Fact]
    public void MissingPriceForOneVariantKeepsTheOtherObservationAndIsIncomplete()
    {
        var target = Target with { Variants = [Target.Variants[0], new CrawlVariant(XlVariantId, "0623555782032", null, "XL", "Black")] };
        var html = Fixture("beta-sl-jacket.html").Replace("\"price\": \"4450.75\"", "\"price\": \"unknown\"");
        var result = PlatouSportProductParser.Parse(html, ProductUrl, target, DateTimeOffset.UtcNow);
        Assert.Equal(CrawlStatus.Incomplete, result.Status);
        Assert.Single(result.ProductMatches);
        Assert.Equal(2, result.VariantMatches.Count);
        Assert.Equal(VariantId, Assert.Single(result.PriceObservations).VariantId);
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
    public async Task DiscoveryContinuesPastUnpricedExactMatch()
    {
        var handler = new FixtureHandler { Unrelated = Fixture("beta-sl-jacket.html").Replace("\"price\": \"4321.50\"", "\"price\": \"unknown\"") };
        using var client = new HttpClient(handler);
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.Equal(4321.50m, Assert.Single(result.PriceObservations).CurrentPrice);
        Assert.Contains("/produkt/item-123/", handler.Paths);
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
    public async Task RobotsMatchesUnicodeAndPercentEncodedPaths()
    {
        var handler = new FixtureHandler { Robots = "User-agent: *\nDisallow: /produkt/å/\n",
            Sitemap = Fixture("product-sitemap.xml").Replace("/produkt/unrelated/", "/produkt/%C3%A5/") };
        using var client = new HttpClient(handler);
        var result = await new PlatouSportCrawler(client, new(RequestInterval: TimeSpan.Zero)).CrawlAsync(Target, CancellationToken.None);
        Assert.Equal(CrawlStatus.Succeeded, result.Status);
        Assert.DoesNotContain("/produkt/%C3%A5/", handler.Paths);
    }

    [Fact]
    public async Task ResponseBodyIsSubjectToTimeout()
    {
        using var client = new HttpClient(new FixtureHandler { StallBody = true });
        var result = await new PlatouSportCrawler(client,
            new(RequestInterval: TimeSpan.Zero, RequestTimeout: TimeSpan.FromMilliseconds(50))).CrawlAsync(Target, CancellationToken.None);
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
        public string Sitemap { get; init; } = Fixture("product-sitemap.xml");
        public string Unrelated { get; init; } = """<script type="application/ld+json">{"@type":"Product","name":"Other","gtin13":"1234567890123"}</script>""";
        public string Product { get; init; } = Fixture("beta-sl-jacket.html");
        public bool StallBody { get; init; }
        public List<string> Paths { get; } = [];
        public List<string> Agents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Agents.Add(request.Headers.UserAgent.ToString());
            if (StallBody)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
            var body = path switch
            {
                "/robots.txt" => Robots,
                "/sitemap_index.xml" => Fixture("sitemap-index.xml"),
                "/product-sitemap26.xml" => Sitemap,
                "/produkt/unrelated/" => Unrelated,
                "/produkt/item-123/" => Product,
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
